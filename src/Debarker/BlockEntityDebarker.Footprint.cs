using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The debarker's multiblock shape in one place (AutoDebarkerPlan T03.02): the footprint offset table
/// <see cref="FootprintLocal"/> with the master first, the per-cell <see cref="CollisionOverrides"/>, the power cell
/// <see cref="InputCellLocal"/>, the chute's feed cell <see cref="FeedCellLocal"/> (T05.04), and the orientation maths
/// that turns all of them into world cells and boxes under <see cref="Facing"/>. A port of <see cref="BlockEntityChopper"/>'s Footprint partial. The ghost placement (T03.03),
/// teardown (T03.04), power node (T04.01) and input dock (T05.04) read the footprint from here, so re-shaping the
/// machine is a table paste.
///
/// Both tables are printed by <c>AutoDebarkerPlan/scan_collision.py</c> from the model
/// (<c>art/shapes/debarker/autodebarker.json</c>; the game copy <c>shapes/block/debarker/debarker.json</c> has the
/// same elements) and checked by its <c>--verify</c> mode: every cell carries at least one box inside [0,1] (which
/// <see cref="OrientBox"/> keeps inside in every facing), and the tables equal a fresh scan. After a Blender edit re-run the scanner, re-paste both tables, and break
/// and re-place any standing machine: its fillers were stamped at placement.
/// </summary>
public partial class BlockEntityDebarker
{
    // The footprint in the master's LOCAL frame, authored as if placed facing SOUTH, the shape's identity variant (memory
    // vs-sawmill-model-cell-mapping: cell = floor(pixel/16), the master is pixels [0,16) on every axis). +Z runs along
    // the log, +X is the side the coupling comes out on, +Y is up; RotateToFacing turns the table onto the other three
    // facings. The machine fills the box X -1..0, Y 0..2, Z -1..1: 17 cells pass the scanner's 4 % fill threshold, and
    // (-1,2,0) above the log joins by the author's call 2026-11-21 (LOG_CELLS in the scanner), so nothing can be placed
    // into the log's top. [0] MUST stay the master (0,0,0); the rest may be in any order. "%" is the share of the cell
    // the model fills. Generated: re-run the scanner rather than editing by hand.
    internal static readonly Vec3i[] FootprintLocal =
    {
        // ── Y=0 ──
        new Vec3i( 0, 0, 0),  // [0] MASTER
        new Vec3i(-1, 0, 1),  // 21%
        new Vec3i( 0, 0, 1),  // 32%
        new Vec3i(-1, 0, 0),  // 17%
        new Vec3i(-1, 0,-1),  // 18%
        new Vec3i( 0, 0,-1),  // 32%
        // ── Y=1 ──
        new Vec3i(-1, 1, 1),  // 16%
        new Vec3i( 0, 1, 1),  // 25%
        new Vec3i(-1, 1, 0),  // 19%
        new Vec3i( 0, 1, 0),  // 38%
        new Vec3i(-1, 1,-1),  // 29%
        new Vec3i( 0, 1,-1),  // 26%
        // ── Y=2 ──
        new Vec3i(-1, 2, 1),  // 7%
        new Vec3i( 0, 2, 1),  // 16%
        new Vec3i(-1, 2, 0),  // 25% with the log
        new Vec3i( 0, 2, 0),  // 18%
        new Vec3i(-1, 2,-1),  // 19%
        new Vec3i( 0, 2,-1),  // 37%
    };

    // The power-input filler's cell (T04.01). The coupling debarker-coupling ends flush on this cell's +X face
    // (x = 16 px), the only face an axle can reach it through, so the network node lives on this filler and the master
    // reads the speed cross-cell (memory vs-mp-consumer-interior-master). MUST be a FootprintLocal cell, or
    // InputCellIndex is -1 and no power filler is placed. Kept in this file below FootprintLocal: static initializers
    // of a partial class run in textual order only within one file, and InputCellIndex reads both.
    internal static readonly Vec3i InputCellLocal = new Vec3i(0, 1, 0);

    // The one cell a chute or hopper feeds the log through (T05.04), not to be confused with InputCellLocal: the cell
    // above the log, fed through its top face, so the feed stands on the machine's roof (the author's call at T03.02,
    // which put this cell into the footprint). MUST be a FootprintLocal cell, or no filler stands there and nothing feeds.
    // Neither eject of T06.03 (the log out of its exit end, the bark from the chute under the disc) uses the roof.
    internal static readonly Vec3i FeedCellLocal = new Vec3i(-1, 2, 0);

