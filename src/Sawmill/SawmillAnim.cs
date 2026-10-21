using System;
using System.Collections.Generic;

namespace ImmersiveWoodworking;

/// <summary>How one sawmill part is driven each frame (mirror of the <c>driver.type</c> field in
/// <c>AutomatedSawmillPlan/sawmill_parts.json</c> / the <c>sawmill-animator.html</c> stand).</summary>
public enum SawmillDriverType { Static, Rotate, Swing, Reciprocate, Rod, Feed, Follow, Ride }

/// <summary>One part's animation driver — the full parameter set the animator stand exposes. A C# transcription of a
/// single <c>parts[]</c> entry's <c>driver</c> object in <c>sawmill_parts.json</c>; only the fields a given driver
/// type reads are meaningful.</summary>
public sealed class SawmillDriver
{
    public SawmillDriverType Type = SawmillDriverType.Static;
    public char Axis = 'x';
    public string Src = "theta";        // "theta" (AngleRad) | "prog" (sawmillProgress, mapped 0..1 -> 0..2π)
    public float Ratio = 1f;
    public float Phase = 0f;

    // rotate ratchet (one tooth per source cycle, smooth window [RiseStart, RiseEnd])
    public bool Ratchet;
    public int Teeth = 12;
    public float? RiseStart;
    public float? RiseEnd;
    public float? Engage;               // legacy fallback for RiseEnd

    // feed (stepped staircase synced to the ratchet)
    public bool Stepped;
    public int Cycles = ImmersiveWoodworkingConfig.SawmillDefaultWork; // sawmill pass length (F-L2-07: the one number)
    public bool SmoothReturn;
    public float Travel;
    public float Offset;

    public float Amp;                   // reciprocate
    public float AmpDeg;                // swing

    public float[]? Pivot;              // explicit rotate/swing pivot (else the mesh-bbox centre is used)

    // rod (two-point link: A near a crank pin, B a slider / wheel-rim / second pin)
    public float[]? Anchor;
    public float[]? AnchorB;
    public string? PinFrom;
    public string? PinFromBottom;
    public bool SlideB;
    public char SlideAxis = 'y';
    public bool Stretch = true;
    public bool RideRim;
    public string? RimPart;
    public float? RimRadius;
    public char WheelAxis = 'x';

    // follow (slide along one axis tracking a point carried by another part's matrix)
    public float[]? Point;
    public string? Target;

    public string? Ride;                // ride-along: reuse another part's matrix verbatim
}

/// <summary>
/// AuditFixPlan T05.07 / F-L10-05 — one machine's REUSED animation buffers: the matrix per part, the two pass flags and
/// the rod's rotation scratch. <see cref="SawmillAnim.BuildMatricesFacing"/> writes into these instead of allocating a
/// fresh tree every frame, so a turning sawmill costs the GC nothing per frame.
///
/// One instance per block entity, NOT a static: two sawmills in view would otherwise overwrite each other's pose (the
/// BE hands the same array to the renderer across frozen frames — see <c>GetPartMatrices</c>). Every consumer treats
/// the result as read-only for the duration of the frame, which is what makes the reuse safe.
/// </summary>
public sealed class SawmillAnimState
{
    /// <summary>One live <c>float[16]</c> per part, indexed to <see cref="SawmillParts.Rules"/>. Declared nullable only
    /// to match the assembler's public return type; every entry is non-null from construction on.</summary>
    internal readonly float[]?[] Mats;

    /// <summary>Topological-pass flag: set when a part's dependencies were satisfied and it was evaluated in the sorted
    /// pass. Drives readiness, exactly like the old local <c>done</c>.</summary>
    internal readonly bool[] Done;

    /// <summary>"Its matrix was written during THIS build" — set by BOTH the topological pass and the index-order sweep
    /// that breaks the lever cycle. Replaces the old <c>mats[k] != null</c> test, which stopped meaning that the moment
    /// the buffers began to persist between frames.</summary>
    internal readonly bool[] Built;

    internal readonly float[] Rot3 = new float[9];
    internal readonly float[] Rot4 = new float[16];

    SawmillAnimState(int n)
    {
        Mats = new float[]?[n];
        for (int i = 0; i < n; i++) Mats[i] = new float[16];
        Done = new bool[n];
        Built = new bool[n];
    }

    /// <summary>Allocate the buffers for one machine (sized to <see cref="SawmillParts.Rules"/>). Called once per client
    /// block entity.</summary>
    public static SawmillAnimState Create() => new SawmillAnimState(SawmillParts.Rules.Length);
}

/// <summary>
/// E07/T07.02 — the procedural-animation matrix assembler for the automated sawmill, a faithful C# port of the
/// RUNTIME of <c>sawmill-animator.html</c> (<c>buildMatrices</c>/<c>animMat</c>, NOT the stand's simplified
/// <c>SwingAngle</c> C# sketch — the stand renders with the full-3D <c>alignRot3</c> Damped-Track rod, so that is what
/// the user tuned against and what we reproduce). One matrix per part, all driven from a single angle θ=<c>AngleRad</c>
/// (+ <c>sawmillProgress</c> for the feed), exactly like vanilla's <c>BEHelveHammer</c> drives its head from
/// <c>mptoggle.AngleRad</c> — so every moving part is synchronous by construction with the visible mechanical axle.
///
/// The driver table (<see cref="Drivers"/>) embeds the user's tuned layout from <c>sawmill_parts.json</c>, keyed by and
/// ordered to match <see cref="SawmillParts.Rules"/> (the single ordering authority; this is the same "C# mirror of the
/// stand" pattern the plan already uses for membership). Units are BLOCKS (pixel/16), angles radians, native model
/// frame ("facing south = identity"); the per-facing rotation is applied by the renderer/T07.06, not here.
///
/// SpeedDecouplingPlan T02.01/T02.02: the <c>fwd</c> clock the caller hands in (<paramref name="thetaFwd"/> of
/// <see cref="BuildMatrices"/>) is the GEARED WORK CLOCK <c>animAngleFwd × SawmillWorkScale</c> (workScale = 1/M), not the
/// raw rectified accumulator — so the Src="fwd" chain (crank disc / sash / saw / levers / ratchets) runs at 1/M of the
/// shaft while <c>mainRotorTwoway</c> stays 1:1 with it on the signed <c>theta</c>. The three cosmetic connection discs
/// read a THIRD clock, <c>thetaDisc</c> (Src="disc"): the caller feeds it the raw shaft-rate rectified clock so the discs
/// hold 1:1 with the shaft (the gear-them-with-the-mechanism dev option was baked out to this look in SpeedDecouplingPlan
/// T04.03). At M=1 all three fwd/disc clocks coincide and this is bit-identical to the pre-feature pose. The gearing lives
/// entirely at the call site; the driver table stays config-free (invariant I5).
///
/// Matrices are <c>float[16]</c>, column-major (index 4*col+row) — the same layout as the engine's <c>Mat4f</c>/
/// <c>Matrixf.Values</c> — so the renderer can feed a part matrix straight to <c>IShaderProgram.ModelMatrix</c>.
/// </summary>
public static class SawmillAnim
{
    const float TAU = (float)(Math.PI * 2.0);
    const float D2R = (float)(Math.PI / 180.0);

