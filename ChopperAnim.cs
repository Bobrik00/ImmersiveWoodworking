using System;
using System.Collections.Generic;

namespace ImmersiveWoodworking;

/// <summary>How one chopper part is driven each frame — a TRIMMED mirror of <see cref="SawmillDriverType"/> plus the
/// chopper-only <c>Trip</c> (rise-and-drop). The chopper mechanism is a single kinematic chain (gear train → pawls →
/// axe beam → head), so the sawmill's slider-crank family (<c>Rod</c>/<c>Follow</c>/<c>Feed</c>/<c>Swing</c>/
/// <c>Reciprocate</c>) is not needed — there is no carriage, no connecting rod, no feed ratchet.</summary>
public enum ChopperDriverType { Static, Rotate, Trip, Ride }

/// <summary>One part's animation driver — the subset of the animator-stand parameter set the chopper's four driver
/// types read. A C# transcription of a single <c>defaultParts()</c> entry's <c>driver</c> object in
/// <c>Mods/chopper-animator.html</c>; only the fields a given type reads are meaningful.</summary>
public sealed class ChopperDriver
{
    public ChopperDriverType Type = ChopperDriverType.Static;
    public char Axis = 'z';

    /// <summary>Which drive angle this part reads — the dual-source rectifier (user's 2026-07-05 design, modeled
    /// 2026-07-09): <c>"signed"</c> = the SIGNED accumulator that follows the mechanical network BOTH ways (× the
    /// per-facing <see cref="BlockEntityChopper.FacingDriveSign"/>), so the visible input rotor co-turns with the
    /// connected network gear on every facing and reverses with a reverse-driven line; <c>"fwd"</c> (default) = the
    /// RECTIFIED forward accumulator (|Δθ|, always increasing), so the asymmetric rise-then-strike ALWAYS plays down
    /// regardless of facing or drive direction. ONLY the twoway input coupling (<c>mainRotorTwoway</c>) is
    /// <c>"signed"</c>; the whole gear train → pawls → axe beam is <c>"fwd"</c>. The signed↔fwd direction change at
    /// the junction is hidden by the modeled static housing (<c>rotorStatic</c>).
    ///
    /// SpeedDecouplingPlan E03/T03.03 adds <c>"disc"</c> — the clock for the three cosmetic connection discs
    /// (<c>rotorDefault/2/3</c>): the caller feeds it the RAW shaft-rate rectified clock so the discs hold 1:1 with the
    /// shaft (the gear-them-with-the-mechanism dev option was baked out in T04.03). The mirror of the sawmill's
    /// <c>"disc"</c> Src.</summary>
    public string Src = "fwd";

    public float Ratio = 1f;   // rotate: revolutions of the part per revolution of θ (sign = direction)
    public float Phase = 0f;   // rotate: radians; trip: cycles (fraction of one trip cycle)

    // trip (rise-and-drop): a smooth/linear rise then a sharp `1−t²` drop, one cycle per source revolution.
    public string Mode = "rotate";  // "rotate" (swing about Pivot) | "translate" (shift along Axis)
    public float AmpDeg;            // trip rotate — peak swing in degrees
    public float Amp;               // trip translate — peak shift in blocks
    public float Cycles = 1f;       // trip cycles per θ revolution
    public float RiseFrac = 0.6f;   // fraction of the cycle spent rising
    public float DropFrac = 0.12f;  // fraction spent dropping (the strike); the rest holds low
    public int Rise = 1;            // rise shape: 0 = linear (constant speed), 1 = smooth (smoothstep), 2 = sharp (ease-out)

    public float[]? Pivot;          // explicit rotate/trip-rotate pivot (else the passed mesh-bbox centre is used)

    public string? Ride;            // ride-along: reuse another part's matrix verbatim
}

/// <summary>
/// AuditFixPlan T05.07 / F-L10-05 — one machine's REUSED animation buffers (twin of <see cref="SawmillAnimState"/>):
/// the matrix per part plus the two pass flags. <see cref="ChopperAnim.BuildMatricesFacing"/> writes into these instead
/// of allocating a fresh tree every frame. One instance per block entity, NOT a static: two choppers in view would
/// otherwise overwrite each other's pose.
/// </summary>
public sealed class ChopperAnimState
{
    /// <summary>One live <c>float[16]</c> per part, indexed to <see cref="ChopperParts.Rules"/>. Nullable only to match
    /// the assembler's public return type; every entry is non-null from construction on.</summary>
    internal readonly float[]?[] Mats;

    /// <summary>Topological-pass flag (readiness), as the old local <c>done</c>.</summary>
    internal readonly bool[] Done;

    /// <summary>"Written during THIS build" — set by both passes; replaces the old <c>mats[k] != null</c> test, which
    /// stopped meaning that once the buffers began to persist between frames.</summary>
    internal readonly bool[] Built;

    ChopperAnimState(int n)
    {
        Mats = new float[]?[n];
        for (int i = 0; i < n; i++) Mats[i] = new float[16];
        Done = new bool[n];
        Built = new bool[n];
    }

