using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// T03.02 — the sawmill's multiblock SHAPE in one place: the footprint as a single offset table (currently 19
/// cells, fitted to the authored model — the count is whatever <see cref="FootprintLocal"/> holds, not a fixed
/// constant), the orientation maths that turns it into world cells, and the per-cell collision/selection derived from the SAME
/// table. Everything downstream reads the footprint from here — the footprint capture + ghost placement (T03.03),
/// the orientation wiring (T03.04), the teardown drop (T03.05), and the I/O / cut-point geometry (E05/E06) — so
/// re-shaping the machine (e.g. once the user's Blender model lands in T03.06) is a one-table edit, never a hunt
/// for magic offsets scattered across placement, teardown and rendering.
/// </summary>
public partial class BlockEntitySawmill
{
    // ───────────────────────── The footprint, in the master's LOCAL frame ─────────────────────────
    //
    // Feed axis = +Z (rotated onto the placement facing by RotateToFacing); +X is the perpendicular horizontal; +Y is
    // up. The footprint is the collision/selection volume fitted to the machine the user authored (occupancy scan
    // 2026-06-17, see the per-cell %% notes below): a SOLID horizontal base on the lower layers plus the two columns
    // that actually rise the machine's full height — the CENTER saw tower and the -X gear/crank tower — for the 19
    // occupied cells listed below, across a roughly 3×4×2-block envelope. The cell COUNT is whatever this table holds,
    // NOT a fixed design constant: it was 5 (the original 3-in-a-row base + a vertical saw channel), then briefly a
    // 24-cell plus-on-every-layer + corner pillar, before the occupancy scan trimmed the empty upper cells to today's
    // 19 (see PLAN.md footprint revisions). The machine sits ON the ground (model authored at Y≥0), so there is NO -Y
    // "pit" cell: every footprint cell is at/above the master, which is what makes placement STANDARD on-ground —
    // CanPlaceBlock only needs the cells above/around free (normally air on flat terrain), no clearance/pit
    // underneath. Index 0 is ALWAYS the master, so callers wanting just the fillers skip [0]. Single source of truth
    // for the shape — see the class summary.
    //
    // FLAT, hand-editable cell list (one line = one collision cell). Edit freely to match the model.
    //   • (X, Y, Z) = cell offset from the placed block. +Z = feed axis; +X = perpendicular; +Y = up.
    //   • Authored as if placed facing SOUTH (= identity); RotateToFacing rotates the WHOLE table for the other 3
    //     facings automatically — so author once for south and place facing south to verify.
    //   • [0] MUST stay (0,0,0) (the master cell). The rest may be in any order.
    //   • "fill%%" = how much of that cell the Blender model occupies (from the occupancy scan) — a guide, not a rule:
    //     the model is a thin frame, so a low %% can still be real structure; 0%% means the cell is empty (collision
    //     there is a "floating" box). The inline %% on the lines below are INDICATIVE (an earlier scan pass) and may
    //     differ from the authoritative per-cell occupancy that actually drove the collision boxes — that set lives in
    //     CollisionOverrides (both are regenerated together by AutomatedSawmillPlan/scan_collision.py; re-run it to
    //     refresh both). Re-build + BREAK and RE-PLACE the machine after editing (ghosts are stamped at
    //     placement; an already-placed machine won't pick up table changes).
    // Fitted to the model's real mass (occupancy scan 2026-06-17): a SOLID base (Y0-Y1, where the player walks — can't
    // walk through) shaped like the machine's footprint, plus the two columns that actually rise the full height — the
    // CENTER saw tower and the -X gear/crank tower. The empty upper cells of the old +X/+Z corner pillar (0% fill =
    // floating cubes) are dropped; the corner is kept only at Y1 where the wheel really sits (12%). NOTE: the model also
    // pokes ~1.5u into cell X=+2 (one block past +X) — that overhang is OUTSIDE this matrix and has no collision; pull
    // it back into the matrix in Blender, or add a (2,*,0) cell here if it should be solid.
    // (X,Y,Z) = cell offset from the placed block; +Z = feed/"south"; +Y = up. Authored for facing SOUTH (identity) —
    // RotateToFacing covers the other 3 facings. [0] MUST stay (0,0,0). Break & RE-PLACE after edits (ghosts stamp at
    // placement). "%%" = model fill of that cell (guide; thin frame ⇒ low %% can still be real).
    internal static readonly Vec3i[] FootprintLocal =
    {
        new Vec3i( 0, 0,  0),  // [0] MASTER — center, base                  (19%)
        // ── Y=0 solid base (plus) ──
        new Vec3i( 1, 0,  0),  // +X arm                                     (11%)
        new Vec3i(-1, 0,  0),  // -X arm  (gear tower base)                  (11%)
        new Vec3i( 0, 0,  1),  // +Z arm                                     ( 7%)
        new Vec3i( 0, 0, -1),  // -Z arm                                     ( 5%)
        // ── Y=1 body + wheel ──
        new Vec3i( 0, 1,  0),  // center                                     (21%)
        new Vec3i( 1, 1,  0),  // +X                                         (12%)
        new Vec3i(-1, 1,  0),  // -X      (gear tower)                       ( 7%)
        new Vec3i( 0, 1,  1),  // +Z      (wheel/log side)                   (26%)
        new Vec3i( 0, 1, -1),  // -Z                                         ( 3%)
        new Vec3i( 1, 1,  1),  // +X/+Z   (wheel corner — real)              (12%)
        // ── Y=2 towers only (saw + gear) ──
        new Vec3i( 0, 2,  0),  // center  (saw tower)                        ( 2%)
        new Vec3i(-1, 2,  0),  // -X      (gear tower)                       ( 2%)
        // ── +X extension (late-added; mixed Y, NOT the saw/gear towers) ──
        new Vec3i( 1, 2,  1),  // +X/+Z   (Y=2)
        new Vec3i( 1, 0,  1),  // +X/+Z   (Y=0 base)
        new Vec3i( 1, 2,  0),  // +X      (Y=2)
        // ── Y=3 towers only (saw + gear) ──
        new Vec3i( 0, 3,  0),  // center  (saw tower)                        ( 1%)
        new Vec3i(-1, 3,  0),  // -X      (gear tower top — real structure)  (15%)
        // ── +X extension top (interleaved here by Y-order; NOT a tower) ──
        new Vec3i( 1, 3,  0)   // +X      (Y=3, +X column top)
    };

