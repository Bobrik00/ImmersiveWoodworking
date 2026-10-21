using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// Block entity for the sawhorse (one trestle). Holds the build <see cref="SawhorseStage"/> and renders the
/// matching mesh, swapping it whenever the stage changes (approach «б»: one block type, the BE owns the mesh —
/// no block variant group). The stage is serialized, so it survives world reloads.
///
/// Mesh handling mirrors <c>BlockEntityChoppingBlock</c>: the mesh is (re)built on the MAIN thread
/// (<see cref="UpdateMesh"/>, from Initialize / FromTreeAttributes) and only READ on the tesselation thread
/// (<see cref="OnTesselation"/>); the reference is swapped atomically and never mutated after assignment.
///
/// The stick→stick→rope build (T03.03) drives the stage through <see cref="OnBuilt"/> (stage 1, placed by
/// <see cref="BehaviorSawhorseBuild"/>) and <see cref="TryBuildStep"/> (stage 2 / complete, from
/// <see cref="BlockSawhorse"/>). Each stage tesselates its authored shape (sawhorses_p1/p2/p3) in
/// <see cref="UpdateMesh"/> (T03.06).
///
/// E04 — multiblock pairing. Two Complete trestles sitting orthogonally on the same Y form a 2-cell "pair":
///  • <see cref="TryFormPair"/> (T04.01) finds the partner at completion / on load; diagonals never pair and a
///    trestle already in a pair is excluded as both candidate and initiator (T04.04 locking).
///  • the pair picks a shared <see cref="pairAxis"/> from the two cells' relative position (T04.02); both meshes
///    rotate onto it in <see cref="UpdateMesh"/>.
///  • a deterministic <see cref="isMaster"/> (lexicographically smaller position) owns the held log
///    (<see cref="heldLog"/>) + saw <see cref="sawProgress"/> + the 2-cell log mesh; the slave proxies to it via
///    <see cref="GetMaster"/> (T04.03). The partner link (<see cref="partnerPos"/>) is serialized and re-resolved
///    lazily after a chunk reload (<see cref="GetPartner"/>). Because the gestures land on the master and not on the
///    clicked cell, the pair also answers the engine's <see cref="IMultiblockOffset"/> question with the master's
///    position, so a land claim cutting between the two trestles is tested where the work happens
///    (<see cref="GetControlBlockPos"/>, AuditFixPlan T07.02).
///  • breaking either trestle dissolves the pair: the master releases its log by progress and the survivor goes
///    single again, free to re-pair (<see cref="OnBlockBroken"/> / <see cref="DissolveToSingle"/>, T04.05).
/// The held log itself is placed in E05 and sawn in E06; this class owns the master-side state container for them.
/// </summary>
public partial class BlockEntitySawhorse : BlockEntity
{
    SawhorseStage stage = SawhorseStage.Stage1;

    public SawhorseStage Stage => stage;

    // --- E04 pair state (all serialized). A single (unpaired) Complete trestle has inPair == false and ignores
    // the rest. partnerPos is the other cell of the pair; isMaster is the deterministic log/progress holder;
    // pairAxis is the shared orientation the Complete mesh rotates onto. ---
    bool inPair;
    BlockPos? partnerPos;
    bool isMaster;
    EnumAxis pairAxis = EnumAxis.Z; // only meaningful while inPair; the mesh only rotates then

    public bool InPair => inPair;
    public bool IsMaster => isMaster;
    public EnumAxis PairAxis => pairAxis;

    // --- Master-side held content (T04.03: the master is the sole holder of the log + saw progress). The log is
    // placed in E05 (TryPutLog) and ripped in E06 (AdvanceSaw raises sawProgress); the container + its
    // serialization and the progress-aware teardown that releases it (ReleaseHeldLog) were set up in E04. ---
    ItemStack? heldLog;
    float sawProgress;
    // How many of the 4 sawing stages have already fired for the current log (0..4), SERIALIZED alongside
    // sawProgress (T06.02). This is the authoritative "stages done" count, NOT re-derived from sawProgress at
    // fire time — so a live SawSeconds config change between holds can neither retro-skip a stage (lower
    // SawSeconds) nor retro-fire an already-done one (higher SawSeconds). Reset with the log (ResetSawState).
    int sawStagesFired;
    // Which ACROSS-THE-LOG side the rip works from (the directional-sawing control). The log is sliced into planks
    // ACROSS its width (the authored model drops boards part_1..part_4 from one long side to the other); by default
    // the cut works from the "forward" side — the part_1 side, which is also the fixed side the dropped planks eject
    // toward in SpawnSawnItem (Z-pair → +X, X-pair → −Z). `false` = that default forward side; `true` = the OPPOSITE
    // side (planks eject the other way + the boards visually vanish in reverse order). Chosen from WHERE THE PLAYER
    // CLICKED on the log at the first cut-start (HitIsReversedSide) and LOCKED for the life of the log (the first
    // sawyer on a fresh cut sets it; co-op joiners and mid-cut re-clicks can't flip it — see TryStartSawing). One
    // flag drives BOTH the plank-eject side AND the visual hide order, so the two can never disagree in world space.
    // Serialized (survives reload mid-cut); reset implicitly by the next fresh cut-start, so ResetSawState leaves it.
    bool sawCutReversed;

    // Which PRODUCT this log rips into (SawModePlan) — the debark-vs-saw counterpart of a "which output" lock. Read off
    // the pit saw's tool mode (F, CollectibleBehaviorSawMode) at the FIRST cut-start and LOCKED for the life of the log,
    // exactly like sawCutReversed: the first sawyer on a fresh cut sets it, co-op joiners can't flip it (TryStartSawing).
    // Auto = the input decides (log/section → planks, debarked → beams), so a fresh log with a modeless/Auto saw keeps the
    // pre-plan behavior. Serialized (survives reload mid-cut); reset implicitly by the next fresh cut-start, so
    // ResetSawState leaves it, and the client mirrors it for the hover-info readout.
    SawMode sawMode = SawMode.Auto;

    // Co-op rip-hold state (TRANSIENT, NOT serialized) — replaces the old single sawHoldBase/sawing pair. A pair is a
    // two-man pit saw: it can be ripped by up to TWO players AT ONCE, but ONLY from OPPOSITE ends. We track at most
    // one sawyer PER END, keyed by the end sign (-1 / +1 from PlayerEndSign), so the map size IS the live multiplier
    // (one end manned → 1×, both ends → 2×) and its two possible keys are the hard cap of two; a second player
    // crowding an already-manned end is refused, not stacked. Each tick a manned end adds its OWN player's elapsed
    // DELTA to sawProgress (not the absolute base+secondsUsed of one shared hold), so two independent holds SUM
    // instead of one overwriting the other — which both makes two opposite-end sawyers cut at 2× and removes the old
    // single-field multiplayer progress-corruption. SawyerHold.LastSecondsUsed is that end-owner's HONEST hold clock —
    // the engine's secondsUsed after BlockSawhorse clamped it to real elapsed time (AuditFixPlan T02.03), never the raw
    // argument, which the server inflates by replaying a packet's frames at +0.02 s each — and the per-tick delta is
    // derived from it. Master-only (every gesture routes through GetMaster); a slave's map stays empty.
    readonly Dictionary<int, SawyerHold> sawEnds = new Dictionary<int, SawyerHold>();

