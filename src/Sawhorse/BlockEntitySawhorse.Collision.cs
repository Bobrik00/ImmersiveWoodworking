using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

public partial class BlockEntitySawhorse
{
    // ───────────────────────────── Collision fitted to the meshes ─────────────────────────────
    //
    // A finished PAIR collides as ONE combined box instead of two full cubes: the two trestles' bounding box,
    // GROWN to include the laid log while one is present. The box is the AABB of the actual authored meshes, so
    // the player walks around the construction the way it looks, and a laid log blocks movement on top.
    //
    // The pair's collider is built per-part from a TRESTLE box and a LOG box, each a single AABB. These are now
    // HAND-AUTHORED in sawhorse.json — attributes.collisionBoxes and attributes.selectionBoxes (collision and
    // selection are SEPARATE sets) — and parsed once at load by BlockSawhorse on BOTH sides; edit the JSON to retune
    // the colliders with no recompile. The values flow in through GetPairBoxes below, so you have full manual control
    // separately for the козлы (trestle) and the бревно (log).
    //
    // The two literals below are ONLY the FALLBACK defaults, used when a JSON box is absent/malformed: the original
    // offline-measured Z-frame AABBs of the authored shapes (sawhorses_p3.json / sawhorse_log.json) — trestle
    // bottom clamped to 0 (its legs dip a hair below the cell floor), log as the WHOLE billet (state 1, the largest
    // of the 20). The combined box deliberately does NOT shrink as the log is sawn — a stable "log present?" toggle.
    // Frame: the master cell's Z-orientation; GetPairBoxes orients onto the pair axis and clamps per cell. A SINGLE /
    // half-built trestle is NOT covered here (GetPairBoxes returns null) — it uses the top-level JSON collisionbox/
    // selectionbox cube. (Auto-deriving these from the shape element tree at load is feasible on both sides — shapes
    // are Universal, Shape.TryGet takes ICoreAPI — but the JSON-authored route above is the chosen manual control.)
    internal static readonly Cuboidf DefaultTrestleBox = new Cuboidf(-0.115f, 0f, 0.388f, 1.049f, 1.005f, 0.675f);
    internal static readonly Cuboidf DefaultLogBox     = new Cuboidf(0.146f, 0.580f, -0.812f, 0.854f, 1.287f, 0.812f);
    static readonly Vec3d BlockCentre  = new Vec3d(0.5, 0.5, 0.5);

    /// <summary>
    /// The collision box(es) for THIS cell when it's part of a finished pair, or null for a single / half-built
    /// trestle (the caller then falls back to the JSON full-cube). The construction's combined AABB — the two
    /// trestles (<see cref="DefaultTrestleBox"/>) unioned with the laid log (<see cref="DefaultLogBox"/>) while
    /// <see cref="heldLog"/> is set — CLAMPED to this cell's [0,1] horizontal footprint, so each cell returns the
    /// self-contained slice of the collider that lies within it. Uses the COLLISION box set (sawhorse.json
    /// attributes.collisionBoxes); <see cref="GetPairSelectionBoxes"/> is the parallel SELECTION variant.
    ///
    /// Per-cell clamping (rather than every cell carrying the whole overhanging box) is what makes the multiblock
    /// solid from EVERY side. VS's collision broadphase (<c>CachingCollisionTester.GenerateCollisionBoxList</c>)
    /// scans only the cells under the entity, with NO horizontal margin, and VS clips MOTION but never ejects an
    /// already-overlapping entity. A box that overhangs into a neighbouring cell which doesn't itself report it is
    /// therefore crossed-into before it's ever scanned — the player penetrates and walks straight through. Along the
    /// pair axis a partner cell carries the other half, but the PERPENDICULAR (long) sides have no such neighbour, so
    /// an overhang there tunnels — which is exactly the bug an earlier "both cells carry the whole box" scheme left
    /// open (axis ends blocked, long sides walk-through). Clamping every face to the cell boundary removes all
    /// overhang: each cell becomes a plain block-like collider the broadphase catches on entry, from any direction.
    /// The geometry mirrors the mesh transforms (<see cref="UpdateMesh"/> / <see cref="PlaceLogMesh"/>): the X-pair
    /// rotates the Z-frame boxes +90° about the block centre, and the partner trestle / the log sit one cell toward
    /// +axis from the master; the slave (always at master+axis) shifts the box back one cell into its own frame
    /// BEFORE the clamp. Server- and client-safe (no meshes / no <see cref="capi"/> needed — pure state + constants),
    /// which collision requires (it runs on both sides). Served from the state-keyed snapshot below (T05.05), so
    /// repeated asks at the same state hand back the SAME array instead of rebuilding the geometry.
    /// </summary>
    internal Cuboidf[]? GetPairCollisionBoxes() => Boxes().Collision;

