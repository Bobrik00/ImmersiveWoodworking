using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E07/T07.04 + T07.05 — the chopper's CLIENT cut effects: the species-coloured chip BURST at each strike (T07.04) and
/// the machine's five-layer SOUND (T07.05). All client-only — the sole callers are the client render frame
/// (<see cref="UpdateCutEffects"/>, from <see cref="ChopperRenderer"/>, with the SAME part matrices it draws) and a
/// 100 ms client tick (<see cref="OnClientEffectsTick"/>, for the ambient loop) — so nothing here runs on the dedicated
/// server. Two gates decide what plays, exactly like <see cref="BlockEntitySawmill"/>'s Effects:
/// <list type="bullet">
///   <item><b>turning</b> = <see cref="IsMechanismTurning"/> (<c>IsComplete &amp;&amp; EffectiveChopSpeed() &gt; 0</c>) —
///   the SAME predicate the render advances its angle on, so the mechanism sounds play exactly when the axe visibly
///   moves, LOG OR NO LOG: the <b>ambient</b> loop, the per-tooth <b>gear-hit</b> clacks, the <b>arm-fall</b> whoosh at
///   the beam's release, and the <b>dropmaul-axehit</b> thud at the bottom of every stroke.</item>
///   <item><b>chopping</b> = <see cref="CanChop"/> — a real, product-resolvable log is actually loaded. Drives the
///   species chip burst AND the <b>choppingwood</b> crack, both at the strike bottom (so an assembled chopper turning
///   over an EMPTY bed thuds but throws no chips and cracks no wood — it chops air).</item>
/// </list>
/// chopping ⊆ turning by construction. The user's authored dropmaul set (2026-07-05): <c>gear-hit</c> = the 16 gear
/// teeth (they occupy the toothed arc = the beam's rise; the toothless remainder is where the axe falls free);
/// <c>arm-fall</c> = the "шурх" as the beam is released; <c>dropmaul-axehit</c> = the maul on the bed regardless of a log;
/// <c>ambient</c> = the continuous mechanism bed. <c>choppingwood</c> is the mod's OWN hand-chop set (reused, so the
/// machine and the hand block sound identical) — it cracks on EVERY strike a real log is loaded (the axe biting the
/// billet), so a log cracks <c>ChopperStrikes</c> times over its pass, the last crossing coinciding with the split.
/// At the default of 1 (the "one slow blow" the ÷48 reducer buys) that is a single crack, on the split itself.
///
/// The release (top) and the strike (bottom) are caught from the drive-angle PHASE crossing the driver's OWN boundaries:
/// the release at <see cref="ChopperAnim.ArmRiseFrac"/> (where the sharp 1−t² drop begins) and the strike at
/// <c>ArmRiseFrac + ArmDropFrac</c> (the beam's Y minimum, spec §8). Phase crossings — not a sampled matrix-Y reversal —
/// because the drop is only ~4% of a cycle and a frame hitch / a fast network can ALIAS a Y-reversal away, dropping a
/// strike's crack even when the drop is narrow; a forward phase crossing catches the boundary even when a frame
/// steps clean over it. The gear clacks ride the SAME phase seam (a per-tooth step gated to the rise window,
/// <see cref="ChopperAnim.ArmRiseFrac"/>, so they clatter over the lift and fall silent through the drop+dwell — spec §8).
/// All three key on <c>animAngle</c> — the exact drive angle that built THIS frame's matrices (advanced by
/// <see cref="BlockEntityChopper.GetPartMatrices"/> right before this call, so a non-null <c>mats</c> means the angle is
/// fresh). All sounds are MONO or OpenAL won't spatialize them (memory vs-positional-audio-mono); a missing file plays silently, no
/// crash (memory vs-mod-asset-path-domain); the loop is driven through <see cref="LoopSound"/>, which fades it in on the
/// gate's rising edge and out on its falling edge (<see cref="LoopFadeSeconds"/>) so it never pops on/off, keeps the
/// handle for a cheap restart, and frees the engine's concurrency slot once the chopper has stood idle a while or the
/// listener has walked out of earshot (AuditFixPlan T06.03); teardown stops and frees it immediately
/// (<see cref="DisposeSound"/>, from the render partial). Volumes/ranges/anchors are baked PLACEHOLDERS, tuned in-game at
/// T07.06.
/// </summary>
public partial class BlockEntityChopper
{
    // ───────────────────────── Strike chips (T07.04) ─────────────────────────