    /// <summary>Allocate the buffers for one machine (sized to <see cref="ChopperParts.Rules"/>). Called once per client
    /// block entity.</summary>
    public static ChopperAnimState Create() => new ChopperAnimState(ChopperParts.Rules.Length);
}

/// <summary>
/// E07/T07.02 — the procedural-animation matrix assembler for the automated chopper, a faithful C# port of the RUNTIME
/// of <c>Mods/chopper-animator.html</c> (<c>buildMatrices</c>/<c>animMat</c>/<c>tripLift</c>) — the tuned stand the user
/// authored the layout against (T07.00, <c>07-visual-sound/00-anim-spec.md</c> §2–§4). One matrix per part, all driven
/// from a single angle θ=<c>AngleRad</c>, exactly like vanilla's <c>BEHelveHammer</c> drives its head from
/// <c>mptoggle.AngleRad</c> — so every moving part is synchronous by construction with the visible mechanical axle.
///
/// A trimmed sibling of <see cref="SawmillAnim"/>: same <c>float[16]</c> column-major (index 4*col+row) matrix core and
/// the same facing conjugation (<see cref="BuildMatricesFacing"/>, <c>R·A·R⁻¹</c>), but the chopper's chain needs only
/// four driver types. The one genuinely new driver is <c>Trip</c> — a rise-and-drop the sawmill has no precedent for
/// (its levers pull a rod; the chopper's teeth LIFT the beam, then let it fall) — giving the explicit bottom-of-stroke
/// moment the strike chips (T07.04) and thud (T07.05) key on. There is no feed/progress: the chopper log stands still
/// and the axe comes to it, so the driver reads θ alone (no <c>prog</c>, no ratchet, no carriage).
///
/// The driver table (<see cref="Drivers"/>) embeds the tuned layout the user EXPORTED from <c>chopper-animator.html</c>
/// (<c>AutoChopperPlan/chopper_parts.json</c>, 2026-07-05 — NOT the file's stale <c>defaultParts()</c> default), keyed
/// by and ordered to match <see cref="ChopperParts.Rules"/> (the single ordering authority). Units are BLOCKS
/// (pixel/16), angles radians, native model frame ("facing south = identity"); the per-facing rotation is applied by
/// <see cref="BuildMatricesFacing"/>, not here.
/// </summary>
public static class ChopperAnim
{
    const float TAU = (float)(Math.PI * 2.0);
    const float D2R = (float)(Math.PI / 180.0);

    /// <summary>
    /// E12/T12.03b (2026-07-13) — the modeled ÷48 reduction gear train. The network-driven rotor coupling turns the
    /// shaft at 1:1; three stages step it down — <c>clutch1</c> 8→32 (÷4), <c>clutch2</c> 8→32 (÷16), and the cam pair
    /// <c>gearActive/gearPassive</c> 8→24 (÷48) — so the pawls (<c>levelerL/R</c>) and the axe beam (<c>arm</c>) complete
    /// exactly ONE lift-drop per <see cref="ReducerRevs"/> shaft revolutions of the GEARED WORK CLOCK (the "one slow
    /// blow"). Every mover period — 1 rev (rotors), 4 (clutch1), 16 (clutch2), 48 (cam/pawls/arm) — DIVIDES this, so the
    /// whole mechanism re-aligns EXACTLY every <see cref="ReducerRevs"/> revolutions of that clock.
    ///
    /// SpeedDecouplingPlan E03/T03.03 ("без ремодела" revision): the ÷48 mechanism is UNCHANGED (arm/pawls/cam keep
    /// <c>Cycles = 1/ReducerRevs</c>), but the render (<c>AdvanceAnimAngle</c>) and the server strike tracker
    /// (<c>OnChopperTick</c>) no longer wrap the raw accumulator HERE. Instead they accumulate the raw shaft angle,
    /// wrap it at the M-dependent <see cref="ImmersiveWoodworkingConfig.ChopperCycleRevs"/>·2π, and feed the drivers the
    /// GEARED read <c>animAngle × <see cref="ImmersiveWoodworkingConfig.ChopperWorkScale"/></c>, which folds on exactly
    /// one whole arm cycle (= ReducerRevs revolutions of the geared clock) at the wrap. At the shipped default (speed 1)
    /// ChopperCycleRevs = ChopperCycleCap = ReducerRevs, so the wrap is at 48·2π and the geared factor is 1.0 — the
    /// pre-feature behaviour bit-for-bit. This constant is now the SAME nominal as
    /// <see cref="ImmersiveWoodworkingConfig.ChopperCycleCap"/> (it ALIASES it — one source of truth for the "48"), and
    /// stays the LCM of all mover periods.
    /// </summary>
    public const float ReducerRevs = ImmersiveWoodworkingConfig.ChopperCycleCap;

    // ───────────────────────── float[16] matrix math (port of the stand's `M`, shared shape with SawmillAnim) ─────────────────────────