    sealed class SawyerHold
    {
        public readonly string Uid;
        public float LastSecondsUsed;
        // Wall-clock stamp of this hold's last OBSERVER effect broadcast (AuditFixPlan T06.01, F-L4-13). Per-hold,
        // not per-station, so two opposite-end sawyers each throw their own dust stream instead of racing for one
        // slot. Server-side only (the client never broadcasts) and transient like the rest of the record.
        public long LastFxMs;
        public SawyerHold(string uid, float lastSecondsUsed) { Uid = uid; LastSecondsUsed = lastSecondsUsed; }
    }

    // ───────────────────────── Crowded end / side: the refusal seam (AuditFixPlan T06.04, F-L4-06) ─────────────────
    //
    // Both hold maps are SERVER-ONLY, so a client cannot tell a free end from a manned one. Its press is therefore
    // OPTIMISTIC by design: TryStartSawing / TryStartDebarking pass their gate on the client and return true, and
    // BlockSawhorse starts the FK animation, whose frame-locked clips carry, while the local half of the hold throws
    // sawdust or chips. The server meanwhile hands this player nothing — the end/side belongs to somebody else — and
    // said so to nobody. A second sawyer at a manned end thus mimed a full rip at zero contribution, and, because the
    // server ends its half of the interaction on that first refused step while the client's half runs on, kept miming
    // even after the owner let go, until the button was released and pressed again.
    //
    // The correction is a TARGETED packet, not synced occupancy: nobody but the refused player needs to know, and a
    // pair nobody is crowding pays nothing. (Syncing the maps would be a full BE resend on every saw press for
    // everyone in range — exactly the traffic T05.04 is cutting. Same trade, and the same reasons, as the chopping
    // block's ChopRefusedPacketId, T02.02.)
    //
    // It is sent from ONE place per station — the mid-hold claim in ContinueSawing / ContinueDebarking — and not from
    // the start gate, for two reasons. The start gate is not where the hold is actually rejected: BlockSawhorse
    // swallows every plain RMB on a held-log pair, so OnBlockInteractStart returns true on the SERVER too and the
    // server's HandUse stays live until the first step returns false (ServerSystemBlockSimulation.callOnUsingBlock).
    // And in the gap between that press and that step the owner may have let go, in which case the claim SUCCEEDS and
    // there was never anything to report. One refusal per hold follows from the same engine loop: the false return
    // clears HandUse, so no later step packet re-enters this code until the next press.

    /// <summary>Block-entity packet: "the end you took is manned by another sawyer, your rip was refused" — server to
    /// the ONE refused player (<see cref="RefuseRipHold"/>), handled in <see cref="OnReceivedServerPacket"/>. A private
    /// id: this BE has no dialog and so no <c>EnumBlockEntityPacketId</c> traffic to collide with.</summary>
    const int RipRefusedPacketId = 2211;

    /// <summary>The strip's counterpart to <see cref="RipRefusedPacketId"/> — "this side is manned by another
    /// debarker" (<see cref="RefuseStripHold"/>).</summary>
    const int StripRefusedPacketId = 2212;

    // CLIENT-ONLY, transient: "the server refused the hold I am optimistically running". Set by the packet and read by
    // BlockSawhorse.OnBlockInteractStep, which from then on keeps the hold ALIVE but silent for the rest of the press
    // (see ClearHoldRefusal for why alive rather than ended); cleared by the next press, in
    // BlockSawhorse.OnBlockInteractStart. A plain bool pair rather than a uid map is honest here: the engine drives the
    // client half of OnBlockInteractStep for the local player alone (see the observer-broadcast note below), so this
    // BE's client copy only ever sees one holder. Master-side, like everything the holds touch — the packet is
    // addressed to the master's Pos, and the two accessors below route there.
    bool ripRefusedLocally;
    bool stripRefusedLocally;

    /// <summary>Whether the server has refused the hold this client is optimistically running (either station).
    /// Master-relative, client-only in practice — the server never sets the flags, so it always reads false.</summary>
    internal bool HoldRefusedLocally
    {
        get
        {
            BlockEntitySawhorse master = GetMaster();
            return master != this ? master.HoldRefusedLocally : ripRefusedLocally || stripRefusedLocally;
        }
    }

