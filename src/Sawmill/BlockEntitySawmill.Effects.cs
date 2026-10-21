using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// E07/T07.04 + T07.05 (+ the 7-sound work layer) — the automated sawmill's CUT EFFECTS, all client-only. Continuous
/// ambience runs on a 100 ms client tick (<see cref="OnClientEffectsTick"/>, registered in <see cref="InitEffects"/>);
/// the timing-critical one-shots (the sash-reversal taps, the ratchet click, the carriage feed-step) run PER RENDER
/// FRAME (<see cref="UpdateMotionAudio"/>, called by <see cref="SawmillRenderer"/> from the SAME matrices it draws) so
/// each lands ON its visible event — the wood-on-wood impact at the dead-centre, the tooth, the notch — rather than ~a
/// 100 ms tick late. Two gates decide what plays:
/// <list type="bullet">
///   <item><b>turning</b> = <see cref="IsMechanismTurning"/> (<c>IsComplete &amp;&amp; EffectiveSawSpeed() &gt; 0</c>) —
///   the SAME predicate that advances the render's <c>animAngle</c>, so a sound plays exactly when the blade visibly
///   moves, log or no log. Drives the continuous <c>slide</c> + ambient <c>squeak</c> loops, the per-stroke
///   <c>tap_top</c>/<c>tap_bot</c> one-shots (sash reversals), and the <c>metal_click</c> ratchet (once per work-clock
///   tooth = <see cref="ImmersiveWoodworkingConfig.SawmillWorkScale"/> teeth per shaft revolution — T02.05).</item>
///   <item><b>cutting</b> = <see cref="CanSaw"/> (T06.05) — a sawable log is actually being fed. Drives the sawdust WAVE
///   (3 cross-spread emitters streamed over each downstroke ∝ the blade's descent — <see cref="EmitSawDustForDescent"/>),
///   the <c>carriage_sliding_forward</c> one-shot (one per forward feed NOTCH — the carriage steps
///   <see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/> times per pass, one per work-clock tooth — NOT a loop, fired at the live
///   carriage position), and the original speed-band stroke samples (<c>sawing_&lt;band&gt;&lt;dir&gt;N</c>, kept verbatim
///   from T07.05). The <c>carriage_sliding_back</c> one-shot fires on the pass-complete edge (the feed snaps 1→0).</item>
/// </list>
/// cutting ⊆ turning by construction, so the cut effects are a subset of the turning ones. Positions come from
/// <see cref="partPivots"/> (built by the renderer, block-local with facing baked in) offset by <see cref="BlockEntity.Pos"/>;
/// the cut point is a deliberate PLACEHOLDER and all volumes/ranges/offsets are tuned in-game (T07.06). All sounds must
/// be MONO .ogg or OpenAL won't spatialize them (memory vs-positional-audio-mono); a missing file plays silently, no
/// crash (memory vs-mod-asset-path-domain). Both loops are driven through <see cref="LoopSound"/>, which stops them on
/// the gate's falling edge, keeps the handle for a cheap restart, and frees the engine's concurrency slot once the mill
/// has stood idle a while or the listener has walked out of earshot (AuditFixPlan T06.03); teardown frees them outright
/// (<see cref="DisposeSound"/>). One-shots are fire-and-forget.
///
/// Sawdust is modelled on the mod's own hand-sawhorse dust (<see cref="BlockEntitySawhorse.SpawnSawDust"/>) — species-
/// coloured flakes off the cut, but emitted as a WAVE: three emitters spread ACROSS the cut (perpendicular to the +Z
/// feed) on the top of the log, STREAMED over each saw DOWNSTROKE in proportion to the blade's descent — so the wave
/// lasts the cut stroke (peaking mid-stroke) and its rate follows the drive speed. Position, force, dust density,
/// lifetime and the carriage-feed window are baked constants (the <c>Dust*</c> consts below; finalized in-game 2026-06-21).
/// </summary>
public partial class BlockEntitySawmill
{
    // Sawdust template, reused per spawn (Color / MinPos / AddPos set each time) — a copy of the sawhorse's SawDust,
    // safe because particle spawning is synchronous + client-only. Small, short-lived flakes that fall off the cut.
    static readonly SimpleParticleProperties SawDust = new SimpleParticleProperties(
        1, 3,
        ColorUtil.ToRgba(255, 170, 130, 90), // overwritten per spawn from the log's own texture (species colour)
        new Vec3d(), new Vec3d(),
        new Vec3f(-0.25f, -0.6f, -0.25f),    // mostly downward, slight sideways scatter
        new Vec3f(0.25f, -0.05f, 0.25f),
        0f,                                   // life — dead: SpawnDustBurst overwrites it with DustLifetimeSeconds
        1f,                                   // gravity — flakes drop and land
        0.1f, 0.25f,                          // min / max size
        EnumParticleModel.Quad)
    {
        WithTerrainCollision = true,
        WindAffected = true,
        WindAffectednes = 0.3f,
    };

    // Index of the saw (blade) part in SawmillParts.Rules — the cut happens at the blade. Its mesh-bbox centre
    // (partPivots, built by the renderer) is the placeholder cut point; the small downward bias drops the spawn toward
    // where the blade meets the fed log. Both are calibrated against the authored geometry in T07.06.
    static readonly int SawPartIndex = Array.FindIndex(SawmillParts.Rules, r => r.IsSaw);
    const double CutPointYBias = -0.35; // blocks below the blade-mesh centre — the SOUND cut point (placeholder; T07.06)

