using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace ImmersiveWoodworking;

/// <summary>The four vanilla wood texture families a log is drawn from, named after their folders under
/// <c>textures/block/wood/</c>: the bark side, the bare (debarked) side, the tree-ring end grain, and the end grain of a
/// debarked log, which lacks the rim of bark.</summary>
internal enum WoodTexFamily { Bark, Debarked, Treetrunk, TreetrunkDebarked }

/// <summary>
/// The one place that turns a wood species into a texture. Used by the half-log slab (atlas build), the half-log
/// item, the sawhorse, the sawmill, the chopper and the chopping block. It replaces five copies of the first two steps
/// below: the slab's own resolver and the <c>WoodTex</c> helpers of <see cref="LogTexSource"/>, the sawhorse,
/// <see cref="ItemHalfLog"/> and the chopping block.
///
/// <para>Lookup order for one family of one species:</para>
/// <list type="number">
/// <item><c>{domain}:block/wood/{family}/{wood}</c>, the species' own domain.</item>
/// <item><c>game:block/wood/{family}/{wood}</c>. Wildcraft Trees keeps bark and end grain in its own domain but ships
/// most debarked textures under <c>game</c>, so the domain is decided per texture, not per species.</item>
/// <item>The texture the species' own log block declares, read off <c>{domain}:log-placed-{wood}-ud</c> /
/// <c>debarkedlog-{wood}-ud</c>, then the same codes in <c>game</c>. It covers a log whose textures do not sit at the
/// vanilla path of its species. A ModDB report (2026-10-13) showed the slab warning for nine species indexed under
/// <c>game</c>, from a mod that was not identified. The likely layout: the mod adds its species to
/// <c>game:worldproperties/block/wood</c>, vanilla's log blocktype makes <c>game:log-*-{wood}-*</c>, and the mod points
/// those logs elsewhere through <c>texturesByType</c>. Steps 1 and 2 then find nothing while the log in the world is
/// drawn correctly. A log-only species in another mod's domain is NOT reached: the client indexes it under
/// <c>game</c>, and step 3 looks there.</item>
/// </list>
///
/// <para>The log comes LAST on purpose: where steps 1 or 2 found a texture before, they still find the same file.
/// Wildcraft Trees' <c>chlorociboria</c> is why the order matters: it ships a debarked texture in both domains as two
/// different files, its log names the <c>game</c> one, and the probe has always picked the <c>wildcrafttree</c> one.
/// What step 3 does change is a family that used to MISS and was then covered by the in-species ladder
/// (<see cref="LogTexSource"/>'s indexer, the slab's <c>split ??= round</c>). Checked on Floral Zones Circumboreal
/// 1.0.1: its larch, Siberian pine and sessile oak ship no debarked texture, and their split faces now show the vanilla
/// larch / pine / oak bare wood their logs name for the inside faces instead of their own bark; its fir, alder and
/// spruce ship no debarked end grain, and a debarked log's caps on the stations now show the end grain their debarked
/// logs declare instead of the side texture. Both now match the log in the world.</para>
///
/// <para>CLIENT ONLY in effect. Step 3 reads <c>Block.Textures</c>, which the server nulls in <c>FreeRAMServer</c>
/// once it has built the block packet after <c>WorldReady</c>; every caller is a client mesh build or the slab's
/// <c>OnCollectTextures</c>, which the engine calls on the client only. On the client the block registry is complete
/// before the atlas is collected, and by then the engine has already split each block's <c>all</c> key into the six
/// face codes, so the bark side is read from <c>north</c>..<c>west</c> with <c>all</c> kept only as a fallback.
/// Only the <c>-ud</c> variant is read: <c>-ns</c>/<c>-we</c> name the rotated <c>{wood}-h</c> bark.</para>
///
/// <para>Existence is checked with <c>Assets.Exists</c>, never <c>TryGet</c>: after joining the client drops the bytes
/// of every texture asset, and <c>TryGet</c> would read the PNG back from disk only to learn that it exists.</para>
/// </summary>
internal static class WoodTextures
{
    // Face codes as they stand on the client after ResolveTextureCodes: `all` is gone, spread over the cube's faces.
    static readonly string[] SideKeys = { "north", "east", "south", "west", "all" };
    static readonly string[] EndKeys = { "up", "down" };
    static readonly string[] InsideKeys = { "inside-north", "inside-east", "inside-south", "inside-west" };

