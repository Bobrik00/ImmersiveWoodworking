using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// E04 (T04.01) — the master's window onto the mechanical-power network. The master cell is INTERIOR to the
/// footprint, so it is NOT a network node itself; the node is the <see cref="BEBehaviorSawmillMP"/> on the
/// power-input ghost at <see cref="InputCellLocal"/> (the exposed -X gear-tower cell, where the model's
/// <c>rotor_default</c> disc sits). This partial gives the rest of the machine a single, master-side RPM reading
/// without anyone else needing to know where the node lives.
/// </summary>
public partial class BlockEntitySawmill
{
    /// <summary>
    /// The sawmill's network speed = the power-input node's <see cref="BEBehaviorMPConsumer.TrueSpeed"/>
    /// (|Network.Speed × GearedRatio|): &gt;0 while the connected network turns, 0 at rest / when no axle is attached
    /// or the machine has no power-input ghost. The single RPM input the network load (T04.02), effective cut speed
    /// (T04.04) and the cut cycle (E06) read; gate work on <c>IsComplete &amp;&amp; NetworkSpeed &gt; 0</c>.
    /// </summary>
    public float NetworkSpeed => InputMechBehavior?.TrueSpeed ?? 0f;

    /// <summary>
    /// The accumulated rotation angle of the drive (radians, wrapped to [0, 2π)), read cross-cell from the
    /// power-input node (<see cref="BEBehaviorMPBase.AngleRad"/>) — the single phase the procedural render (E07)
    /// drives every moving part from, exactly as <c>BEHelveHammer</c> reads <c>mptoggle.AngleRad</c>. Client-available
    /// (the mechanical-power system interpolates the network angle on the client), 0 when unpowered / no input node.
    /// The render freezes its own copy of this when the machine isn't actually cutting (T07.02), so a network that
    /// spins below the torque gate doesn't drive the saw.
    /// </summary>
    public float AngleRad => InputMechBehavior?.AngleRad ?? 0f;

    /// <summary>Radians the drive angle advances per unit of network speed per real second (the twin of
    /// <c>BlockEntityChopper.ShaftRadPerNetworkSpeedSecond</c>). The engine integrates the network angle in
    /// <c>MechanicalNetwork.UpdateAngle</c> as <c>angle += speed·dt·50/10</c> = <c>speed·dt·5</c>, and this drive's angle
    /// is that network angle × <c>GearedRatio</c> — and <see cref="NetworkSpeed"/> (=|Speed·GearedRatio|) already carries
    /// the ratio — so the drive turns at <c>NetworkSpeed·5</c> rad/s and its true (unwrapped) step over a tick/frame is
    /// <c>NetworkSpeed·5·dt</c> rad. Used by <see cref="FeedAdvance"/> and <see cref="AdvanceAnimAngle"/> to recover that
    /// step from the CONTINUOUS speed when the wrapped-angle difference would alias past the Nyquist limit
    /// (ModDbBugfixPlan E02/T02.03, folded into SpeedDecouplingPlan T04.02 — the same fix the chopper carries in
    /// SpeedDecouplingPlan T03.04/T03.05). Verified against the shipped engine (VSSurvivalMod 1.22.2) 2026-09-16.</summary>
    const float ShaftRadPerNetworkSpeedSecond = 5f;