    // How long a chip is VISIBLE, in real seconds. The engine multiplies a template's LifeLength by 5 at spawn (see
    // ParticleLife), so the "1.0" this template used to declare was five seconds on screen — and a chip that arcs
    // off the strike lands in a fraction of one, spending the rest as a motionless quad running terrain physics 30
    // times a second. Cut to a third by author decision 2026-08-28 (AuditFixPlan T06.02 / F-L10-07), in step with
    // the sawhorse's bark chips this template is a copy of. Rate needs no such treatment: unlike the manual
    // stations, a burst here is fired once per STRIKE — a discrete event, already independent of the frame rate.
    const float ChipSeconds = 1.7f;

    // Chip-burst template, reused per spawn (Color / MinPos / AddPos / velocity set each time) — a copy of the
    // sawhorse's BarkChips (chunky quad flakes, gravity, terrain-collision, wind), safe because particle spawning is
    // synchronous + client-only. Chunkier and longer-lived than fine sawdust: an axe strike knocks off splinters, not
    // powder. Colour is overwritten per spawn from the log's own texture (species), so birch reads lighter than oak.
    static readonly SimpleParticleProperties ChipBurst = new SimpleParticleProperties(
        6, 12,                               // one burst per strike: 6–12 chips (SimpleParticleProperties ctor = min,MAX;
                                             // it stores AddQuantity = max-min → Quantity ∈ [6,12); an impulse, NOT a stream)
        ColorUtil.ToRgba(255, 150, 110, 75), // overwritten per spawn from the log's own texture (species colour)
        new Vec3d(), new Vec3d(),
        new Vec3f(), new Vec3f(),            // velocity is set per spawn (ChipMinVel/ChipAddVel) — the ctor args are unused
        ParticleLife.Seconds(ChipSeconds),   // life (real seconds — ParticleLife undoes the engine's ×5)
        1f,                                  // gravity — chips arc off the strike then drop and land
        0.15f, 0.4f,                         // min / max size — chunky splinters (like the sawhorse bark chips)
        EnumParticleModel.Quad)
    {
        WithTerrainCollision = true,
        WindAffected = true,
        WindAffectednes = 0.3f,
    };

    // Chip launch (blocks/tick): a symmetric OUTWARD horizontal pop off the strike + a small upward toss, then gravity.
    // A deliberate PLACEHOLDER — the real spray direction/force is calibrated against the finished model in T07.06 (the
    // chips should fan out of the head↔log seam). Mirrors the sawhorse bark chips' magnitude.
    static readonly float[] ChipMinVel = { -0.5f, 0.15f, -0.5f };
    static readonly float[] ChipAddVel = {  1.0f, 0.35f,  1.0f };

    // Index of the axe BEAM (arm) and the HEAD in ChopperParts.Rules, resolved once. ArmPartIndex now serves only as the
    // "mover matrices are built" guard in UpdateCutEffects (the release/strike moments are PHASE-based, no longer sampled
    // off this matrix's Y). The head's rest pivot is where the chips + strike sound spawn (at the strike the head — a Ride
    // of the arm — sits at lift 0, its authored rest pose, so partPivots[head] IS its contact position); its LIVE matrix
    // positions the arm-fall whoosh at the raised beam.
    static readonly int ArmPartIndex = Array.FindIndex(ChopperParts.Rules, r => r.Id == "arm");
    static readonly int HeadPartIndex = Array.FindIndex(ChopperParts.Rules, r => r.IsHead);

    // Chip spawn point tuning (PLACEHOLDER, calibrated in T07.06). ChipContactYBias drops the spawn from the head-mesh
    // bbox centre down toward where the head meets the log on the bed; ChipSpawnJitter is a small position scatter so the
    // chips of one burst don't all stack on a single pixel.
    const double ChipContactYBias = -0.35;
    const double ChipSpawnJitter = 0.06;

    // ───────────────────────── Sound assets (T07.05) ─────────────────────────

    // The user's authored dropmaul set + the reused hand-chop set. Own-domain paths INCLUDING "sounds/" — a wrong domain
    // plays SILENTLY, no warning; PlaySoundAt does NOT auto-prepend "sounds/" (memory vs-mod-asset-path-domain). All MONO
    // (memory vs-positional-audio-mono); after any audio edit re-copy art/…/Audio/dropmaul → assets/…/sounds/dropmaul.
    static AssetLocation Fx(string name) => new AssetLocation("immersivewoodworking", "sounds/dropmaul/" + name);
    static readonly AssetLocation SndAmbient = Fx("ambient");                                          // continuous mechanism bed (loop)
    static readonly AssetLocation[] SndGearHit = { Fx("gear-hit1"), Fx("gear-hit2"), Fx("gear-hit3") }; // one per gear tooth
    static readonly AssetLocation[] SndArmFall = { Fx("arm-fall1") };                                   // "шурх" as the beam is released (single sample → lean on pitch jitter for variety)
    static readonly AssetLocation[] SndAxeHit = { Fx("dropmaul-axehit1"), Fx("dropmaul-axehit2"), Fx("dropmaul-axehit3") }; // maul on the bed

