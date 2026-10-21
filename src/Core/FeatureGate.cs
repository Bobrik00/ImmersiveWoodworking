using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;

namespace ImmersiveWoodworking;

/// <summary>
/// The asset side of the three subsystem switches (<see cref="ImmersiveWoodworkingConfig.EnableSawmill"/>,
/// <see cref="ImmersiveWoodworkingConfig.EnableChopper"/>, <see cref="ImmersiveWoodworkingConfig.EnableBarkChain"/>),
/// server only. A switched-off subsystem loses what would let a player obtain or find it: the mod's own recipe and
/// patch entries for it are marked <c>enabled: false</c> before anything reads them, and its blocks and items leave
/// the creative inventory and the handbook. Nothing is unregistered: a code that stops existing turns every placed
/// machine and every carried stack into an unknown block or item, so a world that switches a part off keeps what it
/// already has.
///
/// <para>WHY A SEPARATE SYSTEM. The bark chain's patches on vanilla recipes have to be switched off before the
/// engine's JSON patch loader applies them, and that loader runs in AssetsLoaded at ExecuteOrder 0.05, ahead of the
/// main mod system (0.1, which must stay after it: its recipe strip reads patched recipes). Hence 0.04. Recipe files
/// are read later still (RecipeLoader and the cooking loader at 1, ACA's simmering loader), so the same pass covers
/// them; every one of those loaders, and the patch loader, skips an entry whose <c>enabled</c> is false. The config
/// is already loaded by then: AssetsLoaded follows every system's StartPre, where the main system reads it.</para>
///
/// <para>WHY SERVER ONLY. Recipes and patched blocktypes/itemtypes exist only on the server, and the client gets the
/// recipe registry and the block/item registry from it, creative tabs and attributes included. The client's own
/// config is still the defaults at asset time anyway (the server's arrives on join), so a client pass could only
/// ever switch nothing off.</para>
///
/// <para>The per-domain machine recipes are not here: they are templates the main system clones in code, and it
/// simply skips the clone (<c>RegisterSawmillRecipes</c>, <c>RegisterChopperRecipes</c>). Nor is the bark drop
/// (<c>BlockEntitySawhorse.CompleteDebark</c>) or the handbook prose (<c>WoodworkingHandbookInfo</c>).</para>
/// </summary>
public class FeatureGate : ModSystem
{
    const string Domain = "immersivewoodworking";

    // Own assets whose every entry is switched off with the sawmill. The frame and the four parts are code-cloned
    // templates (see the class doc); the blade kit is the one plain recipe.
    static readonly string[] SawmillAssets =
    {
        "recipes/grid/sawmill_blade.json",
    };

    static readonly string[] ChopperAssets =
    {
        "recipes/grid/chopper_head.json",
        "recipes/smithing/chopperheadhalf.json",
    };

    // Every recipe that consumes or makes a bark-chain item, plus the patches that only change OTHER recipes or tags.
    // Deliberately left on (inert without a bark source, and each holds up something that may already be placed or
    // carried): pitkiln_bark_sticks + pitkiln_bark_model (a kiln built with bark kindling), compat/vsroofing_birchbark
    // (a birch-bark roof keeps its material), compat/foodshelves_bark (bark in a jar), poultice_bast +
    // poultice_bark-medicinal (the poultice variants they add stay registered and are hidden below instead).
    static readonly string[] BarkAssets =
    {
        "recipes/barrel/compost-bark.json",
        "recipes/barrel/strongtannin-bark.json",
        "recipes/barrel/weaktannin-bark.json",
        "recipes/clayforming/perforatedpot.json",
        "recipes/cooking/barkbrew.json",
        "recipes/cooking/resin-tar.json",
        "recipes/grid/poultice-bark.json",
        "recipes/grid/poultice-bast.json",
        "recipes/grid/rope-bast.json",
        "recipes/simmering/aca_boiledbark.json",
        "patches/tar_as_resin.json",
        "patches/tar_glue.json",
        "patches/compat/aca_bark.json",
    };

    // First code part of every bark-chain block and item the mod registers. The bark spud and its head are NOT here:
    // debarking stays with the chain off.
    static readonly string[] BarkCodeParts = { "bark", "barkcrushed", "barktar", "barkbrew", "tarpot", "perforatedpot" };

    public override double ExecuteOrder() => 0.04;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void AssetsLoaded(ICoreAPI api)
    {
        ImmersiveWoodworkingConfig config = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
        if (!config.EnableSawmill) DisableAssets(api, "EnableSawmill", SawmillAssets);
        if (!config.EnableChopper) DisableAssets(api, "EnableChopper", ChopperAssets);
        if (!config.EnableBarkChain) DisableAssets(api, "EnableBarkChain", BarkAssets);
    }