    /// <summary>
    /// The CUT GATE the rest of the machine reads (E06 cut cycle, E07 render, Effects): the geared drive speed when
    /// the sawmill may cut RIGHT NOW, or a hard 0 when it can't. Every caller uses it as a boolean
    /// (<c>EffectiveSawSpeed() &gt; 0</c>); the cut PACE itself is set ENTIRELY by the drive RPM in
    /// <see cref="OnSawmillTick"/> (R = <see cref="ImmersiveWoodworkingConfig.SawmillDefaultWork"/>/<see cref="ImmersiveWoodworkingConfig.SawmillSpeed"/>
    /// revolutions per log), NOT by this magnitude — so the blade metal
    /// only gates the cut (it must be present) and never speeds it up. A pure read of the current state: it
    /// accumulates nothing (the saw progress lives in E06) and is side-effect free; safe at rest and off-network
    /// (returns 0, never throws). Read it live each tick — the threshold below comes from the server-synced config so
    /// the client's E07 tempo can't drift from the server's cut logic.
    ///
    /// Gates, in order (both return a hard 0 — the machine STANDS, it does not creep):
    ///   1. No blade kit → 0, even on a fast network (DoD). <c>bladeStack != null</c> is exactly what
    ///      <see cref="IsComplete"/> keys on (the kit installs last, after the 4 mandatory parts), so this doubles as
    ///      "fully assembled". The kit is a single installed item, so its presence alone is the gate.
    ///   2. Below the minimum-torque floor (<see cref="ImmersiveWoodworkingConfig.SawmillMinTorque"/>) → 0: a weak
    ///      line can't move the frame at all, exactly as a vanilla pulverizer/hammer stalls on a dead network. The
    ///      optional flywheel EASES the start (floor × the baked flywheel-relief constant),
    ///      letting a slower network drive it — it lowers the START threshold only.
    /// Otherwise it returns the geared network speed (&gt; 0): the "can cut" signal. The flywheel touches the gate,
    /// not the result.
    /// </summary>
    public float EffectiveSawSpeed()
    {
        // 1. No blade kit → no cut (the cutting part is missing). bladeStack != null implies a fully-assembled
        //    machine because the kit installs last in the wood->metal order (T03.07).
        if (bladeStack == null) return 0f;

        // Compare the torque floor against |TrueSpeed| (NetworkSpeed already carries GearedRatio), NOT the raw
        // network speed — otherwise the same floor would gate a direct line and a reduction line differently. At
        // rest / off-network NetworkSpeed is 0, so any positive floor returns 0 below.
        float trueSpeed = NetworkSpeed;

        // 2. Minimum-torque gate, eased by the optional flywheel (lowers the start threshold). The flywheel relief
        //    multiplier was a dev tuning dial, finalized in-game and baked as a constant (2026-06-21; was config).
        const float FlywheelMinTorqueMult = 0.5f;
        float minTorque = Config.SawmillMinTorque;
        if (hasFlywheel) minTorque *= FlywheelMinTorqueMult;
        if (trueSpeed < minTorque) return 0f;

        // Above the gate → the machine can cut. Return the geared drive speed itself; the cut PACE is the drive RPM
        // (OnSawmillTick's FeedAdvance), so there is NO blade-tier scaling here — the metal gates, it doesn't speed.
        return trueSpeed;
    }

    // Cached world position of the power-input ghost cell. Pos + Facing are invariant for a placed BE, so the input
    // cell never moves — yet NetworkSpeed/AngleRad/EffectiveSawSpeed read it several times per render frame AND every
    // server tick, and the old `GetCells(Pos, Facing)[InputCellIndex]` rebuilt the WHOLE 19-cell footprint
    // (new BlockPos[19] + 19 BlockPos) on every read just to pick one cell. Compute that one cell once and cache it,
    // keyed on the facing it was built for so a Facing set after Initialize (placement / chunk load) self-corrects.
    BlockPos? inputCellPos;
    BlockFacing? inputCellFacing;

    /// <summary>The mechanical-power node on the power-input ghost cell, or null while the machine is unpowered /
    /// the input ghost is missing (e.g. mid-assembly, or InputCellLocal isn't a real footprint cell). The cell
    /// POSITION is cached (it never moves), but the behavior is still resolved fresh each call (an allocation-free
    /// dictionary lookup) so it always follows a re-placed or reloaded ghost.</summary>
    BEBehaviorSawmillMP? InputMechBehavior
    {
        get
        {
            if (InputCellIndex < 0 || Api == null) return null;
            if (inputCellPos == null || inputCellFacing != Facing)
            {
                Vec3i w = RotateToFacing(FootprintLocal[InputCellIndex], Facing);
                inputCellPos = Pos.AddCopy(w.X, w.Y, w.Z);
                inputCellFacing = Facing;
            }
            return Api.World.BlockAccessor.GetBlockEntity(inputCellPos)?.GetBehavior<BEBehaviorSawmillMP>();
        }
    }
}
