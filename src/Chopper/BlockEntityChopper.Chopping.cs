using System;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// E06 — the chop cycle: the server tick that STEPS <c>chopProgress</c> one discrete notch per axe strike (T06.01,
/// bugfix 2026-07-05, freeze-not-reset), the pass completion that computes the firewood/stick batch and consumes the log
/// (T06.02), the forward eject (T06.03), the start gates (T06.04, <c>CanChop</c>) and the head wear (T06.05). Analogue of
/// <see cref="BlockEntitySawmill"/>'s Sawing partial, minus the carriage/feed and the output-full pause — the chopper has
/// ONE moving part and ejects forward, so completion can never be blocked. Unlike the sawmill it does NOT feed
/// continuously: progress JUMPS at the moment the maul lands, it never creeps up between strikes.
///
/// State now (through <b>T06.05</b>): the heartbeat (<see cref="OnChopperTick"/> + its server strike-phase tracker,
/// T06.01), the output seam (<see cref="GetChopBatch"/>) + pass completion (<see cref="CompletePass"/>, T06.02), the
/// forward eject (<see cref="EjectBatch"/>, T06.03), the single start predicate (<see cref="CanChop"/>, T06.04) and the
/// head wear (<see cref="DamageHead"/>, T06.05) are all in — the cycle is fully live server-side. The cosmetic strike
/// chips are NO longer a seam here: they moved off the server to a CLIENT per-strike burst driven off the render's
/// bottom-of-stroke (T07.04, <c>BlockEntityChopper.Effects.UpdateCutEffects</c>), so <see cref="EjectBatch"/> no longer
/// calls a chip stub. T08.04 is the cycle's first in-game smoke.
/// </summary>
public partial class BlockEntityChopper
{
    /// <summary>
    /// E06/T06.04 — the single START-GATE predicate for the chop cycle: the machine may strike RIGHT NOW iff it is fully
    /// assembled (<see cref="IsComplete"/>), a choppable log is loaded (<see cref="IsValidInput"/>, the alloc-free accept
    /// predicate reused from the input filter — the chop product <c>game:firewood</c>/<c>stick</c> is core vanilla and
    /// always resolves, so no separate product-resolve conjunct is needed, E05/T05.03) and the drive turns above the
    /// torque floor with a head fitted
    /// (<see cref="EffectiveChopSpeed"/> &gt; 0, E04). Lifted OUT of the tick's former inline gate (staged in T06.01) into
    /// ONE predicate so the server heartbeat (<see cref="OnChopperTick"/>), the client anim pace and the chip/sound effects
    /// (E07) all gate on the IDENTICAL condition and can never drift — the direct analogue of
    /// <see cref="BlockEntitySawmill.CanSaw"/>. Side-agnostic: every term is a pure read of server-synced state (the network
    /// angle is interpolated on the client). There is NO output-gate conjunct — the chopper ejects forward, so a completed
    /// pass can never be blocked (exactly like the sawmill's <see cref="BlockEntitySawmill.CanSaw"/>).
    /// </summary>
    public bool CanChop()
        // Accept-consistent gate via IsValidInput, NOT GetChopBatch() != null (F-S05-17): the chopper accepts input on
        // IsChoppable ALONE (IsValidInput, BlockEntityChopper.IO.cs — deliberately simpler than the sawmill, since the
        // chop product game:firewood/stick always resolves), so gating the cut on the SAME predicate the log was accepted
        // through keeps the two from diverging AND drops the throwaway product stack GetChopBatch built only to test for
        // null. Every accepted log resolves a batch, so this is behaviour-identical for every reachable input; CompletePass
        // still handles a null batch defensively.
        => IsComplete && IsValidInput(InputSlot.Itemstack) && EffectiveChopSpeed() > 0f;

    /// <summary>The fraction of the axe's lift travel (0 = down on the bed, 1 = fully raised) the head must have
    /// reached before a new log may be loaded UNDER it (<see cref="HeadClearOfBed"/>). At 0.5 the head must be past the
    /// halfway point of its rise — clearly off the bed — so an automated refill can't drop a log around a still-
    /// descended axe. An internal clearance constant grounded in the model geometry (NOT a balance knob): raise toward
    /// 1 for more clearance / a longer feed pause between logs, lower for a tighter one.</summary>
    const float HeadClearLiftThreshold = 0.5f;

    /// <summary>
    /// The ANIMATION-tied input gate: whether the axe head is currently clear of the bed, so a new log may be loaded
    /// WITHOUT the head being buried inside it. The bug it closes (user 2026-07-13): an automated chopper empties its
    /// input at the strike (<see cref="CompletePass"/>, at the BOTTOM of the stroke) and — with no gate — instantly
    /// refills, dropping the fresh log AROUND the still-descended head, which then crawls up out of it. Tied to the SAME
    /// rectified-forward drive angle the axe visibly plays (<see cref="serverAnimAngle"/>, the server twin of the
    /// render's <c>animAngle</c>) via <see cref="ChopperAnim.ArmLift"/>, NOT a wall-clock timer — so a slow network
    /// waits longer for the head to rise, exactly as it looks.
    ///
    /// Two allowances keep it from stranding a log:
    /// <list type="bullet">
    ///   <item>NOT turning → allow. An idle axe RESTS at the bottom (lift 0, its authored rest pose) — that is the
    ///   normal "load me" state, and a stopped/unpowered machine must stay loadable (else a power outage with the head
    ///   frozen low would lock the player out). The head only descends AS PART of a running strike — the exact case the
    ///   bug is about — so gating only while turning fixes it without the stuck-machine trap.</item>
    ///   <item>turning → the head must have lifted to at least <see cref="HeadClearLiftThreshold"/> of its travel.</item>
    /// </list>
    /// Side-aware, reading the accumulator that is LIVE on each side so both agree with the axe you see: the SERVER uses
    /// <see cref="serverAnimAngle"/> (advanced in <see cref="OnChopperTick"/>) — the authoritative gate for the actual
    /// input move — and the CLIENT uses the render's <c>animAngle</c> (advanced in <c>AdvanceAnimAngle</c>), which drives
    /// the visible axe, so the block-info hint (<see cref="GetBlockInfo"/>, client-only) matches the picture. The
    /// enforcement is still server-authoritative: the manual put's client branch swallows the click regardless (its
    /// return is ignored by <see cref="OnInteract"/>) and the auto-push moves items server-side, so a client/server
    /// disagreement at the interpolation edge only affects the passive hint, never the move. Consulted by BOTH input
    /// paths — the manual RMB put (<see cref="TryPutLog"/>) and the hopper auto-push (<see cref="GetAutoPushIntoSlot"/>) —
    /// so the hand and machine feed gate identically (the "hand and hopper paths can't drift" invariant).
    /// </summary>
    public bool HeadClearOfBed()
    {
        if (!IsMechanismTurning()) return true; // idle axe rests low, but that IS the load pose — allow
        // The rectified-forward accumulator that is actually advancing on THIS side (server twin vs render), GEARED by
        // ChopperWorkScale (SpeedDecouplingPlan T03.03) so ArmLift reads the same arm phase the render draws. BOTH sides
        // gear identically, so the gate and the visible axe never disagree by more than the network interpolation. Gearing
        // only ONE side would silently desync this gate exactly when the bed is empty (the case it is consulted for). At
        // speed=1 workScale=1.0 → geared ≡ raw, bit-identical.
        float theta = (Api.Side == EnumAppSide.Server ? serverAnimAngle : animAngle) * Config.ChopperWorkScale;
        return ChopperAnim.ArmLift(theta) >= HeadClearLiftThreshold;
    }