    // Sawdust WAVE (Q1/Q2 2026-06-20): three emitters spread ACROSS the cut (native +X, ⟂ the +Z feed) on the top of
    // the log, all fired together once per saw DOWNSTROKE (UpdateStrokeSound). Their geometry, force, count, lifetime
    // and the carriage-feed window when they're active are baked constants (the Dust* consts below; finalized in-game
    // 2026-06-21, no longer config). Authored in the SOUTH/native frame; RotateLocalToFacing turns the horizontal
    // offset onto the placed facing exactly as the footprint's RotateToFacing does (south = identity). The base launch
    // velocity below is scaled by DustVelocity — it mirrors the hand sawhorse's downward+outward scatter.
    static readonly float[] BaseDustMinVel = { -0.25f, -0.60f, -0.25f };
    static readonly float[] BaseDustAddVel = {  0.50f,  0.55f,  0.50f };

    // Sawdust WAVE geometry / density / lifetime, FINALIZED in-game and BAKED from config (2026-06-21; no longer live
    // config — same treatment as the saw-sound shaping knobs above). DustSpread = half-width of the 3-emitter cross
    // across the feed (native +X); DustAlong = offset along the feed (native +Z); DustHeight = vertical drop onto the
    // log top; DustVelocity = launch-force multiplier on BaseDust*Vel; DustQuantity = particles per emitter per BLOCK
    // of blade descent; DustLifetimeSeconds = how long a flake is VISIBLE, in real seconds (AuditFixPlan T06.02 /
    // F-L10-07: the engine multiplies a template's LifeLength by 5 at spawn — see ParticleLife — so the "2" this
    // constant used to hold was 10 s on screen, nearly all of it a landed quad running terrain physics 30 times a
    // second; cut to a third by author decision 2026-08-28); [DustFeedStart, DustFeedEnd] = the fraction of the
    // carriage feed travel over which dust flies (the blade is in the log).
    const float DustSpread = 0.15f;
    const float DustAlong = 0f;
    const float DustHeight = 0.5f;
    const float DustVelocity = 1f;
    const float DustQuantity = 50f;
    const float DustLifetimeSeconds = 3.3f;
    const float DustFeedStart = 0.1f;
    const float DustFeedEnd = 0.9f;

    // Per-stroke saw samples, grouped by SPEED BAND (slow / medium / fast) and stroke DIRECTION (forward = DOWN,
    // backward = UP) — naming sawing_<band><dir><n>, e.g. ff1 = fast-forward-1, sb2 = slow-backward-2. Two variants
    // each, picked at random; the band is chosen from NetworkSpeed at the reversal (StrokeSoundSet). The band sample
    // already carries the saw's tempo, so we no longer pitch-stretch by speed — only a ±15% variety jitter rides on top.
    // Own-domain path INCLUDING "sounds/" (memory vs-mod-asset-path-domain: a wrong domain plays SILENTLY, no warning;
    // PlaySoundAt does NOT auto-prepend "sounds/", extension auto-added). The FAST pair (ff*/fb*) is deliberately SHARED
    // with the hand sawhorse's animation-sound wildcard (patches/sawing_animations.json: saw/sawing_ff* / saw/sawing_fb*).
    static AssetLocation Saw(string suffix) => new AssetLocation("immersivewoodworking", "sounds/saw/sawing_" + suffix);
    static readonly AssetLocation[] SlowDown   = { Saw("sf1"), Saw("sf2") };
    static readonly AssetLocation[] SlowUp     = { Saw("sb1"), Saw("sb2") };
    static readonly AssetLocation[] MediumDown = { Saw("mf1"), Saw("mf2") };
    static readonly AssetLocation[] MediumUp   = { Saw("mb1"), Saw("mb2") };
    static readonly AssetLocation[] FastDown   = { Saw("ff1"), Saw("ff2") };
    static readonly AssetLocation[] FastUp     = { Saw("fb1"), Saw("fb2") };
    const float StrokePitchJitter = 0.15f; // ±15% random pitch per stroke (variety only — tempo is baked into the band sample)
    const float StrokeSoundRange = 16f;
    const float StrokeSoundVolume = 0.7f;

    // Sound shaping, FINALIZED in-game and baked from the config (2026-06-19; no longer live config). Band split by
    // NetworkSpeed: < SpeedSlowMax → slow band, ≥ SpeedFastMin → fast band, between → medium. The FORWARD (cutting)
    // stroke gets a nonlinear onset delay = SoundDelayMaxSeconds × (1−speed)^SoundDelayExponent (0 at/above speed 1) so
    // its "shhk" lands mid-stroke; the BACKWARD (return) stroke plays immediately (its tuned factor was 0). Volume is
    // constant (the volume-speed-follow knob tuned to 0).
    const float SpeedSlowMax = 0.34f;
    const float SpeedFastMin = 0.67f;
    const float SoundDelayMaxSeconds = 0.8f;
    const float SoundDelayExponent = 1.25f;

    // ───────────────────────── 7-sound work layer (new) ─────────────────────────

