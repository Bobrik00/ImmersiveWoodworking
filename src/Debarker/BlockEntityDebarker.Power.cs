using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// The master's window onto the mechanical network (AutoDebarkerPlan E04). The master is no network node: the node is
/// <see cref="BEBehaviorDebarkerMP"/> on the power filler at <see cref="InputCellLocal"/>, the cell the coupling comes out
/// of, so this partial reads the network speed and the shaft angle from there (T04.01) and the rest of the machine never
/// needs to know where the node lives. <see cref="EffectiveDebarkSpeed"/> gates work on speed and parts (T04.03),
/// <see cref="DebarkAdvance"/> turns the shaft's turn into debark progress (T06.01), integrating the speed where the
/// sawmill's <c>FeedAdvance</c> differences the angle, and <see cref="DriveConnected"/> tells the block info whether an
/// axle meets the coupling (T08.03). A port of <see cref="BlockEntityChopper"/>'s Power partial.
/// </summary>
public partial class BlockEntityDebarker
{
    /// <summary>
    /// How fast the shaft turns: the power node's <see cref="Vintagestory.GameContent.Mechanics.BEBehaviorMPConsumer.TrueSpeed"/>,
    /// |Network.Speed × GearedRatio|. At rest, or with no axle at the coupling, it reads a few millionths rather than 0:
    /// once anything on the network takes load, <c>MechanicalNetwork.updateNetwork</c> holds |Speed| at 1e-6 or more. It
    /// is exactly 0 with no network to read (no node: the filler missing or its chunk not loaded; on the client, a node
    /// without a networkid yet) and on a network still at its starting 0: on the client until the first network packet, on
    /// the server until the first update (the engine waits for every chunk on the line) or for good while nothing on it
    /// takes load (an assembled machine with no axle and <c>DebarkerResistance</c> 0). A network with a node in an unloaded
    /// chunk is not updated and keeps its last speed. Gate on a threshold, never on <c>&gt; 0</c> (memory vs-mechnetwork-speed-never-zero). The
    /// speed input of <see cref="EffectiveDebarkSpeed"/> and the debark progress (T06.01). Readable on both sides.
    /// </summary>
    public float NetworkSpeed => InputMechBehavior?.TrueSpeed ?? 0f;

    /// <summary>
    /// The shaft's angle in radians: the power node's
    /// <see cref="Vintagestory.GameContent.Mechanics.BEBehaviorMPBase.AngleRad"/>, the network's angle times the geared
    /// ratio, reduced with C#'s <c>%</c>, which keeps the sign. So it is no phase in [0, 2π): it goes negative on a network
    /// turning the other way and above 2π on a node the engine reverses (<c>IsRotationReversed</c>). Take
    /// <c>GameMath.Mod(angle, 2π)</c> for a phase, as <c>BEHelveHammer</c> does with its toggle's. The render (E07) turns
    /// the coupling and the disc by it. A stopped network holds its angle; out of any network the node returns the angle
    /// last read through one, 0 if none since load, and a missing node reads 0. Counting turns by differencing this value
    /// aliases on a fast shaft; integrate <see cref="NetworkSpeed"/> instead, as <see cref="DebarkAdvance"/> does (memory
    /// vs-mechpower-anglerad-nyquist).
    /// </summary>
    public float AngleRad => InputMechBehavior?.AngleRad ?? 0f;

    ImmersiveWoodworkingModSystem? modSystem;
    // The mod system is cached, the config is not: the client swaps its whole Config object when the server's arrives
    // (ConfigSyncPacket, possibly after this BE initialized), and a cached Config would keep the client's local defaults,
    // so its animation and sound (E07) would gate on another floor than the server's cycle. As in the chopper. Here
    // because EffectiveDebarkSpeed reads it first; the E06 cycle shares it.
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>
    /// How fast the machine may work right now: <see cref="NetworkSpeed"/> while it is assembled and the shaft turns at
    /// <see cref="ImmersiveWoodworkingConfig.DebarkerMinTorque"/> or faster, else 0, and 0 means the machine stands. The
    /// debark cycle (T06.01, T06.04), the geared mechanism's animation (Part A, T07.03) and the sound (T07.06) read it as
    /// a switch, <c>EffectiveDebarkSpeed() &gt; 0</c>. The shaft, the coupling and a fitted disc stay outside it: they turn
    /// with the network on an unassembled machine too (decision В5), 1:1 with the shaft (invariant I2), so their render
    /// follows <see cref="AngleRad"/> ungated. A calque of <see cref="BlockEntityChopper.EffectiveChopSpeed"/> and
    /// <see cref="BlockEntitySawmill.EffectiveSawSpeed"/>.
    ///
    /// Above the gate the machine turns exactly as fast as the network drives it (invariant I1): the value is the speed
    /// itself, nothing here scales it. Nor does it set the pace of a log: T06.01 integrates the shaft's turn, and a log
    /// costs <see cref="ImmersiveWoodworkingConfig.DebarkerDefaultWork"/> / <see cref="ImmersiveWoodworkingConfig.DebarkerSpeed"/>
    /// revolutions. The knives' metal changes neither, it sets only their wear (decision В2, T06.05).
    ///
    /// Two checks. <see cref="IsComplete"/> fails when a part or the knife set is missing, however fast the line. The
    /// floor is a speed, not a torque, whatever its name says (invariant I10), held against the geared speed
    /// |Network.Speed × GearedRatio|, so a direct line and a geared one gate alike. The floor is also what stops a machine
    /// at rest or with no axle, which reads a few millionths (see <see cref="NetworkSpeed"/>); with the floor at 0 such a
    /// machine counts as running, as on the chopper and the sawmill. With no network to read <see cref="NetworkSpeed"/> is
    /// exactly 0, which the floor turns away or, at floor 0, passes as itself.
    ///
    /// A pure read on either side. The client answers from its synced copies (the parts through the BE sync, the network
    /// speed through a mech-network packet every 40th mech-power tick, the config once on joining), so for a moment after
    /// a part or the speed changes it can disagree with the server.
    /// </summary>
    public float EffectiveDebarkSpeed()
    {
        if (!IsComplete) return 0f;

        float trueSpeed = NetworkSpeed;
        if (trueSpeed < Config.DebarkerMinTorque) return 0f;

        return trueSpeed;
    }

