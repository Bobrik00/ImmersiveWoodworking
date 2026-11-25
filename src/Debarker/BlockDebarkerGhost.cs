using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The invisible filler in the non-master cells of the debarker's footprint (AutoDebarkerPlan), a calque of
/// <see cref="BlockChopperGhost"/>. It has no logic of its own: each proxy below reads the master position from
/// <see cref="BEMultiblockDebarker.Principal"/> and hands the call to the placed <c>debarker-frame</c>, so a click,
/// hint, hover name or pick on any cell answers like the same action on the master. Principal unset (a stray or not yet
/// stamped filler) falls back to the base block, except the pick (the frame stack), the particle colour (0) and the
/// mining, which never finishes. The frame stamps Principal when it places the fillers (T03.03).
///
/// The shape is <c>game:block/basic/nothing</c> with no textures, so <see cref="GetRandomColor"/> borrows the master's;
/// otherwise particles from this cell would be black. The particle box stays the base one, read from this cell's own
/// boxes: the chopper's twin hands back the master's box, the shape of the master cell rather than of this one.
///
/// Collision and selection are this cell's slice of the master's footprint table (T03.02), held on the filler's BE
/// (<see cref="BEMultiblockDebarker.CellBoxes"/>).
///
/// A break on any filler breaks the frame instead (T03.04, <see cref="OnGettingBroken"/>/<see cref="OnBlockBroken"/>),
/// and the frame's <see cref="BlockDebarker.OnBlockRemoved"/> clears the whole footprint. Two cases stay on this cell.
/// Principal unset: survival mining never finishes, as in vanilla, so an unsynced or unstamped filler cannot be dug out
/// of a standing machine. Principal dangling (the frame is gone and its cell holds air, water or someone's block): the
/// filler breaks as a plain block with <c>drops: []</c>, so an orphan left by a teardown that missed its chunk can be
/// mined away. The engine redirects the USE claim test itself, through
/// <see cref="BEMultiblockDebarker.GetControlBlockPos"/>; BuildOrBreak it tests at the clicked cell only, so the break
/// pair tests the frame's cell by hand.
/// </summary>
public class BlockDebarkerGhost : Block
{
    /// <summary>
    /// The mining half of the break: the frame's tier, mining speed and sounds, read at the frame's cell. The resistance
    /// pool is this cell's own, since the client seeds it from the clicked block, so <c>ghost.json</c> and
    /// <c>ghostpower.json</c> keep the frame's <c>resistance: 3.5</c>. It must agree with <see cref="OnBlockBroken"/>
    /// on both guards. Routed past a dangling Principal, the mining tier would be read from whatever stands there now,
    /// a tier above the player's tool would keep the break from ever finishing, and the orphan guard of OnBlockBroken
    /// would be unreachable in the one case it exists for. Past a claim the break would never be allowed to land, and
    /// the player would dig at an invisible cell forever.
    ///
    /// This runs on every damage tick of a held button, so only the first tick of an attempt asks the claim through
    /// TryAccess, which shows the player the refusal; the later ticks ask the silent TestAccess.
    /// <paramref name="counter"/> is the engine's break counter, reset on release or when the aim leaves every block;
    /// sweeping onto this cell from another block with the button held keeps counting, so that attempt is refused
    /// silently. The engine calls this on the client only, always with the local player (base dereferences it too), so
    /// this half is feedback; the authoritative gate is in OnBlockBroken.
    /// </summary>
    public override float OnGettingBroken(IPlayer player, BlockSelection blockSel, ItemSlot itemslot, float remainingResistance, float dt, int counter)
    {
        IWorldAccessor world = api.World;
        BlockPos? principal = (world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEMultiblockDebarker)?.Principal;
        if (principal == null) return 1f;

        Block principalBlock = world.BlockAccessor.GetBlock(principal, BlockLayersAccess.Solid);
        if (principalBlock is not BlockDebarker)
        {
            return base.OnGettingBroken(player, blockSel, itemslot, remainingResistance, dt, counter);
        }

        bool granted = counter == 0
            ? world.Claims.TryAccess(player, principal, EnumBlockAccessFlags.BuildOrBreak)
            : world.Claims.TestAccess(player, principal, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted;
        if (!granted) return 1f;

        BlockSelection bs = blockSel.Clone();
        bs.Position = principal;
        return principalBlock.OnGettingBroken(player, bs, itemslot, remainingResistance, dt, counter);
    }

    /// <summary>
    /// A completed break on a filler breaks the frame, which tears the machine down and drops everything at the frame's
    /// cell (<see cref="BlockDebarker.OnBlockRemoved"/>, <see cref="BlockDebarker.GetDrops"/>). Base is skipped on that
    /// path: this cell goes with the rest of the footprint, and a base break here would add its own particles and a
    /// second break sound.
    ///
    /// The claim is asked again at the frame's cell: the server tested BuildOrBreak once, at the clicked cell, and nothing
    /// downstream tests it again, so a claim drawn across a standing machine would otherwise let a filler outside it
    /// tear down the machine inside. TryAccess runs once per break on each side, so it may speak: it names the claimant,
    /// and on the server it also resyncs the block and the held slot the client predicted. A creative break skips
    /// OnGettingBroken, so its refusal is reported by the client and again by the server. A refusal breaks nothing.
    /// Engine breaks come with no player and stay ungated: there is no one to deny, and TryAccess dereferences the
    /// player.
    /// </summary>
    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer? byPlayer, float dropQuantityMultiplier = 1f)
    {
        BlockPos? principal = (world.BlockAccessor.GetBlockEntity(pos) as BEMultiblockDebarker)?.Principal;
        Block? principalBlock = principal == null ? null : world.BlockAccessor.GetBlock(principal, BlockLayersAccess.Solid);

        // Principal unset, or set and dangling. The test is for the frame itself rather than "not air": the cell may hold
        // water or a block someone built on the ruins, and routing the break there would leave this filler standing
        // forever and, for a foreign block, destroy someone else's property. Kept in step with OnGettingBroken.
        if (principal == null || principalBlock is not BlockDebarker)
        {
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
            return;
        }

        if (byPlayer != null && !world.Claims.TryAccess(byPlayer, principal, EnumBlockAccessFlags.BuildOrBreak)) return;

        principalBlock.OnBlockBroken(world, principal, byPlayer, dropQuantityMultiplier);

        // On the client the player's break code fires the neighbour updates around the cell it broke, which is not the
        // frame's; vanilla BlockMPMultiblockPulverizer fires them around its principal by hand, as here.
        if (api.Side == EnumAppSide.Client)
        {
            foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
            {
                BlockPos npos = principal.AddCopy(facing);
                world.BlockAccessor.GetBlock(npos).OnNeighbourBlockChange(world, npos, principal);
            }
        }
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BEMultiblockDebarker? be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEMultiblockDebarker;
        if (be?.Principal != null && world.BlockAccessor.GetBlockEntity(be.Principal) is BlockEntityDebarker master)
        {
            return master.OnInteract(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    /// <summary>The frame's one-press hold (<see cref="BlockDebarker.OnBlockInteractStep"/>) for a click that
    /// <see cref="OnBlockInteractStart"/> handed to the master.</summary>
    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BEMultiblockDebarker? be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEMultiblockDebarker;
        if (be?.Principal != null && world.BlockAccessor.GetBlockEntity(be.Principal) is BlockEntityDebarker) return true;
        return base.OnBlockInteractStep(secondsUsed, world, byPlayer, blockSel);
    }

    // The master block with the selection moved to the master cell, so every cell shows the master's hints (T08.01
    // fills them in BlockDebarker).
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        BEMultiblockDebarker? be = world.BlockAccessor.GetBlockEntity(selection.Position) as BEMultiblockDebarker;
        if (be?.Principal != null && world.BlockAccessor.GetBlock(be.Principal) is BlockDebarker master)
        {
            BlockSelection bs = selection.Clone();
            bs.Position = be.Principal;
            return master.GetPlacedBlockInteractionHelp(world, bs, forPlayer);
        }
        return base.GetPlacedBlockInteractionHelp(world, selection, forPlayer);
    }

    // This cell's slice of the master's footprint table, so the fillers collide from the same source as the frame
    // (BlockDebarker.GetCollisionBoxes). Principal unset or the master's chunk not loaded: the JSON full cube, which is
    // still solid. Collision and selection are the same boxes, held once on the filler's BE.
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BEMultiblockDebarker be && be.CellBoxes(blockAccessor) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetCollisionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BEMultiblockDebarker be && be.CellBoxes(blockAccessor) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetSelectionBoxes(blockAccessor, pos);
    }