    /// <summary>Index of <see cref="InputCellLocal"/> in <see cref="FootprintLocal"/>, so the world cell is
    /// <c>GetCells(...)[InputCellIndex]</c>; -1 if it is not a footprint cell. Resolved by value, so the table may be
    /// reordered.</summary>
    internal static readonly int InputCellIndex = IndexOfLocal(InputCellLocal);

    static int IndexOfLocal(Vec3i local)
    {
        for (int i = 0; i < FootprintLocal.Length; i++)
            if (FootprintLocal[i].X == local.X && FootprintLocal[i].Y == local.Y && FootprintLocal[i].Z == local.Z) return i;
        return -1;
    }

    /// <summary>The world face an axle attaches to on the power filler: local +X, the coupling side, under
    /// <paramref name="facing"/>, by the same rotation <see cref="RotateToFacing"/> applies to cells (SOUTH gives EAST).
    /// For the connector gate and network discovery of T04.01.</summary>
    internal static BlockFacing InputFaceFor(BlockFacing facing)
        => BlockFacing.FromNormal(RotateToFacing(new Vec3i(1, 0, 0), facing));

    /// <summary>
    /// Rotate a local footprint offset (authored facing SOUTH) onto <paramref name="facing"/>: a proper rotation about Y,
    /// local +Z to <paramref name="facing"/>, local +X to the horizontal 90° from it the same way EAST is from SOUTH
    /// (facing EAST puts local +X on NORTH), Y untouched. SOUTH is the identity, matching the <c>rotateY</c> values of
    /// <c>shapeByType</c> in <c>frame.json</c>. Pure int maths.
    /// </summary>
    internal static Vec3i RotateToFacing(Vec3i local, BlockFacing facing)
    {
        int nx = facing.Normali.X, nz = facing.Normali.Z;
        return new Vec3i(local.X * nz + local.Z * nx, local.Y, -local.X * nx + local.Z * nz);
    }

    /// <summary>
    /// The world cells of the footprint for a master at <paramref name="master"/> facing <paramref name="facing"/>, in
    /// <see cref="FootprintLocal"/> order (index 0 is the master). Placement (T03.03) and teardown (T03.04) both read this,
    /// so they cannot disagree on which cells the machine owns. Static so placement can call it before the BE exists.
    /// </summary>
    internal static BlockPos[] GetCells(BlockPos master, BlockFacing facing)
    {
        var cells = new BlockPos[FootprintLocal.Length];
        for (int i = 0; i < FootprintLocal.Length; i++)
        {
            Vec3i w = RotateToFacing(FootprintLocal[i], facing);
            cells[i] = master.AddCopy(w.X, w.Y, w.Z);
        }
        return cells;
    }

    /// <summary>A world cell's offset in the master's LOCAL frame, the inverse of <see cref="RotateToFacing"/> under
    /// <see cref="Facing"/>. Pure read of <see cref="BlockEntity.Pos"/> and <see cref="Facing"/>, safe off the main
    /// thread.</summary>
    Vec3i LocalOf(BlockPos cellPos)
    {
        int dx = cellPos.X - Pos.X, dy = cellPos.Y - Pos.Y, dz = cellPos.Z - Pos.Z;
        int nx = Facing.Normali.X, nz = Facing.Normali.Z;
        return new Vec3i(dx * nz - dz * nx, dy, dx * nx + dz * nz);
    }

    /// <summary>
    /// This cell's collision boxes, or null when <paramref name="cellPos"/> is not one of this machine's cells (the
    /// caller then keeps the JSON cube). The frame (<see cref="BlockDebarker.GetCollisionBoxes"/>) and every filler
    /// (<see cref="BlockDebarkerGhost.GetCollisionBoxes"/> through <see cref="BEMultiblockDebarker.CellBoxes"/>) route
    /// here, so the whole footprint collides from one table.
    ///
    /// Every box lies inside its own cell's [0,1], which is what keeps the machine tunnel-free: the engine clips motion
    /// but never pushes out an entity already inside a box, and it scans only the cells under the entity, so a box
    /// hanging into a neighbouring cell is entered before it is ever tested (memory vs-multiblock-collision-broadphase).
    /// The scanner cuts boxes at the cell walls and <c>--verify</c> checks it, so no runtime clamp is needed.
    ///
    /// Served from a per-facing table built once (the chopper's AuditFixPlan T05.05): the answer depends only on the
    /// local cell and <see cref="Facing"/>, and Facing never changes in place (turning the machine means break and
    /// re-place). The aiming ray asks for selection boxes every frame it touches the machine, and collision is asked
    /// for every cell under a player at physics rate, so each ask is integer maths and one scan of an int array.
    /// Safe off the main thread: Pos, Facing and an immutable table.
    /// </summary>
    internal Cuboidf[]? GetCellCollisionBoxes(BlockPos cellPos)
    {
        BlockFacing facing = Facing;                         // read once, so the table and the key agree on it
        CellBoxTable? table = cellBoxes;
        if (table == null || table.Facing != facing) cellBoxes = table = BuildCellBoxTable(facing);

        int dx = cellPos.X - Pos.X, dy = cellPos.Y - Pos.Y, dz = cellPos.Z - Pos.Z;
        if (!InPackRange(dx) || !InPackRange(dy) || !InPackRange(dz)) return null;
        int nx = facing.Normali.X, nz = facing.Normali.Z;
        // LocalOf's inverse rotation without its Vec3i allocation.
        int key = PackLocal(dx * nz - dz * nx, dy, dx * nx + dz * nz);

        int[] keys = table.Keys;
        for (int i = 0; i < keys.Length; i++) if (keys[i] == key) return table.Boxes[i];
        return null;
    }

