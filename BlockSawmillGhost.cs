using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The invisible "fake" block that fills the non-master cells of the sawmill's footprint (currently 18 fillers
/// around the master) — the mod's analogue of vanilla <c>BlockMPMultiblockPulverizer</c>. It provides collision (so the multiblock is solid and
/// nothing can be placed inside it) and has NO logic of its own: every player interaction is PROXIED to the master
/// (the placed <c>sawmill-frame</c>) via its <see cref="BEMultiblockSawmill.Principal"/> pointer, so a click / break
/// / pick on ANY cell behaves like the same action on the master. This is what makes the multiblock act as one
/// object. The fillers are placed (with their <see cref="BEMultiblockSawmill.Principal"/> stamped) by the frame's
/// footprint capture in T03.03; this class is the routing skeleton only.
///
/// Each override mirrors the vanilla original's <c>Principal</c> fork:
///  • <see cref="BEMultiblockSawmill.Principal"/> set → delegate to the master block at that position;
///  • Principal null (broken by game code, or never stamped) → fall back to the base/no-op path. The no-op for
///    <see cref="OnGettingBroken"/> is "never break" (return 1), matching vanilla, so a desynced filler can't be
///    chiselled away to orphan the structure.
///  • Principal set but DANGLING — the master was removed and something else (air, water, a block a player built on
///    the ruins) now occupies that cell → the break pair (<see cref="OnGettingBroken"/> + <see cref="OnBlockBroken"/>)
///    treats this filler as an ordinary block and lets the player mine it away. Delegating there instead would both
///    strand the filler forever and, for a foreign block, destroy someone else's property. The remaining proxies
///    (name / pick / colour / particles) still delegate blindly in that state; harmless cosmetics, never a throw.
/// The block ships with no texture (shape <c>game:block/basic/nothing</c>), so <see cref="GetRandomColor"/> and
/// <see cref="GetParticleBreakBox"/> are overridden to borrow the master's — otherwise break particles would be
/// black/zero-sized.
///
/// LAND CLAIMS (AuditFixPlan T07.02). Because these overrides act at a cell the player did not click, the permission
/// the engine granted for the clicked cell is the wrong one, and a claim boundary drawn across a standing footprint
/// turns every filler outside it into a door into the machine inside. The two access flags are covered separately,
/// because the engine treats them separately: for USE it redirects the test itself once the block offers
/// <see cref="IMultiblockOffset"/>, which <see cref="BEMultiblockSawmill.GetControlBlockPos"/> now does; for
/// BuildOrBreak it does not redirect at all, so the break pair below re-tests the master cell by hand.
/// </summary>
public class BlockSawmillGhost : Block
{
    public override float OnGettingBroken(IPlayer player, BlockSelection blockSel, ItemSlot itemslot, float remainingResistance, float dt, int counter)
    {
        IWorldAccessor world = player?.Entity?.World ?? api.World;
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEMultiblockSawmill;
        if (be?.Principal == null) return 1f; // never break — no master to route to

        // Same ORPHAN GUARD as OnBlockBroken, and it MUST agree with it: this method routes the mining resistance to
        // the principal cell, and Block.OnGettingBroken reads GetRequiredMiningTier at the position it is GIVEN. If a
        // dangling Principal pointed at a block whose mining tier outranks the player's tool, resistance would never
        // fall, the break would never complete, and OnBlockBroken's guard would be unreachable dead code in exactly
        // the case it exists for. Falling back to base lets the orphan yield to its own `resistance: 3.5`, untiered.
        Block principalBlock = world.BlockAccessor.GetBlock(be.Principal, BlockLayersAccess.SolidBlocks);
        if (principalBlock is not BlockSawmill)
        {
            return base.OnGettingBroken(player, blockSel, itemslot, remainingResistance, dt, counter);
        }

        // CLAIM GATE, the mining half (AuditFixPlan T07.02, F-L3-03) — and it MUST agree with OnBlockBroken's, for
        // the same reason the orphan guard above does: the engine tests BuildOrBreak once, at the CLICKED cell, and
        // never again, so a filler poking out of a claim is an unguarded handle on the machine inside it. The break
        // itself is refused in OnBlockBroken; refusing the RESISTANCE here as well is what keeps the player from
        // digging at an invisible cell forever for a break that can never be allowed to land. "Never break" (1f) is
        // this method's own no-op for a cell it may not route, already used by the Principal-null fork above.
        //
        // Which of the two claim calls: this runs on every damage tick of a held mouse button, so TryAccess — which
        // MESSAGES the player and re-syncs the block — may not be the one that runs per tick. `counter` is the
        // engine's survivalBreakingCounter, reset to 0 whenever the player lets go or re-aims, so the first tick of
        // each fresh attempt speaks and every later one asks the silent TestAccess: one line per attempt, never a
        // stream. Both engine call sites for this method live in ClientMain, so this half is feedback only — a client
        // that ignores it still meets the authoritative gate in OnBlockBroken. Engine breaks arrive with no player
        // (null) and stay ungated: only a player can be denied, and WorldMap dereferences the player it is handed.
        if (player != null)
        {
            bool granted = counter == 0
                ? world.Claims.TryAccess(player, be.Principal, EnumBlockAccessFlags.BuildOrBreak)
                : world.Claims.TestAccess(player, be.Principal, EnumBlockAccessFlags.BuildOrBreak) == EnumWorldAccessResponse.Granted;
            if (!granted) return 1f;
        }

        BlockSelection bs = blockSel.Clone();
        bs.Position = be.Principal;
        return principalBlock.OnGettingBroken(player, bs, itemslot, remainingResistance, dt, counter);
    }