    // ───────────────────────── Mechanical-power input cell (E04 / T04.01) ─────────────────────────
    //
    // The footprint cell that hosts the power-input ghost (BlockSawmillGhostPower + BEBehaviorSawmillMP) — the one
    // cell an axle can connect to. It is the exposed -X gear-tower cell where the authored model's `rotor_default`
    // disc sits (X≈-1, Y≈3, Z≈0 in cell units from the model scan). The master cell itself is INTERIOR (its four
    // Y=0 neighbours are all filler cells), so it can never be the connector — hence the node lives here. Authored
    // in the SOUTH/local frame like FootprintLocal; GetCells rotates it onto the placed facing.
    // PLACEHOLDER per the plan (T04.01 keeps the connector parametric): the exact cell/height/face is finalized with
    // the authored model at T07.06. MUST be one of FootprintLocal (else InputCellIndex is -1 and no node is placed).
    internal static readonly Vec3i InputCellLocal = new Vec3i(-1, 3, 0);

    /// <summary>Index of <see cref="InputCellLocal"/> within <see cref="FootprintLocal"/> (so the world cell is
    /// <c>GetCells(...)[InputCellIndex]</c>), or -1 if the placeholder isn't a real footprint cell. Resolved by
    /// value, not position, so the table may be reordered freely.</summary>
    internal static readonly int InputCellIndex = IndexOfLocal(InputCellLocal);