    /// <summary>Reset an EXISTING buffer to the identity — the twin of <c>SawmillAnim.SetIdentity</c> (AuditFixPlan
    /// T05.07): every part's matrix is a buffer this machine already owns, so a build allocates nothing.</summary>
    static void SetIdentity(float[] m)
    {
        m[0] = 1; m[1] = 0; m[2] = 0; m[3] = 0;
        m[4] = 0; m[5] = 1; m[6] = 0; m[7] = 0;
        m[8] = 0; m[9] = 0; m[10] = 1; m[11] = 0;
        m[12] = 0; m[13] = 0; m[14] = 0; m[15] = 1;
    }

    static void Translate(float[] m, float x, float y, float z)
    {
        m[12] += m[0] * x + m[4] * y + m[8] * z; m[13] += m[1] * x + m[5] * y + m[9] * z;
        m[14] += m[2] * x + m[6] * y + m[10] * z; m[15] += m[3] * x + m[7] * y + m[11] * z;
    }

    static void Rot(float[] m, float rx, float ry, float rz)
    {
        if (rx == 0 && ry == 0 && rz == 0) return;
        float sx = (float)Math.Sin(rx), cx = (float)Math.Cos(rx), sy = (float)Math.Sin(ry), cy = (float)Math.Cos(ry), sz = (float)Math.Sin(rz), cz = (float)Math.Cos(rz);
        float a01 = sx * sy, a02 = -cx * sy;
        float m00 = cy * cz, m01 = a01 * cz + cx * sz, m02 = a02 * cz + sx * sz, m10 = -cy * sz, m11 = cx * cz - a01 * sz, m12 = sx * cz - a02 * sz, m20 = sy, m21 = -sx * cy, m22 = cx * cy;
        float o0 = m[0], o1 = m[1], o2 = m[2], o3 = m[3], o4 = m[4], o5 = m[5], o6 = m[6], o7 = m[7], o8 = m[8], o9 = m[9], o10 = m[10], o11 = m[11];
        m[0] = m00 * o0 + m01 * o4 + m02 * o8; m[1] = m00 * o1 + m01 * o5 + m02 * o9; m[2] = m00 * o2 + m01 * o6 + m02 * o10; m[3] = m00 * o3 + m01 * o7 + m02 * o11;
        m[4] = m10 * o0 + m11 * o4 + m12 * o8; m[5] = m10 * o1 + m11 * o5 + m12 * o9; m[6] = m10 * o2 + m11 * o6 + m12 * o10; m[7] = m10 * o3 + m11 * o7 + m12 * o11;
        m[8] = m20 * o0 + m21 * o4 + m22 * o8; m[9] = m20 * o1 + m21 * o5 + m22 * o9; m[10] = m20 * o2 + m21 * o6 + m22 * o10; m[11] = m20 * o3 + m21 * o7 + m22 * o11;
    }

    /// <summary>a := a · b (post-multiply, both column-major). Reads a's originals into locals first, so in-place is safe.</summary>
    static void Mul(float[] a, float[] b)
    {
        float a00 = a[0], a01 = a[1], a02 = a[2], a03 = a[3], a10 = a[4], a11 = a[5], a12 = a[6], a13 = a[7], a20 = a[8], a21 = a[9], a22 = a[10], a23 = a[11], a30 = a[12], a31 = a[13], a32 = a[14], a33 = a[15];
        for (int col = 0; col < 4; col++)
        {
            int bb = col * 4;
            float b0 = b[bb], b1 = b[bb + 1], b2 = b[bb + 2], b3 = b[bb + 3];
            a[bb] = b0 * a00 + b1 * a10 + b2 * a20 + b3 * a30; a[bb + 1] = b0 * a01 + b1 * a11 + b2 * a21 + b3 * a31;
            a[bb + 2] = b0 * a02 + b1 * a12 + b2 * a22 + b3 * a32; a[bb + 3] = b0 * a03 + b1 * a13 + b2 * a23 + b3 * a33;
        }
    }

    /// <summary>a := b · a (PRE-multiply, both column-major) — the allocation-free facing conjugation, twin of
    /// <c>SawmillAnim.MulLeft</c> (T05.07). Same operand order per term as the old clone + post-multiply pair, so the
    /// result is bit-identical.</summary>
    static void MulLeft(float[] a, float[] b)
    {
        for (int col = 0; col < 4; col++)
        {
            int cc = col * 4;
            float a0 = a[cc], a1 = a[cc + 1], a2 = a[cc + 2], a3 = a[cc + 3];
            a[cc] = a0 * b[0] + a1 * b[4] + a2 * b[8] + a3 * b[12];
            a[cc + 1] = a0 * b[1] + a1 * b[5] + a2 * b[9] + a3 * b[13];
            a[cc + 2] = a0 * b[2] + a1 * b[6] + a2 * b[10] + a3 * b[14];
            a[cc + 3] = a0 * b[3] + a1 * b[7] + a2 * b[11] + a3 * b[15];
        }
    }