    // The work sounds the user authored — own-domain, "sounds/saw/" folder, no extension (auto-added), lowercase by
    // VS asset convention (rename "Slide.ogg" → "slide.ogg"). MUST be MONO to spatialize (memory vs-positional-audio-mono);
    // a missing file is silent, not a crash. The two loops + ambient squeak are continuous; the taps/click/back are one-shots.
    static AssetLocation SawFx(string name) => new AssetLocation("immersivewoodworking", "sounds/saw/" + name);
    static readonly AssetLocation SndSlide        = SawFx("slide");                    // sash slide, loop
    static readonly AssetLocation SndSqueak       = SawFx("squeak");                   // metal squeak, ambient loop
    static readonly AssetLocation SndTapTop       = SawFx("tap_top");                  // blade hits the top dead centre
    static readonly AssetLocation SndTapBot       = SawFx("tap_bot");                  // blade hits the bottom dead centre
    static readonly AssetLocation SndMetalClick   = SawFx("metal_click");              // ratchet tooth (once per work-clock tooth — T02.05)
    static readonly AssetLocation SndCarriageFwd  = SawFx("carriage_sliding_forward"); // carriage feed notch (one-shot per forward step)
    static readonly AssetLocation SndCarriageBack = SawFx("carriage_sliding_back");    // carriage snap-back on pass complete

    // Part indices into SawmillParts.Rules for the sound anchor points (resolved once). The wheel is the gear-tower
    // anchor for the ambient squeak; the carriage carries the feed-forward loop; lever_active is the ratchet pawl.
    static readonly int WheelPartIndex       = Array.FindIndex(SawmillParts.Rules, r => r.Id == "wheel");
    static readonly int CarriagePartIndex    = Array.FindIndex(SawmillParts.Rules, r => r.Id == "carriage");
    static readonly int LeverActivePartIndex = Array.FindIndex(SawmillParts.Rules, r => r.Id == "lever_active");

    // Tunables (placeholders, baked in-game at T07.06). Loops carry the machine ambience; one-shots punctuate it.
    const float LoopRange = 24f;
    const float FxRange = 16f;
    const float SlideVolume = 0.6f;
    const float SqueakVolume = 0.35f;
    const float CarriageVolume = 0.55f;
    const float CarriagePitchJitter = 0.06f;
    const float CarriageBackVolume = 0.6f;
    const float TapVolume = 0.7f;
    const float TapPitchJitter = 0.08f;
    const float ClickVolume = 0.55f;
    const float ClickPitchJitter = 0.10f;

    // The two continuous loops. Their whole lifecycle — the start/stop edges, the earshot gate, freeing the engine's
    // concurrency slot after a spell of idling, the latch on a load failure — lives in LoopSound, shared with the
    // chopper's ambience (AuditFixPlan T06.03). No fade length is passed: unlike the chopper's mechanism bed, these two
    // start and stop hard, as they always have. The carriage feed is NOT a loop — it's a per-notch one-shot (see
    // UpdateCarriageFeed) — and the one-shots are fire-and-forget (no handle to leak).
    readonly LoopSound slideLoop = new LoopSound(SndSlide, SlideVolume, LoopRange);
    readonly LoopSound squeakLoop = new LoopSound(SndSqueak, SqueakVolume, LoopRange);

    // Index of the sash (the vertically reciprocating blade frame) in SawmillParts.Rules — its matrix Y is the saw's
    // vertical position, sampled each tick to detect stroke reversals (down → f, up → b).
    static readonly int SashPartIndex = Array.FindIndex(SawmillParts.Rules, r => r.Id == "sash");

    // Stroke tracker: the previous sampled sash Y and the last non-flat motion direction (-1 down, +1 up, 0 unknown). A
    // sign flip is a reversal → fire the matching one-shot. Reset whenever the machine stops turning so it re-syncs.
    float prevSashY;
    int sashDir;
    bool sashTracking;

    // Sawdust streaming accumulator: fractional particles-per-emitter carried between frames so the motion-proportional
    // emission (EmitSawDustForDescent) loses no dust to integer rounding. Reset when the machine stops / leaves the cut.
    float sawDustAccum;

    // Ratchet-click tracker: accumulate the shaft's wrap-corrected revolution fraction SCALED by SawmillWorkScale (=speed)
    // and fire metal_click once per WORK-CLOCK tooth (T02.05: the geared wheel steps SawmillWorkScale teeth per shaft rev,
    // so a per-shaft-revolution click desynced from the tooth at speed != 1). Re-seeded whenever the mechanism stops turning.
    float ratchetAngleRef;
    float ratchetAccum;
    bool ratchetSeeded;

    // Carriage forward-step tracker: the last sampled feed NOTCH = floor(clientProgress · SawmillDefaultWork). The carriage
    // advances in SawmillDefaultWork discrete notches per pass; fire carriage_sliding_forward once each time the notch index
    // increases (one forward step). Re-seeded whenever the machine isn't cutting, so a new/reset pass doesn't fire on
    // the snap-back (a decrease never triggers).
    int lastFeedStep;
    bool feedStepSeeded;

    // Carriage-return tracker: the previous sampled feed fraction. A forward feed only increases, so a sharp drop from
    // high progress is the pass-complete snap-back → fire carriage_sliding_back once on that edge.
    float lastReturnSample;
    bool returnSeeded;