    static int IndexOfLocal(Vec3i local)
    {
        for (int i = 0; i < FootprintLocal.Length; i++)
            if (FootprintLocal[i].X == local.X && FootprintLocal[i].Y == local.Y && FootprintLocal[i].Z == local.Z) return i;
        return -1;
    }

    /// <summary>The world face an external axle attaches to, for a machine whose feed axis points
    /// <paramref name="feed"/>: the exposed gear-tower side = the world direction of local -X under the feed
    /// rotation (the same X-Z rotation <see cref="RotateToFacing"/> applies to cell positions). The input disc
    /// (<c>rotor_default</c>) spins about this face's axis. Used by <see cref="BlockSawmillGhostPower"/> (connector
    /// gate) and <see cref="BEBehaviorSawmillMP"/> (network discovery). PLACEHOLDER: the final face comes with the
    /// authored model (T07.06); the visible spin sign is not this method's business — it lives in
    /// <c>BlockEntitySawmill.Render.FacingDriveSign</c>, because the MP node itself renders nothing (T07.08).</summary>
    internal static BlockFacing InputFaceFor(BlockFacing feed)
    {
        // RotateToFacing((-1,0,0), feed) = world (-nz, 0, nx) where (nx,nz) is the feed unit's horizontal.
        int nx = feed.Normali.X, nz = feed.Normali.Z;
        int wx = -nz, wz = nx;
        foreach (BlockFacing f in BlockFacing.HORIZONTALS)
            if (f.Normali.X == wx && f.Normali.Z == wz) return f;
        return BlockFacing.WEST;
    }

    /// <summary>
    /// Rotate a local footprint offset (authored with feed axis = +Z) onto <paramref name="facing"/> — the world
    /// direction the feed axis points. A proper rotation about Y: local +Z → <paramref name="facing"/>, local +X →
    /// that turned 90° clockwise, Y untouched. Works for all 4 horizontal facings, so T03.04 only has to feed in the
    /// real placement facing; until then <see cref="Facing"/> is NORTH and the footprint lies along Z. Pure int maths.
    /// </summary>
    internal static Vec3i RotateToFacing(Vec3i local, BlockFacing facing)
    {
        int nx = facing.Normali.X, nz = facing.Normali.Z; // horizontal unit of the feed direction (Y is 0 for horizontals)
        return new Vec3i(local.X * nz + local.Z * nx, local.Y, -local.X * nx + local.Z * nz);
    }

    /// <summary>
    /// The world cells of the footprint (currently 19) for a master at <paramref name="master"/> facing
    /// <paramref name="facing"/>, in <see cref="FootprintLocal"/> order (index 0 = the master cell itself). The
    /// footprint capture + ghost placement (T03.03) and the teardown (T03.05) both read THIS, so they can never
    /// disagree on which cells the multiblock owns. Static so placement can call it before the BE is initialized.
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

    /// <summary>This world cell's offset in the master's LOCAL frame — the inverse of <see cref="RotateToFacing"/>
    /// (undo the facing rotation), so a world cell maps back to the authored (X,Y,Z) of <see cref="FootprintLocal"/> /
    /// <see cref="CollisionOverrides"/>. PURE read of <see cref="BlockEntity.Pos"/> + <see cref="Facing"/>,
    /// off-main-thread-safe (collision/selection can be queried off the main thread).</summary>
    Vec3i LocalOf(BlockPos cellPos)
    {
        int dx = cellPos.X - Pos.X, dy = cellPos.Y - Pos.Y, dz = cellPos.Z - Pos.Z;
        int nx = Facing.Normali.X, nz = Facing.Normali.Z;
        // inverse rotation (transpose of RotateToFacing's matrix); Y is unrotated
        return new Vec3i(dx * nz - dz * nx, dy, dx * nx + dz * nz);
    }