    /// <summary>The SELECTION outline box(es) for THIS cell of a finished pair, or null for a single / half-built
    /// trestle (caller falls back to the JSON cube). Identical construction to <see cref="GetPairCollisionBoxes"/>
    /// but from the SEPARATE selection box set (sawhorse.json attributes.selectionBoxes), so the white highlight can
    /// be tuned independently of the collider. Routed by <c>BlockSawhorse.GetSelectionBoxes</c>; client-side.</summary>
    internal Cuboidf[]? GetPairSelectionBoxes() => Boxes().Selection;

    // ───────────────────────── The box cache (AuditFixPlan T05.05) ─────────────────────────
    //
    // All three box sets are a pure function of (inPair, pairAxis, isMaster, "does the pair hold a log"), and that
    // state changes only when a log is laid/taken or the pair forms/dissolves. The engine, by contrast, asks CONSTANTLY:
    // the aiming ray calls GetSelectionBoxes every frame while the player looks at the trestle, and — the hot one —
    // ParticlePhysics.UpdateMotion clears its list and re-walks the window on EVERY tick of EVERY particle (30 Hz, no
    // equivalent of CachingCollisionTester's minPosIsUnchanged gate), while this mod drops hundreds of sawdust flakes
    // onto exactly these cells. Building 5-9 fresh Cuboidf per query for an answer that did not change is the cost this
    // cache removes: on a state hit the SAME array instance goes back out, as vanilla Block.GetCollisionBoxes does with
    // its shared CollisionBoxes.
    //
    // Handing out a shared instance is safe because no caller mutates it: CachedCuboidList.Add copies each box into its
    // own Cuboidd, the aiming ray copies into its tmpCuboidd, CollectibleParticleProperties.RandomBlockPos only reads
    // the fields, and the mod's own only other reader is the IW_DEBUG hover line. Thread safety (collision and particle
    // physics both query off the main thread) rests on publishing a fully-built immutable snapshot through ONE volatile
    // reference write; building is a pure function of the state signature, so two threads racing to build produce equal
    // snapshots and either may win.
    sealed class PairBoxSet
    {
        internal readonly int State;
        internal readonly Cuboidf[]? Collision; // null for a single / half-built trestle (caller keeps the JSON cube)
        internal readonly Cuboidf[]? Selection;
        internal readonly Cuboidf[] Particle;   // never null — empty means "collide nothing", never the JSON cube

        internal PairBoxSet(int state, Cuboidf[]? collision, Cuboidf[]? selection, Cuboidf[] particle)
        {
            State = state; Collision = collision; Selection = selection; Particle = particle;
        }
    }

    volatile PairBoxSet? pairBoxes;

#if IW_DEBUG
    // Rebuild counter for the IW_DEBUG hover line — the live half of this task's "a repeat query must not allocate"
    // check. Not interlocked: an off-thread rebuild racing the main thread can lose a count, and a probe that under-
    // reports is fine here (the number is only ever read to confirm it STOPS moving).
    int boxRebuilds;
#endif

    /// <summary>The current snapshot, rebuilt only when the state signature moved. All three sets are built together so
    /// one reference write publishes a consistent whole; a rebuild costs a handful of objects and happens on a log
    /// change or a pair change, not per query.</summary>
    PairBoxSet Boxes()
    {
        int state = BoxState();
        PairBoxSet? set = pairBoxes;
        if (set != null && set.State == state) return set;

#if IW_DEBUG
        boxRebuilds++;
#endif
        BlockSawhorse? sh = Block as BlockSawhorse;
        bool hasLog = (state & BoxStateHasLog) != 0;
        set = new PairBoxSet(
            state,
            GetPairBoxes(sh?.TrestleCollBox ?? DefaultTrestleBox, sh?.LogCollBox ?? DefaultLogBox, hasLog),
            GetPairBoxes(sh?.TrestleSelBox  ?? DefaultTrestleBox, sh?.LogSelBox  ?? DefaultLogBox, hasLog),
            BuildParticleBoxes(hasLog));
        pairBoxes = set;
        return set;
    }