    public override void OnBlockBroken(IWorldAccessor world, BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier = 1f)
    {
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(pos) as BEMultiblockSawmill;
        if (be?.Principal == null)
        {
            // Broken by other game code (or while orphaned): standard block-break treatment.
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
            return;
        }

        // Broken by the player: break the MASTER instead (which dissolves the whole multiblock in T03.05).
        BlockPos principalPos = be.Principal;
        // Read the SOLID layer explicitly: the default layer falls back to the FLUID block when the solid layer is
        // empty, and the frame does not displace liquids (`sidesolid: {all:false}` → DisplacesLiquids false), so a
        // machine built on shallow water — idiomatic, since mech power comes from a water wheel — keeps water in the
        // master cell. Reading the default layer there would hand back the water block, not air.
        Block principalBlock = world.BlockAccessor.GetBlock(principalPos, BlockLayersAccess.SolidBlocks);

        // ORPHAN GUARD: Principal can be set-but-DANGLING — the master is gone but the pointer remains, so the
        // "Principal == null" fork above does NOT catch this. Test for OUR master POSITIVELY rather than for "not
        // air": the cell may hold water, a block another player built on the ruins, or another mod's block, and in
        // every one of those cases routing the break there is wrong twice over — this filler is never removed (base
        // is deliberately skipped below), leaving it invisible, solid and impossible to mine away forever; and for a
        // foreign block we would destroy someone else's property, which is exactly what EnsureFootprintGhosts refuses
        // to do one file over. Break this filler as an ordinary block instead: `drops: []` in ghost.json, so base
        // drops nothing, and it yields to its own `resistance: 3.5`. This clears orphans already sitting in existing
        // saves; the master's OnBlockRemoved is what stops new ones being created. Adopts vanilla
        // BlockMPMultiblockGear's guard — the mod was cloned from BlockMPMultiblockPulverizer, the one vanilla sibling
        // that LACKS it. Kept in lockstep with the identical test in OnGettingBroken, which must agree or the break
        // can never complete to reach this point.
        if (principalBlock is not BlockSawmill)
        {
            base.OnBlockBroken(world, pos, byPlayer, dropQuantityMultiplier);
            return; // no master break happened → nothing to announce to the master's neighbours
        }

        // CLAIM GATE (AuditFixPlan T07.02, F-L3-03). The engine authorised BuildOrBreak for the cell it was CLICKED
        // on and for that cell alone — ServerSystemBlockSimulation.HandleBlockPlaceOrBreak runs exactly one
        // TestBlockAccess, from the packet's own coordinates, and nothing downstream re-tests — so the jump to
        // principalPos below crosses into a cell whose claim nobody asked about. With 18 fillers around the master
        // that is not theoretical: a claim created or resized AFTER the machine was built cuts it freely, and
        // breaking one invisible filler outside the border would otherwise dissolve the whole machine inside it and
        // drop the frame and the saw set at the feet of whoever mined it. The mirror of the Use-side redirect this BE
        // now serves the engine (BEMultiblockSawmill.GetControlBlockPos), which the engine consults for Use and never
        // for BuildOrBreak.
        //
        // TryAccess, not TestAccess: this runs once per completed break, so it may speak — it names the claimant to
        // the player and re-syncs the block and the held slot, which is exactly what a refusal here needs (the
        // client predicted its own break). Refusing means breaking NOTHING: base is deliberately skipped, so the
        // filler stays, invisible and solid, exactly as it was. byPlayer is null for engine breaks (explosions,
        // /we, decay) and those stay ungated — WorldMap dereferences the player it is handed, and there is no one
        // to deny.
        if (byPlayer != null && !world.Claims.TryAccess(byPlayer, principalPos, EnumBlockAccessFlags.BuildOrBreak)) return;

        principalBlock.OnBlockBroken(world, principalPos, byPlayer, dropQuantityMultiplier);

        // Client-only: the player block-breaking code normally fires the neighbour-change notifications; replicate
        // that for the master's horizontals since we short-circuited it. Mirrors the vanilla original.
        if (api.Side == EnumAppSide.Client)
        {
            foreach (BlockFacing facing in BlockFacing.HORIZONTALS)
            {
                BlockPos npos = principalPos.AddCopy(facing);
                world.BlockAccessor.GetBlock(npos).OnNeighbourBlockChange(world, npos, principalPos);
            }
        }

        // Deliberately NOT calling base.OnBlockBroken when the principal was broken (avoids a double drop and the
        // stray break particles vanilla notes for the upper block).
    }