    // The master's hover name (T08.03): "Debarker" once assembled, the frame's name before, from the one source
    // BlockDebarker.GetPlacedBlockName. Principal unset or dangling: the base name, which reads OnPickBlock below.
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        BEMultiblockDebarker? be = world.BlockAccessor.GetBlockEntity(pos) as BEMultiblockDebarker;
        if (be?.Principal != null && world.BlockAccessor.GetBlock(be.Principal) is BlockDebarker master)
        {
            return master.GetPlacedBlockName(world, be.Principal);
        }
        return base.GetPlacedBlockName(world, pos);
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        BEMultiblockDebarker? be = world.BlockAccessor.GetBlockEntity(pos) as BEMultiblockDebarker;
        if (be?.Principal == null)
        {
            // An orphaned filler picks the frame rather than a stack of the invisible block itself. The canonical
            // "-north" variant: with the side variantgroup a bare "debarker-frame" does not resolve.
            return new ItemStack(world.GetBlock(new AssetLocation("immersivewoodworking", "debarker-frame-north")));
        }
        Block principalBlock = world.BlockAccessor.GetBlock(be.Principal);
        return principalBlock.OnPickBlock(world, be.Principal);
    }

    // 0 (vanilla's null-principal value) only ever applies to an orphaned filler.
    public override int GetRandomColor(ICoreClientAPI capi, BlockPos pos, BlockFacing facing, int rndIndex = -1)
    {
        BEMultiblockDebarker? be = capi.World.BlockAccessor.GetBlockEntity(pos) as BEMultiblockDebarker;
        if (be?.Principal == null) return 0;
        Block principalBlock = capi.World.BlockAccessor.GetBlock(be.Principal);
        return principalBlock.GetRandomColor(capi, be.Principal, facing, rndIndex);
    }
}