    /// <summary>
    /// Is <paramref name="cellPos"/> one of this master's footprint cells (the master itself or a ghost-filler)?
    /// Map it to the local frame (<see cref="LocalOf"/>) and test membership of <see cref="FootprintLocal"/>.
    /// </summary>
    bool IsFootprintCell(BlockPos cellPos)
    {
        Vec3i l = LocalOf(cellPos);
        foreach (Vec3i o in FootprintLocal)
            if (o.X == l.X && o.Y == l.Y && o.Z == l.Z) return true;
        return false;
    }

    /// <summary>
    /// This cell's CLAMPED collision slice, or null when <paramref name="cellPos"/> isn't part of the multiblock
    /// (the caller then keeps the JSON cube). The master block (<see cref="BlockSawmill.GetCollisionBoxes"/>) and
    /// every ghost-filler (<see cref="BlockSawmillGhost.GetCollisionBoxes"/>, via
    /// <see cref="BEMultiblockSawmill.Principal"/>) both route here, so all footprint cells (currently 19) collide from ONE source.
    ///
    /// The per-cell clamp to [0,1] is the tunnel-free guarantee (memory vs-multiblock-collision-broadphase, same as
    /// <c>BlockEntitySawhorse.GetPairCollisionBoxes</c>): VS clips motion without ejecting an overlapping entity and
    /// scans with no broadphase margin, so a box overhanging into a neighbour cell that doesn't itself report it is
    /// crossed before it's ever scanned → the player walks through the perpendicular face. Clamping every cell to its
    /// own footprint closes that from all sides. Placeholder stage: each occupied cell is a full unit cube (clamp is a
    /// no-op); when the Blender model lands (T03.06) each cell gets its own authored sub-box here and the clamp starts
    /// doing real work. Off-main-thread-safe (Pos + Facing + static table, fresh box), as collision requires.
    ///
    /// <para>Served from a per-facing table built once (AuditFixPlan T05.05) instead of being re-derived per query: the
    /// answer is a pure function of (local cell, <see cref="Facing"/>), and Facing is read from the block variant in
    /// Initialize and never moves after that (the machine has no in-place rotation — break and re-place). What the
    /// query does now is integer maths plus one scan of an int array; what it used to do was two <c>Vec3i</c>
    /// allocations, two linear passes over the footprint tables and a fresh <c>Cuboidf</c> per box, on EVERY engine
    /// ask — and the engine asks a lot: the aiming ray hits GetSelectionBoxes every frame, and a player or a mob
    /// walking through the machine has the physics window cover a stack of these cells at 30 Hz. The chopper's
    /// Footprint partial carries the same table (mirror pair).</para>
    /// </summary>
    internal Cuboidf[]? GetCellCollisionBoxes(BlockPos cellPos)
    {
        BlockFacing facing = Facing;                         // read once — the table and the key must agree on it
        CellBoxTable? table = cellBoxes;
        if (table == null || table.Facing != facing) cellBoxes = table = BuildCellBoxTable(facing);

        int dx = cellPos.X - Pos.X, dy = cellPos.Y - Pos.Y, dz = cellPos.Z - Pos.Z;
        if (!InPackRange(dx) || !InPackRange(dy) || !InPackRange(dz)) return null; // far-away cell → never ours
        int nx = facing.Normali.X, nz = facing.Normali.Z;
        // Same inverse rotation as LocalOf, without its Vec3i: world cell → the authored (SOUTH) frame.
        int key = PackLocal(dx * nz - dz * nx, dy, dx * nx + dz * nz);

        int[] keys = table.Keys;
        for (int i = 0; i < keys.Length; i++) if (keys[i] == key) return table.Boxes[i];
        return null;                                         // not ours → caller keeps the JSON cube
    }

    // ───────── The per-facing cell → boxes table (AuditFixPlan T05.05) ─────────
    //
    // One entry per FootprintLocal cell, in that table's order: its packed local offset, and the boxes already rotated
    // onto the placed facing, ready to hand straight back. Immutable once built and published through a single volatile
    // reference write, which is what makes it safe to read from the collision / particle / render threads at once;
    // building is a pure function of the facing, so two threads racing to build produce equal tables and either may win.
    // Handing out the SAME array on every query is safe for the same reason it is in vanilla Block.GetCollisionBoxes:
    // no caller mutates it (CachedCuboidList.Add copies into its own Cuboidd, the aiming ray into its tmpCuboidd).
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

