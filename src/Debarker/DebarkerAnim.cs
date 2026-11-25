using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

internal enum DebarkerMotion { Static, Rotate, Shift }

/// <summary>The value a driver reads. <see cref="Signed"/> follows the network both ways (the spindle side, invariant
/// I2); <see cref="Geared"/> is the rectified clock times <c>DebarkerWorkScale</c> (the reducer and the rollers, I3);
/// <see cref="Lift"/> is the eased lift 0..1 (the pedal-to-lift-frame linkage); <see cref="Progress"/> is the client's
/// estimate of the loaded log's progress, read as 2π × progress, so the log's Amount counts turns per log (T07.04; Part A
/// by I4, like the sawmill's carriage).</summary>
internal enum DebarkerClock { None, Signed, Geared, Lift, Progress }

/// <summary>One rule's motion. <c>Rotate</c> turns about the native <see cref="Axis"/> through the pivot by
/// <c>Amount × clock</c> radians: on the shaft clocks <see cref="Amount"/> is turns of the part per turn of the clock, on
/// the lift clock it is the deflection in radians at lift 1, on the progress clock it is turns of the log per log.
/// <c>Shift</c> moves <c>Amount × clock</c> blocks along the axis. Signs follow the right-handed <c>Mat4f</c> rotation
/// (spec T07.01).</summary>
internal sealed record DebarkerDriver(DebarkerMotion Motion, DebarkerClock Clock, char Axis, float Amount,
    float PivotX = 0f, float PivotY = 0f, float PivotZ = 0f);

/// <summary>
/// The debarker's procedural animation (AutoDebarkerPlan T07.03): one matrix per rule of <see cref="DebarkerParts.Rules"/>
/// from the shaft's phase and the clocks the master advances on the client (Render partial): the rectified clock, the
/// lift and, since T07.04, the estimate of the log's progress. A trimmed port of
/// <see cref="ChopperAnim"/>: the same per-rule driver table and the same facing conjugation <c>R·A·R⁻¹</c>, but every
/// turning rule carries its pivot from the spec, nothing rides another part, and the matrix core is the engine's
/// <c>Mat4f</c>.
///
/// <para>The numbers are copied by hand from the spec's table (<c>AutoDebarkerPlan/07-visual-sound/01-anim-spec.md</c>),
/// which <c>AutoDebarkerPlan/debarker_anim.py</c> checks against the model; a change there is copied here.
/// <c>AutoDebarkerPlan/check_debarker_wrap.py</c> compares the geared ratios with that script. Pivots are in blocks in
/// the native frame (south = identity), the coordinate along the axis left at 0, where any value does. The table holds
/// no config value (invariant I5): the work scale reaches it already folded into the geared clock.
/// <c>DebarkerDefaultWork</c> = 12 times every geared ratio is a whole number of turns, so the fold of the rectified
/// clock at <c>DebarkerFwdWrapBound</c> moves no part (I6).</para>
/// </summary>
internal static class DebarkerAnim
{
    const float D2R = GameMath.DEG2RAD;

    /// <summary>Seconds the linkage takes from one end of the lift to the other, accepted by the author at
    /// T07.07.</summary>
    internal const float LiftTravelSeconds = 0.5f;

    /// <summary>The lift frame's swing at lift 1, in degrees, accepted by the author at T07.07. One constant for the
    /// frame's driver and the log's seat on its arms (<see cref="LogSeatShift"/>).</summary>
    const float LiftFrameDegrees = 25f;

    // The log's turn at progress 0 (spec T07.01, decision В12): with it the trailing edge of strip k leaves the knife line
    // at progress k/8, the moment strip k goes out.
    const float LogPhase = GameMath.PI / 8f;

