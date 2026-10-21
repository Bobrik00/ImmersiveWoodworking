using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
    // The saw-stroke sound is NOT played from code: it's bound to the FK saw animation's FRAMES via the metadata's
    // `animationSounds` (patches/sawing_animations.json — sounds/saw/sawing_ff* at frame 5 (forward stroke) +
    // sawing_fb* at frame 10 (back stroke); the "fast" band shared with the automated sawmill). The engine's
    // ClientAnimator fires it once per loop iteration on every client that renders the animation, so it stays in
    // lockstep with the visible arm strokes for the sawyer AND for onlookers — no server-side time throttle (which
    // drifted out of phase with the animation). See T06.08 / the apply-sawing-animation.ps1 sound block.

    // Sawdust RATE and LIFE, both authored in REAL units (AuditFixPlan T06.02 / F-L10-07).
    //
    // RATE is per emitter per SECOND, and there are two emitters (front + under), so a rip throws 192 flakes a
    // second whatever the frame rate. The number is the TOP of the range the old per-step template produced — a
    // 144 fps client saw 2 flakes × 2 emitters × ~48 steps/s — chosen so nobody's picture gets thinner than what
    // it already was; a 30 fps client, who used to get 120/s, gains.
    //
    // LIFE is what a flake is visible for. It was FIVE times the number written here, because the engine multiplies
    // LifeLength by 5 at spawn (see ParticleLife) — the "0.8 s" this template used to declare was 4 s on screen, and
    // a flake lands in well under one of them, so most of that was a motionless quad running terrain physics 30
    // times a second. Cut to a third by author decision 2026-08-28.
    const float SawDustPerEmitterPerSecond = 96f;
    const float SawDustSeconds = 1.3f;

    // Sawdust thrown from the cut while ripping (T06.08), client-side and cosmetic. A static template reused per
    // spawn (Color / MinPos / AddPos / quantity set each time, mirroring ItemAxe.dustParticles) — fine because
    // particle spawn is synchronous and client-only. Small, short-lived flakes that fall (gravity) off the cut and
    // settle.
    //
    // QUANTITY is no longer a ctor range (AuditFixPlan T06.02 / F-L10-07). It used to be 1..3 per emitter per CALL,
    // and the call is one step of the RMB hold, which the engine drives off a 20 ms tick listener: ~30 Hz at both 30
    // and 60 fps, ~48 Hz at 144. Two players watching the same cut therefore saw densities 1.6× apart. SpawnSawDust
    // now banks SawDustPerEmitterPerSecond × the frame's real seconds and spawns the whole part, exactly as the
    // powered sawmill's sawDustAccum has always done.
    static readonly SimpleParticleProperties SawDust = new SimpleParticleProperties(
        0, 0,                                // quantity is set per spawn from the rate accumulator — ctor args unused
        ColorUtil.ToRgba(255, 170, 130, 90), // overwritten per spawn from the log's own texture (species colour)
        new Vec3d(), new Vec3d(),
        new Vec3f(-0.25f, -0.6f, -0.25f),    // mostly downward, slight sideways scatter
        new Vec3f(0.25f, -0.05f, 0.25f),
        ParticleLife.Seconds(SawDustSeconds),  // life (real seconds — ParticleLife undoes the engine's ×5)
        1f,                                   // gravity — flakes drop and land
        0.1f, 0.25f,                          // min / max size
        EnumParticleModel.Quad)
    {
        WithTerrainCollision = true,
        WindAffected = true,
        WindAffectednes = 0.3f,
    };

    // The OBSERVER's copy of the same dust (AuditFixPlan T06.01, F-L4-13) — a SECOND template, never a reuse of the
    // one above, for three reasons. COLOUR: the server has no texture atlas, so it cannot call Block.GetRandomColor;
    // it ships ColorByBlock instead, which SimpleParticleProperties serialises as a BlockId and every RECEIVING
    // client resolves against its own atlas (GetRgbaColor). That field takes PRECEDENCE over Color there, so setting
    // it on the shared template would blank the local sawyer's own per-flake colour. THREAD: in singleplayer client
    // and server live in one process on two threads, and this object is written by the server tick while the client
    // tick writes the one above ~60×/s. QUANTITY: the broadcast is throttled to roughly one burst per hold packet
    // rather than one per frame (WorkFxNetIntervalMs), so each burst carries more flakes to read the same from a few
    // blocks away. Wind is deliberately absent for two reasons: ToBytes does not carry WindAffected/WindAffectednes,
    // so declaring them here would only be a lie, AND a non-zero WindAffectednes makes the engine's BeginParticle read
    // GlobalConstants.CurrentWindSpeedClient — a client-only global — on the server tick that sends this.
    // WithTerrainCollision is not carried either, but its field initialiser is true,
    // so the receiver's copy collides with the ground exactly like the local one.
    // Quantity arithmetic behind the numbers below, so re-tuning stays informed: the local template now emits a
    // FIXED SawDustPerEmitterPerSecond (96) per emitter per second (T06.02 — it used to be a per-frame range, which
    // is what made this comparison frame-rate dependent), while this one fires about 7 bursts a second (one per hold
    // packet). Parity would be ≈14 per burst; 10+12 averages 11, deliberately under it — the observer stands
    // further away, and an under-filled burst reads as a stream while an over-filled one reads as a pulse. LIFE is
    // the same constant as the local copy, so the two read identically; at 1.3 s that keeps eight or nine bursts in
    // the air at once, which is what makes it continuous.
    static readonly SimpleParticleProperties SawDustNet = new SimpleParticleProperties(
        10, 12,
        ColorUtil.ToRgba(255, 170, 130, 90), // replaced per spawn by ColorByBlock, resolved on the receiving client
        new Vec3d(), new Vec3d(),
        new Vec3f(-0.25f, -0.6f, -0.25f),
        new Vec3f(0.25f, -0.05f, 0.25f),
        ParticleLife.Seconds(SawDustSeconds),
        1f,
        0.1f, 0.25f,
        EnumParticleModel.Quad);

    // ───────────────────────── E06: sawing — start gate (T06.01) ─────────────────────────
    //
    // The pair rips a held log into planks/beams ONLY when the player holds an EFFECTIVE saw (SawIsEffective — the
    // pit saw always, an ordinary saw only when AllowVanillaSaw is on) AND stands physically at an END of the
    // construction (past a trestle, along the log axis), not at a side (Q4: by position, not view). The gate is the
    // single entry: when it fails — including an ordinary saw while AllowVanillaSaw is off — the interaction is
    // refused outright (a "need a pit saw" nudge, NO animation / particles / sound / progress); when it passes, the
    // hold runs, scaled by the tool's speed (an ordinary saw rips slower, VanillaSawSpeedMultiplier). T06.01 is just
    // the gate + the slave→master routing; the hold + progress (T06.02), staged yield (T06.03+) and visuals
    // (T06.07/08) hang off this same entry once it returns true.

    /// <summary>
    /// Plain RMB with a tool in hand on a pair that holds a log: begin a sawing hold if allowed. Gate: the tool is an
    /// EFFECTIVE saw (<see cref="SawIsEffective"/> — the pit saw always, an ordinary saw only when
    /// <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> is on) and the player stands at an axis END
    /// (<see cref="IsPlayerAtAxisEnd"/>). When the gate fails the hold never begins — no animation, particles, sound
    /// or progress — and the refusal is SILENT here; the caller (<see cref="BlockSawhorse.OnBlockInteractStart"/>)
    /// raises one position/tool-aware nudge via <see cref="NotifyInteractBlocked"/> only after the strip gate also
    /// failed, so an ordinary saw while the flag is off is rejected with "need a pit saw" exactly like any non-saw,
    /// while a side-standing player is told to STRIP instead. Once it
    /// passes, the hold's per-tick progress is scaled by the tool's speed (<see cref="SawSpeedFor"/>).
    /// DIRECTIONAL — on the FIRST cut-start of a fresh log the clicked side (<paramref name="hitPos"/>, via
    /// <see cref="HitIsReversedSide"/>) picks which across-the-log side the rip works from and LOCKS it for the log
    /// (<see cref="sawCutReversed"/>): the planks eject to that side and the boards visually vanish from it.
    /// CO-OP — the pair is a two-man pit saw: up to TWO players may rip at once,
    /// but ONLY from OPPOSITE ends. On the server this CLAIMS the player's end (<see cref="PlayerEndSign"/>), so two
    /// opposite-end holds each advance progress and the rip runs at 2×; a second player at an ALREADY-manned end is
    /// refused (no same-end stacking — the end map's two keys are the hard cap of two). Routes to the master so either
    /// cell works. Returns true to begin the timed hold (continued in <see cref="ContinueSawing"/>, ended in
    /// <see cref="StopSawing"/>); the caller (<see cref="BlockSawhorse.OnBlockInteractStart"/>) then starts the saw
    /// animation. Evaluated on both sides (the gate is deterministic from synced state); end-claiming and progress are
    /// server-side.
    /// </summary>
    /// <param name="hitPos">World point the player aimed at on the log (<c>blockSel.FullPosition</c>) — its
    /// across-the-log side sets the rip direction on a fresh cut (see <see cref="HitIsReversedSide"/>).</param>
    public bool TryStartSawing(IPlayer byPlayer, ItemSlot? toolSlot, Vec3d hitPos)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.TryStartSawing(byPlayer, toolSlot, hitPos);

        if (!CanSawHere(byPlayer, toolSlot)) return false; // refused silently here — the caller nudges ONCE, after BOTH the strip and the rip gate fail, via NotifyInteractBlocked (so a side-standing player is told to STRIP, not "need a saw")

        if (Api.Side != EnumAppSide.Server) return true;   // client: the gate drives the animation; the server arbitrates ends

        // Weed the hold map BEFORE reading it, exactly as the mid-hold claim does (ClaimOrVerifyEnd's first line) —
        // this START gate read it raw (AuditFixPlan T03.04, F-L5-25). A sawyer who disconnected mid-rip leaves a ghost
        // entry no release ever removes, and the two reads below both believe it: the direction lock treats the map as
        // manned (so a genuinely first sawyer can't set the cut side) and the ownership check refuses that end to
        // everyone, forever. Both self-heal the moment the ghost is dropped here.
        PruneOfflineSawyers();

        // Directional sawing (controlled process): the FIRST sawyer on a FRESH cut picks which across-the-log side the
        // rip works from, by WHERE THEY CLICKED on the log (HitIsReversedSide). LOCKED for the life of the log — once
        // any progress is on the clock OR any end is already manned, a co-op joiner's or a re-grip's click can't flip
        // it (the cut can't reverse mid-log). Set BEFORE claiming this player's end below (so sawEnds is still empty for
        // the very first sawyer). Sync it now (MarkDirty) only when it actually changes, so the client mirrors the
        // visual hide order from the first frame; at progress 0 the whole log still shows regardless, so there's no
        // pop even if the sync lands a tick later.
        if (sawProgress <= 0f && sawStagesFired == 0 && sawEnds.Count == 0)
        {
            // Lock BOTH per-log choices from THIS first sawyer: the directional cut side (from where they clicked) and
            // the cut product (from their saw's tool mode, SawModePlan). A modeless / Auto saw locks Auto, which keeps
            // the pre-plan input-driven product, so nothing changes for a player who never touches F. Co-op joiners
            // can't flip either after this (sawEnds fills below). One MarkDirty when either actually moved.
            bool rev = HitIsReversedSide(hitPos);
            SawMode m = CollectibleBehaviorSawMode.ModeFor(toolSlot?.Itemstack);
            bool changed = rev != sawCutReversed || m != sawMode;
            sawCutReversed = rev;
            sawMode = m;
            if (changed) MarkDirty(true);
        }

        int endSign = PlayerEndSign(byPlayer);
        string uid = byPlayer.PlayerUID;
        // This end already manned by ANOTHER sawyer → refuse (one player per end; two ends = the hard cap of two).
        if (sawEnds.TryGetValue(endSign, out SawyerHold? held) && held.Uid != uid) return false;

        ReleaseSawyer(uid);                            // a player owns at most one end (handles re-grip / end switch)
        // Baseline ZERO, and that is correct rather than a head start: the engine stamps the hold's origin
        // (Controls.UsingBeginMS) immediately after this very OnBlockInteractStart returns, so t=0 IS this instant.
        // The first ContinueSawing tick therefore charges exactly the real seconds that passed between the press and
        // that tick. (Un-clamped it charged MORE than that — the engine's replayed steps inflated the argument, which
        // is what made the zero baseline look like a gift, F-L4-04. The clamp lives at the seam, in
        // BlockSawhorse.OnBlockInteractStep, T02.03.) A MID-hold claim is the other case and baselines differently —
        // see ClaimOrVerifyEnd.
        sawEnds[endSign] = new SawyerHold(uid, 0f);
        return true;
    }

    /// <summary>The single start gate for the rip (used by <see cref="TryStartSawing"/> and re-checked each tick by
    /// <see cref="ContinueSawing"/>): a log is laid, NO strip has begun on it (<c>!<see cref="BarkStarted"/></c> — the
    /// log is locked to whichever action started first, so a log already being DEBARKED can't also be sawn, T05.03),
    /// the rip can actually FINISH (<see cref="SawProductResolves"/> — the dead-end gate the strip has always had, and
    /// the rip did not: a wood whose plank/beam does not exist used to pass all four stages, drop nothing, wear nothing
    /// and take the log with it, AuditFixPlan T03.03),
    /// the held tool is an EFFECTIVE saw (<see cref="SawIsEffective"/> — the pit saw always, an ordinary saw only when
    /// <see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> is on), and the player stands at an axis end. Drives
    /// EVERYTHING — animation, particles, sound and progress all hang off this — so an ordinary saw while the flag is
    /// off fails it and produces nothing at all (the caller's <see cref="NotifyInteractBlocked"/> then names the
    /// missing tool), rather than going
    /// through a no-op motion. (A saw aimed at a log already being stripped is likewise refused — the caller's
    /// <see cref="NotifyInteractBlocked"/> then nudges to discard it before sawing, T05.03.) Deterministic from synced state (the flags + progress are
    /// server-synced), so it answers the same on both sides.</summary>
    bool CanSawHere(IPlayer byPlayer, ItemSlot? toolSlot)
        => heldLog != null && !BarkStarted
           && SawProductResolves(Api.World, heldLog, EffectiveSawMode(toolSlot?.Itemstack))
           && CanSawHereIgnoringLog(byPlayer, toolSlot);

    /// <summary>The cut product this interaction targets (SawModePlan): once a rip is under way the LOCKED
    /// <see cref="sawMode"/> rules (a co-op joiner can't change it), and before it the querying saw's own tool mode does
    /// (<see cref="CollectibleBehaviorSawMode.ModeFor"/>) — the mode <see cref="TryStartSawing"/> will lock. Deterministic
    /// on both sides: at <c>sawProgress</c> 0 the tool's synced attribute is read, after it the synced
    /// <see cref="sawMode"/>. Master-relative (its callers route through <see cref="GetMaster"/>).</summary>
    SawMode EffectiveSawMode(ItemStack? tool) => sawProgress > 0f ? sawMode : CollectibleBehaviorSawMode.ModeFor(tool);

    /// <summary>The rip gate MINUS the "a log is laid" clause — the held tool is an EFFECTIVE saw
    /// (<see cref="SawIsEffective"/>) and the player stands at an axis end (<see cref="IsPlayerAtAxisEnd"/>). Used
    /// during the auto-restock breather (<see cref="restockPending"/>), where the pair is momentarily EMPTY but the
    /// held rip must stay alive until the next log is laid.</summary>
    bool CanSawHereIgnoringLog(IPlayer byPlayer, ItemSlot? toolSlot)
        => SawIsEffective(toolSlot?.Itemstack) && IsPlayerAtAxisEnd(byPlayer);

    /// <summary>
    /// Client-side nudge when a plain-RMB on a laid log started NEITHER the rip NOR the strip — BOTH start gates
    /// (<see cref="CanSawHere"/> / <see cref="CanDebarkHere"/>) refused. Mirrors <c>BEPulverizer</c>'s "add 2 caps at
    /// the same time" message (a short red line above the hotbar), but picks WHICH message by the player's PHYSICAL
    /// position — the rip's END zone vs the strip's long-SIDE zone — and the tool in hand, so the line always names
    /// the action that position/tool is for (the central fix: a side-standing player is told to STRIP, not "need a
    /// saw"):
    ///  • holding an EFFECTIVE saw → if the log is locked to a begun STRIP (<see cref="BarkStarted"/>), "this log is
    ///    being stripped — discard it to saw" (the per-log mode lock, T05.03; a partial strip can only be abandoned,
    ///    not switched); else if the rip could never finish on this wood (<see cref="SawProductResolves"/>), say THAT
    ///    (T03.03) — like the lock, a dead end dominates position, since moving to the end would change nothing;
    ///    otherwise, off the end, "stand at an end" (at the end it would have ripped);
    ///  • holding an EFFECTIVE debark tool → if the log is locked to a begun RIP (<see cref="SawStarted"/>), "this log
    ///    is being sawn — discard it to strip"; else if the wood has no debarked form to become
    ///    (<see cref="DebarkProductResolves()"/>), say that instead of leaving the player to guess; otherwise, on a
    ///    still-barked log off the side, "stand at a side" (an already-debarked log has no bark → silent);
    ///  • holding NEITHER → name the tool the player's ZONE needs, but only for an action that can actually FINISH
    ///    here: a still-barked, strippable log at a long SIDE → the strip tools (the knife listed only when
    ///    <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> admits it), else a rippable log → the rip's saw,
    ///    exactly as the old saw-only nudge did, else — neither resolves — the dead-end line, rather than sending the
    ///    player off to fetch a tool for a product that does not exist.
    /// No-op on the server (<c>Api as ICoreClientAPI</c> is null there — only the client shows it) and when no log is
    /// laid. Master-relative — routes to the master so the geometry (<see cref="IsPlayerAtAxisEnd"/> /
    /// <see cref="IsPlayerAtAxisSide"/>) and the held-log state read the laid log's pair. Distinct error codes per
    /// condition (as the pulverizer does), so the engine coalesces a held repeat of the SAME message while a switch
    /// between conditions still updates the line.
    /// </summary>
    public void NotifyInteractBlocked(IPlayer byPlayer, ItemSlot? toolSlot)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.NotifyInteractBlocked(byPlayer, toolSlot); return; }

        if (Api is not ICoreClientAPI clientApi) return; // server: the gates are silent there; only the client nudges
        if (heldLog == null) return;                     // not a saw/debark attempt (empty pair) — say nothing

        ItemStack? main = toolSlot?.Itemstack;
        ItemStack? off  = byPlayer.Entity?.LeftHandItemSlot?.Itemstack; // byPlayer is the interacting player — never null on this path

        // An EFFECTIVE tool in hand reveals INTENT — point at THAT action's one unmet condition. A saw and a debark
        // tool are mutually exclusive in the main hand (a saw is EnumTool.Saw; the debark tools an axe/knife/bark
        // spud), so at most one branch fires.
        if (SawIsEffective(main)) // wants to RIP — reachable off the end, or mode-locked to a begun strip
        {
            // The log is locked to whichever action began first (T05.03): a begun STRIP (BarkStarted) refuses the saw
            // outright, and a partial strip can't be switched — only discarded (Ctrl+RMB) — so say exactly that. The
            // lock dominates position: it fires regardless of where the player stands. Otherwise the saw is fine and
            // the sole remaining miss is standing off the end.
            if (BarkStarted)
                clientApi.TriggerIngameError(this, "sawlocked", Lang.Get("immersivewoodworking:ingameerror-sawhorse-lockeddebarking"));
            else if (!SawProductResolves(Api.World, heldLog, EffectiveSawMode(main)))
                clientApi.TriggerIngameError(this, "sawnoproduct", Lang.Get("immersivewoodworking:ingameerror-sawhorse-nosawproduct"));
            else if (!IsPlayerAtAxisEnd(byPlayer))
                clientApi.TriggerIngameError(this, "sawneedend", Lang.Get("immersivewoodworking:ingameerror-sawhorse-needend"));
            return;
        }
        if (BarkToolIsEffective(main, off)) // wants to STRIP — reachable off the side, or on a bark-less / saw-locked log
        {
            // Mirror the rip: a begun RIP (SawStarted) locks the log to sawing and refuses the strip, switchable only
            // by discarding → say so (lock dominates position). Otherwise a still-barked free log just needs a side;
            // an already-debarked log has no bark to strip → stay silent (nothing to do).
            if (SawStarted)
                clientApi.TriggerIngameError(this, "debarklocked", Lang.Get("immersivewoodworking:ingameerror-sawhorse-lockedsawing"));
            else if (HasBark && !DebarkProductResolves())
                clientApi.TriggerIngameError(this, "debarknoproduct", Lang.Get("immersivewoodworking:ingameerror-sawhorse-nodebarkproduct"));
            else if (HasBark && !IsPlayerAtAxisSide(byPlayer))
                clientApi.TriggerIngameError(this, "debarkneedside", Lang.Get("immersivewoodworking:ingameerror-sawhorse-needside"));
            return;
        }

        // No effective tool of either kind → name the tool the player's ZONE needs. A still-barked log at a long SIDE
        // is the strip spot → name the debark tools; everywhere else (an end, a corner, or a bark-less log) the rip is
        // the action → name the saw, exactly as the old saw-only nudge did. Each arm now also asks whether that action
        // can FINISH on this wood (T03.03): naming a tool for a product that does not exist sends the player off to
        // fetch it for nothing, so a dead end says so instead. Total by construction — the last arm needs no condition,
        // because "neither action resolves" is the only case left.
        if (HasBark && IsPlayerAtAxisSide(byPlayer) && DebarkProductResolves()) NotifyDebarkToolNeeded(clientApi);
        else if (CanProduceAnySaw(Api.World, heldLog)) NotifySawToolNeeded(clientApi); // any product possible in SOME mode → fetch a saw
        else clientApi.TriggerIngameError(this, "sawnouse", Lang.Get("immersivewoodworking:ingameerror-sawhorse-nouse"));
    }

    /// <summary>Client-side nudge for the ONE put refusal whose reason a player cannot see: the pair is finished and
    /// EMPTY, the held item IS an accepted input, and it was still turned away because this pair could do nothing
    /// whatever with it (<see cref="CanProcessInput"/> — neither its plank/beam nor its debarked form exists,
    /// AuditFixPlan T03.03). Every OTHER <see cref="TryPutLog"/> refusal is either self-evident (a single or half-built
    /// trestle, an already-occupied pair, something that isn't a log in hand) or belongs to the build step the caller
    /// falls through to, so those stay silent exactly as before. Routes to the master (the pair state and the gate both
    /// read the master's); no-op on the server, like <see cref="NotifyInteractBlocked"/>.</summary>
    public void NotifyPutBlocked(ItemSlot? fromSlot)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.NotifyPutBlocked(fromSlot); return; }

        if (Api is not ICoreClientAPI clientApi) return;      // server: silent, as every gate is there
        if (!inPair || !isMaster || heldLog != null) return;  // not the "finished, empty pair" case — say nothing

        ItemStack? stack = fromSlot?.Itemstack;
        if (stack == null || !IsSawableInput(stack)) return;  // not an input at all — that click is the build step's
        if (CanProcessInput(Api.World, stack)) return;        // it WAS acceptable; the put failed for another reason

        clientApi.TriggerIngameError(this, "sawnouse", Lang.Get("immersivewoodworking:ingameerror-sawhorse-nouse"));
    }

    /// <summary>The "wrong tool for the rip" nudge — names the saw the config actually accepts: "a saw" (needsaw)
    /// when ANY ordinary saw rips here (<see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> on, OR
    /// <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> has made ordinary saws the only tool), otherwise "a pit
    /// saw" (needpitsaw). Same error code either way — it's the one "need a saw" condition; the flags only swap the
    /// sentence (so a held repeat coalesces). Mirrors <see cref="SawIsEffective"/>; under RemovePitSaw a player
    /// holding a now-useless pit saw is correctly told to use an ordinary saw.</summary>
    void NotifySawToolNeeded(ICoreClientAPI clientApi)
    {
        string msg = (Config.RemovePitSaw || Config.AllowVanillaSaw)
            ? Lang.Get("immersivewoodworking:ingameerror-sawhorse-needsaw")
            : Lang.Get("immersivewoodworking:ingameerror-sawhorse-needpitsaw");
        clientApi.TriggerIngameError(this, "sawneedtool", msg);
    }

    /// <summary>The "wrong tool for the strip" nudge — names the debark tools, listing the knife only when
    /// <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> admits it (mirroring how <see cref="NotifySawToolNeeded"/>
    /// adapts to the saw config and how <see cref="BlockSawhorse.DebarkSingleHandStacks"/> shows/hides the knife icon).
    /// The bark spud and the axe+offhand-hammer tier are always available, so they are always named.</summary>
    void NotifyDebarkToolNeeded(ICoreClientAPI clientApi)
    {
        string key = Config.AllowKnifeDebark
            ? "immersivewoodworking:ingameerror-sawhorse-needdebarktool"
            : "immersivewoodworking:ingameerror-sawhorse-needdebarktoolnoknife";
        clientApi.TriggerIngameError(this, "debarkneedtool", Lang.Get(key));
    }

    /// <summary>The hold record for the end <paramref name="uid"/> currently owns, or null if they man none. A player
    /// owns at most one end. Server-side.</summary>
    SawyerHold? SawyerFor(string uid)
    {
        foreach (SawyerHold h in sawEnds.Values) if (h.Uid == uid) return h;
        return null;
    }

    /// <summary>Free whatever end <paramref name="uid"/> owns (a player owns at most one); returns true if one was
    /// freed — used to decide whether to persist the progress accumulated before the hold ended. Server-side.</summary>
    bool ReleaseSawyer(string uid)
    {
        int? key = null;
        foreach (var kv in sawEnds) if (kv.Value.Uid == uid) { key = kv.Key; break; }
        if (key == null) return false;
        sawEnds.Remove(key.Value);
        return true;
    }

    /// <summary>Drop any co-op end hold whose player is no longer online — the rip counterpart to
    /// <see cref="PruneOfflineDebarkers"/>. There is no <c>PlayerDisconnect</c> hook, so a sawyer who drops mid-rip
    /// would otherwise leave a ghost <see cref="sawEnds"/> entry that blocks that end forever
    /// (<see cref="ClaimOrVerifyEnd"/> refuses a new player on it) and pins a restock breather open
    /// (<see cref="AbandonPendingRestockIfUnmanned"/> never reaches Count 0). Called before either acts on the map —
    /// and, since AuditFixPlan T03.04, before the START gate reads it too (<see cref="TryStartSawing"/>, F-L5-25),
    /// which had been trusting the raw map while every mid-hold reader weeded it first.
    /// Server-side. (If EVERY sawyer is gone there is no live tick to run this; the stale state self-heals on the next
    /// interaction via <see cref="ResetSawState"/>, and the breather it pinned open now ages out on its own clock —
    /// <see cref="RestockPauseIsStale"/>.)</summary>
    void PruneOfflineSawyers()
    {
        if (sawEnds.Count == 0) return;
        List<int>? dead = null;
        foreach (var kv in sawEnds)
            if (Api.World.PlayerByUid(kv.Value.Uid) == null) (dead ??= new List<int>()).Add(kv.Key);
        if (dead != null) foreach (int k in dead) sawEnds.Remove(k);
    }

    /// <summary>Ensure <paramref name="uid"/> owns the end at <paramref name="endSign"/> — confirm the one they
    /// already hold, or claim a free one (giving up any other end first, since a player owns at most one). Returns the
    /// hold, or null when that end is manned by ANOTHER sawyer (the caller refuses — no same-end stacking). A freshly
    /// claimed end baselines its timer to <paramref name="secondsUsed"/> — 0 delta this tick, contributing from the
    /// next one. That is the MID-hold case (the player stepped across to the other end, or resumed after an INSTANT
    /// restock — the zero-delay path, whose <see cref="ResetSawState"/> empties the map; the BREATHER path keeps
    /// <see cref="sawEnds"/>, so a hold coming out of one is confirmed here, not re-claimed):
    /// the hold has been running for a while and only the time from HERE on is this end's work, so the
    /// seconds already elapsed must not land on it. A hold that begins at the end (<see cref="TryStartSawing"/>)
    /// baselines to 0 instead, because there t=0 really is the press. Server-side.</summary>
    SawyerHold? ClaimOrVerifyEnd(string uid, int endSign, float secondsUsed)
    {
        PruneOfflineSawyers(); // drop ghost holds from disconnected players first, so a live player can claim/reclaim an end
        if (sawEnds.TryGetValue(endSign, out SawyerHold? held))
        {
            return held.Uid == uid ? held : null; // ours → use it; another sawyer's → refuse
        }
        ReleaseSawyer(uid);                        // give up any other end first (one end per player)
        held = new SawyerHold(uid, secondsUsed);   // baseline = now → 0 delta this tick, contributes next
        sawEnds[endSign] = held;
        return held;
    }

    /// <summary>Axis-frame sign of the END the player stands at: +1 toward +<see cref="pairAxis"/>, -1 toward
    /// -<see cref="pairAxis"/>. The horizontal offset from the pair centre along the log axis, reduced to its sign —
    /// the gate (<see cref="IsPlayerAtAxisEnd"/>) already guarantees the player IS at an end. Master-relative.</summary>
    int PlayerEndSign(IPlayer byPlayer)
    {
        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);
        double along = pairAxis == EnumAxis.X ? (byPlayer.Entity.Pos.X - cx) : (byPlayer.Entity.Pos.Z - cz);
        return along >= 0 ? 1 : -1;
    }

    /// <summary>
    /// Whether a click at world point <paramref name="worldHit"/> (blockSel.FullPosition) fell on the REVERSED
    /// across-the-log side — i.e. the side OPPOSITE the default "forward" one — for directional sawing
    /// (<see cref="sawCutReversed"/>). "Forward" is the part_1 side the boards already drop toward, anchored to
    /// <see cref="SpawnSawnItem"/>'s in-game-confirmed eject unit (a Z/N–S pair ejects toward +X, an X/E–W pair
    /// toward −Z): we project the click's offset from the log centre-line onto that forward perp direction, and a
    /// NEGATIVE projection means the player aimed at the far side → reverse. Because the SAME
    /// <see cref="sawCutReversed"/> flag flips both the eject and the visual hide order, "click a side → boards +
    /// cut on that side" stays self-consistent no matter the orientation; the worst a mis-signed convention could do
    /// is invert the control (click right → left), a one-line flip here. The perp axis is across the log (X for a
    /// Z-pair, Z for an X-pair); the centre-line is the master cell centre (the log is one cell wide, so no +½
    /// boundary nudge on the perp axis — that nudge is along the LENGTH only). Master-relative (call on the master;
    /// <see cref="pairAxis"/> / <see cref="Pos"/> are the master's).
    /// </summary>
    bool HitIsReversedSide(Vec3d worldHit)
    {
        double fwdPerpX = pairAxis == EnumAxis.Z ? 1.0 : 0.0;  // forward (part_1 / default-eject) perp unit, world
        double fwdPerpZ = pairAxis == EnumAxis.X ? -1.0 : 0.0;
        double dx = worldHit.X - (Pos.X + 0.5);
        double dz = worldHit.Z - (Pos.Z + 0.5);
        return dx * fwdPerpX + dz * fwdPerpZ < 0.0; // aimed at the side opposite the default forward one → reverse
    }

    // Sawing-stage thresholds as fractions of SawSeconds — coupled to the 20-model transitions 5→6, 14→15, 19→20,
    // 20→∅ (T06.07 / PLAN.md «Ключевые решения»), NOT equal time slices. The 4 stages each fire a yield batch +
    // saw wear (T06.03 / T06.05); the last two thresholds are 1/20 apart, so a fast hold crosses them almost
    // together — by design (the "twice quickly at the end"). Const set under the 20-model decision.
    static readonly double[] StageFractions = { 5.0 / 20.0, 14.0 / 20.0, 19.0 / 20.0, 1.0 };

    /// <summary>How many stage thresholds <paramref name="progress"/> (seconds) has reached, 0..4. Because
    /// <see cref="sawProgress"/> only ever increases, this is the count of stages already completed at that
    /// progress; the persisted <see cref="sawStagesFired"/> drives fire-once in <see cref="AdvanceSaw"/>.</summary>
    static int StagesPassed(float progress, float sawSeconds)
    {
        int n = 0;
        foreach (double f in StageFractions) if (progress >= f * sawSeconds) n++;
        return n;
    }

    /// <summary>
    /// One tick of an active co-op sawing hold, from <see cref="BlockSawhorse.OnBlockInteractStep"/> (fired per
    /// player, per frame). Re-checks the gate (<see cref="CanSawHere"/>) and, if it broke (tool away / stepped off the
    /// end / log gone), frees this player's end and returns false to end their hold. While valid it adds THIS player's
    /// per-tick elapsed DELTA to the master's <see cref="sawProgress"/> via <see cref="AdvanceSaw"/> — so two
    /// opposite-end sawyers each contribute and the rip runs at 2×, while a player whose end is held by ANOTHER sawyer
    /// contributes nothing (returns false; no same-end stacking). Routes to the master. Server-authoritative: the
    /// client re-gates and keeps the hold alive (returns true) but never mutates. Returns true to keep holding (never
    /// ends the interaction AT a threshold — that would restart the wind-up, as in BlockChoppingBlock).
    /// <para><paramref name="secondsUsed"/> must be the HONEST hold clock, not the engine's raw argument: the caller
    /// clamps it through <see cref="ImmersiveWoodworkingModSystem.HonestHoldSeconds"/> (AuditFixPlan T02.03). The raw
    /// one is inflated by the frames the server replays per hold packet, and since the deltas below ACCUMULATE, that
    /// inflation lands in <see cref="sawProgress"/> on every burst (F-L4-04).</para>
    /// </summary>
    public bool ContinueSawing(IPlayer byPlayer, ItemSlot? toolSlot, float secondsUsed)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) return master.ContinueSawing(byPlayer, toolSlot, secondsUsed);

        string uid = byPlayer.PlayerUID;

        // RESTOCK BREATHER (AutoRestock + SawRestockDelaySeconds): the previous log was fully ripped and the pair is
        // paused EMPTY (ConsumeLog set this) before the next log is laid. Keep the held rip ALIVE while the sawyer
        // still holds an effective saw at an end, but advance NO progress (no log yet); when the breather elapses the
        // server lays the next log and the rip resumes. Checked BEFORE CanSawHere, which would fail on the empty pair.
        if (restockPending && !RestockPauseIsStale())
        {
            if (!CanSawHereIgnoringLog(byPlayer, toolSlot)) // bailed mid-pause (tool away / stepped off the end)
            {
                if (ReleaseSawyer(uid) && Api.Side == EnumAppSide.Server) { AbandonPendingRestockIfUnmanned(); MarkDirty(true); }
                return false;
            }
            if (Api.Side != EnumAppSide.Server) return true; // client just keeps the hold alive; the server owns the timing

            // OWNER-ONLY (AuditFixPlan T03.04, F-L5-04): the breather is the middle of somebody's HOLD, never an entry
            // point — so this rides an end the player ALREADY mans (SawyerFor) instead of claiming one (ClaimOrVerifyEnd).
            // The claim was the hole: plain RMB on the empty pair is swallowed by the put/build path, which returns true,
            // so the engine drives OnBlockInteractStep for a passer-by too — with a saw in hand at an end they landed
            // here, claimed a free end, and FinishPendingRestock (its ready-time already past) pulled a log out of THEIR
            // pack onto a station they never touched. A player mid-pause can still walk round to the other end; the
            // switch simply lands on the resumed rip's own ClaimOrVerifyEnd next tick. Nobody can JOIN a paused pair,
            // which matches TryStartSawing — it already refuses on an empty pair (there is no log to gate on).
            SawyerHold? pausing = SawyerFor(uid);
            if (pausing == null)                       // not one of the sawyers whose hold entered this pause
            {
                DbgRestockPause("rip", "refused-nonowner", byPlayer, Api.World.ElapsedMilliseconds - restockTickedAtMs);
                return false;
            }
            pausing.LastSecondsUsed = secondsUsed;     // consume the elapsed as PAUSE, not cutting → resumed rip starts at ~0 delta
            restockTickedAtMs = Api.World.ElapsedMilliseconds; // freshness stamp: this pause still has a live owner

            if (Api.World.ElapsedMilliseconds >= restockReadyAtMs) FinishPendingRestock();
            return true;
        }

        // A pause nobody has ticked for RestockOwnerTimeoutMs is ABANDONED — its owner left by a route that owes us no
        // Stop (disconnect above all), so no later tick would ever finish or clear it. Drop it HERE, on the first tick
        // that reaches it, and fall through: with no log laid the ordinary gate below refuses and this hold ends
        // cleanly, which is exactly what should happen to a saw waved at an empty pair. (AuditFixPlan T03.04.)
        if (restockPending && Api.Side == EnumAppSide.Server)
        {
            DbgRestockPause("rip", "abandoned-stale", byPlayer, Api.World.ElapsedMilliseconds - restockTickedAtMs);
            AbandonPendingRestock();
            PruneOfflineSawyers(); // the same disconnect left a ghost end hold — drop it with the pause it pinned open
            MarkDirty(true);       // the client's synced copy of the flag clears with it
        }

        // Gate broke → this player stops sawing: free their end and persist what they accumulated before bowing out
        // (Step only MarkDirty's at stage crossings, so a partial section would otherwise not be saved).
        if (!CanSawHere(byPlayer, toolSlot))
        {
            if (ReleaseSawyer(uid) && Api.Side == EnumAppSide.Server) MarkDirty(true);
            return false;
        }

        if (Api.Side != EnumAppSide.Server) return true; // client keeps the hold alive; the server owns the progress

        SawyerHold? held = ClaimOrVerifyEnd(uid, PlayerEndSign(byPlayer), secondsUsed);
        if (held == null)
        {
            // This end is manned by ANOTHER sawyer — no same-end stacking. The ONE place the rip reports it
            // (AuditFixPlan T06.04): this player's client is running an optimistic hold it cannot judge, so tell it to
            // drop it. Reached both by a press at an already-manned end (the start gate refused the claim, and the
            // owner still holds it a step later) and by a sawyer who walked round to the other end mid-hold.
            RefuseRipHold(byPlayer);
            return false;
        }

        float delta = secondsUsed - held.LastSecondsUsed;
        held.LastSecondsUsed = secondsUsed;
        // Scale the advance by the tool's speed (pit saw ×1, an ordinary saw by VanillaSawSpeedMultiplier so it rips
        // slower). The gate above (CanSawHere → SawIsEffective) already guaranteed an effective saw, so an ordinary
        // saw while AllowVanillaSaw is off never reaches here — it was refused at start; the SawIsEffective re-check
        // is a defensive belt-and-suspenders. Each manned end advances by its OWN scaled elapsed → opposite ends sum
        // (a pit-saw end and a slower ordinary-saw end contribute 1 + mult).
        ItemStack? tool = toolSlot?.Itemstack;
        // swiftsaw (xSkills, T04.03): scale the rip TEMPO by this end-owner's own swiftsaw factor (×1.20 cap inside, ×1
        // without xSkills). Server-authoritative path — the client never advances progress, so there's no independent
        // client scaling to desync. The saw animation/sound loop is untouched (its baked-in frame sound would drift).
        if (delta > 0f && SawIsEffective(tool)) AdvanceSaw(delta * SawSpeedFor(tool) * SwiftsawSawFactor(byPlayer), byPlayer);
        return true;
    }

    // The rip is split into SAWDUST SECTIONS for the particles: the wood "drops planks" at the StageFractions
    // thresholds { 5/20, 14/20, 19/20, 1 } of SawSeconds, and the spec treats each plank-drop as the end of a cut
    // section ("planks fell → a new cut began"). The final two drops (19/20 & 20/20) are ~1/20 apart, so they're
    // MERGED into one section here (user choice) → three sections ending at { 0.25, 0.70, 1.0 } of the rip. Keep in
    // sync with StageFractions by hand: this is the merged VIEW of it, not the yield logic itself.
    //
    // SIZED BY THE GENERATOR (AuditFixPlan T06.05 / F-L1-08): SawdustSectionCount comes out of SawdustPoints.cs,
    // which tools/apply-sawdust-points.ps1 writes from its own $Front/$Under tables — the ONE place the section
    // count is authored. The explicit array size makes this a COMPILE error rather than an EmitSawDust index
    // out of range: add a fourth section's points there and this initializer no longer fits.
    static readonly double[] SawdustSectionEnds = new double[SawdustSectionCount] { 5.0 / 20.0, 14.0 / 20.0, 1.0 };

    /// <summary>
    /// Throw sawdust off the cut while ripping (T06.08), coloured from the held log's own texture so it matches the
    /// species (<see cref="Block.GetRandomColor"/>, as <c>ItemAxe</c> does for tree-chopping chips). This is the
    /// SAWYER'S OWN copy, drawn locally; everyone else gets <see cref="BroadcastSawDust"/> from the
    /// server. Purely cosmetic: <see cref="BlockSawhorse.OnBlockInteractStep"/> calls this only while a GENUINE rip is
    /// in progress, so it inherits the T06.01 gates (no log / not a pit saw / not at an end → no dust) for free. Routes
    /// to the master — only it holds the log, the pair geometry and the emission bank; a slave proxies.
    ///
    /// <para>Emitted at a RATE, not per call (AuditFixPlan T06.02 / F-L10-07): <see cref="FxRateBudget"/> converts
    /// this frame's slice of the hold clock into a whole-particle count at
    /// <see cref="SawDustPerEmitterPerSecond"/>, so 30 fps and 144 fps throw the same dust per second. The budget is
    /// taken BEFORE the log check on purpose — during the auto-restock breather the pair is momentarily empty, and
    /// running the clock through those frames is what stops the resumed rip from opening with a banked clump.</para>
    ///
    /// TWO moving emitters per call, one per <see cref="SawdustSectionEnds">cut section</see>:
    ///  • the FRONT emitter (in front of the player at the cut — its END follows the player's CURRENT end), and
    ///  • the UNDER emitter (beneath the log).
    /// Each interpolates its spawn point from a per-section START to a per-section END as the section's local time
    /// runs 0→1 (so successive flakes trace the path; each flake then falls under gravity). All start/end points are
    /// authored in the axis frame (along / perp / up) in <see cref="FrontStart"/> etc. — generated and tuned by
    /// tools/apply-sawdust-points.ps1 — and mapped to world by <see cref="AxisToWorld"/> using the player's CURRENT
    /// end (<see cref="PlayerEndSign"/>, live each tick) for the ALONG direction only; PERP is the log's, not the
    /// player's, and turns only with the pair axis and the cut direction (<see cref="sawCutReversed"/>). So the front
    /// burst follows the sawyer to whichever end they stand at, while the cut's side stays where the cut is. No-op on
    /// a slave whose master can't be resolved (heldLog null).
    /// </summary>
    public void SpawnSawDust(IPlayer byPlayer, float secondsUsed)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.SpawnSawDust(byPlayer, secondsUsed); return; }
        if (capi == null) return; // client only

        int perEmitter = FxRateBudget(ref sawDustAccum, ref sawDustLastSeconds, secondsUsed, SawDustPerEmitterPerSecond);
        if (perEmitter <= 0 || heldLog?.Block == null) return; // nothing due this frame, or the pair holds no log
        if (sawProgress <= 0f) return;                         // see EmitSawDust for why nothing may fire at 0

        int color = heldLog.Block.GetRandomColor(capi, Pos, BlockFacing.UP);
        SawDust.Color = color | (255 << 24); // GetRandomColor returns no alpha byte — force opaque (mirrors ItemAxe)
        SawDust.MinQuantity = perEmitter;     // this frame's exact count per emitter …
        SawDust.AddQuantity = 0f;             // … and no second roll on top of it — the accumulator owns the randomness
        EmitSawDust(SawDust, byPlayer, null); // local: the client overload ignores dualCallByPlayer, so it stays null
    }

    // This client's own sawdust bank: the sub-particle remainder carried between frames and the hold clock the last
    // frame was charged at (AuditFixPlan T06.02). Client-side, transient, and read only on the MASTER — SpawnSawDust
    // routes there before touching them. One pair of fields is enough because the local half of the effect only ever
    // runs for the player at this keyboard: the engine calls the client half of OnBlockInteractStep for the local
    // player alone, so a co-op partner's dust arrives through the server broadcast, which keeps its own stamps.
    float sawDustAccum;
    float sawDustLastSeconds;

    /// <summary>
    /// The same two dust emitters, sent from the SERVER to every nearby player EXCEPT the sawyer — the half that was
    /// missing entirely (AuditFixPlan T06.01, F-L4-13): a bystander saw the FK saw animation, heard the frame-locked
    /// hit clips and watched the log lose sections, with not one flake in the air. <paramref name="byPlayer"/> goes in
    /// as dualCallByPlayer precisely because their OWN client already drew this at frame rate in
    /// <see cref="SpawnSawDust"/>, so the broadcast must skip them or they would see double.
    ///
    /// <para>Throttled per hold (<see cref="SawyerHold.LastFxMs"/>, one stamp per manned end) so two opposite-end
    /// sawyers each keep their own stream and neither starves the other, and so a replayed packet burst collapses to a
    /// single packet. Rides the end the player ALREADY mans (<see cref="SawyerFor"/>) rather than claiming one: a
    /// cosmetic path must never author co-op state. Routing through <see cref="GetMaster"/> introduces no new server
    /// mutation either — <see cref="ContinueSawing"/> ran the same resolve one line earlier in the caller.</para>
    /// </summary>
    public void BroadcastSawDust(IPlayer byPlayer)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.BroadcastSawDust(byPlayer); return; }
        if (Api.Side != EnumAppSide.Server || heldLog?.Block == null) return; // server only; the restock breather holds no log
        if (sawProgress <= 0f) return;                                        // see EmitSawDust — both callers gate on it, as the strip's do

        SawyerHold? held = SawyerFor(byPlayer.PlayerUID); // the end this sawyer already mans; never claim one here
        if (held == null || !FxBroadcastDue(ref held.LastFxMs)) return;

        SawDustNet.ColorByBlock = heldLog.Block; // travels as a BlockId; the RECEIVER resolves it against its own atlas
        EmitSawDust(SawDustNet, byPlayer, byPlayer);
    }

    /// <summary>Place and fire this call's two dust emitters into <paramref name="props"/> — the shared body of the
    /// local spawn and the observer broadcast, so the two can never drift in geometry. The template carries the
    /// colour/velocity/gravity and the caller owns which one it is; <paramref name="dualCallByPlayer"/> is null for a
    /// local spawn and the worker for a broadcast.</summary>
    void EmitSawDust(SimpleParticleProperties props, IPlayer byPlayer, IPlayer? dualCallByPlayer)
    {
        // Nothing fires until the rip has actually advanced (sawProgress > 0; both callers gate on it), the same gate
        // and for the same reason as the strip's EmitBarkChips. Beyond being physically right (no wood has been cut at
        // 0), it closes the cosmetic multiplayer glitch that the directional perp (AuditFixPlan T06.05) would otherwise
        // open: sawCutReversed is server-authoritative, set in TryStartSawing and synced with that first MarkDirty,
        // while the client runs SpawnSawDust from frame 1 of the hold (its ContinueSawing returns true without
        // advancing progress). Emitting at 0 would mirror the frame off the STALE flag and throw the first flakes at
        // the far edge of the log for ~1 RTT — on a fresh log clicked from the side opposite the pair's last rip.

        // Rip fraction 0→1 → which section we're in, and the section-local time t (0 at its start drop, 1 at its end).
        float frac = SawSeconds > 0f ? GameMath.Clamp(sawProgress / SawSeconds, 0f, 1f) : 0f;
        int sec = 0;
        while (sec < SawdustSectionEnds.Length - 1 && frac >= SawdustSectionEnds[sec]) sec++;
        double secStart = sec == 0 ? 0.0 : SawdustSectionEnds[sec - 1];
        double secEnd = SawdustSectionEnds[sec];
        double t = secEnd > secStart ? GameMath.Clamp((frac - secStart) / (secEnd - secStart), 0.0, 1.0) : 0.0;

        int endSign = PlayerEndSign(byPlayer); // LIVE: the end the sawyer stands at NOW → the ALONG direction follows
        SpawnDustAt(props, AxisToWorld(LerpPoint(FrontStart[sec], FrontEnd[sec], t), endSign), dualCallByPlayer); // front (at the player's end)
        SpawnDustAt(props, AxisToWorld(LerpPoint(UnderStart[sec], UnderEnd[sec], t), endSign), dualCallByPlayer); // under (beneath the log)
    }

    /// <summary>Spawn one small sawdust puff centred on the world point <paramref name="w"/> (a little position
    /// jitter so a held cut doesn't stack flakes on one pixel). The <paramref name="props"/> template carries the
    /// colour/velocity/gravity; we only move it here.</summary>
    void SpawnDustAt(SimpleParticleProperties props, Vec3d w, IPlayer? dualCallByPlayer)
    {
        props.MinPos.Set(w.X - 0.05, w.Y - 0.05, w.Z - 0.05);
        props.AddPos.Set(0.10, 0.10, 0.10);
        Api.World.SpawnParticles(props, dualCallByPlayer);
    }

    /// <summary>Linear interpolate an axis-frame point (along, perp, up) from <paramref name="a"/> to
    /// <paramref name="b"/> by <paramref name="t"/> ∈ [0,1].</summary>
    static Vec3d LerpPoint(Vec3d a, Vec3d b, double t)
        => new Vec3d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>
    /// Map an axis-frame sawdust point to world. <paramref name="endSign"/> = the end the sawyer CURRENTLY stands at
    /// (<see cref="PlayerEndSign"/>; +1 toward +<see cref="pairAxis"/>, −1 toward −): it drives the ALONG direction
    /// only, so the front burst tracks the player live, while PERP is tied to the LOG (the kerf is on one physical
    /// side and must not mirror to the player's hand):
    ///  • <c>p.X = along</c> — along the log from the pair centre: + AWAY from the player (toward the far end), −
    ///    toward them. Centre 0; the billet spans roughly ±<see cref="DefaultLogBox"/>.MaxZ ≈ ±0.81. DYNAMIC (live end).
    ///  • <c>p.Y = perp</c>  — sideways ACROSS the log, tied to the pair axis (+ = −X for a Z/N–S log, +Z for an
    ///    X/E–W log; 0 = centre-line; half-width ≈ 0.35). The per-orientation sign mirrors the laid-log mesh's +90°
    ///    turn (applied only to the X pair, see <see cref="PlaceLogMesh"/>) so the dust's kerf side stays ON the
    ///    model's real cut for BOTH orientations. NOT player-relative: from the sawyer's view it reads as the
    ///    opposite hand when they move to the other end — correct, because the cut stays on a fixed side of the log.
    ///  • <c>p.Z = up</c>    — height above the block bottom (<see cref="Pos"/>.Y); the laid model spans ≈ 0.58 … 1.46.
    /// </summary>
    Vec3d AxisToWorld(Vec3d p, int endSign)
    {
        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);

        double aSign = -endSign;                            // +along points away from the player's CURRENT end (live)
        double adx = pairAxis == EnumAxis.X ? aSign : 0.0;  // along-axis unit (world) — dynamic, follows the sawyer
        double adz = pairAxis == EnumAxis.Z ? aSign : 0.0;
        // perp unit (world) — across the log, on the KERF side. Two independent signs multiply into it.
        //
        // (1) ORIENTATION. The −X for a Z/N–S pair (vs +Z for an X/E–W pair) is NOT a symmetry break for its own
        // sake: the laid-log MESH is turned +90° about Y only for the X pair (PlaceLogMesh / RebuildStageMesh), so
        // the model's cut side maps to a different world axis per orientation. The sawdust frame carries the SAME
        // authored perp numbers for both orientations, so to keep the dust's cut side ON the model's actual kerf the
        // Z branch must mirror that +90° — i.e. perp → −X for Z, +Z for X. With +X here instead, the N–S dust landed
        // on the mirror side (the cut's three sections read swapped: the centre section right, the two outer ones on
        // the wrong sides). Sign confirmed in-game against both orientations.
        //
        // (2) CUT DIRECTION (AuditFixPlan T06.05 / F-L9-11). The rip walks THREE kerf planes across the log —
        // sawing_1..4 at +0.24 from the log axis, sawing_5..12 at 0, sawing_13..16 at −0.23 (sawhorse_log.json, read
        // after its −45° roll about Z) — and the boards outside each kerf drop as it completes. A reversed cut walks
        // them the OTHER way: HideSequenceReversed blanks sawing_13..16 first and drops part_4 first, and
        // SpawnSawnItem's sideSign ejects to the matching side. The authored points describe the FORWARD walk
        // (+0.24 → 0 → −0.23), so a reversed rip needs the whole frame mirrored across the centre-line — which is
        // exactly what negating this unit does, turning the walk into −0.23 → 0 → +0.24. Without it the dust for the
        // first and last sections sat on the wrong side of the log in a reversed rip: the boards fell from one edge
        // while the dust flew off the other. Synced to the client (FromTreeAttributes), and this whole path is
        // cosmetic and client/broadcast-only, so reading the flag here costs nothing.
        double revSign = sawCutReversed ? -1.0 : 1.0;
        double pdx = (pairAxis == EnumAxis.Z ? -1.0 : 0.0) * revSign;
        double pdz = (pairAxis == EnumAxis.X ? 1.0 : 0.0) * revSign;

        return new Vec3d(
            cx + adx * p.X + pdx * p.Y,
            Pos.Y + p.Z,
            cz + adz * p.X + pdz * p.Y);
    }

    /// <summary>End of ONE player's sawing hold (release / look-away / slot switch), from
    /// <see cref="BlockSawhorse.OnBlockInteractStop"/> / <c>OnBlockInteractCancel</c>. Finalises that player's last
    /// partial tick (charged while their end is still owned, so their saw is still wear-eligible), frees their end,
    /// and persists the progress. The tick really is partial since T02.03: the caller clamps the engine's replayed
    /// <c>secondsUsed</c> to the real hold time first, so this charges only the sliver between the last step and the
    /// release, not that sliver plus the up-to-0.16 s of frames the engine re-ran on its way to Stop.
    /// No-ops for a non-sawyer — the engine also fires Stop after a put/build/take, and for
    /// a second player refused at a manned end, neither of which owns an end. Routes to the master; server-
    /// authoritative. Idempotent: once the end is freed a second Stop/Cancel finds nothing.</summary>
    public void StopSawing(IPlayer byPlayer, float secondsUsed)
    {
        BlockEntitySawhorse master = GetMaster();
        if (master != this) { master.StopSawing(byPlayer, secondsUsed); return; }
        if (Api.Side != EnumAppSide.Server) return; // the server owns progress; the client has nothing to finalise

        string uid = byPlayer.PlayerUID;
        SawyerHold? held = SawyerFor(uid);
        if (held == null) return; // not a genuine saw hold (put/build/take, or a refused same-end second sawyer)

        // Charge the final partial tick, scaled by the tool's speed (pit saw ×1, an allowed ordinary saw by its
        // reduced rate) — same factor as ContinueSawing. An ordinary saw while AllowVanillaSaw is off never owned an
        // end (the start gate refused it), so `held` is null above and we already returned; the SawIsEffective
        // re-check stays as a defensive guard. Resolve the tool from the player's active slot (Stop carries no
        // toolSlot), mirroring DamageActiveSaws.
        ItemStack? tool = byPlayer.InventoryManager.ActiveHotbarSlot?.Itemstack;
        float delta = secondsUsed - held.LastSecondsUsed;
        // Same swiftsaw rip-tempo scale as ContinueSawing (T04.03): this player's own factor charges their final partial tick.
        if (delta > 0f && heldLog != null && SawIsEffective(tool)) AdvanceSaw(delta * SawSpeedFor(tool) * SwiftsawSawFactor(byPlayer), byPlayer); // charge runs here, while this end is still owned
        ReleaseSawyer(uid);                                   // then free it (no-op if AdvanceSaw consumed the log)
        AbandonPendingRestockIfUnmanned();                    // last sawyer let go mid-breather → end the pause (no next log)
        // Persist + sync the final progress. DATA-ONLY (AuditFixPlan T05.04 / F-L10-15): a release lands wherever the
        // rip stopped, which is mid-bucket almost every time, so the 20-state model the client shows is the one it
        // already shows. On the rare release that DID cross a boundary, the AdvanceSaw above already synced it with a
        // redraw. The interaction hints don't need one either: RefreshInteractionHelpOnChange runs off this very tree
        // resend and fires its own client-side MarkBlockDirty when the signature moved.
        MarkDirty();
    }

    /// <summary>
    /// Advance the master's <see cref="sawProgress"/> by <paramref name="delta"/> seconds of cutting (clamped to
    /// <see cref="SawSeconds"/>, monotonic since delta ≥ 0) and fire each stage threshold newly crossed — exactly
    /// once each, in order, even when one advance jumps several (the count is the difference of
    /// <see cref="StagesPassed"/> before/after). Each call adds ONE sawyer's per-tick elapsed delta, so two
    /// opposite-end holds in the same tick sum to a 2× rip. Persists+syncs (<see cref="MarkDirty"/>) only when a
    /// stage fires, the shown 20-state model changes, the rip begins or the recovery grace
    /// (<see cref="LogRecoverable"/>) flips — never every tick. Every fired stage drops a yield batch and
    /// wears EACH active sawyer's pit saw (<see cref="CompleteSawStage"/> → <see cref="DamageActiveSaws"/>).
    /// Server-side, master-only (callers ensure both).
    /// </summary>
    /// <param name="byPlayer">The sawyer whose tick this is. Under <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/>
    /// every batch this advance fires (and the log's sawdust) drops at their feet, so in a two-man rip a section goes to
    /// whoever's stroke completed it.</param>
    void AdvanceSaw(float delta, IPlayer byPlayer)
    {
        if (delta <= 0f) return;
        float sawSeconds = SawSeconds;
        int prevVisual = VisualModelFromProgress(sawProgress); // shown 20-state model before this advance (T06.07)
        bool wasStarted = sawProgress > 0f;                    // SawStarted before this advance (drives the hint-flip sync below)
        bool wasRecoverable = LogRecoverable;                  // recovery grace before this advance (drives the take↔discard hint sync below)
        sawProgress = GameMath.Min(sawProgress + delta, sawSeconds); // delta ≥ 0 → monotonic, clamped to a full rip

        // Fire every stage from the serialized "done" count up to how many thresholds the progress now meets, each
        // exactly once (one advance can jump several — the last two thresholds are 1/20 apart). Using the
        // persisted counter (not a fresh StagesPassed(old) derivation) keeps fire-once robust across a live
        // SawSeconds change, and sawStagesFired only ever climbs, so a finished log can't be re-sawn into a second
        // batch even if SawSeconds is raised afterward.
        int now = StagesPassed(sawProgress, sawSeconds);
        bool fired = false;
        while (sawStagesFired < now)
        {
            // A throw inside the stage must not escape this loop. CompleteSawStage drops the batch first and only then
            // reaches code IW does not own: the saw's DamageItem (every collectible behaviour and Harmony patch on the
            // pit saw, Toolsmith's tinkered-tool wear among them) and the item-spawn hooks. The engine catches a throw
            // from OnBlockInteractStep, logs it and keeps the hold alive (a dedicated server kicks the player instead),
            // so the next hold packet came back here with sawStagesFired never raised: stage 0 dropped its boards again
            // on every packet while the client froze at its last sync, 20% (ModDB report, 2026-10-08).
            try
            {
                CompleteSawStage(sawStagesFired, byPlayer);
            }
            catch (System.Exception e)
            {
                LogStageThrowOnce(sawStagesFired, e);
            }
            sawStagesFired++;
            fired = true;
        }

        // All four stages done (model 20 → empty) → the log is fully ripped: its last batch already dropped in the
        // loop above, so retire the billet and reset to the empty-finished state, freeing the pair for a new log
        // (T06.06). Must run AFTER the loop — ConsumeLog → ResetSawState zeroes sawStagesFired, which the loop uses
        // as its stage index, so clearing mid-loop would re-fire stage 0.
        if (fired && heldLog != null && sawStagesFired >= StageFractions.Length) ConsumeLog(byPlayer);

        // Re-sync the moment the shown 20-state model changes (≤20×/cut at SawSeconds≥2, fewer when decimated) — NOT
        // every tick (DoD: no per-frame MarkDirty) — so the client swaps the log mesh in step with progress. A
        // consumed log resets to model 1, so
        // its visual also differs from prevVisual; `fired` is folded in defensively (a stage boundary is always a
        // model boundary). The client re-derives visualModel from the synced sawProgress — no extra field is sent.
        // Also sync the instant a rip BEGINS (sawProgress 0→>0): SawStarted flips the interaction hints (hides the
        // strip hint, turns Ctrl+RMB take→discard), and the help HUD doesn't poll — so without an explicit sync here
        // the flip would wait for the first visual-bucket boundary (up to ~0.3s at the default, longer when a short
        // rip holds the whole-log model). vs-interaction-help-no-poll.
        //
        // recoverFlip (AuditFixPlan T03.05, F-L4-07) — the SAME argument one boundary later, and the one that made the
        // note lie. The Ctrl+RMB hint reads LogRecoverable, whose edge sits at LogRecoverThreshold (default 25%), while
        // the syncs above land on the VISUAL grid (1/20 of the rip). Those grids only coincide when the threshold is a
        // multiple of 5% — configure 0.33 and the client keeps reading the pre-crossing progress until the next bucket,
        // so the hint still promises "take" over a log the server would already scrap. Forcing a sync exactly where the
        // grace flips detaches the hint from the visual grid entirely, so it switches on the configured value whatever
        // it is. Costs one extra tree resend per log per crossing (the flip is one-way while a log is worked, and
        // ConsumeLog's reset back to recoverable rides a tick that already has `fired` set).
        //
        // The two families of term earn DIFFERENT syncs (AuditFixPlan T05.04 / F-L10-15). `fired` and a changed
        // `newVisual` move the drawn log, so they keep redrawOnClient. `startedFlip` and `recoverFlip` move only what
        // the HINTS say — the mode lock and the take↔discard wording — and the hint path never wanted a redraw: it wants
        // the client's copy of SawStarted / LogRecoverable to be fresh, which a data-only resend delivers, whereupon
        // RefreshInteractionHelpOnChange (FromTreeAttributes) fires its own MarkBlockDirty on the cell the player is
        // actually looking at. Sending a section re-tesselation to every observer to repaint a log that did not move
        // was the cost; the hint boundaries are exactly where it never bought anything.
        int newVisual = heldLog != null ? VisualModelFromProgress(sawProgress) : 1;
        bool startedFlip = !wasStarted && sawProgress > 0f;
        bool recoverFlip = wasRecoverable != LogRecoverable;
        if (fired || newVisual != prevVisual) MarkDirty(true);
        else if (startedFlip || recoverFlip) MarkDirty();
    }

    // Transient: one report per loaded sawhorse. A foreign hook that throws on every DamageItem would otherwise
    // print a stack trace four times per log.
    bool stageThrowLogged;

    /// <summary>Report a sawing stage that threw (<see cref="AdvanceSaw"/> contains it and counts the stage anyway), once
    /// per sawhorse, with the mod prefix the engine's own catch would not add. Server-side.</summary>
    void LogStageThrowOnce(int stage, System.Exception e)
    {
        if (stageThrowLogged) return;
        stageThrowLogged = true;
        Api.Logger.Error("[immersivewoodworking] Sawhorse at {0}: sawing stage {1} threw. The stage is counted and the rip goes on; further throws on this sawhorse are not logged.", Pos, stage);
        Api.Logger.Error(e);
    }

    /// <summary>Clear the per-log sawing state (progress, the fired-stage counter, and any in-progress co-op holds) —
    /// called wherever the held log is placed fresh, taken, scrapped, released or consumed, so a new log always starts
    /// from a clean slate and the fields can never drift. Server-side callers; the progress fields serialize out on
    /// the next MarkDirty (<see cref="sawEnds"/> is transient and not serialized).</summary>
    void ResetSawState()
    {
        sawProgress = 0f;
        sawStagesFired = 0;
        sawEnds.Clear(); // any active co-op holds end with the log
        ResetDebarkState(); // a fresh / placed / taken / scrapped log resets its bark-strip state too (T01.06)
        // A fresh / placed / taken / scrapped log also ends any pending auto-restock breather, so a stale pause can
        // never hijack the next rip. (The pause path in ConsumeLog deliberately does NOT call ResetSawState — it sets
        // restockPending AFTER its own partial reset.)
        AbandonPendingRestock();
    }

    /// <summary>Clear the auto-restock breather outright — the flag plus its finished-log memo. The single place those
    /// two are dropped together, so no caller can clear one and leave the other. Does NOT touch <see cref="sawEnds"/>
    /// (the hold may well continue) and does NOT <c>MarkDirty</c> — the callers already do, at the point that suits
    /// them. Server-side.</summary>
    void AbandonPendingRestock()
    {
        restockPending = false;
        restockFinished = null;
    }

    /// <summary>Whether the pending breather has gone ABANDONED: no owner has ticked it for
    /// <see cref="RestockOwnerTimeoutMs"/> (AuditFixPlan T03.04, F-L5-04). A live hold refreshes the stamp every step,
    /// so this can only be true once the holds behind the pause genuinely stopped — the disconnect case, where the
    /// engine never calls Stop/Cancel and <see cref="AbandonPendingRestockIfUnmanned"/> therefore never runs.
    /// <para>False on the CLIENT by construction: the stamp is server-only, so a client would find every synced pause
    /// "stale" and drop the held rip on every breather — the very interaction <see cref="restockPending"/> is synced to
    /// keep alive. The client mirrors the server's decision through the flag instead.</para></summary>
    bool RestockPauseIsStale()
        => Api.Side == EnumAppSide.Server && Api.World.ElapsedMilliseconds - restockTickedAtMs > RestockOwnerTimeoutMs;

    /// <summary>Dev trace for the two things AuditFixPlan T03.04 added to the breather — a non-owner turned away, and an abandoned
    /// pause dropped. Both are INVISIBLE when they work (a log that stays in a pack, a flag nobody sees), and both need
    /// two players to reach, so this line is what makes a multiplayer session provable: without it a clean run and a
    /// scenario that never armed look identical. <paramref name="ageMs"/> is how long the pause had gone untouched, so
    /// the timeout can be read off the log rather than assumed. Server-side callers; stripped from Release, like every
    /// other <c>[IW-DBG]</c> line.</summary>
    [System.Diagnostics.Conditional("IW_DEBUG")]
    void DbgRestockPause(string kind, string evt, IPlayer? byPlayer, long ageMs)
    {
        Api.World.Logger.Notification("[IW-DBG] restockpause pos={0} kind={1} event={2} player={3} ageMs={4}",
            Pos, kind, evt, byPlayer?.PlayerName ?? "<none>", ageMs);
    }

    /// <summary>End an in-progress auto-restock breather if no sawyer is still holding — the held rip that would have
    /// laid the next log is gone, so no further tick would run to lay it or clear the flag. Covers the "everyone let go
    /// DURING the pause" case (the put/take/discard paths are covered by <see cref="ResetSawState"/>). Server-side.
    /// <para>This is the CLEAN exit, driven by a release the engine actually delivered; the dirty one — a hold that
    /// ended by disconnect and left a ghost end entry so <see cref="sawEnds"/> never reaches 0 — is caught by the
    /// staleness timeout instead (<see cref="RestockPauseIsStale"/>).</para></summary>
    void AbandonPendingRestockIfUnmanned()
    {
        PruneOfflineSawyers(); // a sawyer who disconnected mid-breather leaves a ghost hold — drop it so Count can reach 0
        if (restockPending && sawEnds.Count == 0) AbandonPendingRestock();
    }

    /// <summary>
    /// A sawing stage (0..3, the 4 yield batches) just completed — its 20-model-boundary threshold was crossed.
    /// <see cref="AdvanceSaw"/> owns the fire-exactly-once detection. Drops this stage's batch of the sawn product (a
    /// log → <c>plank-{wood}</c>, a debarked log → <c>supportbeam-{wood}</c>, same species), the per-log total yield
    /// split across the 4 stages "from the end" (<see cref="StageAmount"/>), each batch scattered into piles
    /// (<see cref="DropStageBatch"/>); then wears EACH active sawyer's pit saw for the section
    /// (<see cref="DamageActiveSaws"/>) — both saws in a two-man rip. Consuming the log once ALL stages have fired is
    /// handled in <see cref="AdvanceSaw"/> after the fire loop, not here — see <see cref="ConsumeLog"/>. Server-side,
    /// master-only.
    /// </summary>
    void CompleteSawStage(int stageIndex, IPlayer byPlayer)
    {
        (ItemStack template, int total, int dropCount)? product = GetSawProduct();
        if (product == null) return; // no log / unrecognised species / output collectible missing — nothing to drop

        int amount = StageAmount(product.Value.total, stageIndex);

        // sawyer (xSkills, T03.01): boost THIS section's batch by the BEST active sawyer's forestry yield fraction,
        // chance-based (XSkillsYield.ChanceScaled — floor + fractional +1), clamped so a pile still can't exceed max
        // stack. Applied PER SECTION, NOT once per log: each of the 4 batches is self-contained, so the bonus tracks
        // whoever is actually manning the rip as each section completes AND a mid-rip skill change (an unskilled start
        // joined or handed off to a skilled sawyer) can never desync the per-stage split from a shifting log total —
        // the failure a single cached per-log bonus had, where early stages dropped at the base total and later stages
        // at base+bonus, silently losing part of the bonus. Co-op rule: the best sawyer among the active end-owners
        // sets the section's bonus (the master holds the calc). No-op without xSkills / a Tier-0 sawyer
        // (ResolveSawyerYieldFraction returns 0) → amount stays exactly the vanilla StageAmount, not one unit different.
        float sawyerExtra = ResolveSawyerYieldFraction();
        if (sawyerExtra > 0f)
        {
            int pileCap = product.Value.dropCount * product.Value.template.Collectible.MaxStackSize;
            amount = System.Math.Min(amount + XSkillsYield.ChanceScaled(amount, sawyerExtra, Api.World.Rand), pileCap);
        }

        DropStageBatch(product.Value.template, amount, product.Value.dropCount, byPlayer);

        DamageActiveSaws(); // one completed section wears EACH active sawyer's pit saw by SawDurabilityPerStage

        // Forestry XP per completed section (xSkills, E02/T02.01), to every active sawyer — SEPARATE from the wear pass
        // above so it still accrues on a no-durability server (DamageActiveSaws early-returns when the cost is 0). No-op
        // without xSkills; co-op policy handled in GrantForestryXpToHolders.
        GrantForestryXpToHolders(sawEnds.Values, Config.XSkillsSawXpPerSection, "saw");

        // Seraph Leveling: the batch counts as crafted, as the grid "saw + log" recipe this rip replaces would have
        // (Carpenter wants 120 plank-*). Every sawyer on the log gets the full batch, sawyer bonus included: both
        // made these boards. No-op without Seraph; the facade skips a holder who is offline right now.
        ImmersiveWoodworkingModSystem mod = modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        foreach (SawyerHold held in sawEnds.Values)
            if (Api.World.PlayerByUid(held.Uid) is IPlayer sawyer)
                mod.ReportCraftToSeraph(sawyer, product.Value.template, amount);
    }

    /// <summary>
    /// Charge <see cref="ImmersiveWoodworkingConfig.SawDurabilityPerStage"/> durability to the saw of EVERY player
    /// currently manning an end, for one completed section — so a two-man rip wears BOTH saws (the cut is twice as
    /// fast, the wear is shared honestly). 4 sections per log, so a full solo rip costs 4× this (default 4). Resolves
    /// each end-owner by UID and damages their active hotbar slot only if it is still an EFFECTIVE saw
    /// (<see cref="SawIsEffective"/> — pit saw, or a vanilla saw when allowed; an ineffective vanilla saw and a
    /// mid-hold slot switch to a non-saw never wear); the engine removes a saw whose durability hits zero. No-ops on the 0 config
    /// (no wear). Progress lives in the BE (<see cref="sawProgress"/>) independent of the tool, so a saw that snaps
    /// mid-log loses nothing: a fresh saw resumes the cut. Server-side, master-only.
    /// </summary>
    void DamageActiveSaws()
    {
        int cost = Config.SawDurabilityPerStage;
        if (cost <= 0) return; // 0 = no durability cost (config Sanitize floors negatives to 0)

        foreach (SawyerHold held in sawEnds.Values)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            ItemSlot? slot = player?.InventoryManager.ActiveHotbarSlot;
            // Only an EFFECTIVE saw wears. A flag-off ordinary saw never owns an end (it's refused at the start
            // gate, so it's never in sawEnds), so the ineffective case this guard actually catches is a mid-hold
            // SLOT SWITCH — an end-owner who swapped to a non-saw, or to an ordinary saw after AllowVanillaSaw was
            // turned off mid-rip. Skips that, plus an offline player. (In a mixed co-op rip — one pit saw, one
            // allowed-but-slower ordinary saw — both are effective, so both wear.)
            if (slot?.Itemstack == null || !SawIsEffective(slot.Itemstack)) continue;

            // carefulsawyer (xSkills, T03.03): a chance to spend LESS (or no) durability this section, per THIS sawyer's
            // own forestry tier — so in a two-man rip each saw is spared by its own owner's ability. No-op without xSkills
            // (ApplyCarefulSawyer returns cost unchanged); a full save roll drops the wear to 0 and the DamageItem is skipped.
            int effectiveCost = ApplyCarefulSawyer(player!, cost);
            if (effectiveCost <= 0) continue;
            slot.Itemstack.Collectible.DamageItem(Api.World, player!.Entity, slot, effectiveCost);
            slot.MarkDirty();
        }
    }

    /// <summary>
    /// Reduce a tool-wear <paramref name="cost"/> by the player's carefulsawyer (xSkills, T03.03/T03.04) save — the same
    /// chance-based rounding as the yield bonuses (<see cref="XSkillsYield.ChanceScaled"/>, the carefulminer idiom): the
    /// whole part of <c>cost × saveFraction</c> is spared for certain, the leftover fraction a chance to spare one more.
    /// Returns the cost unchanged when the integration is off, the ability is unlearned, or the save computes to 0; never
    /// below 0. Shared by the pit-saw wear (<see cref="DamageActiveSaws"/>) and the bark-tool wear
    /// (<see cref="DamageActiveDebarkTools"/>) so the two wear paths save identically. Server-side (its callers are);
    /// per-player, so each participant's own carefulsawyer applies to their own tool. Early-returns on a 0 cost (nothing
    /// to save) so a no-durability server never rolls.
    /// </summary>
    int ApplyCarefulSawyer(IPlayer player, int cost)
    {
        if (cost <= 0) return cost;
        ImmersiveWoodworkingModSystem mod = modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        if (!mod.TryGetCarefulSawyerSave(player, out float save)) return cost;
        int saved = XSkillsYield.ChanceScaled(cost, save, Api.World.Rand);
        return System.Math.Max(0, cost - saved);
    }

    /// <summary>The swiftsaw (xSkills, T04.03) rip-tempo multiplier for <paramref name="player"/> — <c>1 + bonus</c>, capped
    /// at ×1.20 inside the facade. 1.0 (no change) without xSkills / with the ability off or unlearned, so the two
    /// <see cref="AdvanceSaw"/> sites multiply unconditionally. Server-side (its callers are server-gated); each end's own
    /// sawyer scales THEIR contribution, so a co-op rip speeds each end by that end-owner's swiftsaw. Only the rip tempo is
    /// scaled — the saw animation/sound loop is never touched. Thin wrapper over the mod system's own
    /// <see cref="ImmersiveWoodworkingModSystem.SwiftsawSawFactor"/>, the same lazy <see cref="modSystem"/> resolve as
    /// <see cref="ApplyCarefulSawyer"/> / <see cref="Config"/>.</summary>
    float SwiftsawSawFactor(IPlayer player)
        => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).SwiftsawSawFactor(player);

    /// <summary>
    /// Grant forestry XP (xSkills, E02) to every ONLINE player currently manning one of <paramref name="holders"/> — a
    /// completed rip section's <see cref="sawEnds"/> (T02.01) or a completed strip's <see cref="barkSides"/> (T02.02) —
    /// for one finished unit of work. Kept deliberately SEPARATE from the tool-wear pass
    /// (<see cref="DamageActiveSaws"/> / <see cref="DamageActiveDebarkTools"/>), which early-returns on a 0-durability
    /// config: XP must accrue even on a no-durability server, so it can't ride the wear loop. The grant is a clean no-op
    /// without xSkills — the facade (<see cref="ImmersiveWoodworkingModSystem.GrantForestryXp"/>) hard-gates to the
    /// server and to an installed, enabled integration. Co-op policy: with
    /// <see cref="ImmersiveWoodworkingConfig.XSkillsCoopXpFullToBoth"/> on (default) each participant earns the FULL unit
    /// XP; with it off the unit's XP is split evenly across the online participants. An offline ghost hold neither earns
    /// nor dilutes the split. Server-side, master-only (its callers are; a redundant side-guard keeps a stray call inert).
    /// </summary>
    /// <param name="site">Call-site tag for the IW_DEBUG XP trace (<c>saw</c> / <c>debark</c>) — behaviour-neutral,
    /// stripped from Release along with every <see cref="ImmersiveWoodworkingModSystem.DbgXp"/> call that reads it.
    /// Both sites share this method verbatim, so the tag is the ONLY way a log tells the two apart.</param>
    void GrantForestryXpToHolders(IEnumerable<SawyerHold> holders, float xp, string site = "?")
    {
        if (Api.Side != EnumAppSide.Server) return;
        ImmersiveWoodworkingModSystem mod = modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        if (xp <= 0f) { mod.DbgXp(site, null, "SKIP: configured xp=" + xp.ToString("0.####") + " <= 0"); return; }

        List<IPlayer>? players = null;
        int ghosts = 0;
        foreach (SawyerHold held in holders)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            if (player == null) { ghosts++; continue; } // offline ghost hold — no credit, no dilution (mirrors the wear pass)
            (players ??= new List<IPlayer>()).Add(player);
        }
        if (players == null)
        {
            // The one failure this method can hide: the unit of work completed, but NOBODY was on the holder map to
            // credit. Silent before, and indistinguishable from a grant that landed — hence this line.
            mod.DbgXp(site, null, "SKIP: no online holder to credit (offline ghost holds=" + ghosts + ")");
            return;
        }

        float per = Config.XSkillsCoopXpFullToBoth ? xp : xp / players.Count;
        foreach (IPlayer player in players) mod.GrantForestryXp(player, per, site);
    }

    /// <summary>
    /// The whole log has been ripped (all four stages fired, model 20 → empty): retire the billet. Its final batch
    /// already dropped (<see cref="CompleteSawStage"/>), so just clear the held log and reset progress — the pair
    /// returns to its empty-finished state, ready to accept a new log, and the synced clear rebuilds the mesh with
    /// no log so the billet visibly vanishes. Same clear as <see cref="DiscardLog"/>, minus the player gesture (here
    /// the log was fully consumed, not abandoned). Server-side, master-only; the caller (<see cref="AdvanceSaw"/>)
    /// MarkDirty's.
    /// </summary>
    void ConsumeLog(IPlayer byPlayer)
    {
        ItemStack? finished = heldLog;

        DropSawdust(byPlayer); // one wearandtear:sawdust per fully-ripped billet — BEFORE the auto-restock branch's early return so every log yields it (no-op when Wear & Tear is absent)

        // AutoRestock WITH a configured breather (SawRestockDelaySeconds): don't snap straight onto the next log.
        // Empty the pair NOW and lay the next one after a short pause (FinishPendingRestock, tick-driven from
        // ContinueSawing), so a held rip reads rip → pause (empty) → place → rip — the saw's counterpart to the
        // chopping block's ChopRestockDelaySeconds. Only enter the pause when a restock is actually available (a peek
        // of the active sawyers' packs), so the rip ends cleanly on the last log with no empty whiff. Keep sawEnds (the
        // held rip stays the active sawyer) and reset only the per-log progress; restockPending is synced so the client
        // keeps the hold alive over the empty pair (see the field comment). The caller (AdvanceSaw) MarkDirty's,
        // syncing heldLog=null + restockPending=true together.
        if (Config.AutoRestock && Config.SawRestockDelaySeconds > 0f && finished != null && PeekRestockSlot(finished) != null)
        {
            heldLog = null;
            sawProgress = 0f;
            sawStagesFired = 0;
            ResetDebarkState(); // the next (restocked) log is fresh — clear any partial bark strip too (T01.06)
            restockFinished = finished;
            restockPending = true;
            restockReadyAtMs = Api.World.ElapsedMilliseconds + (long)(Config.SawRestockDelaySeconds * 1000f);
            restockTickedAtMs = Api.World.ElapsedMilliseconds; // the hold that consumed the log IS this pause's first tick (AuditFixPlan T03.04)
            return;
        }

        // No breather (delay 0, AutoRestock off, or no log left to pull): instant restock exactly as before — read
        // sawEnds + the finished log HERE, before ResetSawState zeroes them; the still-held right-click re-claims its
        // end next tick (ContinueSawing's "claim a free end mid-hold"), so a held rip carries on after a ≤1-tick gap.
        heldLog = TryAutoRestockLog(finished);
        ResetSawState();
    }

    /// <summary>The auto-restock breather (<see cref="restockPending"/>) has elapsed: lay the next log now — pulled
    /// from an active sawyer's pack (same source as the instant path), preferring the same kind/species as the log
    /// that just finished — and end the pause so the held rip resumes on a fresh billet. Leaves the pair empty if no
    /// sawyer still carries a matching log (the loop then ends cleanly next tick, exactly as the instant path does on
    /// the last log). sawProgress/sawStagesFired were already zeroed in <see cref="ConsumeLog"/>; sawEnds was kept, so
    /// the still-held rip stays the active sawyer. Server-side, master-only; MarkDirty syncs the laid log (or the now-
    /// cleared pending state) — the fresh log resets the visual to model 1 so the new billet re-meshes for free.</summary>
    void FinishPendingRestock()
    {
        restockPending = false;
        ItemStack? finished = restockFinished;
        restockFinished = null;
        heldLog = TryAutoRestockLog(finished);
        MarkDirty(true);
    }

    /// <summary>
    /// AutoRestock (E02), server-side, master-only: the next log to lay after one is fully ripped, pulled from an
    /// active sawyer's own pack — or null when <see cref="ImmersiveWoodworkingConfig.AutoRestock"/> is off, no sawyer
    /// is resolvable, or none carries a matching input. Prefers the SAME kind+species as <paramref name="finished"/>
    /// (a birch log → another birch log) so a continuing rip doesn't switch wood while a match remains, falling back
    /// to any sawable input. Must run BEFORE <see cref="ResetSawState"/> (it reads <see cref="sawEnds"/>, still
    /// populated). Takes one log out of the first sawyer that has one, plays the place sound, and returns it. The
    /// active slot (the saw) is never taken from — <see cref="AutoRestockUtil.FindRestockSlot"/> skips it.
    /// </summary>
    ItemStack? TryAutoRestockLog(ItemStack? finished)
    {
        if (Api.Side != EnumAppSide.Server || !Config.AutoRestock || finished == null) return null;

        ItemSlot? src = PeekRestockSlot(finished);
        if (src?.Itemstack == null) return null; // off, no sawyer resolvable, or none carries a matching input

        ItemStack pulled = src.TakeOut(1);
        src.MarkDirty();
        Api.World.PlaySoundAt(BuildSound, Pos.X + 0.5, Pos.Y + 0.5, Pos.Z + 0.5, null, true, 16f);
        return pulled;
    }

    /// <summary>The first active sawyer's inventory slot holding a log to restock after <paramref name="finished"/>,
    /// or null if none — a no-take PEEK (the caller owns the take). STRICT same-kind: a log restocks ONLY logs, a
    /// debarked log ONLY debarked logs (never crossing), preferring the SAME species within that kind. Reads
    /// <see cref="sawEnds"/> (the active sawyers' own packs; the active tool slot is skipped), so call BEFORE
    /// <see cref="ResetSawState"/>. Used both to TAKE the next log (<see cref="TryAutoRestockLog"/>) and to decide
    /// whether a restock breather is worth entering (<see cref="ConsumeLog"/> — mirrors the chopping block's
    /// CanRestock). Server-side, master-only.</summary>
    ItemSlot? PeekRestockSlot(ItemStack finished)
    {
        SawInput finishedKind = GetSawInput(finished);
        if (finishedKind == SawInput.None) return null; // not a recognised input (shouldn't happen for a just-ripped log)
        string? finishedWood = GetWoodSpecies(finished);

        // The accept predicate repeats the manual door's dead-end gate (SawProductResolves, T03.03): auto-feed must
        // never admit what a player could not lay by hand. Without it the breather pulled the NEXT log of an
        // unrippable wood out of the pack and fed it into the same silent hole — one log lost by hand became the whole
        // stack lost unattended, which is what made a P2 finding worth a gate rather than a note.
        System.Func<ItemStack, bool> accept = s => GetSawInput(s) == finishedKind && SawProductResolves(Api.World, s, sawMode);
        System.Func<ItemStack, bool> preferred = s => accept(s) && GetWoodSpecies(s) == finishedWood;

        foreach (SawyerHold held in sawEnds.Values)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            if (player == null) continue;

            ItemSlot? src = AutoRestockUtil.FindRestockSlot(player, accept, preferred);
            if (src?.Itemstack != null) return src;
        }
        return null;
    }

    /// <summary>
    /// The sawn product for the currently held log: a single-item <c>template</c> stack of the result (a
    /// <c>plank-{wood}</c> ITEM for a log or log section, a <c>supportbeam-{wood}</c> BLOCK for a debarked log, bound
    /// to the log's species via <see cref="GetWoodSpecies"/>), the full per-log <c>total</c> yield (config, clamped so a stage
    /// batch can never exceed the output's max stack across its piles), and how many piles a stage batch splits
    /// into (<see cref="ImmersiveWoodworkingConfig.PlankDropCount"/> for planks /
    /// <see cref="ImmersiveWoodworkingConfig.BeamDropCount"/> for beams). Null when there's no log, an unrecognised
    /// species, or the output collectible doesn't exist. Server-side, master-only (read straight off heldLog).
    /// </summary>
    (ItemStack template, int total, int dropCount)? GetSawProduct()
    {
        SawInput kind = GetSawInput(heldLog);
        string? wood = GetWoodSpecies(heldLog);
        if (kind == SawInput.None || wood == null) return null;

        // Resolve the sawn product in the LOG'S OWN domain first, falling back to game. A wood-adding mod registers
        // its planks/beams in its own domain alongside its logs (wildcrafttree:log-placed-azobe-ud rips to
        // wildcrafttree:plank-azobe), while vanilla logs are game:* → game:plank-oak; the log's domain covers both,
        // and the game fallback catches a mod that keeps its logs in its own domain but maps the output to vanilla.
        // (Input acceptance + species parsing are already domain-agnostic — which is why a modded log animates the
        // rip yet used to drop nothing here, the "game"-hardcoded bug this fixes.) heldLog/Block are non-null once
        // kind != None (GetSawInput requires Block).
        string logDomain = heldLog!.Block!.Code.Domain;
        int stages = StageFractions.Length; // the 4 yield batches — same source as the thresholds
        // Product by the LOCKED cut mode (SawModePlan), not the input: Auto keeps the input default (log/section →
        // planks, debarked → beams), a chosen mode forces it. sawMode is set at cut-start (TryStartSawing) and this runs
        // per completed stage, so it's already locked here. A log section still yields the same PlanksPerLog as a log (E08).
        if (ProductIsPlanks(kind, sawMode))
        {
            Item? plank = ResolveProductItem(Api.World, logDomain, "plank-" + wood);
            if (plank == null) return null;
            int total = GameMath.Clamp(Config.PlanksPerLog, 1, stages * Config.PlankDropCount * plank.MaxStackSize);
            return (new ItemStack(plank), total, Config.PlankDropCount);
        }
        else // DebarkedLog → support beams (a placeable BLOCK, not an item)
        {
            Block? beam = ResolveProductBlock(Api.World, logDomain, "supportbeam-" + wood);
            if (beam == null) return null;
            int total = GameMath.Clamp(Config.BeamsPerDebarkedLog, 1, stages * Config.BeamDropCount * beam.MaxStackSize);
            return (new ItemStack(beam), total, Config.BeamDropCount);
        }
    }

    /// <summary>
    /// The BEST sawyer (xSkills, T03.01) extra-yield fraction among the pair's active end-owners — the co-op rule for a
    /// two-man rip: whichever sawyer is more skilled sets the section's bonus (the master runs the calc once, so it can't
    /// double-apply). Re-evaluated each section by <see cref="CompleteSawStage"/>, so the bonus follows whoever is
    /// actually manning the rip as it progresses. Server-side (its caller is); iterates <see cref="sawEnds"/>, reading
    /// each online sawyer's own forestry sawyer bonus through the facade. 0 when the integration is off, no ability is
    /// learned, or no sawyer is online — the section then drops the plain config yield. Reading OTHER players' abilities
    /// is server-safe here (this only ever runs in the server-authoritative stage-completion path).
    /// </summary>
    float ResolveSawyerYieldFraction()
    {
        ImmersiveWoodworkingModSystem mod = modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>();
        float best = 0f;
        foreach (SawyerHold held in sawEnds.Values)
        {
            IPlayer? player = Api.World.PlayerByUid(held.Uid);
            if (player == null) continue; // offline ghost hold — no bonus
            if (mod.TryGetSawyerYield(player, out float f) && f > best) best = f;
        }
        return best;
    }

    /// <summary>The sawn-product ITEM (a plank) at <paramref name="path"/>, looked up in <paramref name="domain"/>
    /// first and then game — so a modded log resolves to its own-domain plank, a vanilla log to game:, and a mod
    /// that keeps the SAME species name but ships the plank in game: still resolves. When neither exists, one last
    /// step: the recipe-declared remap table (<see cref="ImmersiveWoodworkingModSystem.RemappedProductCode"/>) for a
    /// wood mod that RENAMES the species on the way to the plank (FloralZones' pinussibirica log → game:plank-pine, a
    /// mapping only its grid recipe carries). Null if none exists. <c>internal static</c> so the
    /// automated sawmill's input filter (<see cref="SawProductResolves"/>, T05.03) shares this ONE resolver instead of
    /// copying the rule — the mod-independence invariant (resolve by shape + the recipe's own data, never hardcode a
    /// species or game:).</summary>
    internal static Item? ResolveProductItem(IWorldAccessor world, string domain, string path)
    {
        Item? direct = world.GetItem(new AssetLocation(domain, path)) ?? world.GetItem(new AssetLocation("game", path));
        if (direct != null) return direct;
        // The shape lookup found nothing in either domain. A wood mod may still turn this log into a plank whose
        // species name it RENAMED (FloralZones' pinussibirica log → game:plank-pine), a mapping that lives only in the
        // mod's own grid recipe. ImmersiveWoodworkingModSystem harvested that recipe's declared output into a synced
        // table before stripping the recipe; consult it as the last resort so a renamed product is not a dead end.
        string? remap = ImmersiveWoodworkingModSystem.RemappedProductCode(world, domain, path);
        return remap == null ? null : world.GetItem(new AssetLocation(remap));
    }

    /// <summary>The sawn-product BLOCK (a support beam) at <paramref name="path"/>, looked up in
    /// <paramref name="domain"/> first and then game, then the recipe-declared remap table (see
    /// <see cref="ResolveProductItem"/>). Null if none exists.
    /// <c>internal static</c> — shared with the sawmill's input filter, like <see cref="ResolveProductItem"/>.</summary>
    internal static Block? ResolveProductBlock(IWorldAccessor world, string domain, string path)
    {
        Block? direct = world.GetBlock(new AssetLocation(domain, path)) ?? world.GetBlock(new AssetLocation("game", path));
        if (direct != null) return direct;
        string? remap = ImmersiveWoodworkingModSystem.RemappedProductCode(world, domain, path);
        return remap == null ? null : world.GetBlock(new AssetLocation(remap));
    }

    /// <summary>
    /// How many product units stage <paramref name="stageIndex"/> (0-based) yields from <paramref name="total"/>,
    /// split "from the end": every stage gets <c>total / 4</c>, and the <c>total % 4</c> remainder is loaded onto
    /// the LAST stages (each of the final <c>rem</c> stages gets one extra). The sum over all four stages is
    /// exactly <paramref name="total"/> — no loss, no surplus. Examples (total → per stage): 24→6,6,6,6;
    /// 6→1,1,2,2; 9→2,2,2,3; 10→2,2,3,3; 11→2,3,3,3.
    /// </summary>
    static int StageAmount(int total, int stageIndex)
    {
        int stages = StageFractions.Length;
        int baseAmt = total / stages;
        int rem = total % stages;
        return baseAmt + (stageIndex >= stages - rem ? 1 : 0);
    }

    /// <summary>
    /// Scatter one stage's <paramref name="stageAmount"/> of <paramref name="template"/> into separate item
    /// entities: <c>min(dropCount, stageAmount)</c> piles (so 1 unit → 1 pile, never an empty one), the amount
    /// spread as evenly as possible with the remainder loaded one-per-pile (via the shared
    /// <see cref="DropDistribution.SplitEvenly"/>, the same split the chopping block uses in SpawnDistributed).
    /// The total clamp in <see cref="GetSawProduct"/> keeps every pile within the output's max stack. Each pile is
    /// spawned through <see cref="SpawnSawnItem"/> (which owns the drop physics + spawn point). Server-side,
    /// master-only.
    /// </summary>
    void DropStageBatch(ItemStack template, int stageAmount, int dropCount, IPlayer byPlayer)
    {
        int[] sizes = DropDistribution.SplitEvenly(stageAmount, dropCount);
        for (int i = 0; i < sizes.Length; i++)
        {
            ItemStack pile = template.Clone();
            pile.StackSize = sizes[i];

            // Place this pile evenly along the log: a lone pile stays centred, n piles span [−1, +1] which
            // SpawnSawnItem scales by SawDropAlongSpread. Spreads the batch into a line down the billet instead of
            // one bunch at the centre.
            double alongFrac = sizes.Length > 1 ? ((double)i / (sizes.Length - 1) - 0.5) * 2.0 : 0.0;
            SpawnSawnItem(pile, alongFrac, byPlayer);
        }
    }

    // Sawn-product drop physics (T06.04 + the directional-eject refinement). The boards are pushed OFF the
    // construction to ONE side instead of dropping straight down: the spawn point (Y+1.0, the cut height) sits
    // INSIDE the multiblock's log/trestle collision box, and VS collision motion-CLIPS but never EJECTS an
    // overlapping entity, so a straight-down drop just rests on top of the sawhorses (the bug this fixes). A fixed
    // sideways push clears the box so they fall beside the construction.
    //
    // SawDropEjectVelocity — horizontal speed of that push, blocks/tick (same units as the chopping block's 0.075).
    // Item motion decays ~1.7%/tick (AirDragAlways 0.983 → motion.Scale(0.983^(dt·33))), so 0.075 carries ~0.44
    // block and 0.12 carries ~0.70 — enough to clear the one-cell-wide trestle and land next to it. No vertical
    // component: the board drifts sideways and gravity drops it (the user's "fall down, don't pop up").
    const double SawDropEjectVelocity = 0.12;
    // Nudge the spawn point this far toward the eject side first, so the pile starts near the construction's side
    // face rather than dead-centre over the log (helps it escape the collision box).
    const double SawDropEjectOffset = 0.3;
    // Half-extent (axis-frame blocks) over which a batch's piles are spread EVENLY along the log, so the boards
    // come out as a line down the billet instead of one bunch at the centre: pile i of n sits at
    // (i/(n-1) − 0.5)·2·this, i.e. the n piles span [−this, +this] (a single pile stays centred). 0.6 keeps every
    // pile inside the two occupied cells (log half-length ≈ 0.81), and since each pile still exits SIDEWAYS (the
    // perpendicular eject below), the along position never affects whether it clears the collision box.
    const double SawDropAlongSpread = 0.6;
    // Small RANDOM jitter ALONG the log axis only (never across it — the across direction is the deterministic
    // eject), riding on top of the even spread so successive stage batches landing at the same along positions
    // don't stack pixel-perfect.
    const double SawDropAlongJitter = 0.1;

    /// <summary>
    /// Spawn one pile of sawn product, ejected perpendicular to the log toward the DIRECTIONAL cut side
    /// (<see cref="sawCutReversed"/>, chosen from the player's click at cut-start): the default "forward" side is
    /// up × (+axis) — a Z-axis pair throws toward +X (East), an X-axis pair toward −Z (North) — and a reversed cut
    /// throws to the opposite side, in lockstep with the visual so the boards always fall from the side the log is
    /// visibly consumed. A directional push (<see cref="SawDropEjectVelocity"/>) perpendicular to the log clears the
    /// multiblock collision box so the boards fall beside the sawhorses instead of resting on them (VS motion-clips
    /// overlapping entities rather than ejecting them); vertical velocity stays 0 so they drop rather than pop. The
    /// spawn point is nudged toward that side (<see cref="SawDropEjectOffset"/>) and placed along the log at
    /// <paramref name="alongFrac"/> (−1..+1, the pile's even slot in its batch, scaled by
    /// <see cref="SawDropAlongSpread"/>) plus a little random jitter (<see cref="SawDropAlongJitter"/>) so the batch
    /// lands as a spread-out line rather than one bunch. Server-side, master-only (callers ensure both;
    /// <see cref="pairAxis"/> is set on the master).
    ///
    /// The forward-side signs were confirmed in-game; if the boards ever come out on the WRONG side, flip the two
    /// base signs of ejectX/ejectZ below (the reversed case follows automatically off <see cref="sawCutReversed"/>).
    /// </summary>
    /// <param name="alongFrac">This pile's even position along the log within its batch, −1 (one end) … +1 (the
    /// other), 0 for a lone pile. Scaled to blocks by <see cref="SawDropAlongSpread"/>.</param>
    /// <param name="byPlayer">The sawyer whose stroke dropped this pile; it lands at their feet instead under
    /// <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/> (see <see cref="SpawnEjectedItem"/>).</param>
    void SpawnSawnItem(ItemStack stack, double alongFrac, IPlayer byPlayer)
    {
        // Sawing eject: perpendicular to the log, toward the DIRECTIONAL cut side. The "forward" (default) side is the
        // part_1 side — up × (+axis): a Z-axis pair throws toward +X (East), an X-axis pair toward −Z (North).
        // sawCutReversed (set from the player's click at cut-start, HitIsReversedSide) flips it to the opposite side, in
        // lockstep with the visual hide order (HideSequenceReversed) so the boards ALWAYS eject from the same side the
        // log is visibly consumed. If the forward side itself comes out wrong in-game, flip these two base signs.
        double sideSign = sawCutReversed ? -1.0 : 1.0;
        SpawnEjectedItem(stack, alongFrac,
            (pairAxis == EnumAxis.Z ? 1.0 : 0.0) * sideSign,
            (pairAxis == EnumAxis.X ? -1.0 : 0.0) * sideSign,
            byPlayer);
    }

    /// <summary>Spawn one item ejected perpendicular to the log toward the world direction
    /// (<paramref name="ejectX"/>, <paramref name="ejectZ"/>) — a horizontal unit vector across the log. Shared by the
    /// sawing drops (<see cref="SpawnSawnItem"/>, toward the directional cut side) and the debarking drops
    /// (<see cref="CompleteDebark"/> / <see cref="DropRolledBark"/>, AWAY from the debarker). The push
    /// (<see cref="SawDropEjectVelocity"/>) clears the multiblock collision box so the item falls beside the sawhorses
    /// instead of resting on them; vertical velocity stays 0 so it drops rather than pops. The spawn point is nudged
    /// toward that side (<see cref="SawDropEjectOffset"/>) and placed along the log at <paramref name="alongFrac"/>
    /// (−1..+1, scaled by <see cref="SawDropAlongSpread"/>) plus a little jitter. Server-side, master-only.
    /// <para>With <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/> on, none of that runs: the item drops at
    /// <paramref name="byPlayer"/>'s feet (<see cref="DropDistribution.TrySpawnAtPlayer"/>). Being the one sink for the
    /// planks/beams, the sawdust, the bark and the debarked log, this is the only place the sawhorse checks the
    /// switch.</para></summary>
    void SpawnEjectedItem(ItemStack stack, double alongFrac, double ejectX, double ejectZ, IPlayer byPlayer)
    {
        if (DropDistribution.TrySpawnAtPlayer(Config, Api.World, byPlayer, stack)) return;

        var rnd = Api.World.Rand;

        // Along-axis unit (the log direction = +pairAxis) — aims both the even spread and the random jitter.
        double alongX = pairAxis == EnumAxis.X ? 1.0 : 0.0;
        double alongZ = pairAxis == EnumAxis.Z ? 1.0 : 0.0;

        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);

        // Even slot along the log + a little random jitter on top (both ALONG the axis only; the across direction
        // is the deterministic eject).
        double along = alongFrac * SawDropAlongSpread + (rnd.NextDouble() * 2 - 1) * SawDropAlongJitter;
        Vec3d dropPos = new Vec3d(
            cx + ejectX * SawDropEjectOffset + alongX * along,
            Pos.Y + 1.0,
            cz + ejectZ * SawDropEjectOffset + alongZ * along);

        // Fixed sideways push, no vertical: the board clears the cut and falls beside the sawhorses.
        Vec3d velocity = new Vec3d(
            ejectX * SawDropEjectVelocity,
            0.0,
            ejectZ * SawDropEjectVelocity);

        Api.World.SpawnItemEntity(stack, dropPos, velocity);
    }

    // One unit of sawdust per fully-ripped billet, for Wear & Tear (mod) compatibility: its repair/fuel economy
    // (woodfiller, sawdust bricks, composting) is fed by wearandtear:sawdust, described in-mod as the "byproduct
    // created when sawing wood". A FIXED-domain item — resolved DIRECTLY in the wearandtear domain, NOT through the
    // log-domain ResolveProductItem (sawdust is not species/wood-derived, so the domain fallback would be wrong).
    // Baked as a constant rather than a config knob, matching the deliberate decision to bake the sawdust-emission
    // tunables (see ImmersiveWoodworkingConfig notes); 1 keeps parity with the vanilla+W&T plank-recipe baseline.
    const int SawdustPerLog = 1;

    /// <summary>Drop <see cref="SawdustPerLog"/> wearandtear:sawdust beside the construction for one fully-ripped
    /// billet — called once per log from <see cref="ConsumeLog"/> (which itself fires exactly once per log, before
    /// its auto-restock branch). Reuses the sawn-item eject physics (<see cref="SpawnSawnItem"/>) so the dust lands
    /// beside the sawhorses rather than resting on the multiblock collision box. Soft, modinfo-free integration:
    /// <see cref="IWorldAccessor.GetItem"/> returns null when Wear &amp; Tear isn't installed and we drop nothing.
    /// Server-side, master-only (its caller is).</summary>
    void DropSawdust(IPlayer byPlayer)
    {
        Item? dust = Api.World.GetItem(new AssetLocation("wearandtear", "sawdust"));
        // A REMOVED Wear & Tear leaves the item code registered as a missing-item placeholder, so GetItem returns a
        // non-null stub with IsMissing set — spawning it would litter broken sawdust. == null alone only catches a
        // world where the mod was never installed.
        if (dust == null || dust.IsMissing) return; // Wear & Tear absent or removed — soft no-op
        SpawnSawnItem(new ItemStack(dust, SawdustPerLog), 0.0, byPlayer); // one centred pile, same drop physics as the boards
    }

    // The "end zone" the player must stand in to saw, relative to the pair centre, in the axis frame
    // (along = the log direction, perp = across it). EndZoneMinAlong = centre-to-end-face distance: the pair is two
    // cells long, so each end face is exactly 1.0 from the centre — the player must be BEYOND it (collision keeps a
    // valid end player at |along| ≳ 1.3, while a trestle cell centre is only 0.5, so this also rejects standing on
    // the construction). EndZoneMaxPerp = lateral tolerance: the footprint is one cell wide (half-width 0.5), so
    // 0.75 admits the whole end cell plus a little slack while rejecting the diagonal corner / long-side cells.
    const double EndZoneMinAlong = 1.0;
    const double EndZoneMaxPerp = 0.75;

    /// <summary>
    /// The horizontal offset of <paramref name="byPlayer"/> from the pair centre, resolved into the pair's axis
    /// frame: <c>along</c> runs down the log (the pair axis), <c>perp</c> across it (the sides). BOTH position gates
    /// — the rip's end zone (<see cref="IsPlayerAtAxisEnd"/>) and the debark side zone
    /// (<see cref="IsPlayerAtAxisSide"/>) — read the SAME numbers from here, so the two can never drift apart.
    /// Master-relative: <see cref="pairAxis"/> / <see cref="Pos"/> must be the master's (every gate routes through
    /// <see cref="GetMaster"/> first; heldLog ≠ null guarantees inPair &amp; isMaster).
    /// </summary>
    (double along, double perp) PlayerAxisOffset(IPlayer byPlayer)
    {
        // Pair centre (world, horizontal): this master cell's centre nudged half a cell toward the partner, which
        // always sits at +axis. X-pair → centre at the master/partner +X boundary; Z-pair → at the +Z boundary.
        double cx = Pos.X + 0.5 + (pairAxis == EnumAxis.X ? 0.5 : 0.0);
        double cz = Pos.Z + 0.5 + (pairAxis == EnumAxis.Z ? 0.5 : 0.0);

        double dx = byPlayer.Entity.Pos.X - cx;
        double dz = byPlayer.Entity.Pos.Z - cz;
        double along = pairAxis == EnumAxis.X ? dx : dz; // along the log
        double perp  = pairAxis == EnumAxis.X ? dz : dx; // across it (the sides)
        return (along, perp);
    }

    /// <summary>
    /// True when <paramref name="byPlayer"/> stands at an END of the pair — beyond a trestle along the log (pair)
    /// axis AND within the construction's narrow width — rather than at a side or diagonally off a corner. By
    /// PHYSICAL position (Q4), not view. The offset from the pair centre (<see cref="PlayerAxisOffset"/>) is split
    /// into along-axis and perpendicular components; an end position is "far enough along, close enough across" (an
    /// end-cap box past each end face), which keeps BOTH ends valid (symmetric in the along sign) and rejects the
    /// long sides AND the corner diagonals — a plain <c>|along| &gt; |perp|</c> wedge would leak those corners
    /// through, since its apex sits at the centre and ignores the perpendicular extent. Master-relative — call on
    /// the master (heldLog ≠ null guarantees inPair &amp; isMaster, so <see cref="pairAxis"/> is set and
    /// <see cref="Pos"/> is the lex-smaller cell, with the partner one step toward +axis).
    /// </summary>
    bool IsPlayerAtAxisEnd(IPlayer byPlayer)
    {
        (double along, double perp) = PlayerAxisOffset(byPlayer);
        // Past an end face (either end) and laterally aligned — an end-cap box, not a half-plane.
        return System.Math.Abs(along) > EndZoneMinAlong && System.Math.Abs(perp) < EndZoneMaxPerp;
    }
}