    // SpeedDecouplingPlan T02.01 (F-L2-07): the pass-length number the carriage steps / the ratchet teeth mirror — 50 by
    // default — is now the ONE canonical <see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/>, not a private
    // FeedCycles const that merely coincided at 50. The driver literals below reference it directly (a compile-time const,
    // so BuildDrivers reads no live Config — invariant I5).

    // ───────────────────────── float[16] matrix math (port of the stand's `M`) ─────────────────────────

    /// <summary>Reset an EXISTING buffer to the identity. AuditFixPlan T05.07: the assembler writes every part's matrix
    /// into a buffer it already owns (<see cref="SawmillAnimState"/>), so a build allocates nothing — this replaces the
    /// old <c>Id()</c> that handed out a fresh <c>float[16]</c> per part per frame.</summary>
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

    static void Scale(float[] m, float x, float y, float z)
    {
        for (int i = 0; i < 4; i++) { m[i] *= x; m[4 + i] *= y; m[8 + i] *= z; }
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

    /// <summary>a := b · a (PRE-multiply, both column-major). Reads each of a's columns into locals first, so in-place is
    /// safe. The allocation-free half of the facing conjugation (T05.07): the old path cloned the facing matrix and
    /// post-multiplied the part matrix into the clone, throwing the part's own freshly-built buffer away one line later
    /// (the "stillborn half" of F-L10-05, paid on every facing but south). Same operand order per term as that clone +
    /// <see cref="Mul"/> pair, so the result is bit-identical.</summary>
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

    // The vector helpers hand their result back through out-params rather than a fresh float[3] (T05.07): a rod alone
    // asked for a dozen of them per frame.
    static void Vec(float[] m, float x, float y, float z, out float ox, out float oy, out float oz)
    {
        ox = m[0] * x + m[4] * y + m[8] * z + m[12];
        oy = m[1] * x + m[5] * y + m[9] * z + m[13];
        oz = m[2] * x + m[6] * y + m[10] * z + m[14];
    }

    static void AxisVec(char a, out float x, out float y, out float z)
    {
        x = a == 'x' ? 1f : 0f;
        y = a == 'y' ? 1f : 0f;
        z = (a != 'x' && a != 'y') ? 1f : 0f;
    }

    /// <summary>Smoothstep ramp of a within-cycle phase <paramref name="ph"/>∈[0,1) inside the window [rs, re]: 0
    /// below rs, 1 above re, smoothstep between — the ratchet's "hold / step / hold" envelope for one tooth. A
    /// degenerate re ≤ rs collapses to a hard step at rs.</summary>
    static float SmoothWindow(float ph, float rs, float re)
    {
        if (re <= rs) return ph >= rs ? 1f : 0f;
        if (ph <= rs) return 0f;
        if (ph >= re) return 1f;
        float t = (ph - rs) / (re - rs);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Staircase accumulation: +1 per source cycle, ramped smoothly (<see cref="SmoothWindow"/>) inside the
    /// phase window [rs, re], held outside it (the ratchet's "no motion / stepping / done" envelope).</summary>
    static float CyclicStep(float cyc, float rs, float re)
    {
        float n = (float)Math.Floor(cyc);
        return n + SmoothWindow(cyc - n, rs, re);
    }

    /// <summary>Minimal rotation (Rodrigues) aligning unit d0 onto unit d — the full-3D analogue of a Damped Track, so a
    /// rod's head↦A' and tail↦B' both land exactly in any plane. Writes 3×3 row-major (out = R·p) into
    /// <paramref name="R"/> (the caller's scratch, <see cref="SawmillAnimState.Rot3"/>) instead of returning a fresh
    /// array — same arithmetic, term for term.</summary>
    static void AlignRot3(float d0x, float d0y, float d0z, float dx, float dy, float dz, float[] R)
    {
        float vx = d0y * dz - d0z * dy, vy = d0z * dx - d0x * dz, vz = d0x * dy - d0y * dx;
        float c = d0x * dx + d0y * dy + d0z * dz;
        if (c < -0.999999f) // antiparallel → 180° about any perpendicular axis
        {
            float tx = Math.Abs(d0x) < 0.9f ? 1f : 0f, ty = Math.Abs(d0x) < 0.9f ? 0f : 1f, tz = 0f;
            float kx = d0y * tz - d0z * ty, ky = d0z * tx - d0x * tz, kz = d0x * ty - d0y * tx;
            float l = (float)Math.Sqrt(kx * kx + ky * ky + kz * kz); if (l == 0) l = 1; kx /= l; ky /= l; kz /= l;
            R[0] = 2 * kx * kx - 1; R[1] = 2 * kx * ky; R[2] = 2 * kx * kz;
            R[3] = 2 * ky * kx; R[4] = 2 * ky * ky - 1; R[5] = 2 * ky * kz;
            R[6] = 2 * kz * kx; R[7] = 2 * kz * ky; R[8] = 2 * kz * kz - 1;
            return;
        }
        float k = 1f / (1f + c);
        R[0] = 1 - (vy * vy + vz * vz) * k; R[1] = -vz + vx * vy * k; R[2] = vy + vx * vz * k;
        R[3] = vz + vx * vy * k; R[4] = 1 - (vx * vx + vz * vz) * k; R[5] = -vx + vy * vz * k;
        R[6] = -vy + vx * vz * k; R[7] = vx + vy * vz * k; R[8] = 1 - (vx * vx + vy * vy) * k;
    }

    /// <summary>3×3 row-major (out = R·p) → 4×4 column-major in <paramref name="m"/>, ready for <see cref="Mul"/>.</summary>
    static void Rot3to4(float[] R, float[] m)
    {
        m[0] = R[0]; m[1] = R[3]; m[2] = R[6]; m[3] = 0;
        m[4] = R[1]; m[5] = R[4]; m[6] = R[7]; m[7] = 0;
        m[8] = R[2]; m[9] = R[5]; m[10] = R[8]; m[11] = 0;
        m[12] = 0; m[13] = 0; m[14] = 0; m[15] = 1;
    }

    // Src → drive clock. "prog" = the carriage feed (0..1 → 0..2π). "fwd" = the GEARED work clock
    // (animAngleFwd·workScale, 1/M of the shaft) — the crank disc + the whole slider-crank/ratchet mechanism. "disc" =
    // the connection-disc clock (SpeedDecouplingPlan T02.02): the caller feeds it the RAW shaft-rate rectified clock so the
    // discs hold 1:1 with the shaft (the gear-them-with-the-mechanism dev option was baked out, T04.03). Anything else =
    // the SIGNED theta (mainRotorTwoway). At M=1 all three fwd/disc clocks coincide, so the Src split is invisible.
    static float DriveVar(SawmillDriver dv, float theta, float thetaFwd, float thetaDisc, float prog) =>
        dv.Src == "prog" ? prog * TAU : dv.Src == "fwd" ? thetaFwd : dv.Src == "disc" ? thetaDisc : theta;

    // ───────────────────────── Driver table (mirror of sawmill_parts.json) ─────────────────────────

    static readonly SawmillDriver[] Drivers = BuildDrivers();
    static readonly Dictionary<string, int> IndexById = BuildIndex();

    static Dictionary<string, int> BuildIndex()
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < SawmillParts.Rules.Length; i++) d[SawmillParts.Rules[i].Id] = i;
        return d;
    }

