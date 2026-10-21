using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// Resolves a stored ITEM's textures into the BLOCK atlas, so an item mesh (firewood / stick / any laid item) can be
/// tesselated into a mesh that samples the block atlas — needed both for the chunk terrain mesh (which only samples the
/// block atlas) and for the automated chopper's custom renderer (so its workpiece meshes are all block-atlas, exactly
/// like its log-block meshes). A verbatim port of the texture-source logic <see cref="BlockEntityChoppingBlock"/> used
/// to carry inline (mirroring vanilla <c>BlockEntityDisplay</c>), lifted to a standalone class so the hand stand
/// (content + stuck tool) AND the chopper share ONE implementation instead of each re-tesselating items their own way.
///
/// One instance per tesselated item (the item + its cached shape are captured in the ctor), so there is no
/// mid-tesselation scratch state to reset — unlike the old BE-as-source path, which had to stash/clear
/// <c>nowTesselatingObj</c> around every <c>TesselateItem</c> call.
/// </summary>
internal sealed class LaidContentItemTexSource : ITexPositionSource
{
    readonly ICoreClientAPI capi;
    readonly Item item;
    readonly Shape? shape;

    public LaidContentItemTexSource(ICoreClientAPI capi, Item item)
    {
        this.capi = capi;
        this.item = item;
        this.shape = item.Shape?.Base != null ? capi.TesselatorManager.GetCachedShape(item.Shape.Base) : null;
    }

    public Size2i AtlasSize => capi.BlockTextureAtlas.Size;

    public TextureAtlasPosition this[string textureCode]
    {
        get
        {
            IDictionary<string, CompositeTexture>? itemTextures = item.Textures;
            AssetLocation? texturePath = null;

            // Prio 1/2: the collectible's own textures (exact code, then the "all" catch-all).
            if (itemTextures != null && itemTextures.TryGetValue(textureCode, out CompositeTexture? tex)) texturePath = tex.Baked.BakedName;
            else if (itemTextures != null && itemTextures.TryGetValue("all", out tex)) texturePath = tex.Baked.BakedName;
            // Prio 3: a texture declared on the item's shape. Prio 4: treat the code itself as the path.
            if (texturePath == null) shape?.Textures.TryGetValue(textureCode, out texturePath);
            texturePath ??= new AssetLocation(textureCode);

            return GetOrCreateTexPos(texturePath);
        }
    }

    TextureAtlasPosition GetOrCreateTexPos(AssetLocation texturePath)
    {
        ITextureAtlasAPI atlas = capi.BlockTextureAtlas;
        TextureAtlasPosition? texpos = atlas[texturePath];
        if (texpos != null) return texpos;

        if (atlas.GetOrInsertTexture(texturePath, out _, out TextureAtlasPosition inserted, null)) return inserted;

        capi.World.Logger.Warning("[immersivewoodworking] no block-atlas texture {0} for item {1} displayed on a chopping surface.", texturePath, item.Code);
        return atlas.UnknownTexturePosition;
    }
}
