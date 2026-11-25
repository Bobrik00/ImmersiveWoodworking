using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// The one debarker filler that is also the mechanical-power connector (AutoDebarkerPlan T04.01): the model's coupling
/// <c>debarker-coupling</c> exits the +X face of cell (0,1,0), and the network looks for the node in the cell the axle
/// touches, so the node lives on this filler, not on the master. Everything a plain <see cref="BlockDebarkerGhost"/> is
/// (invisible, solid, routes click, break, pick and collision to the master), plus an <see cref="IMechanicalPowerBlock"/>
/// whose connector is open on the coupling face only. The node itself is <see cref="BEBehaviorDebarkerMP"/> on this
/// cell's BE, declared in the <c>entityBehaviors</c> of <c>ghostpower.json</c>. A calque of
/// <see cref="BlockChopperGhostPower"/>.
///
/// The interface is what lets an axle in at all: <c>BEBehaviorMPBase.tryConnect</c> casts the neighbour block to it and
/// gives up on null. It is also required of the node's own block: a network spreading into this cell calls
/// <c>HasMechPowerConnectorAt</c> (<c>BEBehaviorMPBase.spreadTo</c>) and then <c>DidConnectAt</c>
/// (<c>JoinAndSpreadNetworkToNeighbours</c>) on the cell's block cast to the interface, without a null check.
///
/// The master places exactly one of these, at <see cref="BlockEntityDebarker.InputCellLocal"/>, with the <c>side</c>
/// variant set to its facing; every other filler stays a plain ghost with no power role, as vanilla's pulverizer-top
/// carries none.
/// </summary>
public class BlockDebarkerGhostPower : BlockDebarkerGhost, IMechanicalPowerBlock
{
    // The coupling face in world terms, from the facing the master stamped into this block's "side" variant
    // (debarker-ghostpower-{side}). Fixed per block variant, so resolved once.
    BlockFacing couplingFace = BlockFacing.EAST;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        BlockFacing facing = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        couplingFace = BlockEntityDebarker.InputFaceFor(facing);
    }

    /// <summary>Open on the coupling face only. The cell's other five faces border the machine itself: the master below,
    /// fillers on the other four sides.</summary>
    public bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        => face == couplingFace;

    /// <summary>The network of the node on this cell's BE, read the way <c>BlockMPBase.GetNetwork</c> reads it; null
    /// when the cell has no node or the node is in no network, and every engine caller checks for that.</summary>
    public MechanicalNetwork? GetNetwork(IWorldAccessor world, BlockPos pos)
        => (world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorMPBase>() as IMechanicalPowerDevice)?.Network;

    /// <summary>A fixed consumer has nothing to reorient when an axle connects; empty, as in <c>BlockPulverizer</c>.</summary>
    public void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face) { }
}
