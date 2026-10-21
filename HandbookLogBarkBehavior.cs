using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// CLIENT-only handbook add-on carried by every placed log: appends the "When stripped" bark drop list to the log's
/// page as real, CLICKABLE item icons, one line per debarking tool with that tool's chances (comment #2 on ModDB, plus
/// author feedback 2026-09-22/23 — bigger icons, no name text, click-through, and a per-tool split because the tool tier
/// changes the odds). It is the REVERSE of the bark pages' "Stripped from these woods" list.
///
/// <para>WHY A BEHAVIOR AND NOT AN <see cref="ExtraHandbookSection"/>: a section carries only VTML text, and VTML's
/// <c>&lt;itemstack&gt;</c> icon is display-only (<c>VtmlUtil</c> builds it with a null click handler). The handbook's
/// own drop/recipe icons are clickable because <c>CollectibleBehaviorHandbookTextAndExtraInfo.GetHandbookInfo</c>
/// builds a <see cref="SlideshowItemstackTextComponent"/> WITH an <c>onStackClicked</c> that opens the detail page.
/// The one seam to add such components to a collectible we do not own is <c>ICustomHandbookPageContent</c>, which that
/// same method invokes at the very end via <c>collObj.GetCollectibleInterface&lt;ICustomHandbookPageContent&gt;()</c>
/// (a <c>Type.IsAssignableFrom</c> match, so it finds this behavior) once
/// <see cref="WoodworkingHandbookInfo.AttachLogBarkBehavior"/> hangs it on the log.</para>
///
/// <para>The tools mirror <see cref="BlockEntitySawhorse.BarkChanceMultFor"/>: the bark spud (always), an axe+hammer
/// (always), and a knife (only when <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> is on). Each tool's rows
/// and chances come from <see cref="WoodworkingHandbookInfo.LogBarkRows"/> at that tool's live multiplier, so a wood
/// mod's logs are covered for free and the numbers match this server. A generic-only species yields ordinary bark at
/// every tool, so it skips the per-tool split and shows a single icon.</para>
/// </summary>
public class HandbookLogBarkBehavior : CollectibleBehavior, ICustomHandbookPageContent
{
    public HandbookLogBarkBehavior(CollectibleObject collObj) : base(collObj) { }

    public void OnHandbookPageComposed(List<RichTextComponentBase> components, ItemSlot inSlot, ICoreClientAPI capi, ItemStack[] allStacks, ActionConsumable<string> openDetailPageFor)
    {
        // Read at draw time, so the section follows the server's config whichever way the sync raced the level load.
        ImmersiveWoodworkingConfig config = capi.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
        if (!config.EnableBarkChain) return; // a stripped log drops no bark then

        string? species = CodeParts.At(collObj.Code, 2);
        if (species == null) return;

        WoodworkingHandbookInfo pages = capi.ModLoader.GetModSystem<WoodworkingHandbookInfo>();

        List<(string ItemPath, int Pct)> spudRows = pages.LogBarkRows(species, config.BarkChanceMultSpud, out bool anySpecial);

        bool haveText = components.Count > 0;
        CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "immersivewoodworking:iwbark-fromlog-title", ref haveText);
        components.Add(new ClearFloatTextComponent(capi, 2f));

        if (!anySpecial)
        {
            // Every tool yields ordinary bark here, so a per-tool split says nothing — one icon is the whole story.
            AppendIcon(components, capi, "bark-generic", openDetailPageFor);
            components.Add(new RichTextComponent(capi, "\n", CairoFont.WhiteSmallText()));
            components.Add(new ClearFloatTextComponent(capi, 2f));
            return;
        }

        AppendToolBlock(components, capi, "immersivewoodworking:iwbark-tool-spud", spudRows, openDetailPageFor);
        AppendToolBlock(components, capi, "immersivewoodworking:iwbark-tool-axehammer",
            pages.LogBarkRows(species, config.BarkChanceMultAxeHammer, out _), openDetailPageFor);
        if (config.AllowKnifeDebark)
            AppendToolBlock(components, capi, "immersivewoodworking:iwbark-tool-knife",
                pages.LogBarkRows(species, config.BarkChanceMultKnife, out _), openDetailPageFor);

        components.Add(new ClearFloatTextComponent(capi, 2f));
    }

    /// <summary>One tool's block: a label line naming the tool, then that tool's barks one per line — a clickable icon
    /// followed by its chance — the same icon-and-percent-per-line shape the author approved, grouped under each tool.</summary>
    static void AppendToolBlock(List<RichTextComponentBase> components, ICoreClientAPI capi, string labelKey, List<(string ItemPath, int Pct)> rows, ActionConsumable<string> openDetailPageFor)
    {
        components.Add(new RichTextComponent(capi, Lang.Get(labelKey) + ":\n", CairoFont.WhiteSmallText()));
        foreach ((string itemPath, int pct) in rows)
        {
            AppendIcon(components, capi, itemPath, openDetailPageFor);
            components.Add(new RichTextComponent(capi, " " + pct + "%\n", CairoFont.WhiteSmallText()));
        }
    }

    /// <summary>Append one bark item ICON (the same <see cref="SlideshowItemstackTextComponent"/> the handbook's own
    /// drop panels use, so it looks native), clickable through to that bark's page. The page code is the string a
    /// <c>handbook://item-...</c> href resolves to — a mod-domain target keeps its domain, and bark carries no
    /// page-splitting attributes, so it is just <c>item-&lt;domain&gt;:&lt;code&gt;</c>.</summary>
    static void AppendIcon(List<RichTextComponentBase> components, ICoreClientAPI capi, string itemPath, ActionConsumable<string> openDetailPageFor)
    {
        Item? item = capi.World.GetItem(new AssetLocation("immersivewoodworking", itemPath));
        if (item == null) return;

        string pageCode = "item-immersivewoodworking:" + itemPath;
        components.Add(new SlideshowItemstackTextComponent(
            capi, new ItemStack[] { new ItemStack(item) }, 40.0, EnumFloat.Inline,
            cs => openDetailPageFor.Invoke(pageCode)));
    }
}
