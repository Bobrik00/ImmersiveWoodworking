using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// CLIENT-ONLY handbook composition, in two halves. The first BACKFILLS a "made at &lt;station&gt;" section onto
/// collectibles the mod does not own: when it strips the vanilla "axe + log → firewood", "saw + log/debarked log →
/// plank/support beam" and "axe + hammer + log → debarked log" grid recipes (see
/// <see cref="ImmersiveWoodworkingModSystem"/>), the handbook's auto "Created by" section for those OUTPUTS goes empty
/// and the player is left with no pointer to where planks, beams, firewood and debarked logs are now made. It covers
/// vanilla AND any wood mod's items (e.g. wildcrafttree), and adds a "process this here" pointer to the log INPUTS.
/// The second half composes the mod's OWN pages out of a base text plus config-conditional sentences
/// (<see cref="ComposeOwnPages"/>, in the OwnPages part) and hangs the bark drop table off the log and bark pages
/// (clickable icons, and one line for ordinary bark) — so the prose describes THIS server rather than an unconfigured
/// world.
///
/// MECHANISM — the same one vanilla's <c>TradeHandbookInfo</c> ("Sold by trader") uses for collectibles it does
/// not own: at <see cref="IClientEventAPI.LevelFinalize"/>, walk the live collectible registry and write an
/// <see cref="ExtraHandbookSection"/> into each target's <see cref="CollectibleBehaviorHandbookTextAndExtraInfo"/>
/// <c>.ExtraHandBookSections</c> field — NOT the JSON <c>Attributes</c>, which the behavior already cached into
/// that field at its OnLoaded (so a late Attributes edit would never be re-read). Additive, so the item's own
/// sections and any OTHER mod's appended sections all survive — no clobber. Because we iterate the registry
/// rather than patch domain-pinned JSON, it covers every wood domain for free (the same reason the recipe strip
/// lives in code).
///
/// WHEN — <see cref="IClientEventAPI.LevelFinalize"/> AND again on
/// <see cref="ImmersiveWoodworkingModSystem.ClientConfigApplied"/>, because those two are not ordered against each
/// other: the server sends its config at <c>PlayerJoin</c> and the client fires LevelFinalize when the level-finalize
/// packet lands, so composing only at LevelFinalize can bake the client's DEFAULTS into prose about a server that runs
/// something else (AuditFixPlan T09.01, F-L2-09/F-L3-16). Every write below is therefore a REPLACE-or-add keyed on the
/// section title, and every composed page keeps its base lang key on the side, so a second pass rewrites what the first
/// one wrote instead of stacking a duplicate.
///
/// COMPAT with the config: wording is chosen from the LIVE config so the note never lies — "replaced by these
/// stations" when the vanilla recipe is gone, "alongside the crafting grid" when it is kept, and the sticks pointer
/// names only the paths <see cref="ImmersiveWoodworkingConfig.AllowFirewoodToSticks"/> actually leaves open.
///
/// We do not own the backfilled collectibles, so we get-or-create the handbook behavior exactly as
/// <c>SurvivalHandbook.SetupBehaviorAndGetItemStacks</c> does — making us independent of whether that system has
/// run yet (if it runs after us it finds the behavior already present and skips; either order is fine).
/// </summary>
public partial class WoodworkingHandbookInfo : ModSystem
{
    const string Domain = "immersivewoodworking";

    // Shared bold heading above every backfilled section (a lang key — AddHeading runs Lang.Get on it).
    const string SectionTitle = Domain + ":iwsource-title";

    ICoreClientAPI capi = null!;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        // After assets + the level are up (collectibles are long registered, and the survival handbook has had
        // its chance to attach its behavior); we get-or-add the behavior anyway, so exact ordering doesn't matter.
        api.Event.LevelFinalize += Compose;

        // ...and again the moment the server's config replaces our defaults, whichever of the two happens second.
        // Both passes are idempotent, so the cost of the redundant one is a single walk of the registry.
        api.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().ClientConfigApplied += Compose;

