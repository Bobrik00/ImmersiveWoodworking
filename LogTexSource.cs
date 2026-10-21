using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Texture source for the laid-log mesh (LogSawingPlan T05.04): binds the authored log shapes' wood-key faces to the held
/// species' real textures, so ONE geometry set serves every wood type and both inputs. The Blender export
/// gives each shape three texture keys — the round/bark SIDES (<c>wood</c>), the cut END-grain corners
/// (<c>wood_corner</c>) and the freshly-ripped INNER faces (<c>debarked_wood</c>) — and may rename them per
/// shape with a numeric suffix (<c>wood.001</c>, …), so we match by PREFIX (<c>wood_corner</c> before
/// <c>wood</c>, since it also starts with <c>wood</c>; <c>debarked</c> is disjoint from both). Any unrecognised
/// code resolves to the side texture rather than throwing — a KeyNotFoundException on the tesselation path would
/// crash the render loop. Resolution mirrors the engine's <c>ContainedTextureSource</c>: look up the block
/// atlas, then load <c>textures/&lt;path&gt;.png</c> from assets on a miss — and then, before giving up, the
/// species' OTHER two families, so a wood that ships only one of the three is drawn in it everywhere instead of
/// in the unknown texture (see the indexer).
/// </summary>
internal sealed class LogTexSource : ITexPositionSource
{
    readonly ICoreClientAPI capi;
    readonly AssetLocation sideTex;  // #wood*          → bark / debarked round sides
    readonly AssetLocation endTex;   // #wood_corner*   → tree-ring (end-grain) cut faces
    readonly AssetLocation innerTex; // #debarked_wood* → bare wood revealed by the lengthwise rip cut
    readonly string forLogging;

    // Texture paths already complained about, so a missing texture costs ONE log line and not one per FACE. The
    // tesselator calls the indexer below once per shape face, and a miss inserts nothing into the atlas, so without
    // this every face of the mesh repeats the same warning: 42 lines for the half-log item shape alone, again on
    // every world entry (the per-species mesh caches are freed on unload and rebuilt). Keyed by the PATH, not by the
    // caller: the actionable content is "this texture does not exist", which is the same fix whoever asked for it,
    // and a second caller's line would only repeat it. Never cleared, like missingItem/missingState/missingSpecies
    // in BlockHalfLogSlab; the cost is that joining a second world with a DIFFERENT mod set in the same client
    // session will not re-report a path already reported. That loses a repeat of a warning, never a wrong one.
    // Lock-free because tesselation runs off the main thread.
    static readonly ConcurrentDictionary<string, byte> warnedPaths = new ConcurrentDictionary<string, byte>();

    public LogTexSource(ICoreClientAPI capi, AssetLocation sideTex, AssetLocation endTex, AssetLocation innerTex, string forLogging)
    {
        this.capi = capi;
        this.sideTex = sideTex;
        this.endTex = endTex;
        this.innerTex = innerTex;
        this.forLogging = forLogging;
    }

    /// <summary>
    /// Build the species-bound texture source for a sawable input <paramref name="logBlock"/> (the round/bark SIDES,
    /// the cut END-grain corners, and the freshly-ripped INNER faces), exactly as the sawhorse's <c>BuildLogMesh</c>
    /// does — so one authored geometry set serves every wood type and both inputs (barked log / debarked log). Each
    /// texture resolves through <see cref="WoodTextures"/> from the LOG'S OWN domain: that domain, then <c>game:</c>
    /// per texture, then whatever the log block itself declares (the mod-independence invariant; memory
    /// <c>vs-mod-asset-path-domain</c>). Shared by every station that lays a whole log: the sawhorse, the sawmill's
    /// workpiece (AutomatedSawmillPlan T07.03), the chopping block and the chopper.
    /// </summary>
    public static LogTexSource ForLog(ICoreClientAPI capi, Block logBlock, SawInput kind, string wood)
        => ForSpecies(capi, logBlock.Code?.Domain ?? "game", wood, kind == SawInput.DebarkedLog, logBlock.Code?.ToString() ?? wood);

