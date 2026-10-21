using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent.Mechanics;

namespace ImmersiveWoodworking;

/// <summary>
/// T04.01 — the ONE special ghost-filler cell that is ALSO the chopper's mechanical-power connector: the exposed
/// drive-cam cell where the authored model's input disc sits. It is everything a plain <see cref="BlockChopperGhost"/>
/// is (invisible, solid, routes click/break/pick/collision to the master via <see cref="BEMultiblockChopper.Principal"/>)
/// PLUS an <see cref="IMechanicalPowerBlock"/>, so an axle brought to its open face joins the network. The MP node
/// itself is the <see cref="BEBehaviorChopperMP"/> on this cell's BE (declared in the <c>chopper-ghostpower</c>
/// blocktype's entityBehaviors) — it has to live on the cell the axle touches, because the master cell is interior to
/// the footprint and unreachable (see <see cref="BEBehaviorChopperMP"/>). The analogue of the sawmill's
/// <see cref="BlockSawmillGhostPower"/>.
///
/// Only ONE cell uses this block (the master places it at <see cref="BlockEntityChopper.InputCellLocal"/>, oriented to
/// the machine's feed facing via the <c>side</c> variant); every other filler stays a plain BlockChopperGhost with no
/// MP role — mirroring vanilla, where the pulverizer's ghost carries no power wiring and only the principal cell
/// connects.
/// </summary>
public class BlockChopperGhostPower : BlockChopperGhost, IMechanicalPowerBlock
{
    // The exposed face an axle attaches to — the drive-cam side, perpendicular to the feed axis, derived from this
    // block's "side" variant (= the machine's feed facing, stamped at placement). Resolved once in OnLoaded.
    // PLACEHOLDER per the plan: the final face/sign come with the authored model (T07.06).
    BlockFacing inputFace = BlockFacing.WEST;

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        BlockFacing feed = BlockFacing.FromCode(Variant["side"]) ?? BlockFacing.NORTH;
        inputFace = BlockEntityChopper.InputFaceFor(feed);
    }

    /// <summary>The connector is open ONLY on the input face (the exposed drive-cam side). The opposite side faces the
    /// master cell (occupied) and every other face is internal, so they never accept an axle. Mirrors BlockPulverizer's
    /// face gate, narrowed to our single exposed face.</summary>
    public bool HasMechPowerConnectorAt(IWorldAccessor world, BlockPos pos, BlockFacing face, BlockMPBase forBlock)
        => face == inputFace;

    /// <summary>The network of the MP node on this very cell's BE (identical to BlockMPBase.GetNetwork). Works because
    /// <see cref="BEBehaviorChopperMP"/> is attached to this power-ghost's BE.</summary>
    // Returns null when there's no node here — legitimate; IMechanicalPowerBlock is nullable-oblivious (its assembly is
    // #nullable disable) and every caller (BEBehaviorMPBase.tryConnect) null-checks the result. The `!` just silences
    // CS8603 under this mod's Nullable=enable; it does NOT assert non-null.
    public MechanicalNetwork GetNetwork(IWorldAccessor world, BlockPos pos)
        => (world.BlockAccessor.GetBlockEntity(pos)?.GetBehavior<BEBehaviorMPBase>() as IMechanicalPowerDevice)?.Network!;

    /// <summary>Fixed consumer — nothing to reorient when an axle connects (empty, like BlockPulverizer /
    /// BlockMPConsumer).</summary>
    public void DidConnectAt(IWorldAccessor world, BlockPos pos, BlockFacing face) { }
}