        // The Woodworking overview is a page we create rather than a config/handbook asset; see AddGuidePage.
        api.ModLoader.GetModSystem<ModSystemSurvivalHandbook>().OnInitCustomPages += AddGuidePage;
    }

    /// <summary>Rebuild every page this system owns from the config in effect right now: the backfilled sections on
    /// other people's collectibles, and the mod's own config-composed prose.</summary>
    void Compose()
    {
        ImmersiveWoodworkingConfig config = capi.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config;

        // Needs no registry, and before the handbook exists there is no page yet: AddGuidePage composes it then.
        ComposeGuidePage(config);

        // Reachable before the collectible registry has been filled if the config packet wins the race with the level
        // load; the LevelFinalize pass then does the work. Never an error, so it is not logged.
        if (capi.World?.Collectibles == null) return;

        Backfill(config);
        AttachLogBarkBehavior();
        AttachBarkSourcesBehavior();
        ComposeOwnPages(config);
        AnnotateBarkRecipeDisplayStacks();
#if IW_DEBUG
        ComposeSelfCheck();
#endif
    }

    /// <summary>Fill the EMPTY bark slot the handbook's "Mixing (in Barrel)" section draws for the mod's bark chains.
    /// The bark input is matched purely by TAG with no <c>code</c> (source-conventions §2 — so any mod's bark drops
    /// into the chain), and a tags-only <see cref="CraftingRecipeIngredient"/> resolves with a NULL
    /// <c>ResolvedItemStack</c>: <see cref="CraftingRecipeIngredient.Resolve(IWorldAccessor, string)"/> returns before
    /// it ever builds a stack for a non-Exact match. The barrel handbook renders that null as a blank icon, so once the
    /// vanilla whole-log tannin routes are stripped (<see cref="ImmersiveWoodworkingConfig.DisableVanillaLogTannin"/>)
    /// the tannin pages were left showing only "&lt;blank&gt; + water = tannin". We hand the slot a bark to DISPLAY.
    ///
    /// <para>Display only, and safe: barrel matching never consults a tags-only ingredient's <c>ResolvedItemStack</c>
    /// — both <see cref="CraftingRecipeIngredient.SatisfiesAsIngredient"/> and its fast-search twin take the tags
    /// branch, and crafting consumes the real input stack, not this field. Nor is it serialized for a tags-only
    /// ingredient (<c>ToBytes</c> writes it only for an Exact match), so the CLIENT must set it itself after recipes
    /// sync — which is why this runs here, at LevelFinalize, on the same live registry list the handbook reads
    /// (<see cref="Vintagestory.GameContent.ApiAdditions.GetBarrelRecipes"/>). Client-side the ingredient carries a
    /// de-duplication index, so the assignment lands on the shared fast-search key; that key is read only by an Exact
    /// match, never a tags-only one, so it stays inert everywhere but the handbook.
    ///
    /// <para>A blank slot is spotted by the engine's own classification, <c>MatchingType == TagsOnly</c>, never by
    /// <c>Code == null</c>: client-side a tags-only ingredient carries the sentinel code <c>*:*</c> (verified 2026-09-19
    /// with an IW_DEBUG dump — no barrel ingredient on the client has a null code), so a null-code test matches nothing.
    /// The right bark per slot is then chosen by TAG without asking the item: the client carries the tag REGISTRY (it
    /// arrives locked) and the ingredient's own condition (serialised with the recipe), so we build a one-tag probe set
    /// per bark family through <see cref="ICoreAPI.CollectibleTagRegistry"/> and test the condition against it
    /// (<c>ComplexTagCondition.Matches</c> — pure set logic). That sidesteps <see cref="CollectibleObject.GetTags"/>,
    /// whose client-side contents this code never has to rely on. The families are disjoint (bark.json tagsByType), so
    /// each slot matches exactly one probe: green/dry/crushed show the right bark and the bark→compost page's blank slot
    /// is filled the same way. Idempotent: a filled slot reads back non-null, so the second Compose pass (the config
    /// packet after LevelFinalize) walks past it.</summary>
    void AnnotateBarkRecipeDisplayStacks()
    {
        var recipes = capi.GetBarrelRecipes();

        // One probe per bark-tag family the barrel chains use: the single tag → the bark item that stands for it. A tag
        // the client registry doesn't know (TryCreateTagSet != None) or a missing item drops out, so the loop below only
        // ever fills a slot whose condition really is one of ours.
        (string tag, string barkCode)[] families =
        {
            ("bark-tannin",         "bark-tan-green"), // green tanning bark
            ("bark-tannin-dry",     "bark-tan-dry"),   // dried tanning bark
            ("bark-tannin-crushed", "barkcrushed"),    // ground bark
            ("bark-compost",        "bark-generic"),   // the compost sink / the bare `bark` umbrella
        };
        var probes = new List<(TagSet set, ItemStack stack)>(families.Length);
        foreach ((string tag, string barkCode) in families)
        {
            if (capi.CollectibleTagRegistry.TryCreateTagSet(out TagSet set, tag) != TagRegistryError.None) continue;
            ItemStack? stack = BarkDisplayStack(barkCode);
            if (stack != null) probes.Add((set, stack));
        }

        int filled = 0, blanks = 0;
        for (int r = 0; recipes != null && r < recipes.Count; r++)
        {
            var recipe = recipes[r];
            if (recipe.Ingredients == null) continue;
            foreach (var ing in recipe.Ingredients)
            {
                // A blank slot is an unresolved tags-only ingredient that carries a real condition. Classified by
                // MatchingType, not by Code: client-side the code is the `*:*` sentinel, never null. Skip coded/liquid
                // inputs and any slot a prior pass already filled — the second Compose pass then sets nothing.
                if (ing.MatchingType != EnumRecipeMatchType.TagsOnly || ing.ResolvedItemStack != null || ing.Tags.IsEmpty) continue;
                blanks++;

                foreach ((TagSet set, ItemStack stack) in probes)
                {
                    if (!ing.Tags.Matches(set)) continue;
                    ing.ResolvedItemStack = stack.Clone(); // the count comes from the ingredient's own StackSize on read
                    filled++;
                    break;
                }
            }
        }

        if (filled > 0)
            capi.Logger.Notification("[immersivewoodworking] Gave {0} tags-only bark barrel ingredient(s) a display stack so the handbook's Mixing section shows bark, not a blank slot.", filled);
#if IW_DEBUG
        capi.Logger.Notification("[immersivewoodworking] Bark barrel display: {0} recipe(s), {1} probe(s) built, {2} blank tags-only slot(s), {3} filled.", recipes?.Count ?? -1, probes.Count, blanks, filled);
#endif
    }


    /// <summary>A plain one-item stack of the mod's bark item <paramref name="code"/>, or null if it is missing.</summary>
    ItemStack? BarkDisplayStack(string code)
    {
        Item? item = capi.World.GetItem(new AssetLocation(Domain, code));
        return item == null ? null : new ItemStack(item);
    }

    /// <summary>The "made at a woodworking station" note on collectibles the mod does not own.</summary>
    void Backfill(ImmersiveWoodworkingConfig config)
    {
        int annotated = 0;
        foreach (CollectibleObject obj in capi.World.Collectibles)
        {
            if (obj?.Code == null) continue;
            if (obj.Code.Domain == Domain) continue; // never annotate our own blocks/items

            string? textKey = NoteKeyFor(obj, config);
            if (textKey == null) continue;

            CollectibleBehaviorHandbookTextAndExtraInfo bh = GetOrAddHandbookBehavior(obj);
            if (SetSection(bh, SectionTitle, textKey)) annotated++;
        }

        capi.Logger.Notification(
            "[immersivewoodworking] Backfilled woodworking-station handbook notes onto {0} collectible(s) (saw stripped={1}, firewood stripped={2}, debark stripped={3}, firewood to sticks={4}, sawmill={5}, chopper={6}).",
            annotated, config.DisableVanillaPlankCrafting, config.DisableVanillaFirewoodCrafting, config.DisableVanillaDebarkCrafting, config.AllowFirewoodToSticks,
            config.EnableSawmill, config.EnableChopper);
    }

    /// <summary>The lang key of the "made/worked at a station" note for this collectible, or null if it
    /// isn't one we annotate. Whole keys, not suffixes the caller then prefixes: a key that only exists as a bare
    /// suffix is invisible to <c>tools/check-lang.py</c>, which cannot then tell a typo here from a real key
    /// (AuditFixPlan T09.02, F-L7-18). Outputs (plank/supportbeam/firewood) pick a config-aware variant, and so does the
    /// debarked log, which is both an output and an input; the remaining log inputs are always additive. Matched the
    /// same way the rest of the mod identifies wood items: outputs by first code-part, the log family by code-path
    /// prefix (so log-grown trees and resin variants don't get the "processed here" note meant for the
    /// harvested/placed log the stations actually take).
    ///
    /// <para>Firewood is matched by SHAPE rather than by the literal code, through the chopping block's own
    /// <see cref="BlockEntityChoppingBlock.IsFirewoodCodePart"/> — so vanilla's <c>agedfirewood</c> is annotated too
    /// (AuditFixPlan T04.05). That is not cosmetic: the stand now MAKES that item and the strip now removes its grid
    /// recipe, so without the note its handbook page would name no way of obtaining it at all.
    ///
    /// <para>The DEBARKED LOG has no second source anywhere: vanilla makes it from one grid recipe and nothing else, so
    /// with <see cref="ImmersiveWoodworkingConfig.DisableVanillaDebarkCrafting"/> on, its auto "Created by" section is
    /// empty and the page names no way of obtaining the item at all. Its note therefore leads with where the log is
    /// stripped (the sawhorse pair's SIDE action) before repeating what it is then worked into, and switches to the
    /// "alongside the crafting grid" wording when that flag is off (AuditFixPlan T09.03, F-L9-13).
    ///
    /// <para>STICKS are the one output the mod can lose entirely: chopping firewood into sticks is IW's own addition
    /// and <see cref="ImmersiveWoodworkingConfig.AllowFirewoodToSticks"/> switches it off, leaving driftwood as the
    /// only stick path here — and a config that also zeroes every driftwood stick yield leaves none, in which case the
    /// note is dropped rather than made to promise a station that refuses the item (AuditFixPlan T09.01,
    /// F-L2-09).
    ///
    /// <para>Every note that names a powered machine has a twin without it, picked when
    /// <see cref="ImmersiveWoodworkingConfig.EnableSawmill"/> or <see cref="ImmersiveWoodworkingConfig.EnableChopper"/>
    /// is off: the machine's page is hidden then, and the note would point at nothing. The sawmill appears in the
    /// plank, beam and log notes, the chopper only in the firewood ones.</para></summary>
    static string? NoteKeyFor(CollectibleObject obj, ImmersiveWoodworkingConfig config)
    {
        bool sawStripped = config.DisableVanillaPlankCrafting;
        bool firewoodStripped = config.DisableVanillaFirewoodCrafting;
        bool sawmill = config.EnableSawmill;

        string part = obj.FirstCodePart();
        switch (part)
        {
            case "plank":
                if (sawmill) return sawStripped ? Domain + ":iwsource-plank-text" : Domain + ":iwsource-plank-text-also";
                return sawStripped ? Domain + ":iwsource-plank-text-nosawmill" : Domain + ":iwsource-plank-text-also-nosawmill";
            case "supportbeam":
                if (sawmill) return sawStripped ? Domain + ":iwsource-supportbeam-text" : Domain + ":iwsource-supportbeam-text-also";
                return sawStripped ? Domain + ":iwsource-supportbeam-text-nosawmill" : Domain + ":iwsource-supportbeam-text-also-nosawmill";
            case "stick":
                return StickNoteKey(config);
        }

        if (BlockEntityChoppingBlock.IsFirewoodCodePart(part))
        {
            if (config.EnableChopper) return firewoodStripped ? Domain + ":iwsource-firewood-text" : Domain + ":iwsource-firewood-text-also";
            return firewoodStripped ? Domain + ":iwsource-firewood-text-nochopper" : Domain + ":iwsource-firewood-text-also-nochopper";
        }

        string path = obj.Code.Path;
        if (path.StartsWith("debarkedlog-", StringComparison.Ordinal))
        {
            if (sawmill) return config.DisableVanillaDebarkCrafting ? Domain + ":iwsource-debarkedlog-text" : Domain + ":iwsource-debarkedlog-text-also";
            return config.DisableVanillaDebarkCrafting ? Domain + ":iwsource-debarkedlog-text-nosawmill" : Domain + ":iwsource-debarkedlog-text-also-nosawmill";
        }
        if (path.StartsWith("logsection-", StringComparison.Ordinal))
            return sawmill ? Domain + ":iwsource-logsection-text" : Domain + ":iwsource-logsection-text-nosawmill";
        if (path.StartsWith("log-placed-", StringComparison.Ordinal))
            return sawmill ? Domain + ":iwsource-log-text" : Domain + ":iwsource-log-text-nosawmill";
        return null;
    }

    /// <summary>Which of the two stick notes this config earns, or null when it earns neither: both the chopping
    /// stations' firewood step and every driftwood size can be configured away, and a note naming a station that then
    /// refuses the item is worse than no note.</summary>
    static string? StickNoteKey(ImmersiveWoodworkingConfig config)
    {
        if (config.AllowFirewoodToSticks) return Domain + ":iwsource-stick-text";
        bool driftwoodGivesSticks = config.FlotsamSticksLarge > 0 || config.FlotsamSticksNormal > 0 || config.FlotsamSticksSmall > 0;
        return driftwoodGivesSticks ? Domain + ":iwsource-stick-text-flotsam" : null;
    }

    /// <summary>The collectible's handbook behavior, creating + attaching one if it has none — mirroring
    /// <c>SurvivalHandbook.SetupBehaviorAndGetItemStacks</c> so we don't depend on that system having run.</summary>
    CollectibleBehaviorHandbookTextAndExtraInfo GetOrAddHandbookBehavior(CollectibleObject obj)
    {
        CollectibleBehaviorHandbookTextAndExtraInfo? bh = obj.GetBehavior<CollectibleBehaviorHandbookTextAndExtraInfo>();
        if (bh == null)
        {
            bh = new CollectibleBehaviorHandbookTextAndExtraInfo(obj);
            bh.OnLoaded(capi);
            obj.CollectibleBehaviors = obj.CollectibleBehaviors.Append(bh);
        }
        return bh;
    }

    /// <summary>Put our section into the behavior's array: REWRITE the one already carrying this title, else append.
    /// Rewriting rather than skipping is what makes a second pass (the config packet landing after LevelFinalize)
    /// correct rather than a no-op — the first pass may have written a note composed from the client's defaults.
    /// Returns whether the section is present afterwards, which it always is; the bool exists for the counter.
    /// (Vanilla's TradeHandbookInfo uses a LINQ <c>Array.Append</c> whose result it then discards for the non-null
    /// case — we grow the array explicitly so the append actually lands.)</summary>
    static bool SetSection(CollectibleBehaviorHandbookTextAndExtraInfo bh, string title, string textOrKey)
    {
        ExtraHandbookSection[]? existing = bh.ExtraHandBookSections;
        if (existing != null)
        {
            foreach (ExtraHandbookSection s in existing)
            {
                if (s != null && s.Title == title) { s.Text = textOrKey; return true; }
            }
        }

        ExtraHandbookSection section = new ExtraHandbookSection { Title = title, Text = textOrKey };
        if (existing == null || existing.Length == 0)
        {
            bh.ExtraHandBookSections = new[] { section };
        }
        else
        {
            ExtraHandbookSection[] grown = new ExtraHandbookSection[existing.Length + 1];
            Array.Copy(existing, grown, existing.Length);
            grown[existing.Length] = section;
            bh.ExtraHandBookSections = grown;
        }
        return true;
    }
}