    static int Idx(string? id) => id != null && IndexById.TryGetValue(id, out int i) ? i : -1;

    static float[] V(float x, float y, float z) => new float[] { x, y, z };

    /// <summary>Build the per-part drivers in <see cref="SawmillParts.Rules"/> order (the ordering authority). Values
    /// are the user's tuned <c>sawmill_parts.json</c> layout transcribed verbatim, with ONE deliberate adaptation:
    /// the feed-ratchet parts (<c>wheel</c>, <c>spring_rotor</c>) are driven by the rectified <c>fwd</c> clock (an unwrapped
    /// <see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/>-tooth ratchet), not by
    /// <c>theta</c> as the json records — because in-game <c>θ=AngleRad</c> wraps every revolution and a θ-accumulating
    /// ratchet resets each rev (see those two entries). Everything else is verbatim; any rule id without an entry
    /// (lever_static / passive / frame) is Static. SpeedDecouplingPlan T02.01: the <c>fwd</c> value the caller supplies is
    /// the GEARED work clock (<c>animAngleFwd × SawmillWorkScale</c>), so these ratchet teeth and the crank chain run at
    /// 1/M of the shaft; the teeth COUNT is unchanged (still <see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/>
    /// per work-clock revolution). T02.02: the three cosmetic discs rotorDefault1..3 read Src="disc" (the raw-shaft clock,
    /// so they hold 1:1 with the shaft — the geared-disc dev option was baked out in T04.03); the crank disc rotorDefault4
    /// stays on "fwd".</summary>
    static SawmillDriver[] BuildDrivers()
    {
        var rules = SawmillParts.Rules;
        var arr = new SawmillDriver[rules.Length];
        for (int i = 0; i < rules.Length; i++)
        {
            arr[i] = rules[i].Id switch
            {
                // Drive-coupling rotor discs Rotor_default_1..4 (SawmillParts rotorDefault1..4) — restored as movers
                // 2026-07-09 after the model rename froze them (they were the old single `input` disc, now four discs).
                // Each Rotate about its own axis/direction, transcribed from the sawmill-animator export (part4=Rotor_default_1
                // x+1, part3=Rotor_default_2 x−1, part2=Rotor_default_3 z−1, part1=Rotor_default_4 x−1); auto mesh-bbox
                // pivot (= the stand's pivotAuto), no ratchet.
                // INPUT-DIRECTION SPLIT (user, 2026-07-10, refined 2026-07-14): ONLY the twoway coupling (mainRotorTwoway)
                // reverses with the input shaft — Src="theta" (the SIGNED animAngle × FacingDriveSign, follows the network
                // both ways). EVERYTHING ELSE keeps a FIXED spin regardless of which way the shaft is driven — the four
                // rotor discs here, AND (via the crank they head) the whole slider-crank + feed-ratchet chain — all read a
                // RECTIFIED clock (+|Δ| so it only ever grows — and RAW, no FacingDriveSign: T07.06 / F-L2-16, a signed
                // copy shrinks and runs the feed ratchet backward), matching the animator bench where every disc spins one
                // way. The ratio SIGN (which way each disc spins) is a visual pick to confirm in-game. This mirrors the
                // working chopper (only mainRotorTwoway signed, rest rectified); the earlier map had the crank/sash/levers/
                // wheel on the SIGNED clock, so they inverted with the input shaft (user report 2026-07-14) — fixed by
                // pinning the crank to a rectified disc (crankpit below) and moving the wheel/spring ratchets off "theta".
                // SpeedDecouplingPlan T02.02 — CONNECTION DISCS. The three cosmetic connection discs rotorDefault1..3 read
                // Src="disc": the caller feeds "disc" the RAW shaft-rate rectified clock (animAngleDisc), so these discs
                // hold 1:1 with the shaft — the honest "the shaft turns the clutch, the reduction is inside" look, at the
                // rate that matches the connected vanilla gear. This used to be a dev knob (SawmillGearConnectionDiscs)
                // that could instead gear the discs with the mechanism at 1/M; the author judged the 1:1 look live and it
                // was baked in (T04.03, memory vs-sawmill-procedural-anim), so the geared-disc option is gone. rotorDefault4
                // is NOT one of these — it doubles as the CRANK DISC (crankpit pins to it) and stays on the geared work
                // clock (Src="fwd"), or the whole sash/saw/lever/feed chain it heads would fall off the gearing. So three
                // discs run 1:1 and the crank disc runs geared; at M=1 all fwd/disc clocks coincide, so there is no visible
                // difference at the shipped default speed.
                "rotorDefault1" => new SawmillDriver { Type = SawmillDriverType.Rotate, Axis = 'x', Src = "disc",  Ratio =  1f },
                "rotorDefault2" => new SawmillDriver { Type = SawmillDriverType.Rotate, Axis = 'x', Src = "disc",  Ratio = -1f },
                "rotorDefault3" => new SawmillDriver { Type = SawmillDriverType.Rotate, Axis = 'z', Src = "disc",  Ratio = -1f },
                // rotorDefault4 doubles as the CRANK DISC: the connecting rod `crankpit` pins HERE (PinFrom below), so the
                // slider-crank + everything downstream (sash / saw / levers / wheel / feed) inherits this disc's FIXED
                // rectified spin — the mechanism runs one way whichever way the network drives the shaft. This matches the
                // user's 2026-07-14 bench (crankpit.pinFrom = part1 = Rotor_default_4). Auto mesh-bbox pivot (~Y3.5,Z0.375 =
                // the crank axle; the crank throw is anchor A (0.3125,3.28,0.375) offset ~0.22 blocks). SpeedDecouplingPlan
                // T02.02 (crank routing "ii"): this disc stays on Src="fwd" — the GEARED work clock — UNLIKE its three
                // cosmetic siblings above. It is the crank's pin source, so it MUST be geared or the sash/saw/lever/feed
                // chain would come off the 1/M gearing. So this one visibly runs at 1/M while 1..3 run 1:1 with the shaft
                // — the accepted cost of not adding a hidden crank disc (INVARIANTS.md "Решение по диск-муфтам", option ii).
                "rotorDefault4" => new SawmillDriver { Type = SawmillDriverType.Rotate, Axis = 'x', Src = "fwd",   Ratio = 1f }, // -1→+1: the whole RECTIFIED chain runs the wrong way in-game (user live 2026-09-15, T02.06). This disc heads the slider-crank (crankpit pins here), so reversing it turns sash/saw/levers/pawl the other way TOGETHER with the wheel/spring flip below — one coherent reversal, ratchet stays locked to its pawl.
                // The twoway mechanical-power COUPLING disc `MainRotor_twoway` (a SEPARATE disc from the crank, ~X-0.7 in
                // the model; the user's remodel split it off the crankshaft head). It is the ONLY signed part — Src="theta"
                // (the SIGNED animAngle × FacingDriveSign), so it co-turns with the connected network gear and REVERSES
                // with a reverse-driven line, visibly reflecting the input direction. Nothing pins to it anymore (the crank
                // moved to rotorDefault4), so it is purely the visible input coupling — its Ratio SIGN and pivot are a
                // cosmetic pick (Ratio -1 kept from the earlier tuning; the bench uses +1, a visual toss-up for a twoway
                // disc). Explicit pivot (Y,Z)=(3.5,0.5) = this disc's own centre.
                "mainRotorTwoway" => new SawmillDriver { Type = SawmillDriverType.Rotate, Axis = 'x', Src = "theta", Ratio = -1f, Pivot = V(-0.67f, 3.5f, 0.5f) },
                "crankpit" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rod, Axis = 'x',
                    Anchor = V(0.3125f, 3.28f, 0.375f), AnchorB = V(0.50000015f, 2.1562499f, 0.37499996f),
                    // Pin to the RECTIFIED crank disc rotorDefault4 (NOT the signed mainRotorTwoway) — user bench 2026-07-14
                    // (pinFrom=part1) — so the crank, and the whole sash/saw/lever/feed chain that follows it, run one fixed
                    // direction regardless of input-shaft direction. Pinning to mainRotorTwoway put the crank on the signed
                    // clock → the mechanism inverted with the shaft (the reported bug).
                    PinFrom = "rotorDefault4", SlideB = true, SlideAxis = 'y', Stretch = false
                },
                "sash" => new SawmillDriver
                {
                    Type = SawmillDriverType.Follow, Target = "crankpit", Axis = 'y',
                    Point = V(0.50000015f, 2.1562499f, 0.37499996f), Ratio = 1f, Offset = 0f
                },
                "saw" => new SawmillDriver { Type = SawmillDriverType.Ride, Ride = "sash" },
                "lever_top" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rod, Axis = 'z',
                    Anchor = V(1.22f, 2.88f, 1.56f), AnchorB = V(1.22f, 2.61f, 0.37f),
                    PinFromBottom = "sash", SlideB = false, Stretch = false
                },
                "lever_connector" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rod, Axis = 'x',
                    Anchor = V(1.22f, 2.77f, 1.01f), AnchorB = V(1.22f, 2.27f, 1.125f),
                    PinFrom = "lever_top", PinFromBottom = "lever_bottom", SlideB = false, Stretch = false
                },
                "lever_bottom" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rod, Axis = 'z',
                    Anchor = V(1.2460938f, 2f, 0.36f), AnchorB = V(1.2460938f, 2.17f, 1.13f),
                    PinFromBottom = "lever_connector", SlideB = false, Stretch = false
                },
                "lever_active" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rod, Axis = 'x',
                    Anchor = V(1.2578125f, 2.05f, 0.545f), AnchorB = V(1.3007812f, 1.4268802f, 0.95609285f),
                    PinFrom = "lever_bottom", PinFromBottom = "wheel", SlideB = false, Stretch = false,
                    RideRim = true, RimPart = "wheel", RimRadius = 0.49f, WheelAxis = 'x'
                },
                // FEED-RATCHET ROTORS (wheel + spring) — driven by the RECTIFIED accumulator (Src="fwd" = animAngleFwd, the
                // SAME clock as the now-rectified crank/pawl/lever chain), with the ACCUMULATING ratchet (CyclicStep): one
                // tooth per WORK-CLOCK revolution (Ratio = ±1) — geared to SawmillWorkScale teeth per shaft revolution, so
                // the metal_click sound is scaled to match (T02.05) — the step rising in the tuned window of each rev. Src was
                // "theta" (the SIGNED clock) — which stepped the ratchet BACKWARD on a reverse-driven line (user report
                // 2026-07-14: the wheel/feed inverted with the input shaft). Moved to "fwd" so the feed always advances the
                // same way whichever way the network drives the shaft; on a forward-driven line animAngleFwd ≡ animAngle so
                // this is identical to the confirmed-good behaviour, only the reverse-line inversion is fixed. The fwd clock
                // also reaches the drivers WITHOUT FacingDriveSign (T07.06 / F-L2-16): a signed copy SHRINKS instead of
                // growing, and this floor()-accumulating ratchet then steps backward on NORTH/EAST with its rise window
                // mirrored off the pawl's stroke — the caller keeps the facing sign on the SIGNED clock only. Both clocks
                // are UNWRAPPED, continuously-accumulated (BlockEntitySawmill.GetPartMatrices / AdvanceAnimAngle); the
                // SIGNED clock folds at TWOPI·SawmillDefaultWork (M-independent, read mod 2π) and the RECTIFIED clock at the
                // M-dependent SawmillFwdWrapBound (=TWOPI·SawmillDefaultWork·M), so the GEARED work clock
                // animAngleFwd·SawmillWorkScale still completes exactly one SawmillDefaultWork-tooth revolution per fold
                // (invisible) and stays phase-locked to the pawl (both on the fwd clock now — T02.01). The engine's raw
                // AngleRad wraps [0,2π) every rev, on which an accumulating ratchet snaps backward (the old "wheel resets"
                // bug) — the unwrapped accumulator is what avoids that. RiseStart/End pick WHICH stroke half: the wheel uses [0.5,1]
                // (the animator-confirmed UP/pull stroke), the spring its own [0,0.5]; Ratio's SIGN is the turn direction
                // (pitch = Teeth). The CARRIAGE feed stays prog-driven — it ACCUMULATES a linear travel and can't wrap.
                "spring_rotor" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rotate, Axis = 'x', Src = "fwd", Ratio = -1f, // +1→-1: reversed with the whole rectified chain (user live 2026-09-15, T02.06); window [0,0.5] kept (crank reversed too, see wheel)
                    Ratchet = true, Teeth = ImmersiveWoodworkingConfig.SawmillDefaultWork, RiseStart = 0f, RiseEnd = 0.5f
                },
                "wheel" => new SawmillDriver
                {
                    Type = SawmillDriverType.Rotate, Axis = 'x', Src = "fwd", Ratio = -1f, // +1→-1: reversed with the whole rectified chain (user live 2026-09-15, T02.06 — the ratchet ran backward)
                    Ratchet = true, Teeth = ImmersiveWoodworkingConfig.SawmillDefaultWork, Pivot = V(1.29f, 1.0390623f, 0.6406250f),
                    // Rise window KEPT at [0.5,1] across the sign flip — deliberately. With Ratio -1 the ratchet's frac = 1−τ
                    // (mirrored vs +1), which ALONE would push the step onto the wrong stroke half. But the CRANK (rotorDefault4)
                    // reversed in the SAME edit, so the pawl it drives and the wheel's frac mirror TOGETHER: the step stays on
                    // the pawl's pull without re-tuning. That is the whole point of reversing the chain as a unit — flipping the
                    // wheel by ITSELF would need this window mirrored to [0,0.5]; flipping everything together does not.
                    RiseStart = 0.5f, RiseEnd = 1f
                },
                "carriage" => new SawmillDriver
                {
                    Type = SawmillDriverType.Feed, Axis = 'z', Travel = -1.35f, Offset = 0f,
                    Stepped = true, Cycles = ImmersiveWoodworkingConfig.SawmillDefaultWork, RiseStart = 0f, RiseEnd = 0.5f
                },
                "log" => new SawmillDriver { Type = SawmillDriverType.Ride, Ride = "carriage" },
                _ => new SawmillDriver { Type = SawmillDriverType.Static },
            };
        }
        return arr;
    }

    // ───────────────────────── Assembler ─────────────────────────

    // Block-centre fallback pivot, shared and read-only (T05.07: was a fresh float[3] on every miss).
    static readonly float[] DefaultPivot = { 0.5f, 1f, 0.5f };

    static float[] PivotOf(int i, float[]?[]? pivots)
    {
        SawmillDriver dv = Drivers[i];
        if (dv.Pivot != null) return dv.Pivot;
        if (pivots != null && i >= 0 && i < pivots.Length && pivots[i] != null) return pivots[i]!;
        return DefaultPivot;
    }

    // Static dependency graph (F-S04-07): a part's deps are a pure function of the static Drivers table + IndexById, so
    // precompute the WHOLE graph once here instead of allocating a fresh List<int> per part on every topo-sort pass, every
    // frame, inside BuildMatrices. PartDeps is declared below Drivers/IndexById (textually), so the static init order has
    // both ready. DepsOf keeps its signature (now an O(1) lookup) so the BuildMatrices call sites read unchanged.
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
        SawmillDriver dv = Drivers[i];
        var ids = new List<int>();
        if (dv.Type == SawmillDriverType.Rod)
        {
            if (!string.IsNullOrEmpty(dv.PinFrom)) { int k = Idx(dv.PinFrom); if (k >= 0) ids.Add(k); }
            if (!dv.SlideB && !string.IsNullOrEmpty(dv.PinFromBottom)) { int k = Idx(dv.PinFromBottom); if (k >= 0) ids.Add(k); }
        }
        else if (dv.Type == SawmillDriverType.Follow) { if (!string.IsNullOrEmpty(dv.Target)) { int k = Idx(dv.Target); if (k >= 0) ids.Add(k); } }
        else if (dv.Type == SawmillDriverType.Ride) { if (!string.IsNullOrEmpty(dv.Ride)) { int k = Idx(dv.Ride); if (k >= 0) ids.Add(k); } }
        return ids.ToArray();
    }

    static void EvalPart(int i, float theta, float thetaFwd, float thetaDisc, float prog, bool feedReturning, SawmillAnimState st, float[]?[]? pivots)
    {
        SawmillDriver dv = Drivers[i];
        if (dv.Type == SawmillDriverType.Ride)
        {
            int ti = Idx(dv.Ride);
            // COPY the target's matrix, don't alias it (T05.07). The old path handed back the very same array for both
            // parts, which the in-place facing conjugation below would then rotate twice; the copy also keeps every
            // part's buffer its own, so the whole tree is reusable across frames. 16 floats, once per Ride part.
            if (ti >= 0 && st.Built[ti]) Array.Copy(st.Mats[ti]!, st.Mats[i]!, 16);
            else SetIdentity(st.Mats[i]!);
            return;
        }
        AnimMat(i, dv, theta, thetaFwd, thetaDisc, prog, feedReturning, st, pivots);
    }

    /// <summary>Build one matrix per part from (θ, progress), in topological order (a part is evaluated once its
    /// dependencies are; a dependency cycle — the lever_bottom↔lever_connector pair — is broken exactly as the stand
    /// does: the remaining parts evaluate in index order against whatever matrices exist, an unbuilt parent meaning that
    /// rod end stays at its fixed anchor). Writes a <c>float[16]</c> per part into <paramref name="st"/>, indexed to
    /// <see cref="SawmillParts.Rules"/>, and returns that array.
    ///
    /// AuditFixPlan T05.07 / F-L10-05: every buffer here — the matrix array, the flags, each part's matrix and the rod
    /// scratch — belongs to the caller's <see cref="SawmillAnimState"/> and is REUSED, so a build allocates nothing.
    /// <see cref="SawmillAnimState.Built"/> replaces the old "mats[k] != null" test: with the buffers persisting, a
    /// non-null matrix no longer means "evaluated during THIS build", and the cycle-break above depends on exactly that
    /// distinction (the lever connector must see its bottom parent as ABSENT and stay on its fixed anchor, not pick up
    /// last frame's pose). Built is set in both the topological pass and the index-order sweep; Done only in the
    /// topological pass, since it also drives readiness — the same split the null/`done` pair used to encode.</summary>
    public static float[]?[] BuildMatrices(SawmillAnimState st, float[]?[]? pivots, float theta, float thetaFwd, float thetaDisc, float prog, bool feedReturning = false)
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
                if (ready) { EvalPart(i, theta, thetaFwd, thetaDisc, prog, feedReturning, st, pivots); done[i] = true; built[i] = true; remaining--; progressed = true; }
            }
            if (!progressed) break; // dependency cycle — finish below with whatever is available
        }
        for (int i = 0; i < n; i++) if (!done[i]) { EvalPart(i, theta, thetaFwd, thetaDisc, prog, feedReturning, st, pivots); built[i] = true; }
        return st.Mats;
    }

    /// <summary>
    /// T07.06 — build the part matrices for a machine placed on a non-south facing. The whole driver table is authored
    /// in the NATIVE (south) frame (axes, anchors, feed direction), but the mover meshes are tesselated with the facing
    /// baked in (about the block centre, the rotation <c>Block.Shape.rotateY</c> encodes — verified 1:1 with the
    /// footprint's <c>RotateToFacing</c>). So a baked vertex is <c>v = R·v_native</c>, and the correct world transform
    /// for that baked mesh is the native animation CONJUGATED by the facing rotation: <c>A_world = R · A_native · R⁻¹</c>
    /// (then <c>A_world · v = R · A_native · v_native</c> — the native motion, rotated onto the facing).
    ///
    /// Pass NATIVE pivots (the baked mesh-bbox centres inverse-rotated by <paramref name="facingInv"/>; exact for the
    /// 90° facing steps, where R maps axis-aligned boxes to axis-aligned boxes). The returned matrices are in the
    /// world-block-local (baked) frame, so the renderer applies them straight to the baked meshes and the effects layer
    /// applies them to the baked pivots — both unchanged. When <paramref name="facing"/>/<paramref name="facingInv"/>
    /// are null (south, rotateY 0) this is exactly <see cref="BuildMatrices"/> with no per-part cost.
    ///
    /// AuditFixPlan T05.07 / F-L10-05: the conjugation runs IN PLACE (<see cref="MulLeft"/> then <see cref="Mul"/>) on
    /// each part's own buffer. The old path cloned the facing matrix per part and dropped the native matrix it had just
    /// built — on three facings out of four half of every frame's matrices were born dead. Bit-identical result: the
    /// pre-multiply keeps the same operand order per term the clone+post-multiply had.
    /// </summary>
    public static float[]?[] BuildMatricesFacing(SawmillAnimState st, float[]?[]? nativePivots, float theta, float thetaFwd, float thetaDisc, float prog, float[]? facing, float[]? facingInv, bool feedReturning = false)
    {
        float[]?[] mats = BuildMatrices(st, nativePivots, theta, thetaFwd, thetaDisc, prog, feedReturning);
        if (facing == null || facingInv == null) return mats; // south / rotateY 0 → no conjugation
        for (int i = 0; i < mats.Length; i++)
        {
            float[]? m = mats[i];
            if (m == null) continue;
            MulLeft(m, facing);   // m = R · A_native
            Mul(m, facingInv);    // m = R · A_native · R⁻¹
        }
        return mats;
    }

    static void AnimMat(int i, SawmillDriver dv, float theta, float thetaFwd, float thetaDisc, float prog, bool feedReturning, SawmillAnimState st, float[]?[]? pivots)
    {
        float[] m = st.Mats[i]!;
        SetIdentity(m);
        switch (dv.Type)
        {
            case SawmillDriverType.Static:
                return;

            case SawmillDriverType.Rotate:
            {
                float[] piv = PivotOf(i, pivots);
                float a;
                if (dv.Ratchet)
                {
                    int teeth = dv.Teeth > 0 ? dv.Teeth : 12;
                    float step = TAU / teeth;
                    float rs = dv.RiseStart ?? 0f;
                    float re = dv.RiseEnd ?? (dv.Engage ?? 0.5f);
                    if (dv.SmoothReturn && feedReturning)
                    {
                        float peak = CyclicStep(dv.Ratio + dv.Phase / TAU, rs, re);
                        a = peak * step * prog;
                    }
                    else
                    {
                        float cyc = (dv.Ratio * DriveVar(dv, theta, thetaFwd, thetaDisc, prog)) / TAU + dv.Phase / TAU;
                        a = CyclicStep(cyc, rs, re) * step;
                    }
                }
                else a = dv.Ratio * DriveVar(dv, theta, thetaFwd, thetaDisc, prog) + dv.Phase;

                Translate(m, piv[0], piv[1], piv[2]);
                Rot(m, dv.Axis == 'x' ? a : 0, dv.Axis == 'y' ? a : 0, dv.Axis == 'z' ? a : 0);
                Translate(m, -piv[0], -piv[1], -piv[2]);
                return;
            }

            case SawmillDriverType.Swing:
            {
                float[] piv = PivotOf(i, pivots);
                float a = dv.AmpDeg * D2R * (float)Math.Sin(dv.Ratio * DriveVar(dv, theta, thetaFwd, thetaDisc, prog) + dv.Phase);
                Translate(m, piv[0], piv[1], piv[2]);
                Rot(m, dv.Axis == 'x' ? a : 0, dv.Axis == 'y' ? a : 0, dv.Axis == 'z' ? a : 0);
                Translate(m, -piv[0], -piv[1], -piv[2]);
                return;
            }

            case SawmillDriverType.Reciprocate:
            {
                float d = dv.Amp * (float)Math.Sin(dv.Ratio * DriveVar(dv, theta, thetaFwd, thetaDisc, prog) + dv.Phase);
                AxisVec(dv.Axis, out float ux, out float uy, out float uz);
                Translate(m, ux * d, uy * d, uz * d);
                return;
            }

            case SawmillDriverType.Feed:
            {
                AxisVec(dv.Axis, out float ux, out float uy, out float uz);
                float d;
                if (dv.Stepped)
                {
                    int cycles = dv.Cycles > 0 ? dv.Cycles : ImmersiveWoodworkingConfig.SawmillDefaultWork;
                    float rs = dv.RiseStart ?? 0f, re = dv.RiseEnd ?? 0.5f;
                    if (dv.SmoothReturn && feedReturning) d = dv.Offset + dv.Travel * prog;
                    else d = dv.Offset + CyclicStep(cycles * prog, rs, re) * (dv.Travel / cycles);
                }
                else d = dv.Offset + dv.Travel * prog;
                Translate(m, ux * d, uy * d, uz * d);
                return;
            }

            case SawmillDriverType.Follow:
            {
                AxisVec(dv.Axis, out float ux, out float uy, out float uz);
                float[] pt = dv.Point ?? PivotOf(i, pivots);
                int ti = Idx(dv.Target);
                float d = 0;
                // "Built" (not "non-null") is the this-build test now that the buffers persist — see BuildMatrices.
                if (ti >= 0 && st.Built[ti])
                {
                    Vec(st.Mats[ti]!, pt[0], pt[1], pt[2], out float wx, out float wy, out float wz);
                    d = (wx - pt[0]) * ux + (wy - pt[1]) * uy + (wz - pt[2]) * uz;
                }
                d = dv.Ratio * d + dv.Offset;
                Translate(m, ux * d, uy * d, uz * d);
                return;
            }

            case SawmillDriverType.Rod:
                RodMat(i, dv, st, pivots);
                return;
        }
    }

    // Rim-centre fallback for a RimPart that doesn't resolve, shared and read-only (T05.07). NOT the same point as
    // DefaultPivot — the original spelled this one 0.5/0.5/0.5.
    static readonly float[] RimCentreFallback = { 0.5f, 0.5f, 0.5f };

    /// <summary>The two-point link (T05.07: every intermediate vector is a scalar triple now — a rod used to hand the GC
    /// a dozen <c>float[3]</c> per frame; the arithmetic is unchanged term for term). Writes into the part's own buffer.</summary>
    static void RodMat(int i, SawmillDriver dv, SawmillAnimState st, float[]?[]? pivots)
    {
        float[] anA = dv.Anchor ?? PivotOf(i, pivots);
        float[] anB = dv.AnchorB ?? PivotOf(i, pivots);
        float Lrest = (float)Math.Sqrt(Sq(anB[0] - anA[0]) + Sq(anB[1] - anA[1]) + Sq(anB[2] - anA[2]));
        if (Lrest < 1e-4f) Lrest = 1e-4f;

        int ai = Idx(dv.PinFrom);
        bool hasA = !string.IsNullOrEmpty(dv.PinFrom) && ai >= 0 && st.Built[ai];
        float Atx, Aty, Atz;
        if (hasA) Vec(st.Mats[ai]!, anA[0], anA[1], anA[2], out Atx, out Aty, out Atz);
        else { Atx = anA[0]; Aty = anA[1]; Atz = anA[2]; }

        float Btx, Bty, Btz;
        float s = 1f;
        if (dv.RideRim)
        {
            int wi = Idx(dv.RimPart);
            float[] C = wi >= 0 ? PivotOf(wi, pivots) : RimCentreFallback;
            AxisVec(dv.WheelAxis, out float ux, out float uy, out float uz);
            float Rrim;
            if (dv.RimRadius.HasValue && dv.RimRadius.Value > 0) Rrim = dv.RimRadius.Value;
            else { Rrim = (float)Math.Sqrt(Sq(anB[0] - C[0]) + Sq(anB[1] - C[1]) + Sq(anB[2] - C[2])); if (Rrim < 1e-4f) Rrim = 1e-4f; }

            float aA = Atx * ux + Aty * uy + Atz * uz;
            float aT = anB[0] * ux + anB[1] * uy + anB[2] * uz;
            float dax = aT - aA;
            float Leff2 = Math.Max(0f, Lrest * Lrest - dax * dax);
            float Apx = Atx - ux * aA, Apy = Aty - uy * aA, Apz = Atz - uz * aA;
            float cA = C[0] * ux + C[1] * uy + C[2] * uz;
            float Cpx = C[0] - ux * cA, Cpy = C[1] - uy * cA, Cpz = C[2] - uz * cA;
            float dx = Cpx - Apx, dy = Cpy - Apy, dz = Cpz - Apz;
            float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz); if (dist < 1e-6f) dist = 1e-6f;
            float dux = dx / dist, duy = dy / dist, duz = dz / dist;
            float a = (dist * dist + Leff2 - Rrim * Rrim) / (2 * dist);
            float h = (float)Math.Sqrt(Math.Max(0f, Leff2 - a * a));
            float footx = Apx + dux * a, footy = Apy + duy * a, footz = Apz + duz * a;
            float nx = uy * duz - uz * duy, ny = uz * dux - ux * duz, nz = ux * duy - uy * dux;
            float nl = (float)Math.Sqrt(nx * nx + ny * ny + nz * nz); if (nl == 0) nl = 1; nx /= nl; ny /= nl; nz /= nl;
            float c1x = footx + nx * h + ux * aT, c1y = footy + ny * h + uy * aT, c1z = footz + nz * h + uz * aT;
            float c2x = footx - nx * h + ux * aT, c2y = footy - ny * h + uy * aT, c2z = footz - nz * h + uz * aT;
            float d1 = (float)Math.Sqrt(Sq(c1x - anB[0]) + Sq(c1y - anB[1]) + Sq(c1z - anB[2]));
            float d2 = (float)Math.Sqrt(Sq(c2x - anB[0]) + Sq(c2y - anB[1]) + Sq(c2z - anB[2]));
            if (d1 <= d2) { Btx = c1x; Bty = c1y; Btz = c1z; }
            else { Btx = c2x; Bty = c2y; Btz = c2z; }
        }
        else if (dv.SlideB)
        {
            AxisVec(dv.SlideAxis, out float ux, out float uy, out float uz);
            float wx = Atx - anB[0], wy = Aty - anB[1], wz = Atz - anB[2];
            float wd = wx * ux + wy * uy + wz * uz;
            float w2 = wx * wx + wy * wy + wz * wz;
            float disc = Math.Max(0f, wd * wd - (w2 - Lrest * Lrest));
            float t = wd - (float)Math.Sqrt(disc);
            Btx = anB[0] + ux * t; Bty = anB[1] + uy * t; Btz = anB[2] + uz * t;
        }
        else
        {
            int bi = Idx(dv.PinFromBottom);
            bool hasB = !string.IsNullOrEmpty(dv.PinFromBottom) && bi >= 0 && st.Built[bi];
            if (hasB) Vec(st.Mats[bi]!, anB[0], anB[1], anB[2], out Btx, out Bty, out Btz);
            else { Btx = anB[0]; Bty = anB[1]; Btz = anB[2]; }
            float Ltgt = (float)Math.Sqrt(Sq(Btx - Atx) + Sq(Bty - Aty) + Sq(Btz - Atz));
            s = !dv.Stretch ? 1f : Ltgt / Lrest;
        }

        float dRestx = anB[0] - anA[0], dResty = anB[1] - anA[1], dRestz = anB[2] - anA[2];
        float dTgtx = Btx - Atx, dTgty = Bty - Aty, dTgtz = Btz - Atz;
        float Lt = (float)Math.Sqrt(dTgtx * dTgtx + dTgty * dTgty + dTgtz * dTgtz); if (Lt < 1e-4f) Lt = 1e-4f;
        AlignRot3(dRestx / Lrest, dResty / Lrest, dRestz / Lrest, dTgtx / Lt, dTgty / Lt, dTgtz / Lt, st.Rot3);
        Rot3to4(st.Rot3, st.Rot4);

        float[] m = st.Mats[i]!;
        SetIdentity(m);
        Translate(m, Atx, Aty, Atz);
        Mul(m, st.Rot4);
        if (s != 1f) Scale(m, s, s, s);
        Translate(m, -anA[0], -anA[1], -anA[2]);
    }

    static float Sq(float v) => v * v;
}