    // Server-side stroke-phase tracker for DISCRETE strike detection. serverAnimAngle is now SERIALIZED + SYNCED
    // (BlockEntityChopper To/FromTreeAttributes) — the server resumes its strike phase across a reload AND the client
    // phase-aligns its render animAngle to it (BlockEntityChopper.Render.AnchorAnimToServer) so the visible axe-fall
    // coincides with the authoritative split/eject; the other three fields below stay transient/server-only. E12/T12.03b:
    // with the ÷48 reducer the arm completes one lift-drop per ChopperAnim.ReducerRevs (48) revolutions of the geared clock,
    // so the strike can NO LONGER be read off the raw AngleRad (which wraps every revolution — a Cycles=1/48 phase read off
    // it never leaves the bottom of the stroke). Instead we integrate the rectified shaft step into serverAnimAngle —
    // the RECTIFIED forward accumulator (+|Δ|), the server twin of the client's render animAngle (BlockEntityChopper.Render),
    // bounded at the M-dependent ChopperCycleRevs·2π (SpeedDecouplingPlan T03.03) — and read the arm phase (prevServerStrikePh)
    // off the GEARED read serverAnimAngle·ChopperWorkScale. serverAnimAnglePrev is the previous tick's raw AngleRad sample
    // (for the wrapped delta). That delta is exact only below π/tick; above it (fast gearing) the step comes from the
    // continuous NetworkSpeed instead, so the accumulator never stalls at high RPM (T03.04, folding ModDbBugfixPlan
    // E02/T02.01). Rectifying (|Δ|) makes the split ALWAYS play forward (down) whichever way the network
    // drives the shaft — the reversed-propagation node is handled by construction now, AND it matches the client's rectified
    // animAngle so the visible axe-fall and the authoritative split coincide on every node (the old signed tracker left the
    // reverse-playing client without chips/crack). serverStrikeSeeded gates the first sample so a fresh (re)start banks no
    // phantom strike; on an idle gap it re-seeds (serverStrikeSeeded = false) — serverAnimAngle is PRESERVED (freeze), only
    // serverAnimAnglePrev is re-seeded to the current angle so the idle rotation isn't banked (freeze-not-reset, memory
    // vs-saw-hold-progress). serverAnimAngle is serialized (see above); the other three are transient server-only fields.
    float prevServerStrikePh;
    float serverAnimAngle;
    float serverAnimAnglePrev;
    bool serverStrikeSeeded;

    // Throttle for the periodic serverAnimAngle sync (see OnChopperTick): wall-clock ms of the last push, and the cadence.
    // ~1 s keeps the client's render anchor fresh (offset closes within a couple seconds of turning) at negligible traffic.
    long lastAnimSyncMs;
    const long AnimSyncIntervalMs = 1000;

    /// <summary>Radians the drive angle advances per unit of network speed per real second. The engine integrates the
    /// network angle in <c>MechanicalNetwork.UpdateAngle</c> as <c>angle += speed·dt·50/10</c> = <c>speed·dt·5</c>, and
    /// this drive's angle is that network angle × <c>GearedRatio</c> — and <see cref="NetworkSpeed"/> (=|Speed·GearedRatio|)
    /// already carries the ratio — so the drive turns at <c>NetworkSpeed·5</c> rad/s and its true (unwrapped) step over a
    /// tick is <c>NetworkSpeed·5·dt</c> rad. Used in <see cref="OnChopperTick"/> to recover that step from the CONTINUOUS
    /// speed when the wrapped-angle difference would alias past the Nyquist limit (ModDbBugfixPlan E02/T02.01, folded into
    /// SpeedDecouplingPlan T03.04). Verified against the shipped engine (VSSurvivalMod 1.22.2) 2026-09-16.</summary>
    const float ShaftRadPerNetworkSpeedSecond = 5f;

    // ───────────────────────── Render-anchor packet (AuditFixPlan T07.05, F-L3-08) ─────────────────────────
    //
    // The periodic push above used to ride MarkDirty(). Its PAYLOAD is one float that only the render reads
    // (serverAnimAngle → lastSyncedServerAnim → AnchorAnimToServer, BlockEntityChopper.Render.cs), but MarkDirty is a
    // "this block entity's state changed" announcement and the engine answers it broadly, on the SERVER, for every
    // caller: ServerWorldMap.MarkBlockEntityDirty enqueues the position AND calls ServerChunk.MarkModified(), which
    // sets DirtyForSaving = true and refreshes lastReadOrWrite. Three consequences, all paid once a second forever by
    // an assembled powered chopper — the gate at the top of OnChopperTick is "the mechanism turns", which for a
    // machine on a windmill is the normal idle state:
    //   • every autosave (MagicNum.ServerAutoSave, 300 s) rewrites the chunk blob although nothing changed;
    //   • ServerSystemCompressChunks.FindFreeableMemory only takes a chunk whose lastReadOrWrite is older than
    //     MagicNum.UncompressedChunkTTL (15000 ms), so a one-second cadence pins an unpacked chunk in server RAM
    //     for as long as the machine turns;
    //   • ServerSystemBlockSimulation.SendDirtyBlockEntities packs the WHOLE tree (part flags, bedWood, the
    //     serialized headStack, progress, the inventory subtree — ~280 bytes) and sends it to every client holding
    //     the chunk, to move four bytes.
    //
    // So the fix is the CHANNEL, not the cadence or the gate: the anchor goes out as its own block-entity packet and
    // MarkDirty stays for what it means. The gate and the ~1 s spacing are UNCHANGED on purpose — gating the anchor
    // on a loaded log (the reading the finding's title invites) is the one thing that must not happen here, because
    // HeadClearOfBed compares serverAnimAngle on the server against the render's animAngle on the client and is
    // consulted EXACTLY when the bed is empty: let the two drift and the player sees a raised axe, clicks, and the
    // server refuses the log — silently, since TryPutLog raises its red line off the CLIENT's own phase (.IO), which in
    // that state reads clear. The refusal covers ~58% of a 48-revolution cycle: the arm driver rises linearly to 0.82
    // and drops as 1−t², so ArmLift ≥ HeadClearLiftThreshold holds on phase [0.41, 0.834], about 42% of the cycle.
    //
    // On the GATE the audit agrees explicitly ("НЕ просто перенести синк за CanChop()", AuditPlan2/lens/L3-api.md
    // under F-L3-08). On the CADENCE it suggested the opposite of what was done — lower the rate while CanChop is false,
    // or push on a phase-divergence threshold — and that was deliberately not taken: a slower idle cadence only reduces
    // the chunk cost and leaves the WORKING machine (the hopper-fed line, i.e. the normal case) dirtying its chunk at
    // 1 Hz, while moving the channel removes it in both states. Vanilla splits these channels the same way —
    // BEFruitpress pushes its animation state with BroadcastBlockEntityPacket, not MarkDirty — and does it to ALL
    // connected players, so the audience below is tighter than that precedent, not looser.
    //
    // Real state changes still ride MarkDirty untouched: the strike's chopProgress step (OnChopperTick below),
    // CompletePass, the input moves and the part installs.

