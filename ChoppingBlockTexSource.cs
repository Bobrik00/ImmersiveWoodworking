using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Texture source for the chopping block's STAND mesh (ChoppingBlockRedesignPlan E02): binds the authored shape's two wood keys to the
/// stored species' real log textures, so the single block code <c>choppingblock</c> shows every species without a
/// per-wood block variant. The shape (shapes/block/choppingblock.json) declares exactly two keys — the bark SIDES
/// (<c>M1</c>) and the cut END-grain top (<c>M2</c>) — so we route M2 to the tree-ring end texture and anything
/// else (M1, or any stray key) to the bark side texture. An unrecognised code resolves to the bark side rather
/// than throwing: a KeyNotFoundException on the tesselation path would crash the render loop.
///
/// Cross-domain resolution is the CALLER's job (<see cref="WoodTextures"/> picks the domain that actually ships
/// each texture — the species' own mod domain first, then game, then the log block), so this class just loads the
/// AssetLocation it's handed: look it up in the block atlas, then load <c>textures/&lt;path&gt;.png</c> from assets
/// on a miss — mirroring the engine's <c>ContainedTextureSource</c> and the sibling <see cref="LogTexSource"/>.
/// </summary>
internal sealed class ChoppingBlockTexSource : ITexPositionSource
{
    readonly ICoreClientAPI capi;
    readonly AssetLocation barkTex; // #M1 → bark / round sides
    readonly AssetLocation endTex;  // #M2 → tree-ring (end-grain) cut top
    readonly string forLogging;

    // Texture paths already complained about, so a missing texture costs ONE log line and not one per FACE
    // (AuditFixPlan T05.03 / F-L2-28). The twin of LogTexSource.warnedPaths, and it is here for the same reason: the
    // tesselator calls the indexer above once per shape face and a miss inserts nothing into the atlas, so an absent
    // species texture repeated the identical warning 36 times for one stand — and again on every world entry, since
    // the species mesh caches are freed on unload. Keyed by the PATH, not by the caller: the actionable content is
    // "this texture does not exist", which is the same fix whoever asked for it. Never cleared, and lock-free because
    // tesselation runs off the main thread. Kept as its OWN set rather than shared with LogTexSource: the two report
    // different things about a path (a whole species missing vs one stand texture), and a shared set would let the
    // first reporter silence the other.
    static readonly ConcurrentDictionary<string, byte> warnedPaths = new ConcurrentDictionary<string, byte>();

    public ChoppingBlockTexSource(ICoreClientAPI capi, AssetLocation barkTex, AssetLocation endTex, string forLogging)
    {
        this.capi = capi;
        this.barkTex = barkTex;
        this.endTex = endTex;
        this.forLogging = forLogging;
    }

    public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

    /// <summary>The atlas position for one shape texture key. The prefix test is ORDINAL (AuditFixPlan T05.03 /
    /// F-L10-13): <c>M1</c>/<c>M2</c> are shape-authored ASCII keys and the tesselator asks once per face, so the
    /// default culture-sensitive <c>StartsWith</c> ran a linguistic collation where a byte compare is both faster and
    /// the correct question.</summary>
    public TextureAtlasPosition this[string textureCode]
        => GetOrCreate(capi, textureCode.StartsWith("M2", System.StringComparison.Ordinal) ? endTex : barkTex, forLogging);

    /// <summary>Resolve one species texture to its BLOCK-atlas position: look it up in the atlas, then insert
    /// <c>textures/&lt;path&gt;.png</c> from assets on a miss. Falls back to the atlas' "unknown" position rather than
    /// throwing — a KeyNotFoundException on the tesselation path would crash the render loop.
    ///
    /// <c>internal static</c> because the atlas position is ALSO what the block's particle COLOUR needs
    /// (<see cref="BlockChoppingBlock"/>.GetRandomColor, AuditFixPlan T04.06): the stand's species lives only in the
    /// mesh, never in <c>Block.Textures</c>, so the colour has to sample the same texture this source binds — one
    /// load-or-insert, so mesh and chips can never disagree about which image a species resolves to.
    ///
    /// <para>The miss is reported ONCE per path (<see cref="warnedPaths"/>, AuditFixPlan T05.03 / F-L2-28) — the
    /// dedup <see cref="LogTexSource"/> already had and this copy did not.</para></summary>
    internal static TextureAtlasPosition GetOrCreate(ICoreClientAPI capi, AssetLocation path, string forLogging)
    {
        ITextureAtlasAPI atlas = capi.BlockTextureAtlas;
        TextureAtlasPosition? pos = atlas[path];
        if (pos != null) return pos;

        IAsset? asset = capi.Assets.TryGet(path.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
        if (asset != null && atlas.GetOrInsertTexture(path, out _, out TextureAtlasPosition inserted, () => asset.ToBitmap(capi)))
        {
            return inserted;
        }

        if (warnedPaths.TryAdd(path.ToString(), 0))
        {
            capi.World.Logger.Warning("[immersivewoodworking] chopping-block texture {0} ({1}) missing; using unknown.", path, forLogging);
        }
        return atlas.UnknownTexturePosition;
    }
}