    // Local offsets packed into one int, 10 bits each with a +512 bias. Injective over [-512, 511]³, which every
    // footprint offset sits far inside; InPackRange rejects anything outside so a corrupt Principal pointing far away
    // can never alias onto a real cell's key.
    static int PackLocal(int x, int y, int z) => ((x + 512) << 20) | ((y + 512) << 10) | (z + 512);
    static bool InPackRange(int v) => v >= -512 && v <= 511;

    /// <summary>Build the table for one facing: for each footprint cell take its authored boxes from
    /// <see cref="CollisionOverrides"/> (an unlisted cell gets the full unit cube, and an empty override stays empty =
    /// walk-through, exactly as the per-query build did) and rotate each box onto <paramref name="facing"/>.</summary>
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
                boxes[i] = new[] { ClampCell(PlaceholderCellBox()) }; // default: full unit cube (rotation-invariant)
                continue;
            }

            // FRESH boxes: CollisionOverrides is a shared static and must never be mutated.
            var rotated = new Cuboidf[authored.Length];
            for (int b = 0; b < authored.Length; b++) rotated[b] = OrientBox(authored[b], facing);
            boxes[i] = rotated;
        }

        return new CellBoxTable(facing, keys, boxes);
    }

    // ───────── Per-cell collision OVERRIDES: shrink or remove a cell's collision while it stays occupied ─────────
    // EDIT THIS to fix "the model only clips into a cell but I can't walk there". A listed cell stays OCCUPIED (its
    // ghost still blocks placing a block there) and breakable via the master; its SELECTION outline now tracks these
    // SAME boxes (GetCellSelectionBoxes delegates to the collider), so the white highlight hugs the frame too — edit a
    // box here and both collision and selection move together. Cells NOT listed keep the full unit cube (collision AND
    // selection). Coords are 0..1 inside the cell;
    // (X,Y,Z) is the SAME local frame as FootprintLocal — authored facing SOUTH, then OrientBox rotates each box onto
    // the placed facing at query time (GetCellCollisionBoxes), so author/verify with the machine placed facing SOUTH.
    //   • new Cuboidf[0]                              → no collision at all (walk straight through)
    //   • new[]{ new Cuboidf(0,0,0, 1,0.5f,1) }       → partial box, e.g. knee-high ledge you step over
    //   • new[]{ new Cuboidf(0,0,0, 0.3f,1,1) }       → thin wall hugging one side
    static readonly (Vec3i cell, Cuboidf[] boxes)[] CollisionOverrides =
    {
        // Boxes FITTED to the authored model's real mass — generated by AutomatedSawmillPlan/scan_collision.py
        // (occupancy scan 2026-06-17; rotation- and parent-transform accurate, a 1:1 port of the engine's
        // ShapeElement matrix math, so rotated/nested elements land where the game renders them). Re-run that
        // script after Blender edits and re-paste. Cuboidf(x1,y1,z1, x2,y2,z2), 0..1 in-cell; each cell stays
        // OCCUPIED (its ghost still blocks placement) and breakable — only the physical COLLISION shrinks to hug
        // the frame, so you bump the real posts/beams but walk through the open gaps. "%" = model fill of the cell.
        // The two genuinely-solid CORE cells (0,0,0)=42% and (0,1,0)=40%) are deliberately NOT listed → they keep
        // the full unit cube (default), since there is nothing to shrink there.
        // ── Y=0 base (plus) ──
        (new Vec3i( 1, 0,  0), new[]{ new Cuboidf(0.25f,0f,0.125f, 0.562f,1f,1f), new Cuboidf(0f,0f,0.125f, 0.188f,1f,0.562f) }),     // 16%
        (new Vec3i(-1, 0,  0), new[]{ new Cuboidf(0.875f,0f,0.125f, 1f,1f,0.75f) }),                                                  //  6% inner post
        (new Vec3i( 0, 0,  1), new[]{ new Cuboidf(0.125f,0f,0f, 0.875f,1f,1f), new Cuboidf(0.938f,0f,0.438f, 1f,1f,0.688f) }),        // 25%
        (new Vec3i( 0, 0, -1), new[]{ new Cuboidf(0.125f,0f,0f, 0.875f,1f,1f) }),                                                     // 22%
        (new Vec3i( 1, 0,  1), new[]{ new Cuboidf(0f,0f,0.438f, 0.188f,1f,0.688f), new Cuboidf(0.25f,0.625f,0f, 0.375f,1f,0.188f) }), //  5% corner
        // ── Y=1 body + wheel ──
        (new Vec3i( 1, 1,  0), new[]{ new Cuboidf(0f,0f,0.062f, 0.562f,1f,1f) }),                                                     // 13%
        (new Vec3i(-1, 1,  0), new[]{ new Cuboidf(0.875f,0f,0.125f, 1f,1f,1f) }),                                                     //  6% inner post
        (new Vec3i( 0, 1,  1), new[]{ new Cuboidf(0f,0f,0f, 1f,1f,0.938f) }),                                                         // 47% wheel/log
        (new Vec3i( 0, 1, -1), new[]{ new Cuboidf(0.188f,0f,0f, 0.812f,0.062f,1f) }),                                                 //  4% floor beam
        (new Vec3i( 1, 1,  1), new[]{ new Cuboidf(0f,0f,0f, 0.188f,1f,0.75f), new Cuboidf(0.25f,0f,0f, 0.375f,0.438f,0.188f) }),      //  5% corner
        // ── Y=2 towers (saw + gear) ──
        (new Vec3i( 0, 2,  0), new[]{ new Cuboidf(0f,0f,0.125f, 1f,1f,0.625f) }),                                                     // 12% saw tower
        (new Vec3i(-1, 2,  0), new[]{ new Cuboidf(0.875f,0f,0.125f, 1f,1f,0.562f) }),                                                 //  5% inner post
        (new Vec3i( 1, 2,  1), new[]{ new Cuboidf(0f,0f,0f, 0.312f,1f,0.688f) }),                                                     //  9%
        (new Vec3i( 1, 2,  0), new[]{ new Cuboidf(0f,0f,0.125f, 0.375f,1f,1f) }),                                                     // 13%
        // ── Y=3 towers (saw + gear) ──
        (new Vec3i( 0, 3,  0), new[]{ new Cuboidf(0f,0f,0.125f, 1f,0.75f,0.625f) }),                                                  //  5% saw top
        (new Vec3i(-1, 3,  0), new[]{ new Cuboidf(0f,0f,0f, 1f,0.875f,0.875f) }),                                                     // 29% gears
        (new Vec3i( 1, 3,  0), new[]{ new Cuboidf(0f,0f,0.125f, 0.188f,0.75f,0.562f) }),                                             //  6% post
    };

    /// <summary>This cell's selection-outline slice — the white highlight. Returns the SAME per-cell boxes as
    /// <see cref="GetCellCollisionBoxes"/> (the model-fitted <see cref="CollisionOverrides"/> boxes, rotated onto the
    /// placed <see cref="Facing"/> via <see cref="OrientBox"/>, or the full unit cube for an unlisted cell), so the
    /// outline traces the machine's actual frame instead of a blocky full cube per footprint cell. The two were briefly
    /// split — collision fitted to the model, selection a full cube — after the authored boxes landed (T03.06); they
    /// are re-united here so "what you highlight" matches "what you bump into" (the sawhorse idiom and the original
    /// T03.02 intent). Consequence: a cell whose collision override is an empty array (walk-through) also has no
    /// selection outline — you can't click open air. That is harmless because the two solid CORE cells ((0,0,0) and
    /// (0,1,0)) keep the full cube, so the machine is always easily targeted / broken, and every interaction routes to
    /// the master regardless of which cell was hit. Null off-footprint → caller keeps the JSON cube.</summary>
    internal Cuboidf[]? GetCellSelectionBoxes(BlockPos cellPos) => GetCellCollisionBoxes(cellPos);

    // The placeholder per-cell box: a full unit cube, FRESH each call (never a shared mutable static — ClampCell
    // mutates it). T03.06 replaces this with the per-cell authored sub-boxes of the real Blender model.
    static Cuboidf PlaceholderCellBox() => new Cuboidf(0f, 0f, 0f, 1f, 1f, 1f);

    /// <summary>Clamp a box's HORIZONTAL footprint into this cell's [0,1] bounds — the per-cell containment that stops
    /// perpendicular faces tunnelling (see <see cref="GetCellCollisionBoxes"/>). Y is deliberately left as-is,
    /// mirroring <c>BlockEntitySawhorse</c>: vertical overhang is collided with on entry to the cell, it doesn't
    /// tunnel. A no-op on the placeholder full cube; the honesty hook for authored overhanging boxes (T03.06).
    /// Mutates and returns the passed-in box.</summary>
    static Cuboidf ClampCell(Cuboidf box)
    {
        box.X1 = GameMath.Clamp(box.X1, 0f, 1f); box.X2 = GameMath.Clamp(box.X2, 0f, 1f);
        box.Z1 = GameMath.Clamp(box.Z1, 0f, 1f); box.Z2 = GameMath.Clamp(box.Z2, 0f, 1f);
        return box;
    }

    /// <summary>
    /// Rotate an in-cell collision box from the authored SOUTH/local frame (the frame of <see cref="FootprintLocal"/>
    /// and <see cref="CollisionOverrides"/>) onto the placed <see cref="Facing"/>, about the cell centre
    /// (0.5, 0.5, 0.5). It applies the SAME X-Z rotation <see cref="RotateToFacing"/> applies to cell POSITIONS, so the
    /// box tracks its own cell — and therefore the rotated render (the <c>shapeByType</c> rotateY) — for every facing.
    ///
    /// This closes the bug behind "the character bumps where the frame is visibly open": <see cref="GetCells"/> /
    /// <see cref="LocalOf"/> rotated the cell POSITIONS, but the boxes INSIDE each cell were returned in the SOUTH
    /// frame unchanged, so any non-SOUTH placement collided with boxes turned the wrong way (only a SOUTH-facing
    /// machine — the authoring frame — lined up). A 90° turn about the centre keeps a [0,1] box in [0,1], so no clamp
    /// is needed; Y is untouched (the turn is about Y). Returns a FRESH box — the static override array is shared and
    /// must never be mutated. Pure function of <paramref name="facing"/> + the passed box (static since T05.05, which
    /// calls it while building the per-facing table rather than per query), off-main-thread-safe like its caller.
    /// </summary>
    static Cuboidf OrientBox(Cuboidf b, BlockFacing facing)
    {
        int nx = facing.Normali.X, nz = facing.Normali.Z;
        // RotateToFacing's X-Z rotation applied to each corner about (0.5,0.5): (x,z) -> (x*nz + z*nx, -x*nx + z*nz).
        // Two opposite corners suffice for a 90° turn (the result stays axis-aligned); min/max gives the new box.
        float x1 = (b.X1 - 0.5f) * nz + (b.Z1 - 0.5f) * nx + 0.5f;
        float z1 = -(b.X1 - 0.5f) * nx + (b.Z1 - 0.5f) * nz + 0.5f;
        float x2 = (b.X2 - 0.5f) * nz + (b.Z2 - 0.5f) * nx + 0.5f;
        float z2 = -(b.X2 - 0.5f) * nx + (b.Z2 - 0.5f) * nz + 0.5f;
        return new Cuboidf(System.Math.Min(x1, x2), b.Y1, System.Math.Min(z1, z2),
                           System.Math.Max(x1, x2), b.Y2, System.Math.Max(z1, z2));
    }
}