    // The wood-crack when a log is actually split — the mod's OWN hand-chop set (BlockEntityChoppingBlock.ChopSounds), so
    // the machine and the hand block sound identical (task T07.05). An explicit list, like the hand block's — "sounds" is
    // a client asset category, so it can't be folder-scanned on a dedicated server (that side never plays it anyway here).
    static AssetLocation ChopWood(string name) => new AssetLocation("immersivewoodworking", "sounds/chop/" + name);
    static readonly AssetLocation[] SndChopWood = { ChopWood("choppingwood1"), ChopWood("choppingwood2"), ChopWood("choppingwood3"), ChopWood("choppingwood4") };

    // Anchor points into ChopperParts.Rules (resolved once): the ambient loop emanates from the drive gear, the tooth
    // clacks from a pawl (the tooth-engagement point, as the sawmill's metal_click anchors at lever_active).
    static readonly int GearPartIndex = Array.FindIndex(ChopperParts.Rules, r => r.Id == "gearActive");
    static readonly int LevelerPartIndex = Array.FindIndex(ChopperParts.Rules, r => r.Id == "levelerL");

    // The authored gear's tooth count: 16 teeth spread over the toothed arc (= the beam's rise window,
    // ChopperAnim.ArmRiseFrac), the rest of the turn toothless (the axe falls free → silent). User spec 2026-07-05.
    const int ToothCount = 16;

    // Tunables (baked PLACEHOLDERS, tuned in-game at T07.06 — like the sawmill's Fx*/Click* consts). The loop carries the
    // ambience; the one-shots punctuate it. A small ± pitch jitter on each one-shot avoids a "machine-gun" of identical
    // samples (the tempo lives in the sample / the trigger rate, NOT a pitch stretch — memory vs-positional-audio-mono).
    const float LoopRange = 24f;
    const float LoopFadeSeconds = 0.4f; // gentle fade in/out for looping sounds — no abrupt onset/cut (user preferred 0.4 over 0.2)
    const float FxRange = 16f;
    const float AmbientVolume = 0.168f; // 0.7 → 0.28 (to 40%) → 0.168 (a further -40%); user 2026-07-05: still too loud
    const float GearHitVolume = 1.0f; // doubled from 0.5 (user 2026-07-05: tooth clacks too quiet)
    const float GearHitPitchJitter = 0.06f;
    const float ArmFallVolume = 0.7f;
    const float ArmFallPitchJitter = 0.2f; // wide ±0.2 → pitch ∈ [0.8, 1.2]; only one arm-fall sample, so pitch carries the variety (user 2026-07-05)
    const float AxeHitVolume = 0.32f; // 0.56 → 0.32 (E12/T12.02, user 2026-07-12: the maul DROP thud is still too loud vs the wood-crack) — all sounds confirmed MONO (choppingwood re-mastered since the stale stereo note), so this is pure mix, not a spatialization issue; final level in-game (T07.06)
    const float AxeHitPitchJitter = 0.06f;
    const float ChopWoodVolume = 1.15f; // 1.08 → 1.15 (E12/T12.02, user 2026-07-12: the split crack must clearly beat the thud); >1.0 may cap near the master ceiling, so the real lift is the lowered AxeHitVolume above — final level in-game (T07.06)
    const float ChopWoodPitchJitter = 0.08f;

    // The ambient loop. Its whole lifecycle — the fade edges, the earshot gate, freeing the engine's concurrency slot
    // after a spell of idling, the latch on a load failure — lives in LoopSound, shared with the sawmill's two loops
    // (AuditFixPlan T06.03). The one-shots below are fire-and-forget (no handle to leak).
    readonly LoopSound ambientLoop = new LoopSound(SndAmbient, AmbientVolume, LoopRange, LoopFadeSeconds);

    // ───────────────────────── Per-frame trackers ─────────────────────────

    // Stroke-phase tracker (client): the previous sampled beam-cycle phase (ChopperAnim.ArmCyclePhase), the START phase of
    // this frame's advance. A FORWARD crossing of ArmRiseFrac is the RELEASE (the top, where the sharp drop begins) →
    // arm-fall; a forward crossing of ArmRiseFrac+ArmDropFrac is the STRIKE (the beam's Y minimum) → axe-hit thud + (when
    // chopping) chips + wood-crack. The forward ADVANCE is no longer the wrapped [ph − startPh] difference (which aliased
    // at high geared speed, dropping every strike) but the render's net forward advance of animAngle, frameAnimAdvance·workScale
    // mapped onto the arm cycle (SpeedDecouplingPlan T03.05) — startPh here plus that advance feed CrossedForward. Still
    // phase-CROSSING, NOT sampled-Y: a crossing is caught even when a frame steps clean over the narrow drop (which a
    // Y-reversal detector would alias away at high RPM / a hitch — dropping a whole log's crack at StepsPerLog=1). Reset
    // whenever the mechanism stops / the matrices aren't built so a resume re-seeds cleanly instead of banking a stale delta.
    float prevArmPh;
    bool armTracking;