    const int BoxStateInPair   = 1;
    const int BoxStateAxisX    = 2;
    const int BoxStateIsMaster = 4;
    const int BoxStateHasLog   = 8;

    /// <summary>Everything the three box sets depend on, packed into one int. Zero (not in a pair) deliberately ignores
    /// the rest: a single / half-built trestle keeps the JSON cube whatever the other fields say. The "does the pair
    /// hold a log" read keeps the both-cells rule of the old inline check — only the master ever holds
    /// <see cref="heldLog"/>, so a stale/desynced <see cref="isMaster"/> cannot hide the log — but reaches the partner
    /// through the cached lookup in <see cref="PartnerBE"/>, which on the steady path is a field read instead of two
    /// chunk-map lookups. Reference-field reads are atomic → at worst a one-tick-stale box, as before.</summary>
    int BoxState()
    {
        if (!inPair) return 0;
        bool hasLog = heldLog != null || PartnerBE()?.heldLog != null;
        return BoxStateInPair
             | (pairAxis == EnumAxis.X ? BoxStateAxisX : 0)
             | (isMaster ? BoxStateIsMaster : 0)
             | (hasLog ? BoxStateHasLog : 0);
    }

    /// <summary>
    /// Build THIS cell's clamped slice of the assembled-pair box from a chosen <paramref name="trestleBox"/> +
    /// <paramref name="logBox"/> pair (the collision set or the selection set): orient onto the pair axis, mirror the
    /// trestle onto the partner cell, add the log box only while a log is laid, re-express into this cell's frame and
    /// clamp the horizontal footprint to [0,1]. Shared by <see cref="GetPairCollisionBoxes"/> and
    /// <see cref="GetPairSelectionBoxes"/>; null for a single / half-built trestle. Pure read + thread-safe — see the
    /// per-cell-clamping rationale on <see cref="GetPairCollisionBoxes"/>. Called only from <see cref="Boxes"/>, which
    /// passes the <paramref name="hasLog"/> it already read for the state signature (T05.05) — the box maths itself is
    /// unchanged, so a rebuilt snapshot carries exactly the numbers the per-query build used to return.
    /// </summary>
    Cuboidf[]? GetPairBoxes(Cuboidf trestleBox, Cuboidf logBox, bool hasLog)
    {
        if (!inPair) return null; // single / half-built trestle → caller keeps the JSON full-cube

        EnumAxis axis = pairAxis;
        int ax = axis == EnumAxis.X ? 1 : 0; // unit step from master to partner, always toward +axis
        int az = axis == EnumAxis.Z ? 1 : 0;

        // Combined box in the MASTER cell's local frame. The X-pair turn is about the Y axis, so it leaves the Y
        // bounds (incl. the floor clamped to 0) untouched and only swaps the horizontal footprint onto the X axis.
        Cuboidf trestleMaster = OrientForAxis(trestleBox, axis);
        Cuboidf combined = Union(trestleMaster, trestleMaster.OffsetCopy(ax, 0, az)); // + partner trestle (+1 axis)
        if (hasLog)
        {
            Cuboidf log = OrientForAxis(logBox, axis).OffsetCopy(ax, 0, az); // log laid at the +axis boundary
            combined = Union(combined, log);
        }

        // Re-express in THIS cell's frame: a slave sits one cell toward +axis from the master, so shift back by it.
        if (!isMaster) combined = combined.OffsetCopy(-ax, 0, -az);

        // Clamp the HORIZONTAL footprint to this cell [0,1] — see the summary: the overhang on the perpendicular
        // sides lands in air cells that don't report the box, and the no-margin broadphase + no-eject collision
        // means the player crosses the boundary already inside it and tunnels through. Cell-aligned faces close that
        // window from every side; the partner cell still carries its half along the axis, so coverage is unbroken.
        // Y is left as-is — vertical overhang is collided with on entry to the cell, it doesn't tunnel.
        combined = new Cuboidf(
            GameMath.Clamp(combined.X1, 0f, 1f), combined.Y1, GameMath.Clamp(combined.Z1, 0f, 1f),
            GameMath.Clamp(combined.X2, 0f, 1f), combined.Y2, GameMath.Clamp(combined.Z2, 0f, 1f));

        return new[] { combined };
    }