    public override Cuboidf GetParticleBreakBox(IBlockAccessor blockAccess, BlockPos pos, BlockFacing facing)
    {
        BEMultiblockSawmill? be = blockAccess.GetBlockEntity(pos) as BEMultiblockSawmill;
        if (be?.Principal == null) return base.GetParticleBreakBox(blockAccess, pos, facing);
        Block principalBlock = blockAccess.GetBlock(be.Principal);
        return principalBlock.GetParticleBreakBox(blockAccess, be.Principal, facing);
    }

    // This fake block has no texture of its own — without overriding, break particles would be black. Borrow the
    // master's colour; 0 (vanilla's null-principal value) only ever applies to an orphaned filler.
    public override int GetRandomColor(ICoreClientAPI capi, BlockPos pos, BlockFacing facing, int rndIndex = -1)
    {
        BEMultiblockSawmill? be = capi.World.BlockAccessor.GetBlockEntity(pos) as BEMultiblockSawmill;
        if (be?.Principal == null) return 0;
        Block principalBlock = capi.World.BlockAccessor.GetBlock(be.Principal);
        return principalBlock.GetRandomColor(capi, be.Principal, facing, rndIndex);
    }

    public override bool OnBlockInteractStart(IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEMultiblockSawmill;
        if (be?.Principal != null && world.BlockAccessor.GetBlockEntity(be.Principal) is BlockEntitySawmill master)
        {
            return master.OnInteract(byPlayer, blockSel);
        }
        return base.OnBlockInteractStart(world, byPlayer, blockSel);
    }

    /// <summary>Mirror <see cref="BlockSawmill.OnBlockInteractStep"/> through the master so the Shift+RMB cut-mode hold
    /// stays alive when the player clicks a ghost-filler cell (one press = one cycle, memory
    /// vs-block-interact-repeats-while-held); every other gesture returns false, as on the master.</summary>
    public override bool OnBlockInteractStep(float secondsUsed, IWorldAccessor world, IPlayer byPlayer, BlockSelection blockSel)
    {
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(blockSel.Position) as BEMultiblockSawmill;
        if (be?.Principal != null && world.BlockAccessor.GetBlockEntity(be.Principal) is BlockEntitySawmill master)
        {
            return master.IsCutModeGesture(byPlayer);
        }
        return base.OnBlockInteractStep(secondsUsed, world, byPlayer, blockSel);
    }