    /// <summary>Whether the mechanism is actually MOVING right now (<c>IsComplete &amp;&amp; EffectiveSawSpeed() &gt; 0</c>) —
    /// the same gate the render uses to advance <c>animAngle</c>, so the turning-gated sounds (slide/squeak/taps/click)
    /// start and stop exactly with the visible reciprocation, log or no log. Also the FIRST half of
    /// <see cref="CanSaw"/> (AuditFixPlan T05.03), which is the containment made explicit: sawing is turning plus a
    /// resolvable log, and the expensive half is never asked of a mill that isn't running.</summary>
    bool IsMechanismTurning() => IsComplete && EffectiveSawSpeed() > 0f;

    /// <summary>Register the client-side cut-effects tick (sawdust + the work sounds). Called from <see cref="Initialize"/>
    /// only on the client (<c>capi != null</c>); a no-op on the dedicated server. Loops are lazily loaded on first start,
    /// so a sawmill that never runs never loads them.</summary>
    void InitEffects()
    {
        lastReturnSample = clientProgress; // seed the return detector from the loaded fraction (no spurious drop on load)
        returnSeeded = true;
        // 100 ms heartbeat for the continuous ambience (slide/squeak loops + the coarse carriage-return cue); the sawdust
        // and the timing-critical one-shots run per render frame (UpdateMotionAudio / UpdateStrokeSound), not here. BE
        // tick listeners are auto-removed on unload, like the server heartbeat — only the LoopSounds hold an engine
        // resource and need an explicit dispose (DisposeSound, from the render partial's teardown).
        RegisterGameTickListener(OnClientEffectsTick, 100);
    }

    /// <summary>Client cut-effects heartbeat (100 ms) — now just the continuous ambience: the slide/squeak loops
    /// (started/stopped with the mechanism turning) and the coarse pass-complete carriage-return cue
    /// (<see cref="DetectCarriageReturn"/>). The timing-critical one-shots (taps, ratchet, carriage feed-step) AND the
    /// per-downstroke sawdust wave run per render frame in <see cref="UpdateMotionAudio"/> /
    /// <see cref="UpdateStrokeSound"/>, so they land on the visible motion event. Client-only by registration.</summary>
    void OnClientEffectsTick(float dt)
    {
        if (capi == null) return;

        // Continuous ambience only — the timing-critical one-shots (taps, ratchet, carriage feed-step) AND the sawdust
        // wave moved to UpdateMotionAudio / UpdateStrokeSound (per render frame), so the dust pulses on the visible
        // downstroke instead of a coarse 100 ms grid. This tick runs only the loops + the pass-complete return cue
        // (coarse-OK here). Loop positions use the REST pivots (a loop needn't track the sub-block bob).
        bool turning = IsMechanismTurning();
        slideLoop.Update(capi, dt, turning, PartPointWorld(SashPartIndex) ?? MachineCenter());
        squeakLoop.Update(capi, dt, turning, PartPointWorld(WheelPartIndex) ?? MachineCenter());

        DetectCarriageReturn();
    }

    /// <summary>
    /// Per-FRAME motion audio — called by <see cref="SawmillRenderer"/> on the opaque stage with the SAME part matrices
    /// it renders that frame, so the discrete one-shots land ON the visible motion event instead of ~a 100 ms tick
    /// behind it (the lag the coarse effects tick gave the taps). Sash-reversal taps + the band stroke sample +
    /// the ratchet click are turning-gated (a frozen idle pose never reverses → silent); the carriage feed-step and the
    /// per-downstroke sawdust wave are cutting-gated. <paramref name="mats"/> may be null before the mover meshes are built — the readers handle it.
    /// Client-only (the renderer is the sole caller).
    /// </summary>
    public void UpdateMotionAudio(float[]?[]? mats)
    {
        if (capi == null) return;
        bool cutting = CanSaw();

        if (IsMechanismTurning())
        {
            UpdateStrokeSound(mats, cutting); // taps every reversal; band stroke sample only while cutting
            UpdateRatchetClick();             // metal_click once per work-clock tooth (T02.05)
        }
        else
        {
            if (sashTracking) { sashTracking = false; sashDir = 0; sawDustAccum = 0f; } // dropped → re-sync, drop the dust remainder
            ratchetSeeded = false;
        }

        if (cutting) UpdateCarriageFeed(mats); // carriage_sliding_forward — one-shot per forward feed notch
        else feedStepSeeded = false;           // dropped → next pass re-seeds (the snap-back never fires a step)
    }