    // Gear-tooth tracker: accumulate the TRUE forward phase advance of the drive cycle (the render's Nyquist-hybrid shaft
    // step, handed in as dphCycle — SpeedDecouplingPlan T03.05) and fire a clack every ArmRiseFrac/ToothCount of phase —
    // but ONLY inside the rise window (the toothed arc); the drop+dwell is toothless and silent. Re-seeded whenever the
    // mechanism stops. Mirrors the sawmill's UpdateRatchetClick, stepped per tooth (not per revolution) and phase-gated.
    // No wrapped-phase delta of its own: at high geared speed that would alias exactly as the strike detector's used to.
    float toothAccum;
    bool toothSeeded;

    /// <summary>Whether the mechanism is actually MOVING right now (<c>IsComplete &amp;&amp; EffectiveChopSpeed() &gt; 0</c>)
    /// — the same gate the render advances <c>animAngle</c> on, so the turning-gated sounds start/stop exactly with the
    /// visible stroke, log or no log.</summary>
    bool IsMechanismTurning() => IsComplete && EffectiveChopSpeed() > 0f;

    /// <summary>Register the client-side ambient loop tick (100 ms). Called from <see cref="Initialize"/> only on the
    /// client (<c>capi != null</c>); a no-op on the dedicated server. The timing-critical one-shots (gear clacks, arm-fall,
    /// axe-hit) run per render frame in <see cref="UpdateCutEffects"/> so they land ON the visible motion event, not on
    /// this coarse grid. The loop is lazily loaded on first start, so a chopper that never runs never loads it. BE tick
    /// listeners are auto-removed on unload; only the <see cref="LoopSound"/> holds an engine resource and needs an
    /// explicit <see cref="DisposeSound"/> (from the render partial's teardown).</summary>
    void InitEffects()
    {
        RegisterGameTickListener(OnClientEffectsTick, 100);
    }

    /// <summary>Client ambience heartbeat (100 ms): hand the loop the turning gate and the anchor to sound from, and let
    /// <see cref="LoopSound"/> work out the edges. Coarse timing is fine for a loop (it needn't track the sub-frame bob),
    /// so it rides the drive-gear rest pivot. Client-only by registration.</summary>
    void OnClientEffectsTick(float dt)
    {
        if (capi == null) return;
        ambientLoop.Update(capi, dt, IsMechanismTurning(), PartPointWorld(GearPartIndex) ?? MachineCenter());
    }