    // Interaction help (T08.01): forward to the MASTER block with the selection at the master cell, so looking at any
    // ghost-filler shows the same state-dependent hint the frame cell does (the master's GetPlacedBlockInteractionHelp
    // resolves its BE + appends its own base help). Principal unset (orphaned filler) → this block's base help.
    public override WorldInteraction[] GetPlacedBlockInteractionHelp(IWorldAccessor world, BlockSelection selection, IPlayer forPlayer)
    {
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(selection.Position) as BEMultiblockSawmill;
        if (be?.Principal != null && world.BlockAccessor.GetBlock(be.Principal) is BlockSawmill master)
        {
            BlockSelection bs = selection.Clone();
            bs.Position = be.Principal;
            return master.GetPlacedBlockInteractionHelp(world, bs, forPlayer);
        }
        return base.GetPlacedBlockInteractionHelp(world, selection, forPlayer);
    }

    // Hover NAME: route to the MASTER's GetPlacedBlockName (like OnPickBlock / interaction help route), so a ghost cell
    // shows the same state-dependent name the frame cell does — "Лесопилка" once assembled, else "Рама лесопилки".
    // Principal unset (orphaned filler) → this block's base name (via OnPickBlock, the frame stack). Keeps the whole
    // multiblock reading one name from a single source (BlockSawmill.GetPlacedBlockName).
    public override string GetPlacedBlockName(IWorldAccessor world, BlockPos pos)
    {
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(pos) as BEMultiblockSawmill;
        if (be?.Principal != null && world.BlockAccessor.GetBlock(be.Principal) is BlockSawmill master)
        {
            return master.GetPlacedBlockName(world, be.Principal);
        }
        return base.GetPlacedBlockName(world, pos);
    }

    // Collision / selection for a ghost cell: route to the master BE's per-cell footprint box (T03.02), so the 18
    // filler cells are solid from the SAME single source as the master and the whole multiblock stays tunnel-free
    // (memory vs-multiblock-collision-broadphase). Principal unset (orphaned filler, or before T03.03 stamps it) →
    // fall back to this block's JSON full cube, which is still solid. Pure read, off-main-thread-safe like the master's.
    // The cross-cell trip to the master runs ONCE per ghost cell and the answer is then held on the ghost BE
    // (BEMultiblockSawmill.CellBoxes, T05.05) — the collision and selection sets are the same boxes, so both routes
    // land on the same field. Selection alone is asked every frame the aiming ray touches the machine.
    public override Cuboidf[] GetCollisionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BEMultiblockSawmill be && be.CellBoxes(blockAccessor) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetCollisionBoxes(blockAccessor, pos);
    }

    public override Cuboidf[] GetSelectionBoxes(IBlockAccessor blockAccessor, BlockPos pos)
    {
        if (blockAccessor.GetBlockEntity(pos) is BEMultiblockSawmill be && be.CellBoxes(blockAccessor) is Cuboidf[] boxes)
        {
            return boxes;
        }
        return base.GetSelectionBoxes(blockAccessor, pos);
    }

    public override ItemStack OnPickBlock(IWorldAccessor world, BlockPos pos)
    {
        BEMultiblockSawmill? be = world.BlockAccessor.GetBlockEntity(pos) as BEMultiblockSawmill;
        if (be?.Principal == null)
        {
            // Hard-coded fallback for an orphaned filler — better than handing back an invalid stack of the
            // invisible block itself (mirrors the vanilla original returning the pulverizer frame). The canonical
            // "-north" variant (T03.04 added the "side" variantgroup; bare "sawmill-frame" no longer resolves).
            return new ItemStack(world.GetBlock(new AssetLocation("immersivewoodworking", "sawmill-frame-north")));
        }
        Block principalBlock = world.BlockAccessor.GetBlock(be.Principal);
        return principalBlock.OnPickBlock(world, be.Principal);
    }
}