    /// <summary>This cell's selection outline: the same boxes as <see cref="GetCellCollisionBoxes"/>, so the white
    /// highlight traces the frame instead of a full cube per cell. Every interaction routes to the master whichever
    /// cell is hit, so a thin cell's small target costs nothing.</summary>
    internal Cuboidf[]? GetCellSelectionBoxes(BlockPos cellPos) => GetCellCollisionBoxes(cellPos);

    // One entry per FootprintLocal cell, in its order: the packed local offset and the boxes already turned onto the
    // facing. Immutable once built and published by one volatile reference write, so the collision, particle and render
    // threads may read it at once; two threads racing to build it build equal tables. Handing every caller the same
    // array is safe as in vanilla Block.GetCollisionBoxes: no caller mutates it (CachedCuboidList.Add copies, the aiming
    // ray copies into its own tmpCuboidd).
    sealed class CellBoxTable
    {
        internal readonly BlockFacing Facing;
        internal readonly int[] Keys;
        internal readonly Cuboidf[][] Boxes;

        internal CellBoxTable(BlockFacing facing, int[] keys, Cuboidf[][] boxes)
        {
            Facing = facing; Keys = keys; Boxes = boxes;
        }
    }

    volatile CellBoxTable? cellBoxes;

    // A local offset packed into one int, 10 bits per axis with a +512 bias: injective over [-512, 511]^3, far wider than
    // the footprint. InPackRange turns anything outside away, so a corrupt Principal far off can never alias a real key.
    static int PackLocal(int x, int y, int z) => ((x + 512) << 20) | ((y + 512) << 10) | (z + 512);
    static bool InPackRange(int v) => v >= -512 && v <= 511;

    /// <summary>The table for one facing: each footprint cell's boxes from <see cref="CollisionOverrides"/>, turned by
    /// <see cref="OrientBox"/>. A cell missing from the overrides gets the full unit cube, the same as the JSON
    /// fallback.</summary>
    static CellBoxTable BuildCellBoxTable(BlockFacing facing)
    {
        var keys = new int[FootprintLocal.Length];
        var boxes = new Cuboidf[FootprintLocal.Length][];

        for (int i = 0; i < FootprintLocal.Length; i++)
        {
            Vec3i l = FootprintLocal[i];
            keys[i] = PackLocal(l.X, l.Y, l.Z);

            Cuboidf[]? authored = null;
            foreach (var ov in CollisionOverrides)
                if (ov.cell.X == l.X && ov.cell.Y == l.Y && ov.cell.Z == l.Z) { authored = ov.boxes; break; }

            if (authored == null)
            {
                boxes[i] = new[] { new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f) };
                continue;
            }

            // Fresh boxes: CollisionOverrides is shared static data and must never be mutated.
            var rotated = new Cuboidf[authored.Length];
            for (int b = 0; b < authored.Length; b++) rotated[b] = OrientBox(authored[b], facing);
            boxes[i] = rotated;
        }