    /// <summary>Block-entity packet: "the drive phase is here" — server to the nearby clients, once per
    /// <see cref="AnimSyncIntervalMs"/> while the mechanism turns (<see cref="SendAnimAnchor"/>), handled in
    /// <see cref="OnReceivedServerPacket"/>. A private id in the mod's 22xx block (the chopping block holds 2201, the
    /// sawhorse 2211/2212): this BE has no dialog and so no <c>EnumBlockEntityPacketId</c> traffic to collide with.</summary>
    const int AnimAnchorPacketId = 2221;

    /// <summary>How far the anchor packet carries. The anchor keeps the visible axe-fall on the server's split, and
    /// keeps <see cref="HeadClearOfBed"/> agreeing across the sides; both only mean anything to a player who can make
    /// the machine out (its loop sound reaches 24 blocks, its chips 16, the load gesture is arm's length), so 64
    /// blocks is generous headroom rather than a limit anyone can feel. A client further out keeps free-running its
    /// own accumulator off the same synced network angle and re-converges within about a second of turning once it
    /// comes back in range; on chunk load it starts hard-aligned anyway, since <c>serverAnimAngle</c> still travels in
    /// the tree (<c>ToTreeAttributes</c>) and <c>Initialize</c> anchors from it. Note the audience is the only thing
    /// this trims: <c>GetPlayersAround</c> walks the client list whatever the radius.</summary>
    const float AnimAnchorRange = 64f;

    /// <summary>Push <see cref="serverAnimAngle"/> to the clients near enough to care. Server-side; the caller is the
    /// throttle in <see cref="OnChopperTick"/>, so this fires about once a second while the mechanism turns.
    ///
    /// <para>Four raw bytes rather than a protobuf contract: the payload is one float and
    /// <c>SerializerUtil</c> would need a wrapper type to carry it, while a bare
    /// <see cref="BitConverter"/> round trip is exact for every value including zero — which matters, since protobuf
    /// omits default-valued fields and a phase of exactly 0 is reachable (memory
    /// <c>vs-protobuf-implicit-zero-defaults</c>). Serialized ONCE and handed to every recipient.</para></summary>
    void SendAnimAnchor()
    {
        if (Api is not ICoreServerAPI sapi) return;

        IPlayer[] nearby = sapi.World.GetPlayersAround(anchorAudience, AnimAnchorRange, AnimAnchorRange);
        if (nearby.Length == 0) return; // nobody to anchor — skip the serialize too

        byte[] data = BitConverter.GetBytes(serverAnimAngle);
        foreach (IPlayer player in nearby)
        {
            if (player is IServerPlayer serverPlayer) sapi.Network.SendBlockEntityPacket(serverPlayer, Pos, AnimAnchorPacketId, data);
        }
    }

    /// <summary>Centre of the anchor's audience test, built once because <see cref="BlockPos"/> is fixed for this BE's
    /// lifetime. It saves the <see cref="Vec3d"/>, not every allocation: the push still costs
    /// <c>GetPlayersAround</c>'s list + array, the four-byte payload and one packet object per recipient. All of it
    /// once a second, against the ~280-byte tree resend it replaced.
    ///
    /// <para><c>InternalY</c>, not <c>Y</c>: the vertical half of <c>GetPlayersAround</c> compares against the client's
    /// own internal Y, which carries the dimension. Plain <c>Y</c> is dimension-local, so on a machine inside a
    /// mini-dimension the two are offset by whole dimension heights and the audience would silently come back empty.
    /// The packet send below already addresses the position by <c>InternalY</c>, so this keeps the pair
    /// consistent.</para></summary>
    Vec3d? anchorAudienceCache;
    Vec3d anchorAudience => anchorAudienceCache ??= new Vec3d(Pos.X + 0.5, Pos.InternalY + 0.5, Pos.Z + 0.5);

    /// <summary>Client end of <see cref="SendAnimAnchor"/>: take the server's drive phase and re-base the render's
    /// estimate of it, exactly as a tree sync would (<c>FromTreeAttributes</c> does these same two lines). The field
    /// <c>serverAnimAngle</c> itself is deliberately NOT written here — it is dead on the client, where
    /// <see cref="HeadClearOfBed"/> reads the render's own <c>animAngle</c>.</summary>
    public override void OnReceivedServerPacket(int packetid, byte[] data)
    {
        if (packetid == AnimAnchorPacketId)
        {
            if (capi == null || data == null || data.Length < sizeof(float)) return;
            lastSyncedServerAnim = BitConverter.ToSingle(data, 0);
#if IW_DEBUG
            // [chopper-sync] the anchor's own trace. The identical line in FromTreeAttributes used to print at this
            // cadence (the 1 Hz push was a tree resend) and is what the ~25.9 rad startup offset was measured with;
            // now that the anchor rides its own packet, the tree line only fires at real state boundaries, so the
            // per-second offset trace lives here. Same grep tag, same fields, so the two interleave as one series.
            capi.Logger.Notification("[immersivewoodworking] [chopper-sync] CLIENT anchor @{0}ms serverAnimAngle={1:0.000} animAngle={2:0.000} offset={3:0.000} seeded={4}",
                capi.World.ElapsedMilliseconds, lastSyncedServerAnim, animAngle, lastSyncedServerAnim - animAngle, animAngleSeeded);
#endif
            AnchorAnimToServer();
            return;
        }

        base.OnReceivedServerPacket(packetid, data);
    }