    public override void AssetsFinalize(ICoreAPI api)
    {
        ImmersiveWoodworkingConfig config = api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
        if (config.EnableSawmill && config.EnableChopper && config.EnableBarkChain) return;

        int hidden = 0;
        foreach (CollectibleObject obj in api.World.Collectibles)
        {
            if (obj?.Code == null || !IsSwitchedOff(obj, config)) continue;
            Hide(obj);
            hidden++;
        }

        api.Logger.Notification(
            "[immersivewoodworking] Hid {0} block/item type(s) from the creative inventory and the handbook (sawmill={1}, chopper={2}, bark chain={3}).",
            hidden, config.EnableSawmill, config.EnableChopper, config.EnableBarkChain);
    }

    /// <summary>Whether <paramref name="obj"/> belongs to a subsystem this config switches off. Matched by code shape,
    /// not by a list of variants: every sawmill block and item path starts with "sawmill" and every chopper one with
    /// "chopper" ("choppingblock" does not), and the bark chain is a fixed set of first code parts.</summary>
    static bool IsSwitchedOff(CollectibleObject obj, ImmersiveWoodworkingConfig config)
    {
        string path = obj.Code.Path;
        if (obj.Code.Domain == Domain)
        {
            if (!config.EnableSawmill && path.StartsWith("sawmill", System.StringComparison.Ordinal)) return true;
            if (!config.EnableChopper && path.StartsWith("chopper", System.StringComparison.Ordinal)) return true;
            return !config.EnableBarkChain && System.Array.IndexOf(BarkCodeParts, obj.FirstCodePart()) >= 0;
        }

        // The vanilla poultice variants patches/poultice_bast.json and poultice_bark-medicinal.json add:
        // poultice-{material}-{agent}, with bast as a material and bark-medicinal as an agent.
        return !config.EnableBarkChain && obj.Code.Domain == "game"
            && (path.StartsWith("poultice-bast-", System.StringComparison.Ordinal)
                || (path.StartsWith("poultice-", System.StringComparison.Ordinal) && path.EndsWith("-bark-medicinal", System.StringComparison.Ordinal)));
    }

    /// <summary>Out of the creative inventory and out of the handbook. No creative tab and no creative stacks already
    /// keeps the default <c>GetHandBookStacks</c> from making a page, unless the type opts back in with
    /// <c>handbook.include</c>; <c>handbook.exclude</c> is the one flag that wins over that, so it is set too. Both
    /// travel to the client inside the block/item packet.</summary>
    static void Hide(CollectibleObject obj)
    {
        obj.CreativeInventoryTabs = System.Array.Empty<string>();
        obj.CreativeInventoryStacks = null;

        if (obj.Attributes?.Token is not JObject attributes)
        {
            attributes = new JObject();
            obj.Attributes = new JsonObject(attributes);
        }
        if (attributes["handbook"] is not JObject handbook)
        {
            handbook = new JObject();
            attributes["handbook"] = handbook;
        }
        handbook["exclude"] = true;
    }

    /// <summary>Mark every entry of each listed own asset <c>enabled: false</c>. A recipe or patch file is either one
    /// object or an array of them; both shapes are handled. Same write-back and <c>IsPatched</c> idiom as the main
    /// system's recipe passes (<c>ForEachRecipeAsset</c>), but a stricter parse: those read other mods' files the way
    /// the engine does (<c>ReadAssetLikeEngine</c>), these are our own, comment-stripped build output, so a file
    /// <c>JToken.Parse</c> rejects is a broken file of ours. A listed file that is missing or unreadable is reported,
    /// since it means this list has drifted from the assets.</summary>
    static void DisableAssets(ICoreAPI api, string flag, string[] paths)
    {
        int files = 0, entries = 0;
        foreach (string path in paths)
        {
            IAsset? asset = api.Assets.TryGet(new AssetLocation(Domain, path));
            if (asset == null)
            {
                api.Logger.Warning("[immersivewoodworking] {0} is off, but {1}:{2} was not found to switch off.", flag, Domain, path);
                continue;
            }

            JToken token;
            try
            {
                token = JToken.Parse(asset.ToText());
            }
            catch (System.Exception e)
            {
                api.Logger.Warning("[immersivewoodworking] {0} is off, but {1} could not be parsed and stays on. {2}", flag, asset.Location, e.Message);
                continue;
            }

            foreach (JToken entry in token as JArray ?? (IEnumerable<JToken>)new[] { token })
            {
                if (entry is not JObject obj) continue;
                obj["enabled"] = false;
                entries++;
            }

            asset.Data = Encoding.UTF8.GetBytes(token.ToString(Formatting.None));
            asset.IsPatched = true; // see ForEachRecipeAsset: keeps Launch()'s UnloadUnpatchedAssets from restoring the file
            files++;
        }

        api.Logger.Notification("[immersivewoodworking] {0} is off: switched off {1} recipe/patch entr(y/ies) in {2} asset(s).", flag, entries, files);
    }
}