    // The log lies on the lift frame's arms while they would cut into it at rest on the rollers: its lift in pixels
    // against the frame's angle in degrees, sampled off the model at 0.1 px (spec T07.01, "Подъёмная рама и педаль").
    // Below the first angle the arms clear the log. The kink between 23° and 24° is the arm's corner reaching under the
    // log's lower right edge. The table is keyed on the frame's own angle and covers any swing up to its last row, 25°:
    // a larger LiftFrameDegrees needs rows past it (LogSeatShift holds the last value), new arms or a new log a new table.
    static readonly float[] SeatDegrees = { 11.4f, 12f, 15f, 18f, 20f, 22f, 23f, 24f, 25f };
    static readonly float[] SeatPixels = { 0f, 0.10f, 0.67f, 1.04f, 1.29f, 1.53f, 1.71f, 2.37f, 2.56f };

    // Built from the rule ids, so it follows DebarkerParts.Rules whatever their order (the frame is last there and
    // first in the spec).
    static readonly DebarkerDriver[] Drivers = BuildDrivers();

    // The conjugation's intermediate. Matrices are only built on the client's main thread, one call at a time.
    static readonly float[] Scratch = Mat4f.Create();

    static DebarkerDriver[] BuildDrivers()
    {
        var rules = DebarkerParts.Rules;
        var drivers = new DebarkerDriver[rules.Length];
        for (int i = 0; i < rules.Length; i++) drivers[i] = DriverFor(rules[i].Id);
        return drivers;
    }