    /// <summary>
    /// E06/T06.01 (bugfix 2026-07-05 — STEP-AT-STRIKE) — the chop-cycle heartbeat (registered server-side in
    /// <see cref="Initialize"/> at 25 ms, like <c>BEHelveHammer.onEvery25ms</c>). Server-only: the client renders the
    /// synced <c>chopProgress</c> (E07) but never integrates it.
    ///
    /// Unlike the sawmill (and this method's own former T06.01 form), <c>chopProgress</c> does NOT creep up continuously
    /// with the cam rotation. The chopper is a STRIKE, not a feed: progress jumps ONE discrete notch
    /// (<c>ChopperSplitPerStrike</c> = 1/N) at the instant the axe beam bottoms out on the log, then holds flat the rest of the
    /// revolution while the beam rises again. The strike is the drive crossing the beam's bottom-of-stroke cycle phase
    /// (<see cref="ChopperAnim.ArmRiseFrac"/> + <see cref="ChopperAnim.ArmDropFrac"/>) in EITHER rotation sense —
    /// direction-agnostic like the old <c>|Δ|</c> accrual (and the sawmill's <c>FeedAdvance</c>), which is CRITICAL because
    /// the engine flips a reversed-propagation node's angle (<c>BEBehaviorMPBase.AngleRad = 2π − …</c> when
    /// <c>IsRotationReversed()</c>, i.e. <see cref="BlockFacing.DOWN"/>/<see cref="BlockFacing.EAST"/>/<see cref="BlockFacing.SOUTH"/>
    /// propagation), so on those orientations <see cref="AngleRad"/> RUNS BACKWARD for a normally-spinning network — a
    /// forward-only test would leave such a chopper turning forever and splitting nothing. On a forward node this is the
    /// SAME phase the client fires the chips + wood-crack on (<see cref="UpdateCutEffects"/>/<c>OnStrikeBottom</c>), so the
    /// visible axe-fall and the authoritative split land together. A log takes <c>ChopperStrikes</c> = ⌈1/<c>ChopperSpeed</c>⌉ strikes to fill
    /// to 100%. The default is <b>1</b> (at <c>ChopperSpeed</c> = 1) — the "one slow blow":
    /// with the ÷48 reducer the arm completes one lift-and-drop per <see cref="ChopperAnim.ReducerRevs"/> shaft
    /// revolutions, so one strike per log IS the slow pace, not the fast one. (The interim E12/T12.03 default of 50 read
    /// the strike straight off the shaft, one drop per revolution, and needed the count to hold the throughput down;
    /// the modeled reduction gear replaced it.)
    ///
    /// FREEZES — without resetting — while the machine may not chop, so a partially-struck log (only reachable with
    /// <c>ChopperStrikes &gt; 1</c>) resumes from the same fraction once the drive/log gates clear. Re-seeds the phase
    /// tracker on the idle edge so the angle turned during the gap banks no phantom strike (freeze-not-reset, memory
    /// vs-saw-hold-progress). A trimmed cousin of <see cref="BlockEntitySawmill.OnSawmillTick"/> — the sawmill feeds
    /// continuously; the chopper strikes.
    /// </summary>
    void OnChopperTick(float dt)
    {
        if (Api.Side != EnumAppSide.Server) return;

        // Gate the ANGLE ACCUMULATION on the machine TURNING (assembled, head fitted, above torque) — the EXACT predicate
        // the client render advances its animAngle on (BlockEntityChopper.Render.AdvanceAnimAngle), NOT on a loaded log.
        // E12/T12.03b: with the reducer the arm phase is integrated over 48 revs, so it MUST advance in lockstep with the
        // client whether or not a log is loaded — else a machine that idles over an empty bed and then gets a log would
        // have its server phase behind the client's and the split would miss the visible drop. The WORK (chopProgress
        // accrual) additionally gates on a loaded, resolvable log (CanChop) at the strike below. Not turning → re-seed the
        // tracker and return WITHOUT touching chopProgress (freeze-not-reset, memory vs-saw-hold-progress); serverAnimAngle
        // is preserved so a resume continues its slow cam cycle. No output-full pause (the batch ejects forward).
        if (!(IsComplete && EffectiveChopSpeed() > 0f)) { serverStrikeSeeded = false; return; }

        // E12/T12.03b + SpeedDecouplingPlan T03.03 — integrate the drive angle over the ÷48 reducer so the arm phase
        // completes ONCE per ChopperAnim.ReducerRevs (48) revolutions of the geared clock. The raw AngleRad wraps every
        // revolution, so a Cycles=1/48 phase read straight off it never leaves the bottom of the stroke (it only ever spans
        // 1/48 of a cycle) — the strike would never fire. Instead accumulate the rectified shaft step (+|Δ|, computed
        // below — the wrapped difference under the Nyquist limit, the continuous-speed magnitude above it) into
        // serverAnimAngle (RECTIFIED forward, the server twin of the client render's animAngle), bounded at the
        // M-dependent ChopperCycleRevs·2π (so the geared read serverAnimAngle·ChopperWorkScale folds on one whole arm cycle;
        // every mover period 1/4/16/48 divides ReducerRevs), and read the arm phase off THAT geared read. First tick after a
        // (re)start just seeds the references (the
        // raw-angle sample AND the phase) so the initial gap isn't banked as a strike; serverAnimAngle itself is preserved
        // across idle (freeze), only serverAnimAnglePrev re-seeds — the idle rotation is never banked.
        float ang = AngleRad;
        if (!serverStrikeSeeded)
        {
            serverAnimAnglePrev = ang;
            prevServerStrikePh = ChopperAnim.ArmCyclePhase(serverAnimAngle * Config.ChopperWorkScale);
            serverStrikeSeeded = true;
            return;
        }

        // The rectified shaft step this tick. The wrapped-angle difference |Δ| is EXACT only while the shaft turns less
        // than π per tick; above that the [−π,π] fold below reads a genuine forward step as a small backward one, the
        // accumulator stalls and the arm phase freezes — the "chopper breaks completely at high speeds" report (Lielac,
        // ModDbBugfixPlan E02/T02.01, folded here). It aliases at |shaft Δ| > π ⇒ NetworkSpeed·5·dt > π ⇒ NetworkSpeed >
        // 8π ≈ 25 (matching the report's "effective speed above ~25"). The continuous speed gives the TRUE magnitude with
        // no Nyquist limit (ShaftRadPerNetworkSpeedSecond), so below π/tick keep the exact wrapped difference (speed=1 stays
        // bit-identical — invariant I8) and only above it fall back to the speed-derived magnitude. Both are rectified
        // (|·|), so the strike still always plays down whichever way the network drives the shaft.
        float d = ang - serverAnimAnglePrev;
        serverAnimAnglePrev = ang;
        if (d > GameMath.PI) d -= GameMath.TWOPI;        // fold the [0,2π) wrap back to the true short-arc delta
        else if (d < -GameMath.PI) d += GameMath.TWOPI;
        float absD = d < 0f ? -d : d;                    // rectified wrapped difference (exact below π/tick)
        float speedInc = NetworkSpeed * ShaftRadPerNetworkSpeedSecond * dt; // Nyquist-immune true |Δ| from the continuous speed
        float inc = speedInc > GameMath.PI ? speedInc : absD;
        serverAnimAngle += inc;
        // SpeedDecouplingPlan T03.03: the raw accumulator folds at the M-dependent ChopperCycleRevs·2π (the server twin of
        // the render's animBound), so the geared read serverAnimAngle·ChopperWorkScale lands on one whole arm cycle at the
        // fold. speed=1 → 48·2π, the pre-feature bound. T03.04: fold via a TRUE modulo (WrapPos, as the render already
        // does), not a single subtract — a single subtract held only while bound > the tick's step, but at the top of the
        // band ChopperCycleRevs·2π shrinks below π (speed ≳ 96) and one subtract can't return the accumulator to range, so
        // it would drift unbounded (breaking the anti-drift guarantee); and the anti-alias speedInc above can add several
        // revolutions in one tick, more than a single subtract removes. WrapPos handles both. Below ~96 with a normal step
        // this is identical to the old single subtract, so speed=1 is unchanged.
        float bound = Config.ChopperCycleRevs * GameMath.TWOPI;
        serverAnimAngle = WrapPos(serverAnimAngle, bound);

        // Push serverAnimAngle to clients on a light ~1 s cadence while turning, so the render's anchor
        // (BlockEntityChopper.Render.AnchorAnimToServer) re-bases its estimate and closes the ~25.9 rad startup offset
        // BEFORE the once-per-cycle strike sync — otherwise the firewood-before-axe desync would persist through the whole
        // first log. Throttled (not every 25 ms tick) to keep the sync traffic cheap.
        //
        // AuditFixPlan T07.05 / F-L3-08: this used to be a bare MarkDirty(). It now goes out as the anchor's OWN packet
        // (SendAnimAnchor, above) — same gate, same cadence, same behaviour, minus the chunk the announcement dragged
        // along: a powered chopper no longer re-dirties its chunk for saving once a second, no longer holds it unpacked
        // in server RAM past the 15 s compaction TTL, and no longer resends ~280 bytes of tree to move four. The
        // reasoning, and why the gate must NOT gain a "log loaded" term, are with the constants above. The former
        // justification of the MarkDirty form (data-only, F-S12-05: no re-tesselation, since the static frame mesh is
        // rotY-invariant — key `iw-chopper-static-{rotY}`, no angle/progress term) is moot now that the anchor does not
        // touch the block entity's dirty state at all. Real state boundaries keep their own MarkDirty: the strike step
        // below, CompletePass, the input moves.
        long nowMs = Api.World.ElapsedMilliseconds;
        if (nowMs - lastAnimSyncMs >= AnimSyncIntervalMs) { lastAnimSyncMs = nowMs; SendAnimAnchor(); }

        float ph = ChopperAnim.ArmCyclePhase(serverAnimAngle * Config.ChopperWorkScale);
        float startPh = prevServerStrikePh;
        prevServerStrikePh = ph;
        // The forward phase advance this tick, taken DIRECTLY from the rectified shaft step (inc · workScale, spanning the
        // 48·2π arm cycle) rather than from the wrapped [ph − startPh] difference (SpeedDecouplingPlan T03.04). At the top
        // of the speed band the geared step exceeds half a cycle (workScale up to 100 ⇒ up to ~1.04 cycles/tick), which the
        // old [−0.5,0.5] phase fold would mis-read as a small backward step and drop every strike; the direct advance is
        // unambiguous and always ≥ 0 (inc is rectified). CrossedForward handles dph ≥ 1 — it fires once on the single
        // boundary crossing that matters, and multiple arm cycles in one tick still split just the ONE loaded log. This is
        // the true forward advance the client render already integrates as inc, kept in lockstep. speed=1 → workScale=1 and
        // inc ≤ π ⇒ dph ≈ inc/(96π) ≲ 0.01, identical to the value the old fold produced at default.
        float dph = inc * Config.ChopperWorkScale / (ImmersiveWoodworkingConfig.ChopperCycleCap * GameMath.TWOPI);
        if (dph <= 0f) return;           // rectified → forward only; no motion this tick = no strike

        // A STRIKE = the arm crossing its bottom-of-stroke phase (the Y minimum, the moment the maul lands). Because the
        // accumulator is rectified-forward (matching the client's animAngle), this is the SAME phase the client keys its
        // chips/crack on — the split coincides with the visible impact on EVERY node (the old signed tracker split fine but
        // left a reverse-playing client without effects). CrossedForward catches the boundary once even when a 25 ms tick
        // steps clean over the narrow ~2%-of-cycle drop window (a sampled-Y detector would alias it away — and at
        // ChopperStrikes=1 that would drop a whole log).
        float strikePh = ChopperAnim.ArmRiseFrac + ChopperAnim.ArmDropFrac;
        if (!CrossedForward(startPh, dph, strikePh)) return;

#if IW_DEBUG
        // [chopper-sync] desync diagnostic (2026-07-14): log the SERVER strike moment. Compare its wall-clock (@…ms) and
        // net= (the network's UNWRAPPED angle) against the "CLIENT strike" line (Effects.OnStrikeBottom) for the same log:
        // a gap in @…ms = the firewood-before-axe desync; serverAnimAngle vs the client's animAngle = the accumulator
        // offset; net= on both sides tells whether the network's own synced angle is aligned (i.e. whether basing the
        // phase on it would fix it). Grep 'chopper-sync' in server-main + client-main logs.
        Api.Logger.Notification("[immersivewoodworking] [chopper-sync] SERVER strike @{0}ms serverAnimAngle={1:0.000} ph={2:0.000} net={3:0.000} canChop={4}",
            Api.World.ElapsedMilliseconds, serverAnimAngle, ph, InputMechBehavior?.Network?.AngleRad ?? -999f, CanChop());
#endif

        // The maul bottomed out. Only SPLIT if a choppable, product-resolvable log is actually loaded (CanChop) — an
        // assembled, powered chopper runs its whole mechanism LOG OR NO LOG (the axe slams an empty bed too, the client
        // thuds), but an empty strike consumes nothing. chopProgress stays frozen while no log, resuming on the next
        // loaded strike. (Checked HERE, after the phase advanced, so the phase never falls behind the client.)
        if (!CanChop()) return;

        // One strike = ONE discrete split step of ChopperSplitPerStrike = 1/N, N = ⌈1/ChopperSpeed⌉ strikes per
        // log (default 1 → one arm cycle = one strike = one log split, the "one slow blow"). Progress JUMPS here; it does
        // not accrue between strikes. EffectiveChopSpeed stays the GATE (CanChop needs it > 0: head installed + above
        // torque) but does NOT set the step size — the metal is the WEAR tier, not speed; the strike RATE is solely the
        // network RPM. SpeedDecouplingPlan T01.02 swapped the old 1/ChopperStepsPerLog for this precompute (invariant I5,
        // no per-tick arithmetic from the multiplier); T03.04 completed the N-strike detector on the parametric cycle.
        chopProgress += Config.ChopperSplitPerStrike;

        // Complete on the Nth strike even when N·(1/N) floats a hair under 1.0. Float 1/N does not sum back to exactly 1
        // (e.g. speed 0.01 → N=100, 100 × 0.01f = 0.99999994), which a bare `>= 1f` would leave needing an (N+1)-th strike
        // — one blow too many (the minor flagged in the 2026-09-15 inversion revision). Rounding the threshold DOWN by half
        // a step fells a log in EXACTLY N=⌈1/ChopperSpeed⌉ strikes (DoD): the (N−1)-th strike sits at (N−1)/N = 1 − 1/N,
        // comfortably below 1 − 1/(2N), so it never completes early, while the N-th (≈ 1.0) always does. At N=1 the
        // threshold is 0.5 and the single strike (progress → 1.0) completes exactly as before — bit-identical at default.
        if (chopProgress >= 1f - Config.ChopperSplitPerStrike * 0.5f)
        {
            chopProgress = 1f; // clamp to a full pass; CompletePass resets it to 0 once the batch is ejected (T06.02)
            MarkDirty(true);   // pass-complete is a visible-state boundary — sync it (sawhorse-style economy)
            CompletePass();
            return;
        }

        // A partial step (only reachable with N = ChopperStrikes > 1): the log is now begun. ONE sync per strike — a
        // discrete visible-state boundary, not a per-tick creep — so the block-info % (T08.03) and the Ctrl+RMB
        // recover-vs-discard hint (T05.02, via LogRecoverable) reflect the new step. Data-only (redrawOnClient:false):
        // the static frame mesh is chopProgress-invariant (its tessellation key is rotY only, no install/progress term),
        // so a retesselation buys nothing — MarkBlockEntityDirty resends the tree, and FromTreeAttributes refreshes the %
        // readout + the Ctrl+RMB hint (RefreshInteractionHelpOnChange, F-S05-03) without re-meshing the ~423-element model
        // each strike. The movers render from the live network angle, no re-mesh needed. Matches the sawmill's per-step
        // economy (BlockEntitySawmill.Sawing.cs data-only creep). F-S12-03; NOTE this MUST stay after F-S05-03 wired the
        // help refresh into FromTreeAttributes — before that, the retesselation here was the only thing refreshing the hint.
        // Data-only stays right even though the WORKPIECE mesh became progress-dependent in T04.07 (it now stages like the
        // hand stand's): that mesh is not in the chunk terrain pool at all — it is the custom renderer's own upload, and
        // the same FromTreeAttributes this sync triggers rebuilds it (UpdateLogMesh) when the stage actually crosses.
        MarkDirty();
    }

