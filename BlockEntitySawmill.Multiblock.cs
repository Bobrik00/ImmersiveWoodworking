using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// T03.03 — the master side of the ghost footprint: place / re-assert / remove the invisible
/// <see cref="BlockSawmillGhost"/> fillers that occupy the non-master cells of the footprint, each stamped
/// with <see cref="BEMultiblockSawmill.Principal"/> = this master's <see cref="BlockEntity.Pos"/> so its proxy
/// methods (click / break / collision, T03.01/T03.02) route here. Mirrors vanilla
/// <c>BlockPulverizer.PlaceFakeBlock</c> (which SetBlocks <c>mppulverizertop</c> and stamps its
/// <c>BEMPMultiblock.Principal</c>), generalised from the pulverizer's single up-cell to our multi-cell footprint
/// (currently 19 cells) via the shared table (<see cref="GetCells"/>) — so the shape never disagrees with collision (T03.02) or teardown
/// (T03.05). All world-mutating, server-only.
/// </summary>
public partial class BlockEntitySawmill
{
    // The ghost block, resolved once. Its code/domain is fixed (sawmill-ghost in this mod's domain); caching avoids
    // a dictionary lookup per cell per (re)placement.
    Block? ghostBlock;
    Block ResolveGhostBlock() => ghostBlock ??= Api.World.GetBlock(new AssetLocation("immersivewoodworking", "sawmill-ghost"))!;

    // The power-input ghost (E04 / T04.01) for the ONE input cell (InputCellIndex) — it carries the mechanical-power
    // node (BlockSawmillGhostPower + BEBehaviorSawmillMP) and so must be oriented to the machine's feed Facing
    // (sawmill-ghostpower-{side}). Facing is fixed for a placed machine, so caching is safe; resolved lazily.
    Block? powerGhostBlock;
    Block ResolvePowerGhostBlock() => powerGhostBlock ??= Api.World.GetBlock(new AssetLocation("immersivewoodworking", "sawmill-ghostpower-" + Facing.Code))!;

    /// <summary>The block expected at footprint cell index <paramref name="cellIndex"/>: the oriented power-input
    /// ghost at <see cref="InputCellIndex"/>, the plain ghost everywhere else. One source of truth for placement and
    /// self-heal so they can't disagree on which cell is the connector.</summary>
    Block ExpectedGhostAt(int cellIndex) => cellIndex == InputCellIndex ? ResolvePowerGhostBlock() : ResolveGhostBlock();

    /// <summary>
    /// Fill the non-master footprint cells with ghost-fillers, stamping each one's <see cref="BEMultiblockSawmill.Principal"/>
    /// to this master. Called from <see cref="BlockSawmill.OnBlockPlaced"/> right after the frame is set — by then
    /// <see cref="BlockSawmill.CanPlaceBlock"/> has already guaranteed all cells are free, so the SetBlocks land in
    /// air/replaceable space. Server-only; the SetBlock + BE state sync to clients.
    /// </summary>
    public void PlaceFootprintGhosts()
    {
        if (Api.Side != EnumAppSide.Server) return;
        BlockPos[] cells = GetCells(Pos, Facing);          // [0] = this master, [1..] = the fillers
        for (int i = 1; i < cells.Length; i++) StampGhost(ExpectedGhostAt(i), cells[i]); // power-input ghost at InputCellIndex, plain elsewhere
    }

    /// <summary>Set a ghost block at <paramref name="cellPos"/> and point its <see cref="BEMultiblockSawmill.Principal"/>
    /// back at this master. A fresh <see cref="BlockEntity.Pos"/> copy is stored so the two never alias.</summary>
    void StampGhost(Block ghost, BlockPos cellPos)
    {
        Api.World.BlockAccessor.SetBlock(ghost.BlockId, cellPos);
        if (Api.World.BlockAccessor.GetBlockEntity(cellPos) is BEMultiblockSawmill mb)
        {
            mb.Principal = Pos.Copy();
            mb.MarkDirty(true);
        }
    }