    /// <summary>The same mapping for a species known only by its (domain, wood) pair, as a half-log stack carries it:
    /// round sides from bark (or bare wood when <paramref name="debarked"/>), caps from tree-ring end grain (its
    /// debarked variant, without the rim of bark, on a debarked piece), and the inner faces always bare wood, since a
    /// ripped or split face shows the wood under the bark either way. One copy for the whole log, the half-log item
    /// and the half-log laid on a stand, so a half of a stripped log looks like half of the log it came off.</summary>
    public static LogTexSource ForSpecies(ICoreClientAPI capi, string domain, string wood, bool debarked, string forLogging)
    {
        AssetLocation sideTex = WoodTextures.Get(capi, domain, wood, debarked ? WoodTexFamily.Debarked : WoodTexFamily.Bark);
        AssetLocation endTex = WoodTextures.Get(capi, domain, wood, debarked ? WoodTexFamily.TreetrunkDebarked : WoodTexFamily.Treetrunk);
        AssetLocation innerTex = WoodTextures.Get(capi, domain, wood, WoodTexFamily.Debarked);
        return new LogTexSource(capi, sideTex, endTex, innerTex, forLogging);
    }

    public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

    /// <summary>
    /// The atlas position for one shape texture key. Each surface names its OWN family first and then DEGRADES
    /// INSIDE THE SPECIES through the other two, before anything is called unknown (AuditFixPlan T04.05, F-PT-07).
    ///
    /// <para>Why the ladder is needed at all: vanilla ships four wood states that exist as a <c>debarkedlog</c> and
    /// never as a barked <c>log</c> — <c>veryaged</c>, <c>rotten</c>, <c>veryrotten</c> (and <c>aged</c>, which is
    /// the one of the four that does have both). For those, <c>block/wood/bark/{wood}</c> and
    /// <c>block/wood/treetrunk/{wood}</c> exist in no domain at all, so a half-log or a laid log of that species
    /// used to draw the white UNKNOWN texture on its round sides and its end caps. A species drawn in the one family
    /// it does ship reads as wood; unknown reads as a bug.
    ///
    /// <para>The order is the half-log slab's (<c>BlockHalfLogSlab.RetargetSpeciesTextures</c>: <c>round ??= end ??
    /// split</c>, <c>split ??= round</c>, <c>end ??= round</c>) rotated per surface, so the two texture pipelines
    /// degrade the same way and a laid slab cannot end up painted differently from the item it came off.
    ///
    /// <para>The ladder lives HERE and not in <see cref="ForLog"/> deliberately: every caller hands over
    /// <see cref="WoodTextures.Get"/>, which on a miss returns the unchecked <c>game:</c> path, so this indexer is the
    /// one seam every caller passes through.
    ///
    /// <para>The prefix tests are ORDINAL (AuditFixPlan T05.03 / F-L10-13): these are shape-authored ASCII keys, and
    /// the tesselator asks once per face of every mesh this class paints, so the default culture-sensitive
    /// <c>StartsWith</c> ran a linguistic collation where a byte compare is both faster and the correct question.
    /// </summary>
    public TextureAtlasPosition this[string textureCode]
        => textureCode.StartsWith("wood_corner", System.StringComparison.Ordinal) ? GetOrCreate(endTex, sideTex, innerTex)
            : textureCode.StartsWith("debarked", System.StringComparison.Ordinal) ? GetOrCreate(innerTex, sideTex, endTex)
            : GetOrCreate(sideTex, endTex, innerTex);

    /// <summary>Insert the first of <paramref name="candidates"/> the atlas already holds or that exists on disk;
    /// unknown — with ONE warning, keyed on the PRIMARY path — only when none of them does. The fallbacks are silent
    /// on purpose: a species drawn in its own bare wood instead of its own bark is a complete answer, not a
    /// degradation worth a line per species per surface.</summary>
    TextureAtlasPosition GetOrCreate(params AssetLocation[] candidates)
    {
        ITextureAtlasAPI atlas = capi.BlockTextureAtlas;

        foreach (AssetLocation path in candidates)
        {
            TextureAtlasPosition? pos = atlas[path];
            if (pos != null) return pos;

            IAsset? asset = capi.Assets.TryGet(path.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png"));
            if (asset != null && atlas.GetOrInsertTexture(path, out _, out TextureAtlasPosition inserted, () => asset.ToBitmap(capi)))
            {
                return inserted;
            }
        }

        AssetLocation primary = candidates[0];
        if (warnedPaths.TryAdd(primary.ToString(), 0))
        {
            capi.World.Logger.Warning("[immersivewoodworking] sawn-log texture {0} ({1}) missing, and so is every other texture of that species; using unknown.", primary, forLogging);
        }
        return atlas.UnknownTexturePosition;
    }
}