    /// <summary>
    /// E06/T06.02 — the single OUTPUT seam: the whole-log product batch for the currently loaded input, resolved by the
    /// hand chopping block's own logic (<see cref="BlockEntityChoppingBlock.GetChopResult"/>, lifted to internal-static +
    /// <see cref="IWorldAccessor"/> in this task's pre-step). FULL parity with the stand: a log → <c>game:firewood</c>,
    /// firewood → <c>game:stick</c>, each total clamped to its runtime max-stack ceiling; null when
    /// the input is empty / unrecognised / its product can't be resolved (so the tick's gate and — later — <c>CanChop()</c>
    /// (T06.04) fail closed). Reused, never re-implemented, so the machine stays mod-independent and can never drift from
    /// the hand stand (mod-independence rule "the chop product is vanilla on purpose", <c>source-conventions.md</c> —
    /// the <c>game:</c> product is intentional). The tuple follows the CHOPPING-BLOCK
    /// convention — the product's StackSize IS the total amount and the count is the PILE count
    /// (<c>FirewoodDropCount</c>/<c>SticksDropCount</c>) — which <see cref="EjectBatch"/> (T06.03) splits with
    /// <c>DropDistribution.SplitEvenly</c> exactly like the stand's <c>SpawnDistributed</c> (NOT the sawmill's
    /// StackSize=1 / count=total convention; PLAN revision 2026-07-01).
    /// </summary>
    internal (ItemStack product, int count)? GetChopBatch()
        => BlockEntityChoppingBlock.GetChopResult(Api.World, InputSlot.Itemstack, Config, DropAmounts.Chopper(Config));