    /// <summary>
    /// Self-heal the ghost footprint (T03.03), called deferred on load (see <see cref="Initialize"/>). Saved ghosts
    /// normally restore themselves with their serialized <see cref="BEMultiblockSawmill.Principal"/>, so this is a
    /// safety net for fillers lost to corruption / external world edits, NOT the primary mechanism. Per non-master
    /// cell whose chunk is loaded: leave a correct ghost alone, re-stamp a stale one (wrong/null Principal), (re)place
    /// one into an empty cell, and conservatively SKIP a cell holding some other solid block — never destroy a
    /// player's block, even if that leaves the structure compromised. A cell whose chunk isn't loaded yet is skipped
    /// (we can't tell "gone" from "not loaded"); the next load/edit re-checks. Server-only.
    /// </summary>
    public void EnsureFootprintGhosts()
    {
        if (Api.Side != EnumAppSide.Server) return;
        BlockPos[] cells = GetCells(Pos, Facing);
        for (int i = 1; i < cells.Length; i++)
        {
            BlockPos cellPos = cells[i];
            if (Api.World.BlockAccessor.GetChunkAtBlockPos(cellPos) == null) continue; // chunk not loaded → can't validate

            Block expected = ExpectedGhostAt(i);
            Block at = Api.World.BlockAccessor.GetBlock(cellPos);
            if (at.Id == expected.Id)
            {
                // Already the right ghost — just make sure it still points back at us (re-stamp a stale/null Principal).
                if (Api.World.BlockAccessor.GetBlockEntity(cellPos) is BEMultiblockSawmill mb && !Pos.Equals(mb.Principal))
                {
                    mb.Principal = Pos.Copy();
                    mb.MarkDirty(true);
                }
            }
            else if (at.Id == 0 || at is BlockSawmillGhost) // air → lost filler; OR a wrong-type/old ghost of ours
            {                                                // (e.g. a plain ghost at the input cell, pre-E04 saves) → fix it
                StampGhost(expected, cellPos);
            }
            // else: a foreign solid block sits in the cell — leave it untouched (don't stomp a player's block)
        }
    }

    /// <summary>
    /// Remove the ghost-fillers — the space-release half of teardown, kept with placement so breaking the frame
    /// never leaves orphaned fillers. Only clears cells that actually hold OUR ghost pointing back
    /// at us, so it can't delete an unrelated block — which also makes it IDEMPOTENT (a second call finds air and skips
    /// every cell). Called from <see cref="BlockSawmill.OnBlockBroken"/> AND from
    /// <see cref="BlockSawmill.OnBlockRemoved"/> (the universal seam that also catches explosions / worldedit / any
    /// other <c>SetBlock</c>), in both cases while this BE is STILL REGISTERED — which is what matters, since the
    /// ownership test below needs it. (On the <c>OnBlockRemoved</c> seam the frame's block id is already cleared by
    /// the time we run; only the BE deregistration is still pending, and that is what <c>base</c> does.) It has NO
    /// chunk-loaded guard (unlike <see cref="EnsureFootprintGhosts"/>), so a cell in an unloaded chunk can still be
    /// missed — the orphan guard in <see cref="BlockSawmillGhost.OnBlockBroken"/> is the backstop that lets a player
    /// mine such a filler away. The FULL teardown that also DROPS the installed parts + held log
    /// is T03.05 (it extends the frame's break path); this is just the fillers. Server-only.
    /// </summary>
    public void RemoveFootprintGhosts()
    {
        if (Api.Side != EnumAppSide.Server) return;
        BlockPos[] cells = GetCells(Pos, Facing);
        for (int i = 1; i < cells.Length; i++)
        {
            BlockPos cellPos = cells[i];
            // Plain ghost OR power-input ghost (BlockSawmillGhostPower derives from BlockSawmillGhost) — both ours.
            if (Api.World.BlockAccessor.GetBlock(cellPos) is not BlockSawmillGhost) continue;
            if (Api.World.BlockAccessor.GetBlockEntity(cellPos) is BEMultiblockSawmill mb && Pos.Equals(mb.Principal))
            {
                Api.World.BlockAccessor.SetBlock(0, cellPos);
            }
        }
    }
}