    /// <summary>
    /// Per-FRAME cut effects — called by <see cref="ChopperRenderer"/> on the opaque stage with the SAME part matrices it
    /// renders that frame, so every burst/one-shot lands ON the visible motion. While the mechanism turns it fires: the
    /// gear-tooth clacks (drive-angle phase, gated to the rise window), the arm-fall whoosh at the beam's release (its
    /// Y maximum) and the axe-hit thud at the bottom of the stroke (its Y minimum) — the last also throwing the species
    /// chips + the wood-crack when a real log is being chopped (<see cref="CanChop"/>). A no-op on the server (its sole
    /// caller, the renderer, is client-only) and before the mover matrices are built (<paramref name="mats"/> null). Reset
    /// on the idle edge so a resume re-seeds cleanly. The ambient LOOP is handled on the 100 ms tick, not here.
    /// </summary>
    public void UpdateCutEffects(float[]?[]? mats)
    {
        if (capi == null) return;

        // The beam only moves while the mechanism turns (the SAME gate the render advances its angle on), so a frozen
        // beam never rises/falls and never strikes. Reset every per-frame tracker on the falling edge so a resume
        // re-seeds instead of banking a stale phase delta or a mid-arc tooth accumulator.
        if (!IsMechanismTurning())
        {
            armTracking = false;
            toothSeeded = false;
            if (pendingChopVanish) ResolveChopVanish(false); // the axe froze mid-defer → it won't land; drop the held log now (no effects)
            return;
        }

        // Everything below keys on animAngle, which GetPartMatrices advances ONLY when it returns non-null
        // (partPivotsNative built). So a non-null mats guarantees THIS frame's angle is fresh; a null mats (the mover
        // meshes aren't built yet) means the angle didn't advance this frame — wait, re-seeding the trackers so the first
        // real frame banks no jump. Also guards the head-live anchor the arm-fall reads. (Finding-1 hardening: the tooth
        // clack now shares this fresh-angle gate instead of running on a possibly-stale angle.)
        if (mats == null || ArmPartIndex < 0 || ArmPartIndex >= mats.Length || mats[ArmPartIndex] == null)
        {
            armTracking = false;
            toothSeeded = false;
            return;
        }

        // The GEARED-FORWARD work clock (animAngle × ChopperWorkScale — the axe beam is ChopperDriver.Src="fwd", so the
        // arm phase is read off the SAME geared clock the render feeds the mechanism, SpeedDecouplingPlan T03.03): the
        // strike and the tooth clacks track the mechanism's forward progression, identical on every facing and under either
        // drive direction. FacingDriveSign never touches this — it multiplies ONLY the SIGNED input coupling
        // (mainRotorTwoway); the gear train / pawls / arm are all forward. Keying the strike off the signed angle instead
        // would run the arm cycle phase backwards on the flipped facings and the dph<=0 guard below would swallow every
        // strike (no chips / no thud). At speed=1 workScale=1.0 → geared ≡ raw, bit-identical to the pre-feature strike.
        float theta = animAngle * Config.ChopperWorkScale;

        // The cycle-phase advance this frame — the client twin of the server's dph (SpeedDecouplingPlan T03.04/T03.05):
        // frameAnimAdvance (the render's NET forward advance of animAngle — the Nyquist-hybrid shaft step plus the anchor
        // convergence, i.e. the motion the VISIBLE axe made) × workScale, mapped onto the ChopperCycleCap·2π arm cycle.
        // Taken from that precomputed advance, NOT from the wrapped [ph − startPh] difference: at the top of the speed band
        // the geared step exceeds half a cycle (workScale up to 100), which the old [−0.5,0.5] phase fold mis-read as a small
        // backward step and dropped EVERY strike (no chips / no thud / no crack). It is normally ≥ 0 (the shaft step is
        // rectified); it can go briefly negative only while the anchor pulls the display BACK toward the server phase (the
        // startup catch-up), where the dph<=0 guard below correctly reports no forward strike, matching the visibly
        // reversing axe. At speed=1 workScale=1 and frameAnimAdvance ≤ ~π ⇒ dph ≈ advance/(96π) ≲ 0.01, behaviourally the
        // value the old fold produced (invariant I8 relaxation, as the server took in T03.04). frameAnimAdvance is 0 unless
        // the mechanism is turning, and this block is only reached while turning, so a zero here means "no motion yet".
        float dph = frameAnimAdvance * Config.ChopperWorkScale / (ImmersiveWoodworkingConfig.ChopperCycleCap * GameMath.TWOPI);

        // Gear-tooth clacks — a per-tooth step gated to the rise window (spec §8), on the SAME true forward advance.
        UpdateToothClack(theta, dph);

        // Beam RELEASE (top) + STRIKE (bottom) as FORWARD crossings of the driver's own phase boundaries — robust to a
        // frame that steps clean over the ~4%-of-a-cycle drop (a sampled-Y reversal could alias it away, and with
        // ChopperStrikes=1 that drops the whole log's crack). arm-fall fires ALWAYS while turning; the strike thuds
        // ALWAYS and additionally throws chips + cracks wood when a real log is being chopped (CanChop).
        float ph = ChopperAnim.ArmCyclePhase(theta);
        if (!armTracking) { prevArmPh = ph; armTracking = true; return; } // first sample: just seed the phase (startPh)

        float startPh = prevArmPh;
        prevArmPh = ph;
        if (dph <= 0f) return;           // idle / no forward motion this frame: no discrete strike (forward stroke only)

        float rise = ChopperAnim.ArmRiseFrac;
        // Order matters when one (skipped) frame crosses both: release (top) BEFORE strike (bottom) — whoosh then thud.
        if (CrossedForward(startPh, dph, rise)) OnArmReleased(mats);
        if (CrossedForward(startPh, dph, rise + ChopperAnim.ArmDropFrac)) OnStrikeBottom(mats);
    }

    /// <summary>Whether a forward advance of <paramref name="dph"/> from cycle phase <paramref name="startPh"/> passes the
    /// <paramref name="boundary"/> phase (in the cyclic [0,1)). True iff the forward distance from start to the boundary
    /// is positive and within this frame's advance — so the boundary is crossed exactly once, on the frame that steps
    /// onto/over it, even if the frame steps clean over the narrow drop window.</summary>
    static bool CrossedForward(float startPh, float dph, float boundary)
    {
        float d = boundary - startPh;
        d -= (float)Math.Floor(d); // forward distance start→boundary in [0,1)
        return d > 0f && d <= dph;
    }

    /// <summary>The beam was released at the top of its stroke (phase crossing <see cref="ChopperAnim.ArmRiseFrac"/>) —
    /// play the "шурх" whoosh at the head's live (raised) position, from which it falls. Turning-gated (log or not — the
    /// arm always drops). One-shot, fire-and-forget.</summary>
    void OnArmReleased(float[]?[]? mats)
    {
        Vec3d at = PartLiveWorld(mats, HeadPartIndex) ?? ChipPointWorld() ?? MachineCenter();
        PlayRandomAt(SndArmFall, at, ArmFallVolume, FxRange, ArmFallPitchJitter);
    }