    /// <summary>Emit the sawdust for ONE FRAME of the blade's DESCENT (T07.04, Q1/Q2 2026-06-20): accrue
    /// <see cref="DustQuantity"/> particles-per-emitter for every BLOCK the blade has
    /// moved down this frame (<paramref name="descend"/> ≥ 0), spawn the whole-particle part and carry the fraction to
    /// the next frame (<see cref="sawDustAccum"/>) — so the spawn is smooth and framerate-independent, the wave is
    /// spread over the whole down-stroke, and its total/rate scale with the blade's travel (∝ drive speed). Gated to the
    /// carriage-feed window [<see cref="DustFeedStart"/>,
    /// <see cref="DustFeedEnd"/>] ("в какой момент относительно движения тележки"), so
    /// dust only flies while the blade is in the log. Client-only; the caller gates on the cutting down-stroke.</summary>
    void EmitSawDustForDescent(float descend)
    {
        // Feed-window gate: outside [FeedStart, FeedEnd] the blade isn't in the log → no dust, and drop the remainder so
        // re-entering the window doesn't dump a banked burst.
        float prog = GameMath.Clamp(clientProgress, 0f, 1f);
        if (prog < DustFeedStart || prog > DustFeedEnd) { sawDustAccum = 0f; return; }

        sawDustAccum += descend * DustQuantity; // particles per emitter per block of blade descent
        int perEmitter = (int)sawDustAccum;
        if (perEmitter <= 0) return;
        sawDustAccum -= perEmitter; // keep the sub-particle remainder for the next frame (smooth, no rounding loss)
        SpawnDustBurst(perEmitter);
    }

    /// <summary>Spawn one burst of <paramref name="perEmitter"/> sawdust particles at EACH of the three
    /// <see cref="SawDustWaveWorld"/> emitters spread across the cut — one frame's worth of the streaming wave (see
    /// <see cref="EmitSawDustForDescent"/>). Coloured from the loaded log's own texture (<see cref="Block.GetRandomColor"/>,
    /// as the sawhorse and <c>ItemAxe</c> do, so birch reads lighter than oak) and shaped by the live force/lifetime
    /// knobs. No-op if the blade pivot or the log can't be resolved. Client-only.</summary>
    void SpawnDustBurst(int perEmitter)
    {
        Vec3d[]? pts = SawDustWaveWorld();
        ItemStack? log = InputSlot.Itemstack;
        if (pts == null || log?.Block == null) return;

        int color = log.Block.GetRandomColor(capi!, Pos, BlockFacing.UP);
        SawDust.Color = color | (255 << 24); // GetRandomColor carries no alpha byte — force opaque (mirrors the sawhorse)

        // Apply the live FORCE / LIFETIME knobs to the shared template (safe: spawning is synchronous + client).
        float vf = DustVelocity;
        SawDust.MinVelocity.Set(BaseDustMinVel[0] * vf, BaseDustMinVel[1] * vf, BaseDustMinVel[2] * vf);
        SawDust.AddVelocity.Set(BaseDustAddVel[0] * vf, BaseDustAddVel[1] * vf, BaseDustAddVel[2] * vf);
        SawDust.MinQuantity = perEmitter; // particles per emitter this frame; AddQuantity 0 → an exact count at
                                          // normal time speed (the engine scales Quantity by gamespeed, see ParticleLife)
        SawDust.AddQuantity = 0f;
        SawDust.LifeLength = ParticleLife.Seconds(DustLifetimeSeconds); // real seconds → the engine's ×5 units

        foreach (Vec3d pt in pts) SpawnDustAt(pt);
    }

    /// <summary>Spawn one small sawdust puff centred on world point <paramref name="w"/> (a little position jitter so the
    /// flakes don't stack on one pixel). The shared <see cref="SawDust"/> template carries the colour/velocity/gravity
    /// (colour already set by <see cref="SpawnDustBurst"/>); we only move it here. Mirrors the hand sawhorse's
    /// <c>SpawnDustAt</c>.</summary>
    void SpawnDustAt(Vec3d w)
    {
        SawDust.MinPos.Set(w.X - 0.05, w.Y - 0.05, w.Z - 0.05);
        SawDust.AddPos.Set(0.10, 0.10, 0.10);
        capi!.World.SpawnParticles(SawDust);
    }

    /// <summary>The three world-space sawdust-wave emitter points: the blade pivot
    /// (<see cref="partPivots"/>[<see cref="SawPartIndex"/>]) spread by <see cref="DustSpread"/>
    /// across the feed at <see cref="DustAlong"/> along it (rotated onto the placed
    /// <see cref="Facing"/> by <see cref="RotateLocalToFacing"/>) and dropped by
    /// <see cref="DustHeight"/> onto the log's top, offset to the block. Null before
    /// the mover meshes are built or if the model has no saw part. All three offsets are baked constants, finalized in-game.</summary>
    Vec3d[]? SawDustWaveWorld()
    {
        if (partPivots == null || SawPartIndex < 0 || SawPartIndex >= partPivots.Length) return null;
        float[]? piv = partPivots[SawPartIndex];
        if (piv == null) return null;

        // Three fixed emitters across the feed: −spread / centre / +spread (native +X), all at the configured along/height.
        float spread = DustSpread, along = DustAlong, height = DustHeight;
        float[] cross = { -spread, 0f, spread };
        var pts = new Vec3d[cross.Length];
        for (int i = 0; i < cross.Length; i++)
        {
            Vec3d off = RotateLocalToFacing(cross[i], along);
            pts[i] = new Vec3d(Pos.X + piv[0] + off.X, Pos.Y + piv[1] + height, Pos.Z + piv[2] + off.Z);
        }
        return pts;
    }

