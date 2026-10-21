using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// Toolsmith-only render shim for a forged toolhead (the pit-saw blade <c>pitsawblade</c> and the bark-spud
/// head <c>barkspudhead</c>). It swaps in a floor-origined "-toolsmith" shape ONLY while the head is drawn in a
/// Toolsmith workbench slot, and leaves every other render (inventory / hand / dropped / ground storage / tongs /
/// forge) on the head's own centred <c>Shape.Base</c>.
///
/// WHY. The Toolsmith workbench renders a placed part in its slot with a HARDCODED transform (scale 0.5 about the
/// model origin + a fixed slot offset — <c>BlockEntityWorkbench.GetOrCreateFullSlotMesh</c>) and IGNORES every item
/// transform (gui / tp / ground / .tfedit). A part can therefore only be SEATED on the bench through its SHAPE
/// GEOMETRY. Our authored heads are centred at mid-box (y≈7.6) → they float above the slot. The user modelled a
/// second shape re-origined to the box floor (<c>item/&lt;head&gt;-toolsmith</c>) that sits correctly.
///
/// The first fix (patches/toolsmith_head_shape.json — now deleted) simply REPLACED <c>item.Shape.Base</c> with the
/// "-toolsmith" shape whenever Toolsmith was installed. But <c>Shape.Base</c> also drives the inventory / hand /
/// dropped / ground-storage / tong / forge render, and every one of those transforms was tuned for the CENTRED
/// geometry — so the global swap seated the bench but shifted everything else. There is no asset-level "use this
/// shape only on the bench" hook (the bench reads <c>item.Shape.Base</c> directly), so the split is done in code.
///
/// HOW. Both the Toolsmith workbench (<c>GetOrCreateFullSlotMesh</c>) and the vanilla display block entities
/// (<c>BlockEntityDisplay.getMesh</c> → ground storage / shelves / tool racks) fetch a part's in-world mesh through
/// <see cref="IContainedMeshSource.GenMesh"/> when the collectible exposes that interface, falling back to the
/// item's own <c>Shape.Base</c> when it does not, or when GenMesh returns null. This behavior returns the
/// "-toolsmith" mesh ONLY when <c>atBlockPos</c> is a Toolsmith workbench; for every other caller it returns null,
/// so the caller falls back to the normal centred shape. Net effect: normal shape everywhere, "-toolsmith" geometry
/// on the bench only — the exact pre-existing bench look, with the inventory/hand/ground regressions removed. The
/// bench result is byte-identical to the old global-swap path: the bench feeds the SAME "-toolsmith" geometry into
/// the SAME hardcoded slot transform, only now sourced from GenMesh instead of Shape.Base.
///
/// INERT WITHOUT TOOLSMITH. No workbench block exists, so GenMesh only ever sees non-workbench positions and always
/// returns null. The head's <c>Shape.Base</c> is left untouched. Toolsmith attaches <c>CollectibleBehaviorToolHead</c>
/// to the head (never <c>ModularPartRenderingFromAttributes</c>, which it puts on the assembled TOOL), so this is the
/// head's only <see cref="IContainedMeshSource"/> — no clash.
///
/// INVARIANT (keep the heads out of null-unsafe display slots). Returning null is the correct contract for the
/// display block entities that DO null-check it — <c>BlockEntityDisplay.getMesh</c> falls back to
/// <c>getDefaultMesh</c> (which sets the proper BlendNoCull pass), so ground storage / shelves render identically to
/// before. But three vanilla display BEs deref the GenMesh result WITHOUT a null guard — <c>BEToolrack</c>
/// (BEToolrack.cs:96 → :114), <c>BECondenser</c>, <c>BEFruitpress</c>. They are safe today ONLY because these heads
/// can never enter those slots (a toolhead is not a tool, is not <c>rackable</c>, and is not a bucket/liquid
/// container — <c>BEToolrack.PutInSlot</c> gates on GetTool()!=null||rackable). So do NOT make either head
/// <c>rackable</c> or otherwise insertable into a tool rack / condenser / fruit press while it carries this behavior:
/// GenMesh would return null off the bench and those BEs would NRE instead of rendering the head.
/// </summary>
public class BehaviorToolsmithBenchMesh : CollectibleBehavior, IContainedMeshSource
{
    // Cached in OnLoaded — null on the server (CollectibleBehavior exposes no api field of its own). GenMesh is a
    // client-only render hook, so a null capi simply means "never substitute" (caller uses the normal shape path).
    ICoreClientAPI? capi;

    public BehaviorToolsmithBenchMesh(CollectibleObject collObj) : base(collObj) { }

    public override void OnLoaded(ICoreAPI api)
    {
        base.OnLoaded(api);
        capi = api as ICoreClientAPI;
    }

