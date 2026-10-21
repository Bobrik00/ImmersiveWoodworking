using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// CLIENT-only handbook add-on carried by the bark items: appends the "Stripped from these woods" section to the bark's
/// page as a row of real, CLICKABLE log icons (author request 2026-10-20: the bark should show its logs the way a log
/// shows its bark). It is the mirror of <see cref="HandbookLogBarkBehavior"/>, through the same
/// <c>ICustomHandbookPageContent</c> seam and for the same reason: a VTML <c>&lt;itemstack&gt;</c> icon cannot be
/// clicked. Until then the section was a comma-separated list of species names written by
/// <see cref="WoodworkingHandbookInfo"/> into an <see cref="ExtraHandbookSection"/>.
///
/// <para>The row has no percentages, like vanilla's "Obtained by breaking": the chance depends on the debarking tool,
/// and the log's own page, one click away, already gives it per tool. The order still says which wood is likeliest.
/// Ordinary bark draws every log it can come off with some tool, a long row on a modded install (author decision
/// 2026-10-21). It used to say "any wood" flat, which promised ordinary bark off an oak a spud never gives, and a
/// per-tool list of the exceptions in its place read as the opposite of what it meant: two icons under the heading
/// looked like the only two sources.</para>
/// </summary>
public class HandbookBarkSourcesBehavior : CollectibleBehavior, ICustomHandbookPageContent
{
    public HandbookBarkSourcesBehavior(CollectibleObject collObj) : base(collObj) { }

    public void OnHandbookPageComposed(List<RichTextComponentBase> components, ItemSlot inSlot, ICoreClientAPI capi, ItemStack[] allStacks, ActionConsumable<string> openDetailPageFor)
    {
        // Read at draw time, so the section follows the server's config whichever way the sync raced the level load.
        ImmersiveWoodworkingConfig config = capi.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;
        if (!config.EnableBarkChain) return; // a stripped log drops no bark then

        List<Block> logs = capi.ModLoader.GetModSystem<WoodworkingHandbookInfo>().BarkSourceLogs(collObj.Code.Path, config);
        if (logs.Count == 0) return; // no wood here gives this kind, and an empty heading would say otherwise

        bool haveText = components.Count > 0;
        CollectibleBehaviorHandbookTextAndExtraInfo.AddHeading(components, capi, "immersivewoodworking:barksources-title", ref haveText);
        components.Add(new ClearFloatTextComponent(capi, 2f));

        foreach (Block log in logs) AppendIcon(components, capi, log, openDetailPageFor);

        components.Add(new ClearFloatTextComponent(capi, 2f));
    }

    /// <summary>Append one log ICON, the <see cref="SlideshowItemstackTextComponent"/> the handbook's own panels use,
    /// clickable through to that log's page. The page code is resolved the way the handbook resolves its own icons
    /// (<c>getPageCodeForStack</c>, private there): a page-code provider on the block first, else the stack's plain code,
    /// which keeps a wood mod's domain (<c>block-wildcrafttree:log-placed-...</c>) and drops only <c>game:</c>.</summary>
    static void AppendIcon(List<RichTextComponentBase> components, ICoreClientAPI capi, Block log, ActionConsumable<string> openDetailPageFor)
    {
        components.Add(new SlideshowItemstackTextComponent(
            capi, new ItemStack[] { new ItemStack(log) }, 40.0, EnumFloat.Inline,
            cs => openDetailPageFor.Invoke(
                cs.Collectible.GetCollectibleInterface<IHandBookPageCodeProvider>()?.HandbookPageCodeForStack(capi.World, cs)
                ?? GuiHandbookItemStackPage.PageCodeForStack(cs))));
    }
}