    /// <summary>Radians the shaft turns per unit of <see cref="NetworkSpeed"/> per second. The engine turns a network by
    /// <c>speed·dt·50/10</c> = <c>speed·dt·5</c> rad a tick (<c>MechanicalNetwork.ServerTick</c> and <c>UpdateAngle</c>),
    /// and the node's angle is the network's times its geared ratio, which <see cref="NetworkSpeed"/> already carries. The
    /// twin of the sawmill's and the chopper's constant (docs/INVARIANTS.md, the shaft's step per tick).</summary>
    const float ShaftRadPerNetworkSpeedSecond = 5f;

    /// <summary>
    /// The part of a log the disc strips while the shaft turns for <paramref name="dt"/> seconds: the shaft's turn,
    /// <see cref="NetworkSpeed"/>·5·dt rad, over <see cref="ImmersiveWoodworkingConfig.DebarkerFwdWrapBound"/> = 2π·R,
    /// where a log costs R = DebarkerDefaultWork / DebarkerSpeed revolutions. The bound comes precomputed from
    /// <c>Sanitize</c>, so nothing here works on the multiplier (invariant I5); it is positive across the whole speed
    /// band, so the division needs no zero guard. Rectified: a line turning either way strips. A pure read on either
    /// side; the server tick (<see cref="OnDebarkerTick"/>) adds it to the progress only while the machine may work.
    ///
    /// It integrates the speed, where the sawmill's <c>FeedAdvance</c> differences <see cref="AngleRad"/> from one tick
    /// to the next and falls back to the speed only above π per tick. On the server the angle is the worse witness of the
    /// shaft's turn. Its difference aliases above π per tick (memory vs-mechpower-anglerad-nyquist). The network's angle
    /// is a float the engine never folds: it grows from 0 over the server session, and by float32 arithmetic its per-tick
    /// step is rounded by several percent within hours of turning and to nothing after days (memory
    /// vs-mechnetwork-angle-unbounded-float).
    /// It jumps when the node joins another network, and it gives no reference on the first tick after a load (hence the
    /// sawmill's <c>feedSeeded</c> latch). It stands still on a network the server no longer ticks (a node in an unloaded
    /// chunk), while the speed keeps its last value, the client keeps turning the line at that speed and the quern keeps
    /// grinding off it; the integral strips on with them.
    /// </summary>
    float DebarkAdvance(float dt) => NetworkSpeed * ShaftRadPerNetworkSpeedSecond * dt / Config.DebarkerFwdWrapBound;

    // The power filler's world cell, cached: NetworkSpeed is read every server tick of an assembled machine (T06.01),
    // AngleRad every render frame and NetworkSpeed again every frame of an assembled machine (T07.03); the cell never
    // moves (Pos is fixed, Facing never changes in place). Keyed on the facing it was built for, as in the chopper:
    // base.Initialize sets Api one statement before Initialize reads Facing off the block, and a read in between would
    // otherwise keep a cell worked out from the NORTH default.
    BlockPos? inputCellPos;
    BlockFacing? inputCellFacing;

    /// <summary>The node on the power filler, or null while the filler is missing (lost to a world edit; the self-heal
    /// puts it back on the master's next load if the cell is free) or its chunk is not loaded. Looked up on every call,
    /// an allocation-free BE read, so it follows a re-placed filler.</summary>
    BEBehaviorDebarkerMP? InputMechBehavior
    {
        get
        {
            if (Api == null) return null;
            BlockFacing facing = Facing;
            if (inputCellPos == null || inputCellFacing != facing)
            {
                Vec3i w = RotateToFacing(InputCellLocal, facing);
                inputCellPos = Pos.AddCopy(w.X, w.Y, w.Z);
                inputCellFacing = facing;
            }
            return Api.World.BlockAccessor.GetBlockEntity(inputCellPos)?.GetBehavior<BEBehaviorDebarkerMP>();
        }
    }

    /// <summary>
    /// Whether something mechanical meets the coupling: the node is there and the block beyond the coupling face has a
    /// connector facing back, the test <c>BEBehaviorMPBase.tryConnect</c> makes before it joins a network. The block info
    /// (T08.03) tells "no drive" from "too slow" by it. Neither the network nor its speed can: the node opens a network of
    /// its own on every placement and load and keeps it while no axle meets the coupling, and that network reads a few
    /// millionths, or exactly 0 with no load on it (see <see cref="NetworkSpeed"/>), as a connected line at rest does
    /// (memory vs-mechnetwork-speed-never-zero). An axle with nothing turning it counts as connected. A pure read on
    /// either side.
    /// </summary>
    bool DriveConnected()
    {
        if (InputMechBehavior is not { } node) return false;
        BlockFacing face = InputFaceFor(Facing);
        BlockPos pos = node.Pos.AddCopy(face);
        // forBlock null, as the engine passes it for this node, whose block is no BlockMPBase.
        return Api.World.BlockAccessor.GetBlock(pos) is IMechanicalPowerBlock block
            && block.HasMechPowerConnectorAt(Api.World, pos, face.Opposite, null);
    }
}