    /// <summary>The beam bottomed out (the strike) — play the maul-on-bed thud ALWAYS (log or not, user spec) at the
    /// head↔bed contact, and — only when a real log is being chopped (<see cref="CanChop"/>) — throw the species chips and
    /// crack the wood (the hand-chop set; the crack fires every strike a log is loaded, so a log cracks
    /// ChopperStrikes times over its pass — once, on the split, at the default of 1). One-shots, fire-and-forget.</summary>
    void OnStrikeBottom(float[]?[]? mats)
    {
#if IW_DEBUG
        // [chopper-sync] desync diagnostic (2026-07-14): the CLIENT's VISIBLE strike moment. Pair its @…ms with the
        // "SERVER strike" line's @…ms for the same log — that time gap IS the "дрова выпадают, потом анимация" desync.
        // animAngle vs the server's serverAnimAngle = the accumulator offset; net= (client's synced network angle) vs the
        // server's net= shows whether the network angle itself is aligned. seeded= confirms the one-time phase seed fired.
        capi?.Logger.Notification("[immersivewoodworking] [chopper-sync] CLIENT strike @{0}ms animAngle={1:0.000} ph={2:0.000} net={3:0.000} seeded={4} canChop={5}",
            capi.World.ElapsedMilliseconds, animAngle, ChopperAnim.ArmCyclePhase(animAngle * Config.ChopperWorkScale), InputMechBehavior?.Network?.AngleRad ?? -999f, animAngleSeeded, CanChop());
#endif
        Vec3d at = ChipPointWorld() ?? MachineCenter();
        PlayRandomAt(SndAxeHit, at, AxeHitVolume, FxRange, AxeHitPitchJitter);
        if (CanChop())
        {
            SpawnChipBurst();
            PlayRandomAt(SndChopWood, at, ChopWoodVolume, FxRange, ChopWoodPitchJitter);
            // Latch that the client's OWN visible strike fired this loaded log's split. If the server's consume-sync
            // then arrives AFTER this (client-leads phase), DetectChopVanish sees the latch and removes the billet at
            // once instead of deferring — no lingering billet, no double burst (see clientSplitFiredThisLog).
            clientSplitFiredThisLog = true;
        }
        else if (pendingChopVanish)
        {
            // The server already consumed the log this cycle (its phase leads the client render); THIS visible landing
            // is where the log should vanish. Drop the held mesh + throw the consumed log's final chips/crack, so the
            // effects land on the axe you SEE strike (and the final burst isn't suppressed by the emptied slot).
            ResolveChopVanish(true);
        }
    }

    /// <summary>Fire <c>gear-hit</c> once per gear tooth: accumulate the true forward phase advance of the drive cycle
    /// (<paramref name="dphCycle"/>, the render's Nyquist-hybrid shaft step — SpeedDecouplingPlan T03.05, NOT a wrapped
    /// <see cref="ChopperAnim.ArmCyclePhase"/> difference of its own) and trigger every <c>ArmRiseFrac/ToothCount</c> of
    /// phase — but ONLY inside the rise window (the toothed arc, <see cref="ChopperAnim.ArmRiseFrac"/>, read from
    /// <paramref name="theta"/>): the drop+dwell remainder is
    /// toothless (the axe falls free) so it stays silent (spec §8). The clack rate is thus <c>ToothCount × revs/sec</c>
    /// — it FOLLOWS the drive RPM with no pitch stretch. Collapses a laggy multi-tooth frame to a single clack (no
    /// machine-gun). Re-seeded whenever the mechanism stops (the caller clears <see cref="toothSeeded"/>). Mirrors the
    /// sawmill's <c>UpdateRatchetClick</c>, stepped per tooth and phase-gated. Client-only.</summary>
    void UpdateToothClack(float theta, float dphCycle)
    {
        float window = ChopperAnim.ArmRiseFrac;
        if (window <= 0f || ToothCount <= 0) return;
        float step = window / ToothCount;

        float ph = ChopperAnim.ArmCyclePhase(theta);
        if (!toothSeeded) { toothAccum = 0f; toothSeeded = true; return; } // first turning frame: seed the window state, bank no tooth
        if (dphCycle <= 0f) return;       // no forward motion this frame → no clack (rectified: forward only)

        // Toothless gap: outside the rise window (the drop+dwell) there are no teeth → no clack, and drop the partial
        // accumulation so re-entering the arc doesn't dump a banked burst.
        if (ph >= window) { toothAccum = 0f; return; }

        toothAccum += dphCycle;           // the SAME true forward advance the strike detector uses — no wrapped-phase aliasing at high geared speed
        if (toothAccum >= step)
        {
            toothAccum %= step; // keep the sub-tooth remainder; at most one clack per frame (collapse a laggy frame)
            Vec3d at = PartPointWorld(LevelerPartIndex) ?? MachineCenter();
            PlayRandomAt(SndGearHit, at, GearHitVolume, FxRange, GearHitPitchJitter);
        }
    }