    public MeshData GenMesh(ItemSlot slot, ITextureAtlasAPI targetAtlas, BlockPos atBlockPos)
    {
        // Substitute the floor-origined bench shape ONLY inside a Toolsmith workbench. Ground storage, shelves,
        // racks etc. pass their OWN position here → not a workbench → null → the caller uses the item's normal
        // Shape.Base. That keeps every non-bench pose exactly as authored. Server side (no client api) also bails.
        if (capi is not ICoreClientAPI c || atBlockPos == null) return null!;
        if (!IsToolsmithWorkbench(c, atBlockPos)) return null!;

        Shape? shape = GetBenchShape(c);
        if (shape == null) return null!;

        // Reproduce Toolsmith's OWN workbench-slot tessellation (BlockEntityWorkbench.GetOrCreateFullSlotMesh
        // :7343-7388) so the bench mesh is byte-for-byte the old global-swap look: a ShapeTextureSource — which
        // resolves into the BLOCK atlas (ShapeTextureSource.cs:47/64) — seeded from the head item's per-variant
        // textures (its "metal" key → this metal's ingot). The atlas matters: the workbench draws its slot meshes
        // with the BLOCK atlas, so tessellating via the item's own atlas (TesselateShape(collObj,...) → item-atlas
        // UVs) would sample the wrong atlas and render garbled/invisible. targetAtlas (the item atlas Toolsmith
        // passes to GenMesh) is deliberately ignored for the same reason.
        var texSource = new ShapeTextureSource(c, shape, "iw-toolsmithbenchmesh:" + collObj.Code);
        var item = collObj as Item;
        if (shape.Textures != null)
        {
            foreach (var tex in shape.Textures)
            {
                if (item?.Textures != null && item.Textures.TryGetValue(tex.Key, out var itemTex))
                    texSource.textures[tex.Key] = itemTex;
                else
                    texSource.textures[tex.Key] = new CompositeTexture(tex.Value);
            }
        }
        c.Tesselator.TesselateShape("iw-toolsmithbenchmesh", shape, out MeshData mesh, texSource);
        return mesh;
    }

    // The workbench caches slot meshes under its OWN key, so it never calls this. Ground storage does — returning
    // the plain collectible code matches the vanilla default (BlockEntityDisplay.getMeshCacheKey with no mesh
    // source), so non-bench caching is unchanged and stays per-metal-variant.
    public string GetMeshCacheKey(ItemSlot slot)
    {
        // Only ever called for a filled slot (same as vanilla BlockEntityDisplay.getMeshCacheKey).
        return slot.Itemstack!.Collectible.Code.ToString();
    }

    static bool IsToolsmithWorkbench(ICoreClientAPI capi, BlockPos pos)
    {
        Block block = capi.World.BlockAccessor.GetBlock(pos);
        if (block == null || block.Code == null) return false;
        // Toolsmith workbench is toolsmith:workbench-{north|east|south|west} (blocktypes/workbench.json).
        return block.Code.Domain == "toolsmith" && block.FirstCodePart(0) == "workbench";
    }

    Shape? GetBenchShape(ICoreClientAPI capi)
    {
        // Shape lives on Item/Block, not the CollectibleObject base — our heads are always Items.
        AssetLocation? baseLoc = (collObj as Item)?.Shape?.Base;
        if (baseLoc == null) return null;

        // Derive "<Shape.Base>-toolsmith": item/pitsaw-head → item/pitsaw-head-toolsmith,
        // item/barkspudhead → item/barkspudhead-toolsmith. Both files ship in shapes/item/.
        var shapeAsset = new AssetLocation(baseLoc.Domain, "shapes/" + baseLoc.Path + "-toolsmith.json");

        // Parse + RESOLVE once, then cache. IMPORTANT: Shape.TryGet returns an UNRESOLVED shape — its nested child
        // elements (the -toolsmith head is a deep element tree) have no ParentElement links yet
        // (ShapeElement.ResolveReferences, ShapeElement.cs:224-233 sets them), so tessellating it as-is scatters the
        // children at their raw parent-relative coords → the geometry "lands off" the blade. The engine resolves
        // every registered Shape.Base at load, which is why Toolsmith's GetCachedShape path sat correctly in v1; a
        // hand-loaded shape must be resolved by hand. ResolveReferences is idempotent, so caching the resolved copy
        // and tessellating it once per metal variant is safe (mirrors GetCachedShape reuse).
        return ObjectCacheUtil.GetOrCreate(capi, "iw-toolsmithbenchshape-" + shapeAsset, () =>
        {
            Shape? s = Shape.TryGet(capi, shapeAsset);
            s?.ResolveReferences(capi.Logger, shapeAsset.ToShortString());
            return s;
        });
    }
}