    /// <summary>
    /// PARTICLE-only collision boxes for THIS cell — what cosmetic sawdust settles on. Returns the laid LOG box
    /// (cell-local + clamped to [0,1] exactly like <see cref="GetPairCollisionBoxes"/>), so dust lands on the
    /// VISIBLE log instead of the block's full-cube JSON <c>collisionbox</c>. The engine collides particles against
    /// <c>Block.GetParticleCollisionBoxes</c> — which by default is the static JSON cube, NOT our
    /// <see cref="GetPairCollisionBoxes"/> entity box — so without this, flakes piled on the cube's invisible top
    /// face (y≈1) in the OPEN middle of the A-frame and looked stuck in mid-air. The trestle frame itself is left
    /// non-colliding for particles (empty array, never the JSON cube): it's an open A — dust should fall THROUGH
    /// the gaps onto the ground below, which is what the empty result lets it do. No log present → empty, so a bare
    /// or half-built trestle never floats dust either. Pure read + thread-safe (particle physics runs off the main
    /// thread), same constraints as <see cref="GetPairCollisionBoxes"/>. Mirrors the log-laying transform there:
    /// the log sits one cell toward +axis from the master; a slave shifts it back into its own frame before the clamp.
    /// </summary>
    internal Cuboidf[] GetParticleCollisionBoxes() => Boxes().Particle;

    /// <summary>Builds the particle set for the current snapshot — see <see cref="GetParticleCollisionBoxes"/> for what
    /// it is and why. Called only from <see cref="Boxes"/>, with the <paramref name="hasLog"/> already read for the
    /// state signature (T05.05); the geometry is unchanged. This is the set the caching pays off on most: the engine
    /// caches the ENTITY collision path (CachingCollisionTester's minPosIsUnchanged gate) but has no equivalent for
    /// particles, so before the cache every flake of our own sawdust rebuilt this box 30 times a second.</summary>
    Cuboidf[] BuildParticleBoxes(bool hasLog)
    {
        if (!inPair) return System.Array.Empty<Cuboidf>(); // only a finished pair can hold a log → only it has dust

        EnumAxis axis = pairAxis;
        int ax = axis == EnumAxis.X ? 1 : 0;
        int az = axis == EnumAxis.Z ? 1 : 0;

        // No log → no dust to catch and nothing to float on, so collide nothing.
        if (!hasLog) return System.Array.Empty<Cuboidf>();

        // Sawdust settles on the log's COLLISION box (the physical billet), authored in sawhorse.json.
        Cuboidf logBox = (Block as BlockSawhorse)?.LogCollBox ?? DefaultLogBox;
        Cuboidf log = OrientForAxis(logBox, axis).OffsetCopy(ax, 0, az); // log laid at the +axis boundary
        if (!isMaster) log = log.OffsetCopy(-ax, 0, -az);                // re-express in this (slave) cell's frame

        // Clamp the horizontal footprint to this cell so each cell returns just its slice (CachedCuboidList.Add
        // offsets the box by the cell pos). Y left as-is so the box keeps the log's full height (top ≈ 1.287).
        log = new Cuboidf(
            GameMath.Clamp(log.X1, 0f, 1f), log.Y1, GameMath.Clamp(log.Z1, 0f, 1f),
            GameMath.Clamp(log.X2, 0f, 1f), log.Y2, GameMath.Clamp(log.Z2, 0f, 1f));

        return new[] { log };
    }

    /// <summary>Orient a Z-frame box onto the pair axis: the X-pair turns it +90° about the block centre (the same
    /// turn <see cref="UpdateMesh"/> / <see cref="PlaceLogMesh"/> give the meshes), the Z-pair keeps it as authored.
    /// Returns a fresh copy either way, so the shared <c>static</c> source box is never mutated.</summary>
    static Cuboidf OrientForAxis(Cuboidf zFrame, EnumAxis axis)
        => axis == EnumAxis.X ? zFrame.RotatedCopy(0f, 90f, 0f, BlockCentre) : zFrame.Clone();

    /// <summary>Smallest box enclosing both — a fresh <see cref="Cuboidf"/>, inputs left untouched.</summary>
    static Cuboidf Union(Cuboidf a, Cuboidf b) => new Cuboidf(
        System.Math.Min(a.X1, b.X1), System.Math.Min(a.Y1, b.Y1), System.Math.Min(a.Z1, b.Z1),
        System.Math.Max(a.X2, b.X2), System.Math.Max(a.Y2, b.Y2), System.Math.Max(a.Z2, b.Z2));
}