        return new CellBoxTable(facing, keys, boxes);
    }

    // Per-cell boxes, in-cell coordinates 0..1, in the same SOUTH-authored local frame as FootprintLocal. Generated by
    // scan_collision.py from the model without the log, except (-1,2,0), whose single box covers the log's upper 7 px:
    // a player on the roof stands on the log rather than in it, and a click on the log's top lands in the machine. The
    // machine's own parts in that cell are only the tops of the feed rollers and the thrust (2 %, up to about 4 px), so
    // with no log loaded most of that box is air. Selection follows the same boxes (GetCellSelectionBoxes).
    static readonly (Vec3i cell, Cuboidf[] boxes)[] CollisionOverrides =
    {
        (new Vec3i( 0, 0, 0), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.438f,1.000f,1.000f), new Cuboidf(0.625f,0.000f,0.000f, 0.938f,1.000f,1.000f), new Cuboidf(0.438f,0.562f,0.000f, 0.562f,1.000f,1.000f) }),  // 36%
        (new Vec3i(-1, 0, 1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,1.000f,1.000f) }),  // 21%
        (new Vec3i( 0, 0, 1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.938f,1.000f,1.000f) }),  // 32%
        (new Vec3i(-1, 0, 0), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,0.500f,1.000f) }),  // 17%
        (new Vec3i(-1, 0,-1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,1.000f,1.000f) }),  // 18%
        (new Vec3i( 0, 0,-1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.938f,1.000f,1.000f) }),  // 32%
        (new Vec3i(-1, 1, 1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,1.000f,1.000f) }),  // 16%
        (new Vec3i( 0, 1, 1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.438f,1.000f,1.000f), new Cuboidf(0.625f,0.000f,0.625f, 0.875f,0.875f,0.875f), new Cuboidf(0.438f,0.000f,0.000f, 0.562f,1.000f,0.438f) }),  // 25%
        (new Vec3i(-1, 1, 0), new[]{ new Cuboidf(0.000f,0.375f,0.000f, 0.938f,1.000f,1.000f), new Cuboidf(0.000f,0.125f,0.000f, 0.250f,0.500f,1.000f) }),  // 19%
        (new Vec3i( 0, 1, 0), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,1.000f,1.000f) }),  // 38%
        (new Vec3i(-1, 1,-1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,1.000f,1.000f) }),  // 29%
        (new Vec3i( 0, 1,-1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.438f,1.000f,1.000f), new Cuboidf(0.625f,0.000f,0.125f, 0.875f,1.000f,0.375f), new Cuboidf(0.438f,0.000f,0.562f, 0.562f,1.000f,1.000f) }),  // 26%
        (new Vec3i(-1, 2, 1), new[]{ new Cuboidf(0.625f,0.000f,0.000f, 1.000f,0.375f,0.875f), new Cuboidf(0.062f,0.000f,0.000f, 0.375f,0.188f,0.250f) }),  // 7%
        (new Vec3i( 0, 2, 1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.500f,1.000f,1.000f), new Cuboidf(0.438f,0.000f,0.000f, 0.562f,0.125f,0.250f) }),  // 16%
        (new Vec3i(-1, 2, 0), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,0.438f,1.000f) }),  // 25% with the log
        (new Vec3i( 0, 2, 0), new[]{ new Cuboidf(0.062f,0.000f,0.000f, 0.250f,0.750f,1.000f), new Cuboidf(0.000f,0.812f,0.000f, 0.438f,1.000f,1.000f), new Cuboidf(0.438f,0.000f,0.000f, 0.562f,0.438f,1.000f) }),  // 18%
        (new Vec3i(-1, 2,-1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 1.000f,1.000f,1.000f) }),  // 19%
        (new Vec3i( 0, 2,-1), new[]{ new Cuboidf(0.000f,0.000f,0.000f, 0.875f,1.000f,1.000f) }),  // 37%
    };

    /// <summary>
    /// Turn an in-cell box from the SOUTH-authored local frame onto <paramref name="facing"/>, about the cell centre
    /// (0.5, 0.5, 0.5), by the same X-Z rotation <see cref="RotateToFacing"/> applies to cell positions. Without it only
    /// a SOUTH-placed machine collides where it is drawn: the cells move under the facing, the boxes inside them would
    /// not. A 90° turn about the centre keeps a [0,1] box inside [0,1]; Y is untouched. Returns a fresh box.
    /// </summary>
    static Cuboidf OrientBox(Cuboidf b, BlockFacing facing)
    {
        int nx = facing.Normali.X, nz = facing.Normali.Z;
        // Two opposite corners suffice for a 90° turn (the result stays axis-aligned); min/max gives the new box.
        float x1 = (b.X1 - 0.5f) * nz + (b.Z1 - 0.5f) * nx + 0.5f;
        float z1 = -(b.X1 - 0.5f) * nx + (b.Z1 - 0.5f) * nz + 0.5f;
        float x2 = (b.X2 - 0.5f) * nz + (b.Z2 - 0.5f) * nx + 0.5f;
        float z2 = -(b.X2 - 0.5f) * nx + (b.Z2 - 0.5f) * nz + 0.5f;
        return new Cuboidf(System.Math.Min(x1, x2), b.Y1, System.Math.Min(z1, z2),
                           System.Math.Max(x1, x2), b.Y2, System.Math.Max(z1, z2));
    }
}
