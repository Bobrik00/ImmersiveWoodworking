using System;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// The debarker's node on the mechanical-power network (AutoDebarkerPlan T04.01), a calque of
/// <see cref="BEBehaviorChopperMP"/>. A terminal consumer like the quern (the pulverizer passes power through): an axle
/// at the coupling face feeds it <see cref="BEBehaviorMPConsumer.TrueSpeed"/> = |Network.Speed × GearedRatio|. The master
/// reads the speed and the shaft angle from here through <see cref="BlockEntityDebarker.NetworkSpeed"/> and
/// <see cref="BlockEntityDebarker.AngleRad"/>.
///
/// It sits on the BE of the power filler (<see cref="BlockDebarkerGhostPower"/>, <c>entityBehaviors</c> in
/// <c>ghostpower.json</c>), not on the master: the engine wants the node on the very cell the axle touches
/// (<c>BEBehaviorMPBase.tryConnect</c> reads the behavior at the connector cell and dereferences it), and the coupling
/// comes out of the cell above the master (memory vs-mp-consumer-interior-master).
///
/// The consumer base does the rest: no exits (a dead end), TrueSpeed, and leaving the network when the block goes. That
/// last part is the whole of the teardown on this side. <see cref="BlockEntityDebarker.RemoveFootprintGhosts"/> clears
/// this cell with <c>SetBlock(0)</c>, the engine removes the BE, and <c>BEBehaviorMPBase.OnBlockRemoved</c> has the
/// network rebuilt without this node and leaves it. A chunk unload is no removal: <c>OnBlockUnloaded</c> leaves the node
/// in its network, as it does for every vanilla mechanical block.
///
/// The node renders nothing: <c>ghostpower.json</c> sets <c>mechPartShape: null</c> (memory
/// vs-mechpartshape-null-not-empty), and the coupling and the disc are the master's to draw (E07). So
/// <see cref="BEBehaviorMPBase.AxisSign"/> stays as the consumer base seeds it from the <c>side</c> variant, the log's
/// axis rather than the shaft's: only the mech renderers read it.
///
/// The load and the speed limit on joining a turning line are T04.02's, <see cref="GetResistance"/> and
/// <see cref="JoinNetwork"/>, calques of the chopper's and the sawmill's. The consumer base still fills its JSON-fed
/// <see cref="BEBehaviorMPConsumer.Resistance"/> (0.1, since the behavior's properties in <c>ghostpower.json</c> set
/// none), but its only reader, the base <c>GetResistance</c>, is overridden here.
/// </summary>
public class BEBehaviorDebarkerMP : BEBehaviorMPConsumer
{
    public BEBehaviorDebarkerMP(BlockEntity blockentity) : base(blockentity) { }

    public override void Initialize(ICoreAPI api, JsonObject properties)
    {
        // The facing comes from this filler's own "side" variant, which the master stamps to its facing at placement;
        // the Principal link cannot serve, since on placement the master sets it only after SetBlock has run this
        // Initialize. The discovery face has to be set before base.Initialize: there the server joins the network behind
        // that face or opens its own, on placement and on every load (the server ignores the networkid saved in the tree,
        // BEBehaviorMPBase.FromTreeAttributes).
        BlockFacing facing = BlockFacing.FromCode(Block.Variant["side"]) ?? BlockFacing.NORTH;
        OutFacingForNetworkDiscovery = BlockEntityDebarker.InputFaceFor(facing);

        base.Initialize(api, properties);
    }

    // ───────────────────────── Network load and speed limit (T04.02) ─────────────────────────

    // The load of a machine that is not fully assembled: the no-axle branch of vanilla BEBehaviorMPPulverizer.GetResistance,
    // and the chopper's and the sawmill's value. A constant rather than config, since it only says the mechanism is not
    // there yet. It does not follow DebarkerResistance, so with that set below 0.005 an unassembled frame drags more than
    // an assembled machine, as on the siblings.
    const float IncompleteResistance = 0.005f;

    ImmersiveWoodworkingModSystem? modSystem;
    ImmersiveWoodworkingConfig Config => (modSystem ??= Api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>()).Config;

    /// <summary>The master this node serves, through the filler's <see cref="BEMultiblockDebarker.Principal"/>, or null
    /// while the link is unset (mid-placement) or the master's chunk is not loaded. Looked up on every call, as the
    /// pulverizer re-reads its <c>BEPulverizer</c> in <c>GetResistance</c>.</summary>
    BlockEntityDebarker? Master
    {
        get
        {
            if (Blockentity is not BEMultiblockDebarker ghost || ghost.Principal == null || Api == null) return null;
            return Api.World.BlockAccessor.GetBlockEntity(ghost.Principal) as BlockEntityDebarker;
        }
    }