    /// <summary>Rotate a horizontal block-local offset authored in the SOUTH/native frame (<paramref name="lx"/> = native
    /// +X across the feed, <paramref name="lz"/> = native +Z along it) onto the placed <see cref="Facing"/> — the SAME
    /// X-Z rotation <see cref="RotateToFacing"/> applies to footprint cells (south = identity), so the dust offsets line
    /// up with the facing-rotated <see cref="partPivots"/>. Y is unrotated. (For non-south facings this shares the
    /// model's own facing calibration, a T07.06 item.)</summary>
    Vec3d RotateLocalToFacing(double lx, double lz)
    {
        int nx = Facing.Normali.X, nz = Facing.Normali.Z;
        return new Vec3d(lx * nz + lz * nx, 0.0, -lx * nx + lz * nz);
    }

    /// <summary>Detect saw-stroke reversals from the animation and fire the matching one-shots: <c>tap_top</c> when the
    /// sash reverses into descending (top dead centre) and <c>tap_bot</c> when it reverses into rising (bottom dead
    /// centre), ALWAYS while turning; plus the original speed-band stroke sample (T07.05's <see cref="TriggerStroke"/>),
    /// fired ONLY when <paramref name="cutting"/> (its log-gated behaviour, unchanged); plus — while DESCENDING and
    /// cutting — the sawdust stream (<see cref="EmitSawDustForDescent"/>), emitted in proportion to the blade's downward
    /// travel each frame so the wave lasts the whole down-stroke and its rate follows the drive speed. Reads the sash
    /// part's vertical position from the prebuilt <paramref name="mats"/> (the live network angle) so the bursts track
    /// the visible blade. Client-only (called from the turning branch of <see cref="UpdateMotionAudio"/>).</summary>
    void UpdateStrokeSound(float[]?[]? mats, bool cutting)
    {
        if (mats == null || SashPartIndex < 0 || SashPartIndex >= mats.Length || mats[SashPartIndex] == null) return;
        float y = mats[SashPartIndex]![13]; // sash follow-driver translates along Y only → m[13] is its vertical offset

        if (!sashTracking) { prevSashY = y; sashDir = 0; sashTracking = true; return; } // first sample: just seed

        // Per-FRAME sampling (UpdateMotionAudio), so the reversal is caught within a frame of the dead-centre — the
        // wood-on-wood impact — not ~a 100 ms tick after, when the sash has already started the next phase. Advance the
        // reference ONLY on motion above the dead-zone: the few near-zero frames right at the extreme (sash velocity → 0)
        // then neither false-flip nor, at low drive speed, starve the detector, and the flip fires the instant the sash
        // leaves the extreme in the new direction.
        float dy = y - prevSashY;
        const float eps = 1e-4f;
        if (dy <= eps && dy >= -eps) return; // sub-dead-zone (at/near an extreme) → hold the reference and wait

        int dir = dy > 0 ? 1 : -1;
        if (sashDir != 0 && dir != sashDir)
        {
            bool intoDown = dir < 0; // reversal into descending = TOP dead centre; into rising = BOTTOM
            Vec3d at = PartLiveWorld(mats, SashPartIndex) ?? MachineCenter();
            PlayOneShotAt(intoDown ? SndTapTop : SndTapBot, at, TapVolume, FxRange, TapPitchJitter);
            if (cutting) TriggerStroke(intoDown); // reversal into down → forward (f) samples, into up → backward (b)
        }

        // Sawdust STREAMS over the cutting DOWNSTROKE in proportion to how far the blade actually descends this frame
        // (motion-driven): the wave lasts the whole down-stroke (Q "чуть дольше"), peaks mid-stroke where the blade moves
        // fastest, and its overall rate scales with the drive speed (more travel/s → more dust/s) — the "∝ speed" the
        // single-burst model couldn't give. Up-strokes (dir > 0) throw nothing (the blade is lifting out of the kerf).
        if (cutting && dir < 0) EmitSawDustForDescent(-dy);

        sashDir = dir;
        prevSashY = y;
    }