    static DebarkerDriver DriverFor(string id) => id switch
    {
        // Part B: 1:1 with the signed shaft, and turning on an incomplete machine too (decision В5).
        "spindle" or "disc" or "knives" => new(DebarkerMotion.Rotate, DebarkerClock.Signed, 'x', 1f, 0f, 1.5f, 0.5f),

        // Part A, ratios by tooth count: the countershaft ×5, the vertical shaft ×10, the worm drops it to ×5/4 on
        // feed3, the rollers chain feed3 → feed2 → feed1 at 1:1. The chain of signs runs the log surface down at the
        // knives (author's call, T07.01).
        "gearMain" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'x', -1f, 0f, 1.5f, 0.5f),
        "gearIdler" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'x', 2.5f, 0f, 2.375f, -0.375f),
        "pinion" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'x', -5f, 0f, 2.75f, -0.75f),
        "vshaft" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'y', -10f, -0.4375f, 0f, -0.75f),
        "feed3" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'z', 1.25f, -0.78125f, 2.03125f, 0f),
        "feed2" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'z', -1.25f, -0.59375f, 1.71875f, 0f),
        "feed1" => new(DebarkerMotion.Rotate, DebarkerClock.Geared, 'z', 1.25f, -0.28125f, 1.59375f, 0f),

        // The lift linkage: the model pose is a seated log (lift 0); these are the deflections of an empty machine
        // (lift 1). The author accepted the amplitudes at T07.07.
        "liftFrame" => new(DebarkerMotion.Rotate, DebarkerClock.Lift, 'z', LiftFrameDegrees * D2R, -0.8125f, 1.78125f, 0f),
        "cam" => new(DebarkerMotion.Rotate, DebarkerClock.Lift, 'z', -9.5f * D2R, -0.125f, 1.4375f, 0f),
        "rod" => new(DebarkerMotion.Shift, DebarkerClock.Lift, 'y', 0.04875f),
        "pedal" => new(DebarkerMotion.Rotate, DebarkerClock.Lift, 'z', -8f * D2R, -0.0625f, 0.21875f, 0f),

        // The log (T07.04): 9 turns per log about its own axis, the surface running down at the knives (decision В12).
        // BuildMatricesFacing adds its phase and its seat on the lift frame's arms.
        "log" => new(DebarkerMotion.Rotate, DebarkerClock.Progress, 'z', -9f, -0.25f, 2.125f, 0f),

        // idlerPin and liftShaft come with their part but never move; the frame is in the chunk mesh.
        _ => new(DebarkerMotion.Static, DebarkerClock.None, 'x', 0f)
    };

    /// <summary>How far the lift frame's arms lift the log, in blocks, at the eased lift <paramref name="eased"/>: the
    /// frame's angle <c>LiftFrameDegrees × eased</c> interpolated in the seat table, 0 while the arms clear the log, the
    /// table's last value past its last angle.</summary>
    static float LogSeatShift(float eased)
    {
        float deg = LiftFrameDegrees * eased;
        if (deg <= SeatDegrees[0]) return 0f;
        for (int i = 1; i < SeatDegrees.Length; i++)
        {
            if (deg > SeatDegrees[i]) continue;
            float t = (deg - SeatDegrees[i - 1]) / (SeatDegrees[i] - SeatDegrees[i - 1]);
            return (SeatPixels[i - 1] + (SeatPixels[i] - SeatPixels[i - 1]) * t) / 16f;
        }
        return SeatPixels[SeatPixels.Length - 1] / 16f;
    }

    /// <summary>The per-machine matrix buffers, one per rule, reused every frame.</summary>
    internal static float[][] CreateBuffers()
    {
        var mats = new float[Drivers.Length][];
        for (int i = 0; i < mats.Length; i++) mats[i] = Mat4f.Create();
        return mats;
    }

    /// <summary>
    /// Writes every rule's matrix into <paramref name="mats"/> (from <see cref="CreateBuffers"/>) and returns it.
    /// <paramref name="thetaSigned"/> is the shaft's phase already times <c>FacingDriveSign</c>,
    /// <paramref name="thetaGeared"/> the rectified clock times <c>DebarkerWorkScale</c>, <paramref name="lift"/> the raw
    /// lift 0..1, eased here by smoothstep, <paramref name="progress"/> the client's estimate of the log's progress 0..1.
    /// The log turns by <c>π/8 − 2π · 9 · progress</c> about its axis and is then lifted by its seat on the lift frame's
    /// arms (spec T07.01: the turn first, then the shift). The meshes are tesselated with the facing baked in about the
    /// block centre, so each native matrix A becomes <c>R·A·R⁻¹</c>; null <paramref name="facing"/> means south, no
    /// conjugation. Static rules stay identity.
    /// </summary>
    internal static float[][] BuildMatricesFacing(float[][] mats, float thetaSigned, float thetaGeared, float lift,
        float progress, float[]? facing, float[]? facingInv)
    {
        float eased = lift * lift * (3f - 2f * lift);
        for (int i = 0; i < Drivers.Length; i++)
        {
            DebarkerDriver dv = Drivers[i];
            float[] m = mats[i];
            Mat4f.Identity(m);
            if (dv.Motion == DebarkerMotion.Static) continue;

            float v = dv.Amount * (dv.Clock switch
            {
                DebarkerClock.Signed => thetaSigned,
                DebarkerClock.Geared => thetaGeared,
                DebarkerClock.Progress => GameMath.TWOPI * progress,
                _ => eased
            });

            if (dv.Motion == DebarkerMotion.Shift)
            {
                Mat4f.Translate(m, m, dv.Axis == 'x' ? v : 0f, dv.Axis == 'y' ? v : 0f, dv.Axis == 'z' ? v : 0f);
            }
            else
            {
                if (i == DebarkerParts.LogIndex)
                {
                    // The leftmost factor is applied last, so the seat lifts the log already turned about its own axis;
                    // native Y survives the facing conjugation.
                    v += LogPhase;
                    Mat4f.Translate(m, m, 0f, LogSeatShift(eased), 0f);
                }
                Mat4f.Translate(m, m, dv.PivotX, dv.PivotY, dv.PivotZ);
                if (dv.Axis == 'x') Mat4f.RotateX(m, m, v);
                else if (dv.Axis == 'y') Mat4f.RotateY(m, m, v);
                else Mat4f.RotateZ(m, m, v);
                Mat4f.Translate(m, m, -dv.PivotX, -dv.PivotY, -dv.PivotZ);
            }

            if (facing != null && facingInv != null)
            {
                Mat4f.Mul(Scratch, facing, m);
                Mat4f.Mul(m, Scratch, facingInv);
            }
        }
        return mats;
    }
}
