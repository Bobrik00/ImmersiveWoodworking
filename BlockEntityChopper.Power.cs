using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// E04 — the master's window onto the mechanical-power network. The master cell is INTERIOR to the footprint, so it is
/// NOT a network node itself; the node is the <see cref="BEBehaviorChopperMP"/> on the power-input ghost at
/// <see cref="InputCellLocal"/> (the exposed drive-cam cell, where the model's input disc sits). This partial gives the
/// rest of the machine a single, master-side RPM reading without anyone else needing to know where the node lives.
///
/// T04.01 fills <see cref="NetworkSpeed"/> / <see cref="AngleRad"/> and the cross-cell node lookup; T04.03 adds the
/// <c>EffectiveChopSpeed</c> torque+head gate. (The optional flywheel that once eased that gate was cut 2026-07-04.)
/// </summary>
public partial class BlockEntityChopper
{
    /// <summary>
    /// The chopper's network speed = the power-input node's <see cref="BEBehaviorMPConsumer.TrueSpeed"/>
    /// (|Network.Speed × GearedRatio|): &gt;0 while the connected network turns, 0 at rest / when no axle is attached
    /// or the machine has no power-input ghost. The single RPM input the network load (T04.02), effective chop speed
    /// (T04.03) and the chop cycle (E06) read; gate work on <c>IsComplete &amp;&amp; NetworkSpeed &gt; 0</c>.
    /// </summary>
    public float NetworkSpeed => InputMechBehavior?.TrueSpeed ?? 0f;

    /// <summary>
    /// The accumulated rotation angle of the drive (radians, wrapped to [0, 2π)), read cross-cell from the
    /// power-input node (<see cref="BEBehaviorMPBase.AngleRad"/>) — the single phase the procedural render (E07) drives
    /// every moving part from, exactly as <c>BEHelveHammer</c> reads <c>mptoggle.AngleRad</c>. Client-available (the
    /// mechanical-power system interpolates the network angle on the client), 0 only when there is no input node (the
    /// <c>?? 0f</c> fallback) — a connected-but-unpowered drive returns the engine's frozen last angle, not 0. The chop
    /// cycle (E06) reads a DISCRETE strike off this angle — one notch of progress each time the arm crosses its
    /// bottom-of-stroke phase, NOT a continuous delta accrual — so the axe never outruns the animation — memory
    /// vs-saw-hold-progress.
    /// </summary>
    public float AngleRad => InputMechBehavior?.AngleRad ?? 0f;

    ImmersiveWoodworkingModSystem? modSystem;
    // Live, server-synced config (same accessor pattern as BEBehaviorChopperMP / BlockEntitySawmill) — the torque floor
    // below is read fresh each call so a server retune reaches the client and its E07 tempo can't drift. First consumer
    // is EffectiveChopSpeed, so the accessor lives here in .Power.cs (the sawmill keeps its twin in .Parts.cs — same
    // private-partial visibility, so the E06 chop cycle can reuse it either way).
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>
    /// The CHOP GATE the rest of the machine reads (E06 chop cycle, E07 render, Effects): the geared drive speed when
    /// the chopper may strike RIGHT NOW, or a hard 0 when it can't. Every caller uses it as a boolean
    /// (<c>EffectiveChopSpeed() &gt; 0</c>); the chop PACE itself is set ENTIRELY by the drive RPM (one lift-cycle = one
    /// axe strike, the log's work R = ChopperCycleCap / <see cref="ImmersiveWoodworkingConfig.ChopperSpeed"/>
    /// revolutions), stepped one discrete notch per strike in E06 — NOT by this magnitude — so the head metal only gates the chop (it must be
    /// present) and never speeds it up (metal = WEAR tier, T03.07/E06 DamageHead). A pure read of the current state: it
    /// accumulates nothing (the chop progress lives in E06) and is side-effect free; safe at rest and off-network
    /// (returns 0, never throws). Calque of <see cref="BlockEntitySawmill.EffectiveSawSpeed"/>.
    ///
    /// Gates, in order (both return a hard 0 — the machine STANDS, it does not creep):
    ///   1. No head → 0, even on a fast network (DoD). <c>headStack != null</c> is exactly what <see cref="IsComplete"/>
    ///      keys on (the head installs last, after the 3 mechanism parts in the wood→metal order T03.07), so this
    ///      doubles as "fully assembled". The head is a single installed item, so its presence alone is the gate.
    ///   2. Below the minimum-torque floor (<see cref="ImmersiveWoodworkingConfig.ChopperMinTorque"/>) → 0: a weak line
    ///      can't move the axe arm at all, exactly as a vanilla pulverizer/hammer stalls on a dead network. (The optional
    ///      flywheel that used to lower this start threshold was cut 2026-07-04.)
    /// Otherwise it returns the geared network speed (&gt; 0): the "can chop" signal. Compare the floor against
    /// <see cref="NetworkSpeed"/> (= |TrueSpeed| with GearedRatio), NOT raw network speed, or a direct line and a
    /// reduction line would gate differently. Memory vs-saw-hold-progress.
    /// </summary>
    public float EffectiveChopSpeed()
    {
        // 1. No head → no chop (the cutting part is missing). headStack != null implies a fully-assembled machine
        //    because the head installs last in the wood->metal order (T03.07).
        if (headStack == null) return 0f;

        // Compare the torque floor against |TrueSpeed| (NetworkSpeed already carries GearedRatio), NOT the raw network
        // speed — otherwise the same floor would gate a direct line and a reduction line differently. At rest /
        // off-network NetworkSpeed is 0, so any positive floor returns 0 below.
        float trueSpeed = NetworkSpeed;

        // 2. Minimum-torque gate: a weak line can't move the axe arm at all, exactly as a vanilla pulverizer/hammer
        //    stalls on a dead network. (The optional flywheel that used to ease this start threshold was cut 2026-07-04.)
        if (trueSpeed < Config.ChopperMinTorque) return 0f;

        // Above the gate → the machine can chop. Return the geared drive speed itself; the chop PACE is the drive RPM
        // (E06 steps chopProgress one discrete notch per strike), so there is NO head-tier scaling here — the metal
        // gates, it doesn't speed.
        return trueSpeed;
    }

    // Cached world position of the power-input ghost cell. Pos + Facing are invariant for a placed BE, so the input
    // cell never moves — yet NetworkSpeed/AngleRad read it every render frame AND every server tick, and rebuilding the
    // whole footprint (new BlockPos[] + N BlockPos) on every read just to pick one cell would be wasteful. Compute that
    // one cell once and cache it, keyed on the facing it was built for so a Facing set after Initialize (placement /
    // chunk load) self-corrects. Mirrors BlockEntitySawmill.Power.cs.
    BlockPos? inputCellPos;
    BlockFacing? inputCellFacing;

    /// <summary>The mechanical-power node on the power-input ghost cell, or null while the machine is unpowered / the
    /// input ghost is missing (e.g. mid-assembly, or InputCellLocal isn't a real footprint cell). The cell POSITION is
    /// cached (it never moves), but the behavior is still resolved fresh each call (an allocation-free block-entity
    /// lookup) so it always follows a re-placed or reloaded ghost.</summary>
    BEBehaviorChopperMP? InputMechBehavior
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
            return Api.World.BlockAccessor.GetBlockEntity(inputCellPos)?.GetBehavior<BEBehaviorChopperMP>();
        }
    }
}