    // ───────────────────────── Strike chips (T07.04, unchanged) ─────────────────────────

    /// <summary>Throw one cosmetic chip burst at the strike point (under the head, on the bed), coloured from the loaded
    /// log's own texture (<see cref="Block.GetRandomColor"/>, as the sawhorse and <c>ItemAxe</c> do). No-op if the head
    /// pivot or the log can't be resolved (a burst is only ever requested while <see cref="CanChop"/>, so the log is
    /// normally present — but stay defensive). Client-only (its callers guard <c>capi</c>).
    ///
    /// Asks the STACK for its colour, not the world (AuditFixPlan T04.06). A log carries its species in the block code,
    /// so both answer the same for one — but a fed CHOPPING BLOCK carries it as a stack attribute, and the position we
    /// would hand the world overload is the MACHINE's: it would find a chopper there, never the stand being chopped,
    /// and every wood would throw the stand's default-oak chips. The stack overload picks the same texture the
    /// workpiece is painted with (<c>BlockChoppingBlock.GetRandomColor</c>). For a log the two are identical by
    /// construction: both resolve "up" if the block declares it and the first texture otherwise.</summary>
    void SpawnChipBurst()
    {
        Vec3d? at = ChipPointWorld();
        ItemStack? log = InputSlot.Itemstack;
        if (at == null || log?.Block == null) return;

        int color = log.Block.GetRandomColor(capi!, log);
        ChipBurst.Color = color | (255 << 24); // GetRandomColor carries no alpha byte — force opaque (mirrors the sawhorse)

        // A symmetric outward+upward burst off the strike (placeholder force, T07.06). Set on the shared template each
        // spawn (safe: spawning is synchronous + client), exactly like the sawhorse's BarkChips.
        ChipBurst.MinVelocity.Set(ChipMinVel[0], ChipMinVel[1], ChipMinVel[2]);
        ChipBurst.AddVelocity.Set(ChipAddVel[0], ChipAddVel[1], ChipAddVel[2]);
        ChipBurst.MinPos.Set(at.X - ChipSpawnJitter, at.Y - ChipSpawnJitter, at.Z - ChipSpawnJitter);
        ChipBurst.AddPos.Set(2.0 * ChipSpawnJitter, 2.0 * ChipSpawnJitter, 2.0 * ChipSpawnJitter);

        capi!.World.SpawnParticles(ChipBurst);
    }

    /// <summary>Throw the DEFERRED final chips + wood-crack for a log the server ALREADY consumed (so the input slot is
    /// empty and <see cref="SpawnChipBurst"/>/<see cref="CanChop"/> can no longer read it) — coloured from the passed
    /// <paramref name="log"/> the client kept in <c>pendingChopLog</c>. Fired from <see cref="ResolveChopVanish"/> on
    /// the client's visible strike so the final burst lands on the axe you see strike (fixing the server-empties-first
    /// suppression). Chips only for a BLOCK log (a firewood item carries no <c>Block</c> colour, like the live path);
    /// the crack always plays. Client-only.</summary>
    void SpawnDeferredChopEffects(ItemStack log)
    {
        Vec3d? at = ChipPointWorld();
        if (at == null) return;

        if (log.Block != null)
        {
            int color = log.Block.GetRandomColor(capi!, log);
            ChipBurst.Color = color | (255 << 24); // GetRandomColor carries no alpha byte — force opaque (mirrors SpawnChipBurst)
            ChipBurst.MinVelocity.Set(ChipMinVel[0], ChipMinVel[1], ChipMinVel[2]);
            ChipBurst.AddVelocity.Set(ChipAddVel[0], ChipAddVel[1], ChipAddVel[2]);
            ChipBurst.MinPos.Set(at.X - ChipSpawnJitter, at.Y - ChipSpawnJitter, at.Z - ChipSpawnJitter);
            ChipBurst.AddPos.Set(2.0 * ChipSpawnJitter, 2.0 * ChipSpawnJitter, 2.0 * ChipSpawnJitter);
            capi!.World.SpawnParticles(ChipBurst);
        }

        PlayRandomAt(SndChopWood, at, ChopWoodVolume, FxRange, ChopWoodPitchJitter);
    }