    /// <summary>Clear the refusal, from the next press (<see cref="BlockSawhorse.OnBlockInteractStart"/>).
    ///
    /// <para>Why the press and not the hold's end: while a refusal stands, the client's interaction step returns TRUE
    /// and the hold stays alive doing nothing. That is deliberate. The engine treats a swallowed right-click as a
    /// REPEATING gesture — the moment a block interaction ends while the button is still down,
    /// SystemMouseInWorldInteractions re-arms it after BuildRepeatDelay (0.25 s) and calls OnBlockInteractStart again,
    /// which is how holding RMB builds trestle after trestle. Ending the refused hold would therefore restart the
    /// animation, the dust and the whole server round trip four times a second for as long as the button is held. A
    /// live-but-silent hold suppresses that repeat at its source (the re-arm is gated on HandUse being None), so one
    /// press costs exactly one refusal.</para>
    ///
    /// <para>Clearing on the press also heals the one stale case: a packet that lands AFTER the player let go would
    /// otherwise gag a later, legitimate hold.</para></summary>
    internal void ClearHoldRefusal()
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.ClearHoldRefusal(); return; }
        ripRefusedLocally = false;
        stripRefusedLocally = false;
    }

    /// <summary>Tell <paramref name="byPlayer"/> that another sawyer already mans the end they stand at.</summary>
    void RefuseRipHold(IPlayer byPlayer) => SendHoldRefusal(byPlayer, RipRefusedPacketId);

    /// <summary>Tell <paramref name="byPlayer"/> that another debarker already mans the side they stand at.</summary>
    void RefuseStripHold(IPlayer byPlayer) => SendHoldRefusal(byPlayer, StripRefusedPacketId);

    /// <summary>Send one refusal packet to the refused player alone. Server-side; a no-op anywhere else, so the call
    /// sites need no side gate of their own (they already run under one).</summary>
    void SendHoldRefusal(IPlayer byPlayer, int packetId)
    {
        if (Api.Side == EnumAppSide.Server && byPlayer is IServerPlayer serverPlayer)
        {
            ((ICoreServerAPI)Api).Network.SendBlockEntityPacket(serverPlayer, Pos, packetId);
        }
    }

    /// <summary>Client end of the refusal (<see cref="SendHoldRefusal"/>): drop the optimistic hold this client had
    /// already started, and say why. Hard-cancels the work animation with no ease-out tail
    /// (<see cref="BlockSawhorse.CancelWorkAnim"/>) so its frame-locked clips stop with it and the arms read "nothing
    /// happened"; fades the strip's generic loop, which is this client's OWN handle and never the owner's; and raises
    /// the line in the ingameerror channel (a VTML channel — the message must carry no bare <c>&lt;</c>). The local
    /// sawdust/chips stop with the flag, on the very next step. Nothing the server owns is predicted before this
    /// lands: the client never advances progress, fires a stage or wears a tool, so the whole visible cost of the
    /// round trip is a few frames of animation and a pinch of dust.</summary>
    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid == RipRefusedPacketId || packetid == StripRefusedPacketId)
        {
            IPlayer? me = capi?.World.Player;
            if (me == null) return;

            bool rip = packetid == RipRefusedPacketId;
            if (rip) ripRefusedLocally = true;
            else { stripRefusedLocally = true; StopDebarkSound(); }

            BlockSawhorse.CancelWorkAnim(me);
            capi!.TriggerIngameError(this, rip ? "sawendbusy" : "debarksidebusy", Lang.Get(rip
                ? "immersivewoodworking:ingameerror-sawhorse-endbusy"
                : "immersivewoodworking:ingameerror-sawhorse-sidebusy"));
            return;
        }

        base.OnReceivedServerPacket(packetid, data);
    }

    // ───────────────────────── Observer effect broadcasts (AuditFixPlan T06.01, F-L4-13) ─────────────────────────
    //
    // Both work effects of the pair — the rip's sawdust and the strip's chips + generic loop — used to exist ONLY in
    // the working player's own process: they are spawned from BlockSawhorse.OnBlockInteractStep under a
    // `world.Side == Client` gate, and the engine calls the client half of that hook for the LOCAL player alone
    // (SystemMouseInWorldInteractions drives it off game.player + game.BlockSelection, with no loop over players).
    // A bystander therefore watched a saw cut air: the FK animation played, the frame-locked hit sounds carried, the
    // log visibly lost sections, and not one flake fell. Replication for particles exists only through the SERVER
    // call (ServerMain packs the properties into a packet; the client overload ignores dualCallByPlayer entirely), so
    // the server side of the same hook now emits a second, throttled copy addressed to everyone EXCEPT the worker,
    // whose own client already drew it at full frame rate.

    /// <summary>Wall-clock spacing between two observer effect broadcasts of one hold. The server does NOT get a
    /// tidy one Step per tick: it REPLAYS the frames each 0.15 s hold packet covered — the same burst
    /// <see cref="ImmersiveWoodworkingModSystem.HonestHoldSeconds(IWorldAccessor, IPlayer, float)"/> exists to undo —
    /// so Step arrives as a handful of calls microseconds apart, then a gap. A wall-clock gate collapses each burst
    /// to ONE broadcast by itself, which is the point: the value below is a floor, and the rate that actually lands
    /// is about one burst per packet. Ungated, a purely cosmetic effect would put tens of particle packets a second
    /// on the wire per worker.</summary>
    const long WorkFxNetIntervalMs = 100;

    /// <summary>Whether enough wall-clock has passed since <paramref name="lastMs"/> to send the next observer
    /// broadcast, stamping it when it has. <paramref name="minIntervalMs"/> defaults to the particle spacing; the
    /// strip loop passes its own, longer clip spacing. Server-side; a zero stamp fires immediately, so the first
    /// tick of a hold is never swallowed.</summary>
    bool FxBroadcastDue(ref long lastMs, long minIntervalMs = WorkFxNetIntervalMs)
    {
        long now = Api.World.ElapsedMilliseconds;
        if (lastMs != 0L && now - lastMs < minIntervalMs) return false;
        lastMs = now;
        return true;
    }

    // The CLIENT twin of the broadcast throttle above (AuditFixPlan T06.02 / F-L10-07). The worker's own copy of an
    // effect is drawn from OnBlockInteractStep, and that hook is not a clock: the engine drives it off a 20 ms tick
    // listener, which fires once per frame at best, so it lands at ~30 Hz on a 30 fps client, ~30 Hz on a 60 fps one
    // (16.7 ms frames only clear the 20 ms bar every second frame) and ~48 Hz at 144. Anything emitted "per step" is
    // therefore emitted at a rate the player's hardware picks — two sawyers on one log threw dust 1.6× apart. These
    // two members turn a per-step call into a per-SECOND rate, the way the powered sawmill's sawDustAccum already
    // did for the blade's descent.

    /// <summary>Longest slice of real time one work-effect frame may be paid for. A frame that took longer — a
    /// stall, a chunk load, a client that dropped to single-digit fps — is charged this much and no more, so the
    /// catch-up never arrives as one visible clump. At 10 fps the rate is still met exactly; below that the effect
    /// thins out, which is the right way for it to fail.</summary>
    const float WorkFxMaxFrameSeconds = 0.1f;

    /// <summary>How many WHOLE units of a per-second emission rate this frame is worth, carrying the sub-unit
    /// remainder in <paramref name="accum"/> so nothing is lost to rounding and the stream stays smooth.
    /// <paramref name="secondsUsed"/> is the hold clock, which on the client is real wall time since the press
    /// (<c>ElapsedMilliseconds - Controls.UsingBeginMS</c>; <see cref="ImmersiveWoodworkingModSystem.HonestHoldSeconds"/>
    /// passes it through untouched on this side), so its frame-to-frame delta IS the frame's duration.
    ///
    /// <para>A non-positive delta means the clock restarted — a fresh hold, or a re-grip — and the bank is DROPPED
    /// rather than carried: a returning player must not be met by the burst that accrued while they were away. That
    /// is the same rule the sawmill applies when the blade leaves its feed window. Costs the first frame of every
    /// hold, which at ~20 ms is not visible.</para></summary>
    int FxRateBudget(ref float accum, ref float lastSeconds, float secondsUsed, float perSecond)
    {
        float dt = secondsUsed - lastSeconds;
        lastSeconds = secondsUsed;
        if (dt <= 0f) { accum = 0f; return 0; }
        accum += GameMath.Min(dt, WorkFxMaxFrameSeconds) * perSecond;
        int whole = (int)accum;
        accum -= whole;
        return whole;
    }

    // ── Debark side-strip state (T01.06), the SIDE counterpart to the rip's saw fields above ──
    // barkProgress / barkStagesFired mirror sawProgress / sawStagesFired: a serialized scalar + a fire-once stage
    // counter, so a partial strip survives release/reload and the final stage can never re-fire. The thin slice (E01)
    // has ONE stage = the full strip (BarkStageFractions = { 1.0 }); E04 subdivides it for the progressive bark
    // visual. Reset with the log alongside the saw fields (ResetSawState → ResetDebarkState). barkSides is the
    // TRANSIENT co-op map (like sawEnds), NOT serialized: at most one debarker PER SIDE, keyed by the side sign
    // (PlayerSideSign, the sign of the across-the-log offset), so two opposite sides strip at 2× and a second debarker
    // crowding a manned side is refused. Reuses SawyerHold (a uid + last-elapsed record). Master-only (every debark
    // gesture routes through GetMaster); a slave's map stays empty.
    float barkProgress;
    int barkStagesFired;
    // Which END of the log the bark is peeled from — the debark counterpart to sawCutReversed. The dedicated debark
    // model hides its nine bands part_1..part_9 ALONG the length (+axis end → −axis end by default); `false` = that
    // default direction, `true` = peel from the OTHER end (bands + moving chip edge both reversed). Chosen from WHERE
    // THE PLAYER CLICKED along the log at the first strip-start (HitIsBarkReversedSide) and LOCKED for the log. Only
    // the VISUAL peel direction rides this flag — the finished debarked log always ejects AWAY from the debarker
    // (DebarkAwayEjectDir), independent of it. Serialized (survives reload mid-strip); ResetDebarkState leaves it
    // (auto-corrected by the next fresh strip-start), exactly like sawCutReversed.
    bool barkReversed;
    readonly Dictionary<int, SawyerHold> barkSides = new Dictionary<int, SawyerHold>();

    // Auto-restock breather (E02 + SawRestockDelaySeconds): set on the master when a held rip fully consumes a log
    // AND AutoRestock is on with a configured pause AND a sawyer still carries a matching log. While true the pair
    // sits EMPTY (heldLog == null) for SawRestockDelaySeconds before the next log is laid (FinishPendingRestock), so a
    // held rip reads rip → pause → place → rip (the saw's analogue of the chopping block's ChopHold.InDelay). restockPending
    // is the ONLY one SERIALIZED/synced — the client needs it to keep the held rip alive over the empty pair, since its
    // CanSawHere would otherwise see heldLog == null and end the interaction. The timer (server real-time ms) and the
    // finished-log memo (same-kind restock preference) are server-only/transient. Cleared FOUR ways: ResetSawState
    // (any put/take/discard), the last sawyer letting go mid-pause (AbandonPendingRestockIfUnmanned), the pair
    // dissolving under it (ReleaseHeldLog, AuditFixPlan T03.04) and — for the hold that ended without ever reaching Stop — the
    // freshness timeout below.
    bool restockPending;
    long restockReadyAtMs;       // Api.World.ElapsedMilliseconds at which to lay the next log (server-only)
    ItemStack? restockFinished;  // the just-finished log, for the same-kind/species restock preference (server-only)
    long restockTickedAtMs;      // freshness stamp: when an OWNER last ticked the breather (server-only) — see RestockOwnerTimeoutMs

    // How long a restock breather may go untouched before it counts as ABANDONED (AuditFixPlan T03.04, F-L5-04). The
    // pause has always been kept alive by the holder's own ticks and torn down by their release — but the engine owes
    // us no Stop/Cancel when a hold ends by DISCONNECT, so the last sawyer dropping mid-breather left a ghost end hold
    // (AbandonPendingRestockIfUnmanned then never sees Count 0) and a pause pinned open with its ready-time long past.
    // The next player to right-click that empty pair with a saw in hand fell straight into the still-pending branch and
    // had a log yanked out of THEIR pack. So ownership is held by a freshness STAMP rather than by an exit event —
    // exactly the recipe BlockEntityChoppingBlock.ChopHoldOwnerTimeoutMs uses for the same reason, and the same 2 s: a
    // live hold refreshes it every step, so only a hold that genuinely stopped ticking can age out. Checked lazily (on
    // the next tick that reaches the pause) because with nobody holding there is no tick to check it in — the same
    // self-healing idiom as PruneOfflineSawyers and the load-time heal in Initialize.
    const long RestockOwnerTimeoutMs = 2000;

    // Auto-restock breather for DEBARKING — the SIDE counterpart to the rip's restockPending trio above. Set on the
    // master when a held strip fully debarks a log (the bare debarkedlog drops out, like the rip's planks) AND
    // AutoRestock is on with a configured DebarkRestockDelaySeconds AND a debarker still carries a barked log. While
    // true the pair sits EMPTY (heldLog == null) for that delay before the next BARKED log is laid
    // (FinishPendingDebarkRestock), so a held strip reads strip → pause → place → strip. barkRestockPending is the ONLY
    // one SERIALIZED/synced — the client needs it to keep the held strip alive over the empty pair, since its
    // CanDebarkHere would otherwise see heldLog == null and end the interaction (same reason as restockPending). The
    // timer (server real-time ms) and the finished-log memo (same-species restock preference) are server-only/transient.
    // Cleared the same four ways as the rip's (ResetDebarkState, AbandonPendingDebarkRestockIfUnmanned, the pair
    // dissolving, and the shared freshness timeout).
    bool barkRestockPending;
    long barkRestockReadyAtMs;       // Api.World.ElapsedMilliseconds at which to lay the next barked log (server-only)
    ItemStack? barkRestockFinished;  // the just-finished BARKED log, for the same-species restock preference (server-only)
    long barkRestockTickedAtMs;      // freshness stamp, the SIDE mirror of restockTickedAtMs (server-only) — see RestockOwnerTimeoutMs

    // Lazily-resolved mod system gives the live, server-synced SawSeconds (mirrors BlockChoppingBlock's
    // ChopSeconds), so editing the config changes the rip duration.
    ImmersiveWoodworkingModSystem? modSystem;
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;
    float SawSeconds => Config.SawSeconds;

    // Client-only display mesh; see the class summary for the threading contract.
    ICoreClientAPI? capi;
    MeshData? stageMesh;
    // Master-only display mesh for the laid log (E05): the billet rendered on top of both trestles. Null on a
    // slave / empty pair (only the master holds heldLog). Same threading contract as stageMesh — (re)built on the
    // main thread (EnsureLogState, from UpdateMesh) and only READ in OnTesselation. It's a REFERENCE into logMeshes
    // (the per-state cache below), so a state swap (E06) is a cheap re-point, not a re-tesselation.
    MeshData? logMesh;
    // The per-state log meshes (T05.04), master-only. Index 0 = authored state 1 (whole log) … index 19 = state 20
    // (last sliver). Filled LAZILY: each state is tesselated the first time the cut reaches it (EnsureLogState), not
    // all 20 up front — building 20 heavy shapes at once on the main thread was the log-placement micro-freeze.
    // Since AuditFixPlan T05.01 this is NOT this BE's own array: it is a REFERENCE to the process-wide array shared by
    // every pair holding the same species+direction+axis (ResolveStateCaches / ObjectCacheUtil), so a second pair and a
    // chunk reload re-point instead of re-tesselating. Re-pointed when the held log changes (BuildLogMesh, tagged by
    // logMeshesFor); the array itself outlives the log, and outlives this BE, up to the client session. Null while no
    // log is held, and an individual entry stays null for a state whose shape failed to load. Mutated only on the main
    // thread (logMesh is what the tess thread reads).
    MeshData?[]? logMeshes;
    // What the logMeshes / debarkMeshes references were last resolved for — the held-log block code plus both hide
    // directions and the pair axis (LogCacheKey, e.g. "game:log-placed-oak-ud|sf|bf|az"), or null when no log is held.
    // UpdateMesh compares it to the current state to choose re-resolve-the-arrays (log / direction / axis changed) vs a
    // cheap state re-point (same log, new progress) — so a saw swap never re-tesselates all 20 shapes.
    string? logMeshesFor;
    // Which authored log state is currently shown, 1..LogStateCount (state 1 = whole log) while ripping, or
    // 1..DebarkStateCount (state 1 = full bark) while stripping bark. DERIVED (client-side) from sawProgress
    // (VisualModelFromProgress = clamp(floor(sawProgress/SawSeconds*20)+1, 1, 20), then decimated by SwapStride) for the
    // rip, or from barkProgress (BarkVisualFromProgress) for the strip, NOT serialized: at rest it's 1, the cut walks it
    // 1→20 as sawProgress climbs. SelectLogState points logMesh at logMeshes[visualModel-1] (rip) or
    // debarkMeshes[visualModel-1] (strip). (T06.07)
    int visualModel = 1;

    // Texture source for the CURRENTLY held log, retained so each authored state can be tesselated on demand
    // (EnsureLogState) the first time the cut reaches it — instead of building all 20 up front. Species-specific
    // (bound to heldLog's wood); rebuilt by BuildLogMesh when the held log changes, null while no log is held.
    LogTexSource? logTexSource;

    // What the current stageMesh (the trestle itself) was last built for. The stage mesh depends ONLY on the build
    // stage and the pair orientation, so RebuildStageMesh runs only when one of these changes — not on every sync.
    // Without this gate UpdateMesh re-tesselated the trestle shape on every MarkDirty, including each saw-progress
    // tick. (stageMesh == null also forces a (re)build, so these need no separate "not built yet" sentinel.)
    SawhorseStage stageMeshStage;
    bool stageMeshInPair;
    EnumAxis stageMeshAxis;

    // Sound played at each build step (place trestle / add stick / finish with rope). TEMPORARY placeholder:
    // reuses the chopping block's place clip (assets/immersivewoodworking/sounds/place.ogg). Mod-domain path —
    // it's our own asset (PlaySoundAt does NOT auto-prepend "sounds/", so the path includes it).
    static readonly AssetLocation BuildSound = new AssetLocation("immersivewoodworking", "sounds/place");

    public override void Initialize(ICoreAPI api)
    {
        base.Initialize(api);
        capi = api as ICoreClientAPI;
        if (capi != null)
        {
            UpdateMesh();                  // build the initial stage mesh; the first chunk tesselation picks it up
            lastHelpSig = HelpSignature(); // seed so the first post-join sync only refreshes hints on a real change
        }

        // Server, on load: heal a STALE auto-restock breather. restockPending / barkRestockPending are SERIALIZED, but
        // the breather can only ever advance while a hold is active (sawEnds / barkSides) AND its real-time timer is
        // live — and ALL THREE of those are transient (never saved). So a world saved mid-breather reloads a pending
        // flag with no hold and no timer to ever FinishPending… or AbandonPending…IfUnmanned it, leaving the pair
        // logically "pending" over an empty footprint until the next interaction. Both hold maps are necessarily empty
        // here at load, so a set flag is unambiguously stale → clear it (the load-time mirror of
        // AbandonPending(Debark)RestockIfUnmanned). Server-only; the client's copy carries no held strip on load and
        // self-heals on the next synced state.
        if (api.Side == EnumAppSide.Server && (restockPending || barkRestockPending)
            && sawEnds.Count == 0 && barkSides.Count == 0)
        {
            AbandonPendingRestock();
            AbandonPendingDebarkRestock();
            MarkDirty(true);
        }

        // Server, on load: once neighbouring chunks have had a moment to load, reconcile our pair state once —
        //  • already in a pair → validate the partner still exists and points back (heals a half-pair left by a
        //    partner broken while this chunk was unloaded);
        //  • single & Complete → try to pair with a neighbour that loaded alongside us (covers worlds built
        //    before pairing existed, and a teardown survivor re-pairing on reload).
        // Only Complete trestles can pair, so nothing schedules for a half-built one. A freshly BUILT trestle
        // initializes at Stage1 (so it doesn't schedule here); its pairing fires directly from TryBuildStep.
        if (api.Side == EnumAppSide.Server && stage == SawhorseStage.Complete)
        {
            RegisterDelayedCallback(_ => ResolvePairingOnLoad(), 100);
        }
    }

    /// <summary>
    /// Draws the current stage mesh and (master only) the laid-log mesh on top, returning true to SUPPRESS the
    /// default block (cube) mesh, so the trestle is represented entirely by the per-stage mesh (approach «б»).
    /// Returns false (drawing the default mesh) only in the brief window before the stage mesh is built, so the
    /// block is never invisible. Runs on the tesselation thread — only READS the atomically-swapped references.
    /// </summary>
    public override bool OnTesselation(ITerrainMeshPool mesher, ITesselatorAPI tessThreadTesselator)
    {
        // Master-only: the laid log, one elongated mesh spanning both cells on top of the pair (T05.03). A slave
        // or empty pair has logMesh == null and draws nothing extra; the master always carries it while a pair
        // holds a log (so it's only ever present on a Complete trestle).
        MeshData? log = logMesh;
        if (log != null) mesher.AddMeshData(log);

        MeshData? mesh = stageMesh;
        if (mesh == null) return base.OnTesselation(mesher, tessThreadTesselator); // block's own shape until built
        mesher.AddMeshData(mesh);
        return true; // we drew the whole block — skip the default mesh
    }

    /// <summary>
    /// Called right after <see cref="BehaviorSawhorseBuild"/> places this trestle from a plain right-click with
    /// a stick: spend the stick that built it and play the build sound. The stage is already
    /// <see cref="SawhorseStage.Stage1"/> (the freshly-created BE's default), so this only does the bookkeeping.
    /// Server-side (the behavior only calls it on the server).
    /// </summary>
    public void OnBuilt(IPlayer byPlayer, ItemSlot fromSlot)
    {
        if (Api.Side != EnumAppSide.Server) return;
        stage = SawhorseStage.Stage1; // explicit: the build always starts here, regardless of any future default
        ConsumeAndConfirm(byPlayer, fromSlot);
    }

    /// <summary>
    /// Called right after the trestle is placed FROM A CRAFTED ITEM STACK (the optional
    /// <see cref="ImmersiveWoodworkingConfig.CraftableSawhorse"/> hand-craft, routed here by
    /// <see cref="BlockSawhorse.OnBlockPlaced"/>): the trestle is delivered fully assembled, so jump straight to
    /// <see cref="SawhorseStage.Complete"/> — skipping the in-world stick→stick→rope stages — then persist + sync and
    /// look for an orthogonal partner to pair with, exactly as <see cref="TryBuildStep"/> does the moment it reaches
    /// Complete. The item already paid the build cost (2 sticks + 1 rope in the recipe), so nothing is consumed here.
    /// Server-side (the placement hook only calls it on the server). Idempotent enough to be harmless if the trestle
    /// is somehow already Complete (TryFormPair no-ops while already paired).
    /// </summary>
    public void OnPlacedAsCompleteFromItem()
    {
        if (Api.Side != EnumAppSide.Server) return;
        stage = SawhorseStage.Complete;
        MarkDirty(true);   // persist + sync → the client re-meshes to the finished trestle
        TryFormPair();     // pair with an orthogonal Complete neighbour if one is adjacent (no-op while single)
    }

    /// <summary>
    /// Plain right-click on an already-placed trestle: advance the build if the player's active item fits the
    /// current stage — a stick takes <see cref="SawhorseStage.Stage1"/> → <see cref="SawhorseStage.Stage2"/>,
    /// rope takes Stage2 → <see cref="SawhorseStage.Complete"/>. Any other gesture (stick on a finished trestle,
    /// rope on stage 1, an unrelated/empty hand) changes nothing and spends nothing. Server-authoritative: the
    /// client just reflects the synced stage, so held items never visibly "jump". Returns true if it advanced.
    /// </summary>
    public bool TryBuildStep(IPlayer byPlayer)
    {
        if (Api.Side != EnumAppSide.Server) return false; // authoritative on the server; syncs via MarkDirty

        ItemSlot slot = byPlayer.InventoryManager.ActiveHotbarSlot;
        if (slot?.Itemstack == null) return false;
        string part = slot.Itemstack.Collectible.FirstCodePart();

        if (stage == SawhorseStage.Stage1 && part == "stick") stage = SawhorseStage.Stage2;
        else if (stage == SawhorseStage.Stage2 && part == "rope") stage = SawhorseStage.Complete;
        else return false; // gesture doesn't fit this stage → no change, no consumption

        ConsumeAndConfirm(byPlayer, slot);

        // Just reached Complete → look for an orthogonal partner to pair with (T04.01). No-op while single.
        if (stage == SawhorseStage.Complete) TryFormPair();
        return true;
    }

    /// <summary>Spend one build item from <paramref name="fromSlot"/> (skipped in creative), play the build
    /// sound at this trestle, and persist + sync the new stage so the client re-meshes. Server-side.</summary>
    void ConsumeAndConfirm(IPlayer byPlayer, ItemSlot fromSlot)
    {
        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Creative)
        {
            fromSlot.TakeOut(1);
            fromSlot.MarkDirty();
        }

        // Build sound — temporary placeholder (place.ogg, the same clip the chopping block uses); a dedicated
        // construction sound can replace it later. Server-side here → broadcast once to everyone nearby.
        Api.World.PlaySoundAt(BuildSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);

        MarkDirty(true); // persist + sync → the client's FromTreeAttributes rebuilds the stage mesh
    }

    public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        base.FromTreeAttributes(tree, worldForResolving);
        stage = (SawhorseStage)tree.GetInt("stage", (int)SawhorseStage.Stage1);

        inPair = tree.GetBool("inPair", false);
        partnerPos = tree.GetBlockPos("partnerPos", null);
        isMaster = tree.GetBool("isMaster", false);
        pairAxis = (EnumAxis)tree.GetInt("pairAxis", (int)EnumAxis.Z);
        sawProgress = tree.GetFloat("sawProgress", 0f);
        sawStagesFired = tree.GetInt("sawStagesFired", 0);
        sawCutReversed = tree.GetBool("sawCutReversed", false); // directional-sawing side (client reads it for the visual hide order)
        sawMode = (SawMode)tree.GetInt("sawMode", (int)SawMode.Auto); // locked cut product (SawModePlan); client mirrors it for the hover readout
        barkProgress = tree.GetFloat("barkProgress", 0f);     // side-strip progress (T01.06), the debark counterpart to sawProgress
        barkStagesFired = tree.GetInt("barkStagesFired", 0);
        barkReversed = tree.GetBool("barkReversed", false);   // directional-debark peel end (client reads it for the visual band order + chip edge)
        restockPending = tree.GetBool("restockPending", false); // synced so the client keeps a held rip alive over the empty pause
        barkRestockPending = tree.GetBool("barkRestockPending", false); // debark counterpart — keeps a held strip alive over the empty pause
        heldLog = tree.GetItemstack("heldLog");
        heldLog?.ResolveBlockOrItem(worldForResolving); // tree itemstacks load unresolved (see GetItemstack docs)

        // Rebuild the mesh from the synced/loaded stage + pair axis (client only); mirrors BlockEntityChoppingBlock.
        if (Api?.Side == EnumAppSide.Client)
        {
            UpdateMesh();
            RefreshInteractionHelpOnChange(); // recompose stale interaction hints on a state change without re-hover (vs-interaction-help-no-poll)

            // The generic strip loop is a CLIENT, master-held ILoadedSound whose release routes through the
            // interacted cell (StopDebarkSound). If a pair is dissolved through the SLAVE cell while a strip runs, the
            // surviving master never gets that Stop and its clip orphans (plays out, handle dangles till chunk unload).
            // A SYNCED clear of the held log (dissolve / take / discard / scrap / consume) is the reliable client-side
            // signal to stop it. DisposeDebarkSound is LOCAL (non-routing) so it can only stop THIS cell's own sound —
            // a slave's heldLog is always null but its debarkSound is null too (the sound lives on the master), so this
            // is a safe no-op on a slave. NOT during the restock breather (heldLog null but barkRestockPending): there
            // the strip is still held and UpdateDebarkSound fades the loop itself.
            if (heldLog == null && !barkRestockPending) DisposeDebarkSound();
        }
    }

    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        base.ToTreeAttributes(tree);
        tree.SetInt("stage", (int)stage);

        tree.SetBool("inPair", inPair);
        if (partnerPos != null) tree.SetBlockPos("partnerPos", partnerPos);
        tree.SetBool("isMaster", isMaster);
        tree.SetInt("pairAxis", (int)pairAxis);
        tree.SetFloat("sawProgress", sawProgress);
        tree.SetInt("sawStagesFired", sawStagesFired);
        tree.SetBool("sawCutReversed", sawCutReversed); // directional-sawing side (synced so the client mirrors the visual + the info)
        tree.SetInt("sawMode", (int)sawMode); // locked cut product (SawModePlan), synced so the client's hover readout matches
        tree.SetFloat("barkProgress", barkProgress);
        tree.SetInt("barkStagesFired", barkStagesFired);
        tree.SetBool("barkReversed", barkReversed); // directional-debark peel end (synced so the client mirrors the visual + chips)
        tree.SetBool("restockPending", restockPending);
        tree.SetBool("barkRestockPending", barkRestockPending);
        if (heldLog != null) tree.SetItemstack("heldLog", heldLog);
    }

    // ───────────────────────── Collectible mappings (survive an id remap) ──────────────────────────────────────────
    //
    // The master trestle's held log (heldLog) is a raw stack carrying a world-specific numeric block/item id (a slave's
    // is always null). Without remapping it, an imported schematic or a world whose id table differs would resolve it
    // to the wrong log / null. Mirrors BEHelveHammer.HammerStack (the choppingblock / sawmill blade-kit idiom).
    // partnerPos and the pair flags are re-resolved lazily and carry no collectible id, so they need no remap.

    public override void OnStoreCollectibleMappings(Dictionary<int, AssetLocation> blockIdMapping, Dictionary<int, AssetLocation> itemIdMapping)
    {
        base.OnStoreCollectibleMappings(blockIdMapping, itemIdMapping);
        heldLog?.Collectible.OnStoreCollectibleMappings(Api.World, new DummySlot(heldLog), blockIdMapping, itemIdMapping);
    }

    public override void OnLoadCollectibleMappings(IWorldAccessor worldForResolve, Dictionary<int, AssetLocation> oldBlockIdMapping, Dictionary<int, AssetLocation> oldItemIdMapping, int schematicSeed, bool resolveImports)
    {
        base.OnLoadCollectibleMappings(worldForResolve, oldBlockIdMapping, oldItemIdMapping, schematicSeed, resolveImports);
        heldLog?.FixMapping(oldBlockIdMapping, oldItemIdMapping, worldForResolve);
    }

    /// <summary>
    /// Drop exactly the materials built into this trestle so far, by stage (Stage1 → 1 stick, Stage2 → 2 sticks,
    /// Complete → 2 sticks + 1 rope). There is no "sawhorse" item — the block's default self-drop is disabled in
    /// sawhorse.json (drops: []), so the only thing that ever falls out is its sticks/rope, spawned here.
    ///
    /// Called by the engine's break path for BOTH a player breaking it AND the Unstable behavior breaking it on
    /// lost support (BreakBlock(pos, null) → byPlayer null). Server-side; the <c>OnBlockBroken</c> counterpart to
    /// <c>BlockEntityChoppingBlock.OnBlockRemoved</c> — a sawhorse never burns (no combustibleProps), so OnBlockBroken
    /// always runs, whereas the flammable choppingblock uses OnBlockRemoved to spill on a burn too.
    ///
    /// E04 (T04.05): if this trestle is in a pair, dissolve it here. Because a lone trestle can't hold a log, the
    /// master releases its log by state (untouched → drops whole; begun → lost) and the surviving partner becomes
    /// a single Complete trestle again, free to re-pair. The pair teardown runs in ALL game modes (the multiblock
    /// must dissolve even on a creative break); only the trestle's own build materials are suppressed in creative.
    /// </summary>
    public override void OnBlockBroken(IPlayer? byPlayer = null)
    {
        base.OnBlockBroken(byPlayer);
        if (Api.Side != EnumAppSide.Server) return;

        // Dissolve the pair first, before the creative early-out below, so it happens regardless of game mode.
        // Each trestle releases its OWN log: if WE are the master we're being removed, so drop the log here at
        // the break site; the surviving partner releases its log (if it's the master) inside DissolveToSingle.
        // ReleaseHeldLog is idempotent + a no-op on a slave, so the two paths never double-drop.
        if (inPair)
        {
            BlockEntitySawhorse? partner = PartnerBE();
            if (isMaster) ReleaseHeldLog();  // we hold the log and are being removed → drop at the break site
            partner?.DissolveToSingle();     // the survivor goes single (releasing its own log) and may re-pair

            inPair = false;                  // we're being removed; clear our own link so any late read is clean
            partnerPos = null;
        }

        // Match vanilla block-break drop semantics (Block.SpawnDropsAndRemoveBlock): suppress the BUILD MATERIALS
        // when a CREATIVE player breaks it (they invested nothing), but DO drop when there's no player — e.g. the
        // Unstable behavior breaking it on lost support passes byPlayer == null.
        if (byPlayer != null && byPlayer.WorldData.CurrentGameMode == EnumGameMode.Creative) return;

        int sticks = stage == SawhorseStage.Stage1 ? 1 : 2; // Stage1 = 1 laid; Stage2 and Complete = 2 laid
        int ropes = stage == SawhorseStage.Complete ? 1 : 0; // rope only goes in at completion

        Vec3d dropPos = Pos.ToVec3d().Add(0.5, 0.5, 0.5);
        SpawnMaterial("stick", sticks, dropPos);
        SpawnMaterial("rope", ropes, dropPos);
    }

    /// <summary>Spawn <paramref name="count"/> of game:<paramref name="code"/> as <paramref name="count"/>
    /// SEPARATE single-item entities at <paramref name="pos"/> (so two sticks drop as two items, not one stack
    /// of two), no-op for count ≤ 0. Server-side; used by <see cref="OnBlockBroken"/>.</summary>
    void SpawnMaterial(string code, int count, Vec3d pos)
    {
        if (count <= 0) return;
        Item? item = Api.World.GetItem(new AssetLocation("game", code));
        if (item == null) return;
        for (int i = 0; i < count; i++)
        {
            Api.World.SpawnItemEntity(new ItemStack(item, 1), pos);
        }
    }

    public override void GetBlockInfo(IPlayer forPlayer, StringBuilder sb)
    {
        base.GetBlockInfo(forPlayer, sb);

        // Player-facing (T07.03): what's laid on the pair and how far the rip has progressed. Read from the MASTER
        // so either cell of a pair shows the SAME line (T04.03 — "interact with either part, same effect", here at
        // the info level). A single / half-built trestle and an empty pair hold no log, so this adds nothing — they
        // never show stray info (the DoD "пустая/недостроенная не показывает мусор"). Runs client-side on hover; the
        // master's heldLog / sawProgress are server-synced, so the slave proxy (GetMaster) is a pure read here.
        BlockEntitySawhorse master = GetMaster();
        if (master.heldLog != null)
        {
            sb.AppendLine(master.heldLog.GetName()); // already-localized log name, e.g. "Birch log"

            // Sawing progress as a percentage of a full rip (sawProgress / SawSeconds). Shown only once the cut has
            // actually begun, so an untouched log reads as just its name (no "0%" noise). SawSeconds is live config;
            // guard against a degenerate 0 so we never divide by it.
            float sawSeconds = SawSeconds;
            if (master.sawProgress > 0f && sawSeconds > 0f)
            {
                int percent = GameMath.Clamp((int)(master.sawProgress / sawSeconds * 100f), 0, 100);
                sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawhorse-progress", percent));
                // The product this rip is LOCKED to (SawModePlan) — shown next to progress so a mid-rip log reveals
                // whether it's yielding planks or beams. Only while ripping (this block), so an untouched log stays quiet
                // (the pit saw's F menu is where the choice is made before the cut).
                sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawhorse-cutmode", master.sawMode.LangName()));
            }

            // Debarking progress as a percentage of a full strip (barkProgress / DebarkSeconds) — the side-action
            // counterpart of the rip line above. Shown only once stripping has actually begun (an untouched log reads
            // as just its name) AND while the strip is genuinely unfinished: the barkStagesFired < BarkStageFractions
            // .Length clause mirrors ShowDebarkModel's guard (BlockEntitySawhorse.Render.cs) so a STUCK log reads the
            // same on both surfaces. A modded wood with no debarkedlog variant leaves CompleteDebark unable to
            // transform (T01.07): it warns and returns WITHOUT ResetDebarkState, pinning barkProgress at DebarkSeconds
            // and barkStagesFired at max. The render then keeps showing the still-barked model; without this clause the
            // tooltip would contradict it with a permanent, misleading "100%". With it, a stuck log reads as just its
            // (still-barked) name, exactly as the render shows. The mode lock (T05.03) makes sawing and debarking
            // mutually exclusive on one log, so at most ONE of these two lines is ever non-zero. No extra sync is
            // needed: barkProgress is serialized (To/FromTreeAttributes) and AdvanceDebark MarkDirty's on every
            // visual-bucket change exactly like sawProgress, so this client-side hover read sees it climb. DebarkSeconds
            // is live config; guard against a degenerate 0 so we never divide by it.
            float barkSeconds = DebarkSeconds;
            if (master.barkProgress > 0f && barkSeconds > 0f && master.barkStagesFired < BarkStageFractions.Length)
            {
                int percent = GameMath.Clamp((int)(master.barkProgress / barkSeconds * 100f), 0, 100);
                sb.AppendLine(Lang.Get("immersivewoodworking:blockinfo-sawhorse-debark-progress", percent));
            }
        }

#if IW_DEBUG
        // Dev aid, gated to the -iwdebug build (tools/apply-*.ps1 -p:IwDebug=true) so it never clutters the shipped
        // tooltip: stage, pairing and the fitted collision box stay verifiable in-world. This WAS the default
        // GetBlockInfo body before T07.03 moved the player-facing info above to the release behaviour. The collision
        // line is a live probe — look at the pair with and without a log, Y2 should jump ~1.0 → ~1.29 as the box
        // grows; if it doesn't, the build is stale or the log isn't being seen (see vs-multiblock-collision memory).
        sb.AppendLine("Stage: " + stage);
        if (inPair)
        {
            sb.AppendLine("Pair: " + (isMaster ? "master" : "slave→" + partnerPos) + ", axis " + pairAxis);

            // T01.02 debark-gate geometry, live for the hovering player. Read on the MASTER (its pairAxis/Pos), so
            // either cell reports the same along/perp. Walk around a (laid or empty) pair to verify the thresholds:
            // `side` is true ONLY at a long face, `end` ONLY past a trestle, and BOTH are false on the corner
            // diagonals (the dead zone) — the two gates never overlap (see BlockEntitySawhorse.Debarking.cs).
            (double dAlong, double dPerp) = master.PlayerAxisOffset(forPlayer);
            sb.AppendLine($"Geom: along={dAlong:0.00} perp={dPerp:0.00} end={master.IsPlayerAtAxisEnd(forPlayer)} side={master.IsPlayerAtAxisSide(forPlayer)}");

            // T01.03 debark tool-tier gate, live for the hovering player's hands (main = active hotbar, off = left
            // hand — the T01.01 spike). `eff` is true only with a valid tier (axe + offhand hammer, or a knife while
            // AllowKnifeDebark); `mult` is the matched tier's speed factor (spud 1.0 ref / axe+hammer 0.3 / knife 0.15 default —
            // only meaningful when eff); `can` folds in position + heldLog. Swap tools and hover to verify the tiers
            // before the interaction path exists (T01.06).
            ItemSlot? dbgMain = forPlayer.InventoryManager?.ActiveHotbarSlot;
            ItemStack? dbgOff = forPlayer.Entity?.LeftHandItemSlot?.Itemstack;
            sb.AppendLine($"Bark: eff={master.BarkToolIsEffective(dbgMain?.Itemstack, dbgOff)} mult={master.BarkSpeedFor(dbgMain?.Itemstack, dbgOff):0.00} can={master.CanDebarkHere(forPlayer, dbgMain)}");

            if (master.heldLog != null)
            {
                sb.AppendLine("Saw " + master.sawProgress.ToString("0.0") + "s, stages " + master.sawStagesFired);
                // T01.06 strip cycle: barkProgress climbs to DebarkSeconds while holding RMB at a side with a debark
                // tool; survives release (serialized). barkSides.Count is the live co-op multiplier (sides manned).
                sb.AppendLine("Bark " + master.barkProgress.ToString("0.0") + "s, stages " + master.barkStagesFired + ", sides " + master.barkSides.Count);
            }
            Cuboidf[]? cb = GetPairCollisionBoxes();
            if (cb is { Length: > 0 })
            {
                Cuboidf b = cb[0];
                sb.AppendLine($"Coll: [{b.X1:0.00},{b.Y1:0.00},{b.Z1:0.00}]..[{b.X2:0.00},{b.Y2:0.00},{b.Z2:0.00}]");
            }
            // T05.05 live probe: how many times the box snapshot has been rebuilt on THIS cell. Aim at a pair while
            // sawing (the mod's own sawdust queries these boxes ~30 Hz per flake) and the number must sit still,
            // stepping only when a log goes on or comes off, or when the pair forms or dissolves.
            sb.AppendLine($"Boxes: {boxRebuilds} rebuild(s), state {(pairBoxes?.State ?? -1)}");
        }
#endif
    }

}