    /// <summary>
    /// E06/T06.02 — pass completion, invoked from <see cref="OnChopperTick"/> once <c>chopProgress</c> reaches a full
    /// pass. Computes the batch FROM THE STILL-LOADED input (<see cref="GetChopBatch"/> reads the input slot, so this runs
    /// BEFORE the log is consumed — the "compute before consume" lesson), EJECTS it forward out of the machine
    /// (<see cref="EjectBatch"/>, T06.03 — pulverizer-style, never buffered in an output slot), then consumes the input
    /// log and resets the progress under the next one. Order mirrors <see cref="BlockEntitySawmill.CompletePass"/>
    /// (compute → eject → consume → reset) MINUS its <c>DropSawdust</c> (Wear &amp; Tear opt-out: chopping yields chips,
    /// not sawdust, and strips no W&amp;T recipe — PLAN "Ключевые решения"). The eject uses the already-captured batch and
    /// never re-reads the slot, so consume-after-eject is order-safe. The head wear (<c>DamageHead</c>, T06.05) wires in
    /// at the seam below.
    ///
    /// Completion is UNCONDITIONAL (no "output full → pause"): the batch is ejected forward, never into a slot, so a full
    /// world around the machine just piles / despawns the firewood — the pass always finishes. It fires exactly once per
    /// log: consuming the input empties the slot → <see cref="GetChopBatch"/> returns null next tick → the gate is false
    /// until another log loads, so no double-eject. Server-only by its caller (<see cref="OnChopperTick"/> returns
    /// off-server before reaching here).
    /// </summary>
    void CompletePass()
    {
        // Compute the batch while the log is still present; the tick gates accrual on GetChopBatch() != null, so this is
        // normally non-null — but stay defensive (a corrupted / externally-edited input could null it) and clear the
        // dead progress rather than leaving it stuck at 1.0.
        (ItemStack product, int count)? batch = GetChopBatch();
        if (batch == null)
        {
            chopProgress = 0f;
            MarkDirty(true);
            return;
        }

        // Eject the whole batch forward, out of the machine (never buffered → a completed pass can't be blocked by a full
        // world), then consume the input log and reset under the next. No DropSawdust (W&T opt-out — see summary).
        EjectBatch(batch.Value.product, batch.Value.count);
        InputSlot.Itemstack = null;
        InputSlot.MarkDirty();
        chopProgress = 0f;
        chopsCompleted++; // signal the client this emptying was a CHOP → it defers the log's visual vanish to its own axe-landing (see chopsCompleted)
        MarkDirty(true);

        // One completed log wears the installed head by the config charge (T06.05); may break it, which flips IsComplete
        // false so the next tick stalls the station until a new head is installed. Mirrors the sawmill's DamageBladeKit
        // call in the same position (last, after eject + consume + reset).
        DamageHead();
    }