    /// <summary>The world-space chip / strike-sound point: the head part's mesh-bbox centre (<see cref="partPivots"/>,
    /// already in block-local coords with the facing rotation baked in), offset to the block and biased down toward the
    /// head↔log contact on the bed. At the strike the head (a <see cref="ChopperDriverType.Ride"/> of the arm) sits at
    /// lift 0 — its authored rest pose — so the rest pivot IS its contact position, no live-matrix transform needed. Null
    /// before the mover meshes are built or if the model has no head part. The Y bias is a PLACEHOLDER, calibrated in
    /// T07.06.</summary>
    Vec3d? ChipPointWorld()
    {
        if (partPivots == null || HeadPartIndex < 0 || HeadPartIndex >= partPivots.Length) return null;
        float[]? piv = partPivots[HeadPartIndex];
        if (piv == null) return null;
        return new Vec3d(Pos.X + piv[0], Pos.Y + piv[1] + ChipContactYBias, Pos.Z + piv[2]);
    }

    // ───────────────────────── Sound plumbing (loop + one-shots + anchors) ─────────────────────────

    /// <summary>Play one random sample from <paramref name="set"/> at <paramref name="at"/> with an optional
    /// ±<paramref name="pitchJitter"/> variety on pitch (tempo lives in the trigger rate / the sample, never a pitch
    /// stretch). Fire-and-forget (no handle to leak); a no-op for an empty set. Client-only.</summary>
    void PlayRandomAt(AssetLocation[] set, Vec3d at, float volume, float range, float pitchJitter)
    {
        if (capi == null || set.Length == 0) return;
        System.Random rnd = capi.World.Rand;
        AssetLocation loc = set[rnd.Next(set.Length)];
        float pitch = pitchJitter > 0f ? 1f + ((float)rnd.NextDouble() * 2f - 1f) * pitchJitter : 1f;
        capi.World.PlaySoundAt(loc, at.X, at.Y, at.Z, null, pitch, range, volume);
    }

    /// <summary>World-space REST position of a part (its mesh-bbox centre <see cref="partPivots"/> offset by
    /// <see cref="BlockEntity.Pos"/>), or null before the mover meshes are built / for an unknown part — used for the
    /// (near-)stationary anchors (the drive-gear loop, the pawl clacks).</summary>
    Vec3d? PartPointWorld(int idx)
    {
        if (partPivots == null || idx < 0 || idx >= partPivots.Length) return null;
        float[]? piv = partPivots[idx];
        if (piv == null) return null;
        return new Vec3d(Pos.X + piv[0], Pos.Y + piv[1], Pos.Z + piv[2]);
    }

    /// <summary>World-space LIVE position of a part: its rest pivot transformed by this frame's animation matrix
    /// (<paramref name="mats"/>, column-major <c>float[16]</c>) and offset by <see cref="BlockEntity.Pos"/> — used for
    /// the moving anchor (the head at the top of the stroke, where the arm-fall whoosh starts). Falls back to the rest
    /// pivot when there is no matrix, or null when the pivot isn't built yet. Mirrors the sawmill's PartLiveWorld.</summary>
    Vec3d? PartLiveWorld(float[]?[]? mats, int idx)
    {
        if (partPivots == null || idx < 0 || idx >= partPivots.Length) return null;
        float[]? piv = partPivots[idx];
        if (piv == null) return null;
        float[]? m = (mats != null && idx < mats.Length) ? mats[idx] : null;
        if (m == null) return new Vec3d(Pos.X + piv[0], Pos.Y + piv[1], Pos.Z + piv[2]);
        float x = m[0] * piv[0] + m[4] * piv[1] + m[8] * piv[2] + m[12];
        float y = m[1] * piv[0] + m[5] * piv[1] + m[9] * piv[2] + m[13];
        float z = m[2] * piv[0] + m[6] * piv[1] + m[10] * piv[2] + m[14];
        return new Vec3d(Pos.X + x, Pos.Y + y, Pos.Z + z);
    }

    /// <summary>Fallback anchor (master cell, one block up) when a part pivot isn't available yet.</summary>
    Vec3d MachineCenter() => new Vec3d(Pos.X + 0.5, Pos.Y + 1.0, Pos.Z + 0.5);

    /// <summary>Stop + free the ambient loop and clear the stroke/tooth trackers on teardown. The one-shots are
    /// fire-and-forget (no handle to stop). Called from the render partial's <c>OnBlockUnloaded</c>/<c>OnBlockRemoved</c>
    /// so a reloaded BE re-syncs cleanly and no loop hangs in the world.</summary>
    void DisposeSound()
    {
        armTracking = false;
        toothSeeded = false;
        // Teardown stops without the graceful fade — the BE is going away, so a 0.4 s tail is pointless and would be
        // cut by the free anyway — and hands the engine's concurrency slot straight back.
        ambientLoop.Dispose();
    }
}