    static void AxisVec(char a, out float x, out float y, out float z)
    {
        x = a == 'x' ? 1f : 0f;
        y = a == 'y' ? 1f : 0f;
        z = (a != 'x' && a != 'y') ? 1f : 0f;
    }

    // ───────────────────────── Trip (rise-and-drop) profile — port of the stand's tripLift ─────────────────────────

    /// <summary>The rise-and-drop lift ∈[0,1] for a within-cycle phase, given the source cycle count
    /// <paramref name="cyc"/> (=<c>Cycles·θ/2π + Phase</c>): rise over [0, <paramref name="rf"/>] in the shape
    /// <paramref name="rise"/> (0 linear / 1 smooth / 2 sharp), a SHARP <c>1−t²</c> drop over the next
    /// <paramref name="df"/> (the strike — accelerating like a fall), then HELD AT 0 to the cycle end (the
    /// bottom-of-stroke dwell = the moment of impact). A verbatim port of <c>tripLift</c> in
    /// <c>chopper-animator.html</c>.</summary>
    static float TripLift(float cyc, float rf, float df, int rise)
    {
        float r = Clamp01(rf), d = Clamp(df, 0f, 1f - r);
        float ph = cyc - (float)Math.Floor(cyc);
        if (ph <= r)
        {
            float t = r <= 0 ? 1f : ph / r;
            return rise == 0 ? t : rise == 2 ? t * (2f - t) : t * t * (3f - 2f * t);
        }
        if (ph <= r + d)
        {
            float t = d <= 0 ? 1f : (ph - r) / d;
            return 1f - t * t; // sharp drop — the strike; accelerates like a fall
        }
        return 0f; // hold low until the next cycle — the bottom of the stroke is the strike moment
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

    // ───────────────────────── Driver table (mirror of chopper-animator.html defaultParts) ─────────────────────────

    static readonly ChopperDriver[] Drivers = BuildDrivers();
    static readonly Dictionary<string, int> IndexById = BuildIndex();

    // ───────────────────────── Effects seam (T07.05) — expose the axe-beam's Trip cycle ─────────────────────────

    // The axe-beam ("arm") driver, resolved once (declared AFTER IndexById so the static init order has the index ready).
    // The tooth-clack gate (BlockEntityChopper.Effects.UpdateToothClack) reads its RiseFrac + cycle phase so the gear
    // clacks share the EXACT rise window that shapes the visible lift — one riseFrac/cyc, no duplicated literal, so a
    // T07.06 retune moves the clacks WITH the lift instead of drifting off it (spec §8). Falls back to a default driver
    // if the model somehow lacks an "arm" part (defensive — "arm" is always in ChopperParts.Rules).
    static readonly ChopperDriver ArmDriverRef = ResolveArm();

    static ChopperDriver ResolveArm() { int i = Idx("arm"); return i >= 0 ? Drivers[i] : new ChopperDriver(); }

    /// <summary>The axe-beam's rise fraction (the toothed arc of the cycle, spec §8): the gear-tooth clacks
    /// (<c>BlockEntityChopper.Effects.UpdateToothClack</c>) fire over phase [0, this) and fall silent through the
    /// drop+dwell (the toothless gap where the axe falls free). Also the phase where the beam is RELEASED (its top →
    /// arm-fall). The SAME <c>RiseFrac</c> that shapes the arm's <see cref="TripLift"/>, so a T07.06 retune moves the
    /// clacks and the lift together.</summary>
    public static float ArmRiseFrac => ArmDriverRef.RiseFrac;

    /// <summary>The axe-beam's drop fraction: the beam bottoms out (its Y minimum = the STRIKE) at phase
    /// <c>ArmRiseFrac + ArmDropFrac</c>, then holds low through the dwell (spec §8). The strike-sound / chip seam
    /// (<c>BlockEntityChopper.Effects</c>) fires on the forward crossing of that phase — robust to a frame that
    /// straddles the narrow drop window (the sampled-Y detector could alias it away at high RPM / a frame hitch).</summary>
    public static float ArmDropFrac => ArmDriverRef.DropFrac;

    /// <summary>The axe-beam's within-cycle phase ∈[0,1) for drive angle <paramref name="theta"/> — <c>frac(Cycles·θ/2π
    /// + Phase)</c>, the SAME cyc that drives the arm's <see cref="TripLift"/> (spec §8). The tooth-clack gate reads it so
    /// the clacks stay locked to the visible lift across the four facings and any recalibration.</summary>
    public static float ArmCyclePhase(float theta)
    {
        float cyc = ArmDriverRef.Cycles * (theta / TAU) + ArmDriverRef.Phase;
        return cyc - (float)Math.Floor(cyc);
    }

    /// <summary>The axe-beam's within-cycle LIFT ∈ [0,1] for drive angle <paramref name="theta"/> — the head's height
    /// as a fraction of its travel (0 = at the bottom of the stroke / the idle rest pose, buried on the bed; 1 = fully
    /// raised). The animation-tied INPUT GATE (<see cref="BlockEntityChopper.HeadClearOfBed"/>) reads it so a new log
    /// can only be loaded once the head has physically risen clear of the bed — the SAME lift that shapes the arm's
    /// visible <see cref="TripLift"/> (spec §8), so the gate tracks the axe you actually see rather than a wall-clock
    /// timer. Same cyc as <see cref="ArmCyclePhase"/>.</summary>
    public static float ArmLift(float theta)
    {
        float cyc = ArmDriverRef.Cycles * (theta / TAU) + ArmDriverRef.Phase;
        return TripLift(cyc, ArmDriverRef.RiseFrac, ArmDriverRef.DropFrac, ArmDriverRef.Rise);
    }

    static Dictionary<string, int> BuildIndex()
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < ChopperParts.Rules.Length; i++) d[ChopperParts.Rules[i].Id] = i;
        return d;
    }

    static int Idx(string? id) => id != null && IndexById.TryGetValue(id, out int i) ? i : -1;

    static float[] V(float x, float y, float z) => new float[] { x, y, z };

    /// <summary>Build the per-part drivers in <see cref="ChopperParts.Rules"/> order (the ordering authority). Values
    /// are the user's tuned <c>chopper-animator.html</c> export (2026-07-13, WITH the ÷48 reduction gear train — see
    /// <see cref="ReducerRevs"/>) transcribed VERBATIM, except the exact <c>1/48</c> fractions replace the export's
    /// rounded <c>0.0208</c> (the tooth counts give EXACTLY 1/48, so the code is precise). The signed twoway coupling
    /// spins at 1:1 with the shaft (<c>Rotate</c> about X, Src="signed"); the three cosmetic discs read the Src="disc"
    /// clock (SpeedDecouplingPlan T03.03 — the raw shaft-rate clock, so they hold 1:1 with the shaft; the geared-disc dev
    /// option was baked out in T04.03); the reducer gears step the GEARED work clock down (<c>clutch1</c> ÷4,
    /// <c>clutch2</c> ÷16, the cam pair ÷48, all <c>Rotate</c> about Z), the pawls lift-and-drop (<c>Trip</c> rotate,
    /// mirror ∓15°, once per 48 revs of that clock), the axe beam lifts-and-drops (<c>Trip</c> translate Y — the strike,
    /// once per 48 revs), the head rides the beam (<c>Ride</c>), the frame/base is <c>Static</c>. Any rule id without an
    /// entry falls to Static.</summary>
    static ChopperDriver[] BuildDrivers()
    {
        var rules = ChopperParts.Rules;
        var arr = new ChopperDriver[rules.Length];
        for (int i = 0; i < rules.Length; i++)
        {
            arr[i] = rules[i].Id switch
            {
                // ── ROTOR / RECTIFIER assembly (ONLY mainRotorTwoway reverses w/ shaft; the three cosmetic discs are flag-switchable) ─────
                // Re-tuned 2026-07-09 from the user's chopper-animator export: the twoway coupling is SPLIT into per-ring
                // movers, each with its own axis + spin sign. INPUT-DIRECTION SPLIT (user, 2026-07-10, refined): ONLY the
                // twoway coupling (mainRotorTwoway) is SIGNED — it co-turns with the connected network gear and REVERSES with
                // a reverse-driven line. The downstream mechanism (gears/pawls/arm) stays rectified-forward so the strike
                // always plays DOWN. A NO-explicit-Pivot part falls back to the mesh-bbox centre at runtime.
                // SpeedDecouplingPlan E03/T03.03 — CONNECTION DISCS (mirror of the sawmill T02.02). The three cosmetic
                // connection discs (rotorDefault / rotorDefault2 / rotorDefault3) read Src="disc": the caller feeds "disc"
                // the RAW shaft-rate rectified clock (animAngleDisc, M-INDEPENDENT), so these discs hold 1:1 with the shaft
                // — the honest "the shaft turns the coupling, the ÷48 reduction is inside" look. This used to be a dev knob
                // (ChopperGearConnectionDiscs) that could instead gear the discs with the mechanism at 1/M; the author
                // judged the 1:1 look live and it was baked in (T04.03), so the geared-disc option is gone. Unlike the
                // sawmill there is NO crank disc among them (the chopper's chain is gear→pawl→arm, no rod). At M=1
                // animAngleDisc equals the pre-feature rotor spin, so there is no visible difference at the shipped default.
                "rotorDefault"    => new ChopperDriver { Type = ChopperDriverType.Rotate, Src = "disc", Axis = 'x', Ratio = -1f, Phase = 0f, Pivot = V(-0.67f, 3.5f, -0.5f) }, // Rotor_default_1
                "rotorDefault2"   => new ChopperDriver { Type = ChopperDriverType.Rotate, Src = "disc", Axis = 'x', Ratio =  1f, Phase = 0f },                                 // Rotor_default_2 (auto pivot)
                "mainRotorTwoway" => new ChopperDriver { Type = ChopperDriverType.Rotate, Src = "signed", Axis = 'x', Ratio = -1f, Phase = 0f },                                 // MainRotor_twoway (22 el.; Rotor_default_3 ring un-nested 2026-07-09, auto pivot)
                // rotorDefault3 (15 el., un-nested 2026-07-09). Src="disc" (RECTIFIED, M-independent, 1:1 with the shaft)
                // like the other two cosmetic discs — spins one way and does NOT reverse when the shaft is driven the
                // other way. Only mainRotorTwoway reverses with the shaft now (user 2026-07-10). Axis Z, ratio −1 (which way it spins is a visual pick to confirm in-game).
                "rotorDefault3"   => new ChopperDriver { Type = ChopperDriverType.Rotate, Src = "disc", Axis = 'z', Ratio = -1f, Phase = 0f },
                // rotorStatic — the modeled housing that hides the signed↔forward junction. Mover + STATIC driver, gated
                // on hasDrive, so it appears only once the drive is installed (like the coupling it shrouds).
                "rotorStatic"     => new ChopperDriver { Type = ChopperDriverType.Static },

                // ── GEAR TRAIN → PAWLS → AXE BEAM (RECTIFIED FORWARD — Src default "fwd", strike always down) ───────────
                // REDUCER stages (E12/T12.03b, from the user's 2026-07-13 chopper-animator export): each turns 1/N of the
                // shaft, fwd-rectified (default Src) so the strike always plays down, auto pivot (the gears are centred on
                // their own hubs). clutch1 = ÷4 output, clutch2 = ÷16 output; they multiply into the cam pair's ÷48.
                "clutch1"     => new ChopperDriver { Type = ChopperDriverType.Rotate, Axis = 'z', Ratio =  1f / 4f,  Phase = 0f },
                "clutch2"     => new ChopperDriver { Type = ChopperDriverType.Rotate, Axis = 'z', Ratio = -1f / 16f, Phase = 0f },
                // CAM PAIR — the ÷48 output stage (8→24 on the cam shaft). Its 16 teeth lift the axe rack; here it is just
                // the visible gear spin (1/48 rev per shaft rev), counter-rotating pair. Pivots = the authored hubs.
                "gearActive"  => new ChopperDriver { Type = ChopperDriverType.Rotate, Axis = 'z', Ratio =  1f / ReducerRevs, Phase = 0f, Pivot = V(0.041f, 3.5f, 0.437f) },
                "gearPassive" => new ChopperDriver { Type = ChopperDriverType.Rotate, Axis = 'z', Ratio = -1f / ReducerRevs, Phase = 0f, Pivot = V(0.959f, 3.5f, 0.562f) },
                // PAWLS — tooth-follower swings up then snaps back (Trip rotate about the hinge Y≈3.165), rise SMOOTH,
                // short 0.12 drop, phase 0.6. Left/right mirror hinges (∓15°). Now ONE swing per 48 shaft revs (reducer).
                "levelerL" => new ChopperDriver { Type = ChopperDriverType.Trip, Mode = "rotate", Axis = 'z', AmpDeg = -15f, Cycles = 1f / ReducerRevs, Phase = 0.6f, RiseFrac = 0.35f, DropFrac = 0.12f, Rise = 1, Pivot = V(0.922f, 3.165f, 0.5f) },
                "levelerR" => new ChopperDriver { Type = ChopperDriverType.Trip, Mode = "rotate", Axis = 'z', AmpDeg = 15f, Cycles = 1f / ReducerRevs, Phase = 0.6f, RiseFrac = 0.35f, DropFrac = 0.12f, Rise = 1, Pivot = V(0.078f, 3.165f, 0.5f) },
                // AXE BEAM — slow LINEAR lift (riseFrac 0.82) then a hard snap drop (dropFrac 0.02): vertical Y translate,
                // amp 1.38. ONE lift-drop per 48 shaft revs (Cycles = 1/ReducerRevs — the reducer's "one slow blow"). The
                // bottom of this stroke is the strike (chips/thud + the server split key on ArmRiseFrac/ArmDropFrac, spec §8).
                "arm" => new ChopperDriver { Type = ChopperDriverType.Trip, Mode = "translate", Axis = 'y', Amp = 1.38f, Cycles = 1f / ReducerRevs, Phase = 1f, RiseFrac = 0.82f, DropFrac = 0.02f, Rise = 0 },
                // HEAD — rides the beam verbatim, staying seated on the axe as it lifts/drops.
                "head" => new ChopperDriver { Type = ChopperDriverType.Ride, Ride = "arm" },

                _ => new ChopperDriver { Type = ChopperDriverType.Static }, // log (hidden), frame
            };
        }
        return arr;
    }

    // ───────────────────────── Assembler ─────────────────────────

    // Block-centre fallback pivot, shared and read-only (T05.07: was a fresh float[3] on every miss).
    static readonly float[] DefaultPivot = { 0.5f, 1f, 0.5f };

    static float[] PivotOf(int i, float[]?[]? pivots)
    {
        ChopperDriver dv = Drivers[i];
        if (dv.Pivot != null) return dv.Pivot; // every animated chopper rotate/trip-rotate part has an explicit pivot
        if (pivots != null && i >= 0 && i < pivots.Length && pivots[i] != null) return pivots[i]!;
        return DefaultPivot;
    }

    // Static dependency graph (F-S05-19, twin F-S04-07): a part's deps are a pure function of the static Drivers table +
    // IndexById (the chopper's only edge is Ride→target, head→arm), so precompute the whole graph once instead of
    // allocating a List<int> per part on every topo-sort pass, every frame, in BuildMatrices. Declared below
    // Drivers/IndexById so the static init has them ready; DepsOf keeps its signature as an O(1) lookup.
    static readonly int[][] PartDeps = BuildDeps();

    static int[][] BuildDeps()
    {
        var deps = new int[Drivers.Length][];
        for (int i = 0; i < Drivers.Length; i++) deps[i] = ComputeDeps(i);
        return deps;
    }

    static int[] DepsOf(int i) => PartDeps[i];

    static int[] ComputeDeps(int i)
    {
        ChopperDriver dv = Drivers[i];
        var ids = new List<int>();
        if (dv.Type == ChopperDriverType.Ride && !string.IsNullOrEmpty(dv.Ride)) { int k = Idx(dv.Ride); if (k >= 0) ids.Add(k); }
        return ids.ToArray();
    }

    /// <summary>The drive angle a part reads — the SIGNED accumulator (network-following, already × FacingDriveSign by
    /// the caller) for the input coupling, the flag-switchable DISC clock for the cosmetic connection discs, else the
    /// GEARED forward work clock. See <see cref="ChopperDriver.Src"/>.</summary>
    static float Theta(ChopperDriver dv, float thetaFwd, float thetaSigned, float thetaDisc) =>
        dv.Src == "signed" ? thetaSigned : dv.Src == "disc" ? thetaDisc : thetaFwd;

    static void EvalPart(int i, float thetaFwd, float thetaSigned, float thetaDisc, ChopperAnimState st, float[]?[]? pivots)
    {
        ChopperDriver dv = Drivers[i];
        if (dv.Type == ChopperDriverType.Ride)
        {
            int ti = Idx(dv.Ride);
            // COPY, don't alias (T05.07): the head shared the arm's very array, which the in-place facing conjugation
            // would then rotate twice — and an aliased entry can't own a reusable buffer either.
            if (ti >= 0 && st.Built[ti]) Array.Copy(st.Mats[ti]!, st.Mats[i]!, 16);
            else SetIdentity(st.Mats[i]!);
            return;
        }
        AnimMat(i, dv, thetaFwd, thetaSigned, thetaDisc, st, pivots);
    }

    /// <summary>Build one matrix per part from θ, in topological order (a <c>Ride</c> part is evaluated once its target
    /// is — the head after the arm it rides). Returns a <c>float[16]</c> per part, indexed to
    /// <see cref="ChopperParts.Rules"/>. Port of the stand's <c>buildMatrices</c>; the chopper's only dependency is
    /// head→arm, so the guard loop settles in two passes.
    ///
    /// The dual-source split (the rectifier): <paramref name="thetaSigned"/> drives only the input coupling
    /// (<c>Src=="signed"</c> — the twoway rotor/rings, already × <see cref="BlockEntityChopper.FacingDriveSign"/> by the
    /// caller), so the visible input co-turns with the connected network gear on every facing and reverses with the line;
    /// <paramref name="thetaFwd"/> (the rectified |Δθ| accumulator) drives everything downstream (gear train → pawls → axe
    /// beam), so the drop-maul's asymmetric rise-then-strike ALWAYS plays down regardless of facing or drive direction.
    /// The facing of the whole machine is still handled by the conjugation in <see cref="BuildMatricesFacing"/> (a
    /// similarity transform, correct on every facing); FacingDriveSign only re-aligns the SIGNED input to the network
    /// axle, it never touches the forward chain (a global θ flip would reverse the strike — the reason a plain
    /// per-facing sign can't be used here).</summary>
    /// AuditFixPlan T05.07 / F-L10-05: every buffer belongs to the caller's <see cref="ChopperAnimState"/> and is
    /// REUSED, so a build allocates nothing. <see cref="ChopperAnimState.Built"/> replaces the old "mats[k] != null"
    /// test — with the buffers persisting, a non-null matrix no longer means "written during THIS build".</summary>
    public static float[]?[] BuildMatrices(ChopperAnimState st, float[]?[]? pivots, float thetaFwd, float thetaSigned, float thetaDisc)
    {
        int n = Drivers.Length;
        bool[] done = st.Done, built = st.Built;
        Array.Clear(done, 0, n);
        Array.Clear(built, 0, n);
        int remaining = n, guard = 0;
        while (remaining > 0 && guard++ <= n)
        {
            bool progressed = false;
            for (int i = 0; i < n; i++)
            {
                if (done[i]) continue;
                bool ready = true;
                foreach (int d in DepsOf(i)) if (!done[d]) { ready = false; break; }
                if (ready) { EvalPart(i, thetaFwd, thetaSigned, thetaDisc, st, pivots); done[i] = true; built[i] = true; remaining--; progressed = true; }
            }
            if (!progressed) break; // no cycles in the chopper chain, but keep the guard the stand uses
        }
        for (int i = 0; i < n; i++) if (!done[i]) { EvalPart(i, thetaFwd, thetaSigned, thetaDisc, st, pivots); built[i] = true; }
        return st.Mats;
    }

    /// <summary>
    /// T07.06 — build the part matrices for a machine placed on a non-south facing. The whole driver table is authored
    /// in the NATIVE (south) frame, but the mover meshes are tesselated with the facing baked in (about the block centre,
    /// the rotation <c>Block.Shape.rotateY</c> encodes). So a baked vertex is <c>v = R·v_native</c>, and the correct
    /// world transform for that baked mesh is the native animation CONJUGATED by the facing rotation:
    /// <c>A_world = R · A_native · R⁻¹</c>. When <paramref name="facing"/>/<paramref name="facingInv"/> are null (south,
    /// rotateY 0) this is exactly <see cref="BuildMatrices"/> with no per-part cost. Mirror of
    /// <see cref="SawmillAnim.BuildMatricesFacing"/>.
    ///
    /// Because every animated chopper part carries an EXPLICIT native pivot in the driver table, <paramref name="nativePivots"/>
    /// is only a fallback (never consumed by a real mover) — but it is still threaded through for parity and in case a
    /// future part is added without an explicit pivot.
    /// </summary>
    public static float[]?[] BuildMatricesFacing(ChopperAnimState st, float[]?[]? nativePivots, float thetaFwd, float thetaSigned, float thetaDisc, float[]? facing, float[]? facingInv)
    {
        float[]?[] mats = BuildMatrices(st, nativePivots, thetaFwd, thetaSigned, thetaDisc);
        if (facing == null || facingInv == null) return mats; // south / rotateY 0 → no conjugation
        for (int i = 0; i < mats.Length; i++)
        {
            // In place (T05.07): the old path cloned the facing matrix per part and dropped the native matrix it had
            // just built. Bit-identical — MulLeft keeps the same operand order per term.
            float[]? m = mats[i];
            if (m == null) continue;
            MulLeft(m, facing);  // m = R · A_native
            Mul(m, facingInv);   // m = R · A_native · R⁻¹
        }
        return mats;
    }

    static void AnimMat(int i, ChopperDriver dv, float thetaFwd, float thetaSigned, float thetaDisc, ChopperAnimState st, float[]?[]? pivots)
    {
        float theta = Theta(dv, thetaFwd, thetaSigned, thetaDisc); // signed for the input coupling, disc for the cosmetic discs, else geared forward
        float[] m = st.Mats[i]!;
        SetIdentity(m);
        switch (dv.Type)
        {
            case ChopperDriverType.Static:
                return;

            case ChopperDriverType.Rotate:
            {
                float[] piv = PivotOf(i, pivots);
                float a = dv.Ratio * theta + dv.Phase;
                Translate(m, piv[0], piv[1], piv[2]);
                Rot(m, dv.Axis == 'x' ? a : 0, dv.Axis == 'y' ? a : 0, dv.Axis == 'z' ? a : 0);
                Translate(m, -piv[0], -piv[1], -piv[2]);
                return;
            }

            case ChopperDriverType.Trip:
            {
                // cyc = Cycles·θ/2π + Phase. θ is the rectified-forward accumulator for every Trip part (all Src="fwd"),
                // so the rise-then-strike always plays down. lift ∈ [0,1].
                float cyc = dv.Cycles * (theta / TAU) + dv.Phase;
                float lift = TripLift(cyc, dv.RiseFrac, dv.DropFrac, dv.Rise);
                if (dv.Mode == "rotate")
                {
                    float[] piv = PivotOf(i, pivots);
                    float a = dv.AmpDeg * D2R * lift;
                    Translate(m, piv[0], piv[1], piv[2]);
                    Rot(m, dv.Axis == 'x' ? a : 0, dv.Axis == 'y' ? a : 0, dv.Axis == 'z' ? a : 0);
                    Translate(m, -piv[0], -piv[1], -piv[2]);
                }
                else // translate
                {
                    AxisVec(dv.Axis, out float ux, out float uy, out float uz);
                    float d = dv.Amp * lift;
                    Translate(m, ux * d, uy * d, uz * d);
                }
                return;
            }
        }
        // Ride is handled in EvalPart (copies the target's matrix); never reaches here
    }
}