    /// <summary>
    /// E06/T06.05 — wear the installed head by ONE completed log. The head (<c>chopperhead-{metal}</c>) carries native
    /// VS durability (<c>durabilitybytype</c> in <c>itemtypes/chopper/head.json</c> — the SINGLE source, T01.02, no C#
    /// table), so this just charges <see cref="ImmersiveWoodworkingConfig.ChopperHeadWearMultiplier"/> points (default
    /// 1/log; 0 = wear off) against the stored <see cref="headStack"/> and, on reaching 0, breaks it. Called once per
    /// completed pass from <see cref="CompletePass"/>, AFTER the batch is ejected and the log consumed — a direct calque
    /// of <see cref="BlockEntitySawmill.DamageBladeKit"/> (<c>bladeStack</c> → <see cref="headStack"/>, blade multiplier →
    /// head multiplier).
    ///
    /// Done by hand rather than via the engine's <c>DamageItem</c>/<c>DestroyItem</c>: those dereference a
    /// <c>byEntity</c> for the break sound/particles (Collectible.cs) and the machine has no entity. So we read/write the
    /// durability attribute directly (<c>GetRemainingDurability</c>/<c>SetDurability</c>) and, on a break, clear the stack
    /// + play the standard tool-break sound at the master. A worn head removed (Ctrl+RMB <see cref="TryTake"/>) or dropped
    /// on teardown (<see cref="AppendPartDrops"/>) clones with its durability attribute intact, so the slot bar/tooltip
    /// show the wear in inventory. Server-only (its caller is).
    /// </summary>
    void DamageHead()
    {
        if (headStack == null) return;

        float rate = Config.ChopperHeadWearMultiplier;
        if (rate <= 0f) return; // wear disabled (config Sanitize floors negatives to 0)

        int max = headStack.Collectible.GetMaxDurability(headStack);
        if (max <= 1) return; // the head item declares no durability → nothing to wear

        int cost = (int)Math.Round(rate);
        if (cost < 1) cost = 1; // any positive rate costs at least 1/log — integer durability has no sub-1 granularity

        int left = headStack.Collectible.GetRemainingDurability(headStack) - cost;
        if (left > 0)
        {
            headStack.Collectible.SetDurability(headStack, left);
            // Data-only (redrawOnClient:false, F-S12-04): the head mover is cached by HeadMetalKey (the collectible code),
            // NOT by durability, so a wear decrement never changes the mesh — a re-tesselation buys nothing. The tree
            // resend still carries headStack with its durability attribute (ToTreeAttributes), so the block-info "X / Y"
            // durability readout (GetBlockInfo) refreshes via FromTreeAttributes. (The BREAK path below stays
            // redrawOnClient:true — there headStack goes null, the head mover drops out and the machine really re-meshes.)
            MarkDirty();
            return;
        }

        // Worn out → the head breaks: destroyed (not dropped), IsComplete flips false, the station stops until a new
        // head is installed. Reset the (already-0) progress defensively and sound the break at the master. Then refresh
        // the client render (E07/T07.01 wired the Render partial): with headStack now null the head mover drops out, so
        // the machine re-renders headless (dedup'd, server no-op — the client sees it via the MarkDirty tree sync too).
        headStack = null;
        chopProgress = 0f;
        Api.World.PlaySoundAt(new AssetLocation("game:sounds/effect/toolbreak"), Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null);
        MarkDirty(true);
        UpdateRenderer();
    }