    /// <summary>The vanilla path of <paramref name="family"/> for <paramref name="wood"/>, without domain.</summary>
    internal static string PathOf(WoodTexFamily family, string wood) => family switch
    {
        WoodTexFamily.Bark => "block/wood/bark/" + wood,
        WoodTexFamily.Debarked => "block/wood/debarked/" + wood,
        WoodTexFamily.Treetrunk => "block/wood/treetrunk/" + wood,
        _ => "block/wood/treetrunk/debarked/" + wood,
    };

    /// <summary>The texture of <paramref name="family"/> for <paramref name="wood"/>, or null when none of the three
    /// steps finds one that exists. For callers that degrade on their own (the half-log slab's ladder).</summary>
    public static AssetLocation? Find(ICoreAPI api, string domain, string wood, WoodTexFamily family)
    {
        string path = PathOf(family, wood);

        AssetLocation own = new AssetLocation(domain, path);
        if (Exists(api, own)) return own;

        if (domain != "game")
        {
            AssetLocation vanilla = new AssetLocation("game", path);
            if (Exists(api, vanilla)) return vanilla;
        }

        return DeclaredByLog(api, domain, wood, family)
            ?? (domain != "game" ? DeclaredByLog(api, "game", wood, family) : null);
    }

    /// <summary><see cref="Find"/>, or the unchecked <c>game:</c> path on a miss. For the
    /// <see cref="LogTexSource"/> / <see cref="ChoppingBlockTexSource"/> callers, whose texture source does its own
    /// fallback and warning for a path that does not exist.</summary>
    public static AssetLocation Get(ICoreClientAPI capi, string domain, string wood, WoodTexFamily family)
        => Find(capi, domain, wood, family) ?? new AssetLocation("game", PathOf(family, wood));

    static AssetLocation? DeclaredByLog(ICoreAPI api, string domain, string wood, WoodTexFamily family) => family switch
    {
        WoodTexFamily.Bark => Declared(api, domain, "log-placed-", wood, SideKeys),
        WoodTexFamily.Treetrunk => Declared(api, domain, "log-placed-", wood, EndKeys),
        // A barked log shows its bare wood on the inside faces of vanilla's model, so it is the second witness.
        WoodTexFamily.Debarked => Declared(api, domain, "debarkedlog-", wood, SideKeys)
            ?? Declared(api, domain, "log-placed-", wood, InsideKeys),
        _ => Declared(api, domain, "debarkedlog-", wood, EndKeys),
    };

    /// <summary>The first of <paramref name="keys"/> on the log block that names an existing texture. Only the
    /// <c>Base</c> is taken, cloned: the engine shares one <c>CompositeTexture</c> object between every block that
    /// declares the same plain texture, so the original must not be touched, and the log's rotation and overlays
    /// belong to the log's model, not to ours.</summary>
    static AssetLocation? Declared(ICoreAPI api, string domain, string codePrefix, string wood, string[] keys)
    {
        Block? log = api.World.GetBlock(new AssetLocation(domain, codePrefix + wood + "-ud"));
        if (log?.Textures == null) return null;

        foreach (string key in keys)
        {
            if (!log.Textures.TryGetValue(key, out CompositeTexture? tex) || tex?.Base == null) continue;
            // `unknown` is what a block of an uninstalled mod and a wildcard that matched nothing are painted with;
            // it exists as an asset, so without this it would pass for the species' texture.
            if (tex.Base.Path == "unknown") continue;
            if (Exists(api, tex.Base)) return tex.Base.Clone();
        }
        return null;
    }

    static bool Exists(ICoreAPI api, AssetLocation loc)
        => api.Assets.Exists(loc.CopyWithPathPrefixAndAppendixOnce("textures/", ".png"));
}