    /// <summary>Fire <c>metal_click</c> once per ratchet TOOTH — one work-clock revolution, =
    /// <see cref="ImmersiveWoodworkingConfig.SawmillWorkScale"/> teeth per shaft revolution (SpeedDecouplingPlan T02.05):
    /// accumulate the wrap-corrected revolution fraction of <see cref="AngleRad"/> (which wraps to [0,2π) each turn)
    /// SCALED by <see cref="ImmersiveWoodworkingConfig.SawmillWorkScale"/> and trigger on each whole-tooth crossing,
    /// collapsing multiple teeth in one tick to a single click. So the click lands on the visible wheel tooth at any
    /// speed multiplier, not once per shaft revolution (which only matched the tooth at speed=1). Re-seeded whenever the
    /// mechanism stops turning. Client-only (called from the turning branch, so it clicks log-or-no-log like the wheel).</summary>
    void UpdateRatchetClick()
    {
        float ang = AngleRad;
        if (!ratchetSeeded) { ratchetAngleRef = ang; ratchetAccum = 0f; ratchetSeeded = true; return; }

        float d = ang - ratchetAngleRef;
        if (d > GameMath.PI) d -= GameMath.TWOPI;       // [0,2π) wrap, not real motion → fold to the short-arc delta
        else if (d < -GameMath.PI) d += GameMath.TWOPI;
        ratchetAngleRef = ang;
        if (d < 0f) d = -d; // magnitude: a tooth advances in either rotation sense

        // SpeedDecouplingPlan T02.05 — GEAR the click onto the WORK CLOCK. The visible ratchet wheel (SawmillAnim
        // wheel/spring_rotor, Src="fwd") steps on animAngleFwd·SawmillWorkScale, i.e. SawmillWorkScale (=speed=1/M) teeth
        // per SHAFT revolution, so a raw once-per-shaft-revolution click desynced from the tooth at any speed != 1 (at
        // speed=2 it clicked once per two teeth, at speed=0.5 twice per tooth). Scaling the raw shaft fraction by
        // SawmillWorkScale fires the click once per WORK-CLOCK tooth = exactly one visible wheel tooth at any M, so it
        // lands SawmillDefaultWork times per pass — the same tempo as the carriage notch (UpdateCarriageFeed). At speed=1
        // workScale=1f and this is d/TWOPI exactly, bit-identical to the pre-feature one-click-per-shaft-revolution. It
        // stays on its OWN raw-shaft accumulator (not the cutting-gated clientProgress the carriage reads): the click is
        // turning-gated and must fire log-or-no-log, and the raw accumulator carries no fold artifact (animAngleFwd folds
        // at the pass, losing its 50th tooth), so scaling here gives the true cumulative tooth count.
        ratchetAccum += d * Config.SawmillWorkScale / GameMath.TWOPI;
        if (ratchetAccum >= 1f)
        {
            ratchetAccum -= (float)Math.Floor(ratchetAccum); // keep the sub-tooth remainder; one click per tick max
            Vec3d at = PartPointWorld(LeverActivePartIndex) ?? MachineCenter();
            PlayOneShotAt(SndMetalClick, at, ClickVolume, FxRange, ClickPitchJitter);
        }
    }

    /// <summary>Fire <c>carriage_sliding_forward</c> once on EACH forward feed notch — the carriage advances in
    /// <see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/> discrete steps over a pass (one ratchet tooth per work-clock revolution), so the
    /// notch index <c>floor(clientProgress · SawmillDefaultWork)</c> increments once per forward step. Plays at the live carriage
    /// position (so it tracks the travelling cart). Only increases trigger — the pass-complete reset (a decrease) never
    /// fires here (that's <see cref="DetectCarriageReturn"/>); seeded on the first cutting tick so a mid-pass load /
    /// resume doesn't fire a stray step. Multiple notches in one tick collapse to a single play. Client-only (called from
    /// the cutting branch).</summary>
    void UpdateCarriageFeed(float[]?[]? mats)
    {
        int step = (int)(GameMath.Clamp(clientProgress, 0f, 1f) * ImmersiveWoodworkingConfig.SawmillDefaultWork);
        if (!feedStepSeeded) { lastFeedStep = step; feedStepSeeded = true; return; } // first cutting tick: seed, don't fire

        if (step > lastFeedStep)
        {
            Vec3d at = PartLiveWorld(mats, CarriagePartIndex) ?? MachineCenter();
            PlayOneShotAt(SndCarriageFwd, at, CarriageVolume, FxRange, CarriagePitchJitter);
        }
        lastFeedStep = step; // a decrease (pass reset) updates without firing
    }

    /// <summary>Fire <c>carriage_sliding_back</c> once on the pass-complete snap-back. The feed fraction only ever
    /// INCREASES while cutting and is reset to 0 by the server's pass completion (synced via <c>FromTreeAttributes</c>),
    /// so a sharp drop from a high fraction is that reset — the carriage jumping back to the infeed. (There is no
    /// animated ~2 s return in the live mod yet; the carriage snaps. A real return animation is a T07.03 follow-up.)
    /// Runs every tick regardless of the gates. Client-only.</summary>
    void DetectCarriageReturn()
    {
        float p = clientProgress;
        if (!returnSeeded) { lastReturnSample = p; returnSeeded = true; return; }
        if (lastReturnSample > 0.5f && p < lastReturnSample - 0.3f)
        {
            Vec3d at = PartPointWorld(CarriagePartIndex) ?? MachineCenter();
            PlayOneShotAt(SndCarriageBack, at, CarriageBackVolume, FxRange, 0f);
        }
        lastReturnSample = p;
    }

    /// <summary>The sample set for the current drive speed + stroke direction: slow / medium / fast BAND by
    /// <see cref="NetworkSpeed"/> (<see cref="SpeedSlowMax"/> / <see cref="SpeedFastMin"/> thresholds), forward (DOWN) vs
    /// backward (UP).</summary>
    AssetLocation[] StrokeSoundSet(float speed, bool down)
    {
        if (speed < SpeedSlowMax) return down ? SlowDown : SlowUp;
        if (speed >= SpeedFastMin) return down ? FastDown : FastUp;
        return down ? MediumDown : MediumUp;
    }

