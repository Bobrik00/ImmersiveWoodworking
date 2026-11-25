using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The master side of the ghost footprint (AutoDebarkerPlan T03.03), a port of <see cref="BlockEntityChopper"/>'s
/// Multiblock partial: fill the non-master cells of <see cref="GetCells"/> with <see cref="BlockDebarkerGhost"/>
/// fillers, each stamped with <see cref="BEMultiblockDebarker.Principal"/> = this master, re-assert them on load, and
/// clear them when the frame goes (T03.04, <see cref="RemoveFootprintGhosts"/>).
/// The cell at <see cref="InputCellIndex"/> gets the <see cref="BlockDebarkerGhostPower"/> oriented by
/// <see cref="Facing"/> (<c>debarker-ghostpower-{side}</c>), the rest the plain ghost. Placement, self-heal and
/// teardown all read the one footprint table, so they cannot disagree on which cells the machine owns.
/// Everything here mutates the world and runs on the server only; the SetBlocks and the BE trees sync to clients.
/// </summary>
public partial class BlockEntityDebarker
{
    // Both codes are fixed and Facing never changes in place, so each block is resolved once.
    Block? ghostBlock;
    Block ResolveGhostBlock() => ghostBlock ??= Api.World.GetBlock(new AssetLocation("immersivewoodworking", "debarker-ghost"))!;

    Block? powerGhostBlock;
    Block ResolvePowerGhostBlock() => powerGhostBlock ??= Api.World.GetBlock(new AssetLocation("immersivewoodworking", "debarker-ghostpower-" + Facing.Code))!;

    /// <summary>The block footprint cell <paramref name="cellIndex"/> should hold: the oriented power ghost at
    /// <see cref="InputCellIndex"/>, the plain ghost elsewhere. Placement and self-heal both ask here.</summary>
    Block ExpectedGhostAt(int cellIndex) => cellIndex == InputCellIndex ? ResolvePowerGhostBlock() : ResolveGhostBlock();

    /// <summary>Fill the non-master footprint cells with fillers pointing back at this master. Called from
    /// <see cref="BlockDebarker.OnBlockPlaced"/> right after the frame is set. A player's placement has passed
    /// <see cref="BlockDebarker.CanPlaceBlock"/> first, so the cells are free; a frame set by a command or another mod's
    /// SetBlock has not, and its fillers overwrite whatever the cells hold.</summary>
    public void PlaceFootprintGhosts()
    {
        if (Api.Side != EnumAppSide.Server) return;
        BlockPos[] cells = GetCells(Pos, Facing);          // [0] is this master
        for (int i = 1; i < cells.Length; i++) StampGhost(ExpectedGhostAt(i), cells[i]);
    }

    // A copy of Pos, never the live reference, so the filler's pointer cannot move with this BE's own position object.
    void StampGhost(Block ghost, BlockPos cellPos)
    {
        Api.World.BlockAccessor.SetBlock(ghost.BlockId, cellPos);
        if (Api.World.BlockAccessor.GetBlockEntity(cellPos) is BEMultiblockDebarker mb)
        {
            mb.Principal = Pos.Copy();
            mb.MarkDirty(true);
        }
    }

    /// <summary>
    /// Self-heal of the footprint, run 100 ms after every <see cref="Initialize"/>: after a load, and after a placement,
    /// where it finds every cell already right. Saved fillers come back on their own with their serialized
    /// <see cref="BEMultiblockDebarker.Principal"/>; this catches the ones lost to a world edit or a command. Per
    /// non-master cell: a filler pointing at another standing debarker, or at one in a chunk not loaded yet, is left
    /// to it; the right ghost whose BE points anywhere else, or nowhere, is re-stamped; air or a ghost of the wrong kind (a plain one on the power cell) gets
    /// the expected ghost, water left in the fluid layer included; any other block is left alone, so a player's block
    /// is never destroyed even if the machine stays short a cell. A cell whose chunk is not loaded is skipped, since
    /// "gone" and "not loaded yet" look the same; the next load checks it again.
    /// </summary>
    public void EnsureFootprintGhosts()
    {
        if (Api.Side != EnumAppSide.Server) return;
        BlockPos[] cells = GetCells(Pos, Facing);
        for (int i = 1; i < cells.Length; i++)
        {
            BlockPos cellPos = cells[i];
            if (Api.World.BlockAccessor.GetChunkAtBlockPos(cellPos) == null) continue;

            // A cell freed by a world edit can be taken into a neighbouring debarker's footprint before this master
            // reloads. Its filler then points at that machine, and claiming it would cut the neighbour's cell off. A
            // master whose chunk is not loaded yet cannot be told from a dead one, so its fillers are left alone too.
            BEMultiblockDebarker? mb = Api.World.BlockAccessor.GetBlockEntity(cellPos) as BEMultiblockDebarker;
            BlockPos? principal = mb?.Principal;
            if (principal != null && !Pos.Equals(principal)
                && (Api.World.BlockAccessor.GetChunkAtBlockPos(principal) == null
                    || Api.World.BlockAccessor.GetBlockEntity(principal) is BlockEntityDebarker)) continue;

            // The solid layer only: the ghost does not displace liquids, so water stays in its cell's fluid layer, and the
            // default read would answer "water" for a lost filler on a machine built in the shallows (memory
            // vs-getblock-default-returns-fluid).
            Block expected = ExpectedGhostAt(i);
            Block at = Api.World.BlockAccessor.GetBlock(cellPos, BlockLayersAccess.Solid);
            if (at.Id == expected.Id)
            {
                if (mb != null && !Pos.Equals(principal))
                {
                    mb.Principal = Pos.Copy();
                    mb.MarkDirty(true);
                }
            }
            else if (at.Id == 0 || at is BlockDebarkerGhost)
            {
                StampGhost(expected, cellPos);
            }
        }
    }

    /// <summary>
    /// The space-release half of the teardown (T03.04): clear every non-master cell holding a filler whose
    /// <see cref="BEMultiblockDebarker.Principal"/> points at this master. A neighbouring debarker's filler and a
    /// player's block are left as they are. Called from <see cref="BlockDebarker.OnBlockRemoved"/>, before its base (see
    /// there). A break started on a filler clears that filler here while its own
    /// <see cref="BlockDebarkerGhost.OnBlockBroken"/> is still on the stack; that does not recurse, because the filler's
    /// base <c>OnBlockRemoved</c> only drops its BE. A filler in a chunk not loaded at the time is missed and stays as an
    /// orphan (see <see cref="BlockDebarkerGhost"/>).
    /// </summary>
    public void RemoveFootprintGhosts()
    {
        if (Api.Side != EnumAppSide.Server) return;
        BlockPos[] cells = GetCells(Pos, Facing);
        for (int i = 1; i < cells.Length; i++)
        {
            BlockPos cellPos = cells[i];
            // The power ghost derives from the plain one, so one test covers both. Water in the fluid layer stays: the
            // ghost never displaced it, and SetBlock(0) clears the solid layer only.
            if (Api.World.BlockAccessor.GetBlock(cellPos, BlockLayersAccess.Solid) is not BlockDebarkerGhost) continue;
            if (Api.World.BlockAccessor.GetBlockEntity(cellPos) is BEMultiblockDebarker mb && Pos.Equals(mb.Principal))
            {
                Api.World.BlockAccessor.SetBlock(0, cellPos);
            }
        }
    }
}