    /// <summary>
    /// The load the machine puts on its network. <c>MechanicalNetwork.updateNetwork</c> asks for it through
    /// <see cref="BEBehaviorMPBase.GetTorque"/> every fifth tick of the mech-power system; the client never asks, so the
    /// config read here is the server's. The split of vanilla <c>BEBehaviorMPPulverizer.GetResistance</c>,
    /// <c>hasAxle ? 0.085 : 0.005</c>, keyed on the master's <see cref="BlockEntityDebarker.IsComplete"/>: an assembled
    /// machine loads the line with <see cref="ImmersiveWoodworkingConfig.DebarkerResistance"/> (0.085 by default, the
    /// pulverizer's value; the quern's <c>MPConsumer</c> properties set no <c>resistance</c>, so it loads the base's 0.1), a
    /// partly built machine or a master that cannot be reached with <see cref="IncompleteResistance"/>. A missing knife set
    /// counts as unassembled, a worn-through one included, since T06.05 destroys it; the knives' metal does not count, it
    /// sets only the wear. The load reads neither the network speed nor the log, so a machine drags the same idle as busy,
    /// like the chopper, the sawmill and the pulverizer.
    /// </summary>
    public override float GetResistance()
    {
        BlockEntityDebarker? master = Master;
        if (master == null || !master.IsComplete) return IncompleteResistance;

        return Config.DebarkerResistance;
    }

    /// <summary>
    /// The speed limit on joining a network that already turns: vanilla <c>BEBehaviorMPPulverizer.JoinNetwork</c> line for
    /// line, as in the chopper and the sawmill. The ×1.6 and the threshold 1 are vanilla's, kept identical to the
    /// pulverizer's and the helve hammer toggle's, so every machine that limits speed on joining trims a line the same way
    /// (the quern has no limit). When this node's speed on the line, |Speed × GearedRatio|, is above 1 / 1.6 = 0.625, the
    /// network's <c>Speed</c> and <c>clientSpeed</c> are divided at once so that the node reads 0.625; afterwards the torque
    /// balance spins the line back up if it can. Without it the disc would start at the full speed of a fast, lightly
    /// loaded line. A windmill rotor aims at 0.6 at most (<c>BEBehaviorWindmillRotor.TargetSpeed</c>), so on a direct 1:1
    /// windmill line the limit fires at most on a brief overshoot and trims about 1%; a full cut takes a geared-up line or
    /// a creative rotor.
    ///
    /// Runs on both sides and on more than the first contact. The server joins on placement and on every load: this node
    /// has a discovery face, which in vanilla only rotors have, so a reload of its chunk beside a line that stayed loaded
    /// rejoins the line's frozen network and cuts that speed, and the rebuild carries the cut on (the chopper and the
    /// sawmill do the same; a pulverizer rejoins through the rebuild at speed 0). The client joins its copy of the network
    /// from a synced networkid or a connect on placement. Where both sides cut, the client's <c>Speed</c> drops now rather
    /// than at the next network packet (every 40th mech-power tick), and its animation speed <c>clientSpeed</c>, which
    /// packets do not reset, drops at once rather than easing down over many frames. Where only the client cuts (it loads
    /// a node already in a fast line, or resyncs one after a rebuild), the next packet restores <c>Speed</c> and
    /// <c>clientSpeed</c> climbs back over many frames: a client-only dip in the line's animation, as with the pulverizer.
    /// <c>base.JoinNetwork</c> goes first: it does the join and fires <see cref="BEBehaviorMPConsumer.OnConnected"/>.
    /// </summary>
    public override void JoinNetwork(MechanicalNetwork network)
    {
        base.JoinNetwork(network);

        // No engine path hands this node a null network (leaving goes through LeaveNetwork), but the base accepts one and
        // the vanilla original guards it with `network == null ? 0f : …`. Nullable flow cannot carry that test into the
        // block below, hence the early return; the behaviour is the same.
        if (network == null) return;

        float speed = Math.Abs(network.Speed * GearedRatio) * 1.6f;
        if (speed > 1f)
        {
            network.Speed /= speed;
            network.clientSpeed /= speed;
        }
    }
}