    /// <summary>Fire a stroke sound. The FORWARD (cutting) stroke gets a speed-dependent, NONLINEAR onset delay so its
    /// sound lands when the blade is actually mid-stroke rather than at the momentary turnaround: delay =
    /// <see cref="SoundDelayMaxSeconds"/> × (1 − <see cref="NetworkSpeed"/>)^<see cref="SoundDelayExponent"/> — 0 at/above
    /// "speed 1", small at high speed, rising only toward 0 (≈0.34 s at speed 0.5, up to ≈0.8 s near 0). The BACKWARD
    /// (return) stroke isn't cutting, so it plays IMMEDIATELY at the reversal (its tuned factor was 0). The band +
    /// direction are resolved here from the stroke's speed; the delayed play re-checks <see cref="CanSaw"/>, so a cut
    /// that stops (or the BE unloading) during the wait drops the pending sound instead of firing a stray "shk".</summary>
    void TriggerStroke(bool down)
    {
        float speed = NetworkSpeed;
        AssetLocation[] set = StrokeSoundSet(speed, down);

        if (!down) { PlayStroke(set); return; } // return stroke isn't cutting → play at once (tuned backward factor = 0)

        float t = GameMath.Clamp(1f - speed, 0f, 1f);
        float delaySec = SoundDelayMaxSeconds * (float)Math.Pow(t, SoundDelayExponent);
        int delayMs = (int)(delaySec * 1000f);
        if (delayMs <= 0) { PlayStroke(set); return; }
        capi!.Event.RegisterCallback(_ => { if (capi != null && CanSaw()) PlayStroke(set); }, delayMs);
    }

    /// <summary>Play one random sample from <paramref name="set"/> (the speed band already chosen by the caller) at the
    /// cut point, at constant volume and a ±15% variety jitter on pitch — the BAND carries the tempo, so no
    /// speed-pitch-stretching here. One-shot, client-local, fire-and-forget; mirrors the chopping block's randomized
    /// play.</summary>
    void PlayStroke(AssetLocation[] set)
    {
        if (capi == null || set.Length == 0) return;
        System.Random rnd = capi.World.Rand;
        AssetLocation loc = set[rnd.Next(set.Length)];

        float pitch = 1f + ((float)rnd.NextDouble() * 2f - 1f) * StrokePitchJitter; // ±15% variety; tempo is in the sample

        Vec3d at = SawCutPointWorld() ?? new Vec3d(Pos.X + 0.5, Pos.Y + 1.0, Pos.Z + 0.5);
        capi.World.PlaySoundAt(loc, at.X, at.Y, at.Z, null, pitch, StrokeSoundRange, StrokeSoundVolume);
    }

    // ───────────────────────── Sound plumbing (loops + one-shots + anchor points) ─────────────────────────

    /// <summary>Play one positioned one-shot with an optional ±<paramref name="pitchJitter"/> variety on pitch.
    /// Fire-and-forget (no handle to leak); client-only.</summary>
    void PlayOneShotAt(AssetLocation loc, Vec3d at, float volume, float range, float pitchJitter)
    {
        if (capi == null) return;
        float pitch = 1f;
        if (pitchJitter > 0f) pitch = 1f + ((float)capi.World.Rand.NextDouble() * 2f - 1f) * pitchJitter;
        capi.World.PlaySoundAt(loc, at.X, at.Y, at.Z, null, pitch, range, volume);
    }

    /// <summary>World-space REST position of a part (its mesh-bbox centre <see cref="partPivots"/> offset by
    /// <see cref="BlockEntity.Pos"/>), or null before the mover meshes are built / for an unknown part — used for the
    /// (near-)stationary anchors (gear-tower squeak, ratchet, the snapped-back carriage).</summary>
    Vec3d? PartPointWorld(int idx)
    {
        if (partPivots == null || idx < 0 || idx >= partPivots.Length) return null;
        float[]? piv = partPivots[idx];
        if (piv == null) return null;
        return new Vec3d(Pos.X + piv[0], Pos.Y + piv[1], Pos.Z + piv[2]);
    }

    /// <summary>World-space LIVE position of a part: its rest pivot transformed by this frame's animation matrix
    /// (<paramref name="mats"/>, column-major <c>float[16]</c>) and offset by <see cref="BlockEntity.Pos"/> — used for
    /// the moving anchors (the sash at a reversal, the travelling carriage). Falls back to the rest pivot when there is
    /// no matrix, or null when the pivot isn't built yet.</summary>
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

    /// <summary>Stop + free the loop sounds and clear the stroke/ratchet trackers on teardown. The one-shots are
    /// fire-and-forget (no handle to stop). Called from the render partial's <c>OnBlockUnloaded</c>/<c>OnBlockRemoved</c>
    /// so a reloaded BE re-syncs cleanly and no loop hangs in the world.</summary>
    void DisposeSound()
    {
        sashTracking = false;
        sashDir = 0;
        ratchetSeeded = false;

        slideLoop.Dispose();
        squeakLoop.Dispose();
    }

    /// <summary>World-space cut point (T07.04/T07.05 placeholder): the blade part's mesh-bbox centre
    /// (<see cref="partPivots"/>, already in block-local coords with the facing rotation baked in by the mesh build),
    /// offset to the block position and biased down toward where the blade meets the fed log. Null before the mover
    /// meshes are built (no renderer yet) or if the model has no saw part. Calibrated against the authored geometry in
    /// T07.06 (the small Y bias, and whether the point should track the carriage feed).</summary>
    Vec3d? SawCutPointWorld()
    {
        if (partPivots == null || SawPartIndex < 0 || SawPartIndex >= partPivots.Length) return null;
        float[]? piv = partPivots[SawPartIndex];
        if (piv == null) return null;
        return new Vec3d(Pos.X + piv[0], Pos.Y + piv[1] + CutPointYBias, Pos.Z + piv[2]);
    }
}