    /// <summary>
    /// E06/T06.03 — eject the completed batch (<paramref name="dropCount"/> piles totalling
    /// <paramref name="template"/>.StackSize) out of the machine toward the PLAYER, pulverizer-style (never an output
    /// slot). The exit is world <see cref="Facing"/>.Opposite — the SAME player-side eject the sawmill uses
    /// (<see cref="BlockEntitySawmill.EjectBatch"/>): the drop-maul strikes vertically and the log stays put, so the
    /// split billets have no carriage-dictated side and simply tumble off the FRONT of the bed toward the player who
    /// placed it. (Until 2026-07-14 this ejected toward local +Z = world <see cref="Facing"/> — out the BACK, away from
    /// the player — the "перед и зад" the user asked to swap; the auto-feed dock moved to the opposite face in the same
    /// change, see <see cref="IsBackCentralInputDock"/>.) The batch is FLUNG under physics rather than parked a cell
    /// ahead: each pile spawns just outside the bed's front face (collision-free — only the master bed cell carries the
    /// solid centre-fill box, so a billet a fraction past it clears the frame and won't hang inside the multiblock,
    /// memory vs-multiblock-collision-broadphase) at bed-surface height, and gets a gentle FLAT horizontal toss so it
    /// flies straight off the bed and drops ~½ cell ahead (no arc, per the user 2026-07-14). The pile
    /// split uses the CHOPPING-BLOCK convention — <paramref name="template"/>.StackSize is the total and
    /// <paramref name="dropCount"/> is the pile count (<c>FirewoodDropCount</c>/<c>SticksDropCount</c>, carried in the
    /// batch) — exactly like the stand's <c>SpawnDistributed</c>, so it does NOT re-derive the count from the item class
    /// the way the sawmill does. Spawn point / velocity are PLACEHOLDER magnitudes calibrated in-game (T08.04), like the
    /// hand block's own pop. Completion is unconditional — a full world just piles / despawns the firewood on the
    /// ground. Server-only (its sole caller <see cref="CompletePass"/> is).
    /// </summary>
    void EjectBatch(ItemStack template, int dropCount)
    {
        int total = template.StackSize; // chopping-block convention: the total rides in the template's StackSize
        if (total <= 0) return;         // degenerate batch: nothing to eject

        int max = template.Collectible.MaxStackSize;

        // ── Toss physics (T06.03 + user 2026-07-14 "throw the firewood under physics, don't spawn it a cell ahead") ──
        // Blocks/tick (SpawnItemEntity velocity → Pos.Motion, AirDrag 0.983/tick, memory vs-item-drop-velocity-calibration);
        // all PLACEHOLDER, tuned in-game (T08.04) like the hand block's DropHorizontal/VerticalVelocity. Refined
        // 2026-07-14 (user) to a FLAT horizontal toss (no arc) ~30% weaker than the first pass — raise forwardVel for a
        // longer throw, add a little upVel back for an arc, or drop forwardVel toward the old feather push (≈0.02) for a
        // tight drop into a hopper.
        const double spawnDist = 0.7;    // blocks from the master centre along the exit dir — ~0.2 past the bed's front face, into the free cell
        const double spawnHeight = 0.8;  // Pos.Y + this = bed-surface height (where the billet sits before it splits)
        const double forwardVel = 0.084; // toward the player, per pile — a gentle HORIZONTAL toss (was 0.12; user 2026-07-14: -30%)
        const double upVel = 0.0;        // no upward pop → flies straight out and drops under gravity (user: "просто горизонтально")
        const double lateralVel = 0.04;  // ± sideways scatter so the pieces don't leave in single file
        const double rowSpread = 0.4;    // positional fan across the exit (kept from the old row layout, tightened)

        // The split firewood is flung off the bed toward the PLAYER = world Facing.Opposite (the sawmill's player-side
        // exit). The drop-maul strikes vertically and the log stays put, so the billets just tumble off the front of the
        // bed — there is no carriage exit to respect. The old +Z (world Facing) eject threw them out the back, away from
        // the player; this is the "swap перед и зад" fix (the auto-feed dock swapped to the opposite face too).
        Vec3i outDir = Facing.Opposite.Normali;

        // Spawn just OUTSIDE the bed toward the exit — the central column's near-exit cell (local (0,*,-1) in the exit
        // dir) is collision-free (only the master bed cell (0,0,0) has the solid centre-fill box, ending at the bed's
        // front face), so the billet clears the frame and won't hang inside a collision box (memory
        // vs-multiblock-collision-broadphase). Bed-surface height, so it reads as flying OFF the block, not out of the
        // ground. spawnDist < 1 keeps it hugging the machine (the "spawns at the machine, not a cell ahead" fix); the
        // velocity carries it the rest of the way.
        double cx = Pos.X + 0.5 + outDir.X * spawnDist;
        double cy = Pos.Y + spawnHeight;
        double cz = Pos.Z + 0.5 + outDir.Z * spawnDist;

        // Horizontal axis ACROSS the exit (the perpendicular of outDir): fans the piles into a short row and carries the
        // sideways velocity scatter. If the exit runs along Z, spread along X, and vice-versa.
        double perpX = outDir.Z != 0 ? 1 : 0, perpZ = outDir.X != 0 ? 1 : 0;

        // Chopping-block-style pile split: several visual stacks rather than one (dropCount = FirewoodDropCount /
        // SticksDropCount, carried in the batch). SplitEvenly caps piles to the total and sums back to it exactly; guard
        // a non-positive count defensively (config is Sanitize-clamped, but stay safe like FeedAdvance's steps guard).
        int[] sizes = DropDistribution.SplitEvenly(total, dropCount < 1 ? 1 : dropCount);

        var rand = Api.World.Rand;
        for (int i = 0; i < sizes.Length; i++)
        {
            // Even slot in the row across the exit (i-th of n in [-rowSpread/2 .. +rowSpread/2]) + a little position
            // jitter so successive batches don't land pixel-identical; a lone pile stays centred.
            double slot = sizes.Length > 1 ? ((i + 0.5) / sizes.Length - 0.5) * rowSpread : 0.0;
            double jx = (rand.NextDouble() * 2 - 1) * 0.05, jz = (rand.NextDouble() * 2 - 1) * 0.05;
            Vec3d pos = new Vec3d(cx + perpX * slot + jx, cy, cz + perpZ * slot + jz);

            // Directed toss: FLAT along the exit (no upward pop) + a small ± sideways spread, so the pieces fly
            // horizontally off the bed and drop under gravity ~½ cell ahead instead of arcing or dropping in place.
            double lateral = (rand.NextDouble() * 2 - 1) * lateralVel;
            Vec3d velocity = new Vec3d(
                outDir.X * forwardVel + perpX * lateral,
                upVel,
                outDir.Z * forwardVel + perpZ * lateral);

            // One pile is normally a single entity (a per-log firewood/stick count ≪ MaxStackSize); split defensively if
            // a pile ever exceeds it.
            int remaining = sizes[i];
            while (remaining > 0)
            {
                int n = remaining < max ? remaining : max;
                ItemStack drop = template.Clone();
                drop.StackSize = n;
                Api.World.SpawnItemEntity(drop, pos, velocity);
                remaining -= n;
            }
        }

        // NO chip burst here. The cosmetic chips are a CLIENT, per-STRIKE effect fired off the animation's
        // bottom-of-stroke (T07.04, BlockEntityChopper.Effects.UpdateCutEffects), NOT a once-per-log server spawn — so a
        // log makes chips fly on each of its ChopperStrikes strikes, not one burst at completion (spec §8 anticipated
        // relocating the former SpawnChips() placeholder seam that lived here off the server). See the Effects partial.
    }
}
