using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace ImmersiveWoodworking;

/// <summary>
/// The mod's OWN handbook pages, composed on the client from the config in effect (AuditFixPlan T09.01, closing
/// F-L3-16 / F-L9-12 and the R20 root with it).
///
/// <para>THE RULE the pages are written to: prose names no NUMBER that lives in the config, and asserts no FACT the
/// config can switch off. Numbers are gone from the text outright — "the reducer turns shaft rotation into one slow,
/// heavy blow" instead of a strike count that a reducer ratio or <c>ChopperSpeed</c> would falsify. Facts that a
/// flag really does remove (the half-log stage, firewood → sticks, an ordinary saw on the trestles, the knife strip)
/// cannot be written neutrally without losing the point, so they live in SEPARATE sentence keys that this file drops
/// into numbered placeholders of the page's base text. One base key per page keeps the paragraph order authored by the
/// translator rather than by the code, and a flag turning off substitutes an EMPTY string, which
/// <see cref="Tidy"/> then closes up.</para>
///
/// <para>MECHANISM — the composed page is written back into the SAME
/// <see cref="ExtraHandbookSection"/> the block's JSON declared, matched by its TITLE key, with the composed TEXT put
/// where the lang key used to be. That works because the engine renders a section with
/// <c>Lang.Get(section.Text)</c> and <c>Lang.Get</c> answers a miss with the string it was handed
/// (<c>TranslationService.GetUnformatted</c>) — so a finished sentence passes through untouched. Composing every pass
/// from the BASE key (never from what a previous pass wrote) is what keeps the second pass, the one the server's
/// config packet triggers, correct rather than compounding.</para>
///
/// <para>This file also feeds the two bark lists that are drawn rather than written, both off the live
/// <see cref="BarkDropTable"/> and filtered to the species this install actually has: the log pages' "When stripped"
/// (<see cref="HandbookLogBarkBehavior"/>) and the bark pages' "Stripped from these woods"
/// (<see cref="HandbookBarkSourcesBehavior"/>, a row of clickable log icons since 2026-10-20; until then a line of
/// species names in an <see cref="ExtraHandbookSection"/>). Before T09.01 the bark pages' list was literal words in five
/// lang files — it named willow, linden, elm and poplar on an install with no Wildcraft Trees, and could not follow a
/// tree mod's JSON patch into the table at all — while the client's copy of the table, loaded on both sides for exactly
/// this purpose, was read by nobody (F-L9-12).</para>
/// </summary>
public partial class WoodworkingHandbookInfo
{
    // ── The composed pages ──────────────────────────────────────────────────────────────────────────────
    // Matched by the TITLE key their blocktype JSON declares (attributes.handbook.extraSections), which is stable and
    // unique per page; the base TEXT key is the one that JSON also declares, and stays the fallback for anything that
    // reads the asset without us (a JSON dump, a wiki scraper).
    const string ChoppingBlockTitle = Domain + ":choppingblock-handbook-title";
    const string ChoppingBlockText = Domain + ":choppingblock-handbook-text";
    const string SawhorseTitle = Domain + ":sawhorse-handbook-title";
    const string SawhorseText = Domain + ":sawhorse-handbook-text";

    // The Woodworking overview, the page every other page links to as handbook://craftinginfo-woodworking. It names
    // the bark chain and both machines, each in a paragraph of its own that the config can switch off.
    const string GuidePageCode = "craftinginfo-woodworking";
    const string GuideTitle = Domain + ":craftinginfo-woodworking-title";
    const string GuideText = Domain + ":craftinginfo-woodworking-text";

    // The page object the handbook dialog currently holds, so a config packet that lands after the dialog was built
    // can rewrite it. Replaced each time the dialog rebuilds its pages (/debug reloadhandbook does).
    GuiHandbookTextPage? guidePage;

    // The special bark KINDS that carry a table entry, mapped to the fresh item each rolls to. `generic` is the
    // remainder, so it has no entry — every wood can drop it. Drives both the forward "which woods drop this" list
    // (BarkSourceLogs / HandbookBarkSourcesBehavior) and the reverse per-log drop list (LogBarkRows /
    // HandbookLogBarkBehavior).
    static readonly (string ItemPath, string Kind)[] BarkSourcePages =
    {
        ("bark-tan-green",   "tan"),
        ("bark-birch-green", "birch"),
        ("bark-bast",        "bast"),
        ("bark-medicinal",   "medicinal"),
    };

    // species → the placed log that stands for it on the bark pages, built once from the block registry (see
    // WoodLogs). Rebuilt never: the registry a client holds is the server's and does not change while the level is up.
    Dictionary<string, Block>? woodLogs;

    /// <summary>Compose every page whose text depends on the config. Called for each pass of <see cref="Compose"/>;
    /// every step is a rewrite, so passes are idempotent.</summary>
    void ComposeOwnPages(ImmersiveWoodworkingConfig config)
    {
        string choppingBlock = Tidy(Lang.Get(ChoppingBlockText,
            Lang.Get(ChopSplitFragment(config.HalfLogChopStage, config.DropsAtPlayer)),
            config.AllowFirewoodToSticks ? Lang.Get(Domain + ":choppingblock-handbook-frag-sticks") : ""));

        string sawhorse = Tidy(Lang.Get(SawhorseText,
            Lang.Get(RipToolFragment(config)),
            config.AllowKnifeDebark ? Lang.Get(Domain + ":sawhorse-handbook-frag-knife") : "",
            Lang.Get(DebarkedDropFragment(config.DropsAtPlayer))));

        int composed = 0;
        foreach (CollectibleObject obj in capi.World.Collectibles)
        {
            if (obj?.Code == null || obj.Code.Domain != Domain) continue;

            // Get-or-add, as Backfill does: no JSON of ours declares the behavior, the survival handbook attaches it in
            // its own LevelFinalize, and that handler runs AFTER this one (both at ExecuteOrder 0.1, and
            // immersivewoodworking sorts ahead of survival in the load order). Reading it with GetBehavior found nothing
            // on either pass and composed zero sections: the bark pages lost their species list and the chopping block
            // and trestle pages showed the base text's raw {0}. The handbook then sees ours and adds no second one.
            CollectibleBehaviorHandbookTextAndExtraInfo bh = GetOrAddHandbookBehavior(obj);

            if (WriteComposed(bh, ChoppingBlockTitle, choppingBlock)) composed++;
            if (WriteComposed(bh, SawhorseTitle, sawhorse)) composed++;
        }

        capi.Logger.Notification(
            "[immersivewoodworking] Composed {0} handbook section(s) from the live config (half-log stage={1}, firewood to sticks={2}, ordinary saw={3}, knife strip={4}, drops at player={5}).",
            composed, config.HalfLogChopStage, config.AllowFirewoodToSticks, config.AllowVanillaSaw, config.AllowKnifeDebark, config.DropsAtPlayer);
    }

    /// <summary>Add the Woodworking overview to the handbook as it builds its text pages. The page used to be a
    /// <c>config/handbook</c> asset, and that route cannot carry config-dependent text: the dialog calls
    /// <see cref="GuiHandbookTextPage.Init"/> on every asset page BEFORE it raises this event, and <c>Init</c> runs
    /// <c>Lang.Get</c> on the text key with no arguments, so a base text with <c>{0}</c> would throw inside
    /// <c>string.Format</c> and log an error on every launch. A page created here is initialised once, from the finished
    /// text. Vanilla's tutorial pages are added the same way (ModSystemTutorial).</summary>
    void AddGuidePage(List<GuiHandbookPage> pages)
    {
        guidePage = new GuiHandbookTextPage { pageCode = GuidePageCode, Title = GuideTitle };
        ComposeGuidePage(capi.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().Config);
        pages.Add(guidePage);
    }

    /// <summary>Write the overview's text from <paramref name="config"/> and re-run <c>Init</c>, which rebuilds the
    /// rendered text. A no-op until the handbook has created the page. <c>Init</c> passes a text under 255 characters
    /// through <c>Lang.Get</c>, which hands back a string that is no key unchanged; the composed text has no braces
    /// left to trip its formatting.</summary>
    void ComposeGuidePage(ImmersiveWoodworkingConfig config)
    {
        if (guidePage == null) return;
        guidePage.Text = GuidePageText(config.EnableBarkChain, config.EnableSawmill, config.EnableChopper);
        guidePage.Init(capi);
    }

    static string GuidePageText(bool bark, bool sawmill, bool chopper) => Tidy(Lang.Get(GuideText,
        bark ? Lang.Get(Domain + ":craftinginfo-woodworking-frag-bark") : "",
        sawmill ? Lang.Get(Domain + ":craftinginfo-woodworking-frag-sawmill") : "",
        chopper ? Lang.Get(Domain + ":craftinginfo-woodworking-frag-chopper") : ""));

    /// <summary>Which sentence says what a log splits into on the block, and where it lands. The half-log stage is not
    /// optional prose but a fork: with it off a whole log goes straight to firewood, so the page needs the OTHER sentence
    /// rather than one sentence fewer. <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/> moves the drop from the
    /// block to the chopper's feet, and both sentences name the spot, so each has an at-player twin. Every key is
    /// written out whole so tools/check-lang.py can resolve it.</summary>
    static string ChopSplitFragment(bool halfLog, bool atPlayer)
    {
        if (halfLog) return atPlayer ? Domain + ":choppingblock-handbook-frag-halflog-atplayer" : Domain + ":choppingblock-handbook-frag-halflog";
        return atPlayer ? Domain + ":choppingblock-handbook-frag-direct-atplayer" : Domain + ":choppingblock-handbook-frag-direct";
    }

    /// <summary>Where the debarked log lands after a strip: beside the pair by default, at the debarker's feet with
    /// <see cref="ImmersiveWoodworkingConfig.DropsAtPlayer"/> on. A fragment rather than prose in the page text only
    /// because of that switch; with it off the page composes to exactly the sentence it always had.</summary>
    static string DebarkedDropFragment(bool atPlayer)
        => atPlayer ? Domain + ":sawhorse-handbook-frag-debarked-atplayer" : Domain + ":sawhorse-handbook-frag-debarked";

    /// <summary>Which sentence describes the rip tool on this config. Three genuinely different worlds, not shades of
    /// one: the pit saw as the fast tool with ordinary saws behind it, the pit saw alone
    /// (<see cref="ImmersiveWoodworkingConfig.AllowVanillaSaw"/> off), and no pit saw at all
    /// (<see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/>), where an ordinary saw is the only rip tool there is and
    /// works regardless of the other flag.</summary>
    static string RipToolFragment(ImmersiveWoodworkingConfig config)
    {
        if (config.RemovePitSaw) return Domain + ":sawhorse-handbook-frag-nopitsaw";
        return config.AllowVanillaSaw ? Domain + ":sawhorse-handbook-frag-bothsaws" : Domain + ":sawhorse-handbook-frag-pitsawonly";
    }

    /// <summary>Write a composed page into the section carrying <paramref name="title"/>, if this collectible has one.
    /// Returns whether it did, so the caller can count. Never appends: a page we compose is a page whose JSON already
    /// declares the section, and an appended second copy would render the whole text twice.</summary>
    static bool WriteComposed(CollectibleBehaviorHandbookTextAndExtraInfo bh, string title, string composed)
    {
        ExtraHandbookSection[]? sections = bh.ExtraHandBookSections;
        if (sections == null) return false;

        foreach (ExtraHandbookSection s in sections)
        {
            if (s != null && s.Title == title) { s.Text = composed; return true; }
        }
        return false;
    }

    /// <summary>"Which woods drop this bark" for the bark item <paramref name="itemPath"/>: one placed log per species
    /// that can give it, filtered to the woods this install actually ships, likeliest first — the logs
    /// <see cref="HandbookBarkSourcesBehavior"/> draws as clickable icons. A special kind lists the species the live
    /// table gives it to; ordinary bark lists every wood whose roll leaves it a share with at least one debarking tool
    /// (<see cref="GivesGenericBark"/>). Empty for a path that is no bark kind, and when no matching wood is present (a
    /// table a tree mod emptied, or a kind whose only species come from a mod that isn't installed): there is nothing
    /// true to show then.</summary>
    public List<Block> BarkSourceLogs(string itemPath, ImmersiveWoodworkingConfig config)
    {
        List<Block> result = new List<Block>();
        bool generic = itemPath == "bark-generic";
        string? kind = null;
        foreach ((string path, string entryKind) in BarkSourcePages)
            if (path == itemPath) kind = entryKind;
        if (kind == null && !generic) return result;

        BarkDropTable table = capi.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().BarkDrops;

        // Every species with a log here, table or not: a wood the table does not list rolls ordinary bark every time,
        // and gives no special kind, so the loop needs no second source for either list.
        List<(Block Log, float Chance, string Word)> woods = new List<(Block, float, string)>();
        foreach (KeyValuePair<string, Block> wood in WoodLogs())
        {
            string species = wood.Key;
            Block log = wood.Value;

            // A special kind: its chances summed, so a patch that lists the same kind twice for one species gives one
            // icon at the chance the roll really has (Roll walks both slices, and either lands on this kind).
            float special = 0f, chance = 0f;
            foreach (BarkDropTable.BarkDropEntry entry in table.ResolveDrops(species))
            {
                special += entry.Chance;
                if (entry.Kind == kind) chance += entry.Chance;
            }

            if (generic)
            {
                if (!GivesGenericBark(species, config)) continue;
                // Ordinary bark: the table remainder, what is left of the roll once every special slice is taken. Zero
                // for a wood whose specials take the whole table roll (oak and birch, 1.0), which still gives ordinary
                // bark to a weaker tool and so stands at the end of the row rather than off it.
                chance = System.Math.Max(0f, 1f - special);
            }
            else if (chance <= 0) continue;

            woods.Add((log, chance, WoodName.SpeciesWord(species, log.Code.Domain)));
        }

        // Likeliest first — the ORDER carries what the numbers used to say out loud ("most reliably from oak"), and
        // unlike a percentage it stays true for every debarking tool; the per-tool odds are one click away, on the log's
        // own page. Ties fall back to the localised word so two installs with the same table always draw the same row.
        woods.Sort((a, b) =>
        {
            int byChance = b.Chance.CompareTo(a.Chance);
            return byChance != 0 ? byChance : string.CompareOrdinal(a.Word, b.Word);
        });

        foreach ((Block log, float _, string _) in woods) result.Add(log);
        return result;
    }

    /// <summary>Whether a placed log of <paramref name="species"/> can give ordinary bark with ANY debarking tool this
    /// server allows: the bark spud, the axe and hammer, and the knife while
    /// <see cref="ImmersiveWoodworkingConfig.AllowKnifeDebark"/> is on, each at its live multiplier. False only where
    /// every tool's special odds take the whole roll (a config that raises the axe and the knife to the spud's 1.0 makes
    /// oak and birch such woods). Decided by <see cref="LogBarkRows"/> itself, rounding and all, so a log on the ordinary
    /// bark page is exactly a log whose own page shows ordinary bark for some tool. Like those rows it reads the base
    /// (copper) spud multiplier; a steel spud or the xSkills perk only shrinks the ordinary share, and a tool that never
    /// gives it is no reason to hide a wood another tool strips it from.</summary>
    bool GivesGenericBark(string species, ImmersiveWoodworkingConfig config)
    {
        if (RowsHaveGeneric(LogBarkRows(species, config.BarkChanceMultSpud, out _))) return true;
        if (RowsHaveGeneric(LogBarkRows(species, config.BarkChanceMultAxeHammer, out _))) return true;
        return config.AllowKnifeDebark && RowsHaveGeneric(LogBarkRows(species, config.BarkChanceMultKnife, out _));
    }

    static bool RowsHaveGeneric(List<(string ItemPath, int Pct)> rows)
    {
        foreach ((string itemPath, int _) in rows)
            if (itemPath == "bark-generic") return true;
        return false;
    }

    /// <summary>Attach the "Stripped from these woods" log icons to the bark pages: the mirror of
    /// <see cref="AttachLogBarkBehavior"/>, so a player can go from a bark to the tree that sheds it as easily as the
    /// other way round. The same <c>ICustomHandbookPageContent</c> seam, for the same reason:
    /// a VTML <c>&lt;itemstack&gt;</c> icon cannot be clicked. Idempotent like its twin, and attached unconditionally:
    /// <see cref="HandbookBarkSourcesBehavior"/> reads <see cref="ImmersiveWoodworkingConfig.EnableBarkChain"/> when the
    /// page is drawn.</summary>
    void AttachBarkSourcesBehavior()
    {
        int attached = 0;
        foreach (string itemPath in BarkSourceItemPaths())
        {
            Item? item = capi.World.GetItem(new AssetLocation(Domain, itemPath));
            if (item == null || item.GetBehavior<HandbookBarkSourcesBehavior>() != null) continue;

            item.CollectibleBehaviors = item.CollectibleBehaviors.Append(new HandbookBarkSourcesBehavior(item));
            attached++;
        }

        if (attached > 0)
            capi.Logger.Notification("[immersivewoodworking] Attached the stripped-from-these-woods list to {0} bark type(s).", attached);
    }

    /// <summary>The bark items whose page names the woods they come from: every special kind, then ordinary bark.</summary>
    static IEnumerable<string> BarkSourceItemPaths()
    {
        foreach ((string itemPath, string _) in BarkSourcePages) yield return itemPath;
        yield return "bark-generic";
    }

    /// <summary>Attach the "When stripped" drop-list to every placed-log page — the REVERSE of the bark pages'
    /// "Stripped from these woods" list (comment #2, ModDB): a player reads a tree and learns what bark it sheds,
    /// rather than opening each bark page and scanning it for the species. Unlike the other sections this one is not
    /// text but real, clickable item ICONS (the same component the handbook's own drop/recipe panels use), which a VTML
    /// <c>&lt;itemstack&gt;</c> cannot be — so instead of an <see cref="ExtraHandbookSection"/> the log carries a
    /// <see cref="HandbookLogBarkBehavior"/> that appends its icons through <c>ICustomHandbookPageContent</c> when the
    /// page is drawn (author feedback 2026-09-22: bigger icons, no name text, click-through). Idempotent: a second
    /// Compose pass sees the behavior already present and skips. Attached unconditionally: the behavior itself draws
    /// nothing while <see cref="ImmersiveWoodworkingConfig.EnableBarkChain"/> is off, reading the config when the page
    /// is drawn rather than when it is attached.</summary>
    void AttachLogBarkBehavior()
    {
        int attached = 0;
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            // The same log family Backfill and WoodLogs match: the harvested/placed log the stations take, never
            // a grown trunk or resin variant (source-conventions.md 2).
            if (!CodeParts.Is(block.Code, 0, "log") || !CodeParts.Is(block.Code, 1, "placed")) continue;
            if (block.GetBehavior<HandbookLogBarkBehavior>() != null) continue; // already attached (2nd Compose pass)

            block.CollectibleBehaviors = block.CollectibleBehaviors.Append(new HandbookLogBarkBehavior(block));
            attached++;
        }

        if (attached > 0)
            capi.Logger.Notification("[immersivewoodworking] Attached the bark-when-stripped drop list to {0} placed-log type(s).", attached);
    }

    /// <summary>The bark drop rows for a placed log of <paramref name="species"/> stripped with a tool of chance
    /// multiplier <paramref name="mult"/>, likeliest-first, each an item path plus its whole-percent chance, closed by
    /// the ordinary-bark remainder — the data <see cref="HandbookLogBarkBehavior"/> renders as clickable icons, once
    /// per debarking tool. <paramref name="anySpecial"/> tells a species that yields a special bark from a generic-only
    /// one (multiplier-independent: it is whether the table lists a special at all), so the behavior shows the per-tool
    /// split only where the tool actually changes the outcome.
    ///
    /// <para><paramref name="mult"/> is the tool's <see cref="BlockEntitySawhorse.BarkChanceMultFor"/> value read LIVE
    /// off the server-synced config (a bark spud 1.0, axe+hammer 0.75, a knife 0.5 by default); it scales each special
    /// chance, and the leftover is ordinary bark, exactly as <see cref="BarkDropTable.Roll"/> does. Live, so the numbers
    /// match this server — R20 forbids a HARDCODED config number in prose, and a live per-drop chance shown the way the
    /// engine shows its own drops is the deliberate exception the author asked for. The spud's steel metal-tier bonus is
    /// deliberately not modelled here: the row shows the base (copper) spud odds, the floor a better spud only lifts.</para></summary>
    public List<(string ItemPath, int Pct)> LogBarkRows(string species, float mult, out bool anySpecial)
    {
        BarkDropTable table = capi.ModLoader.GetModSystem<ImmersiveWoodworkingModSystem>().BarkDrops;

        List<BarkDropTable.BarkDropEntry> specials = new List<BarkDropTable.BarkDropEntry>();
        foreach (BarkDropTable.BarkDropEntry entry in table.ResolveDrops(species))
            if (entry.Chance > 0) specials.Add(entry);

        anySpecial = specials.Count > 0; // a property of the SPECIES, not of the tool multiplier

        // Likeliest first; ties by kind name so the printed order is stable across installs with the same table.
        specials.Sort((a, b) =>
        {
            int byChance = b.Chance.CompareTo(a.Chance);
            return byChance != 0 ? byChance : string.CompareOrdinal(a.Kind, b.Kind);
        });

        List<(string ItemPath, int Pct)> rows = new List<(string, int)>(specials.Count + 1);
        int specialPct = 0;
        foreach (BarkDropTable.BarkDropEntry entry in specials)
        {
            string? itemPath = KindItemPath(entry.Kind);
            if (itemPath == null) continue; // a kind with no display mapping — skip it, never show a raw code

            int pct = (int)System.Math.Round(entry.Chance * mult * 100.0);
            if (pct <= 0) continue;
            specialPct += pct;
            rows.Add((itemPath, pct));
        }

        // The generic remainder is whatever is left of 100% once the (scaled) special slices are taken — the table
        // keeps the base chances summing to <= 1 and mult is clamped to <= 1, so this never goes negative.
        int genericPct = 100 - specialPct;
        if (genericPct > 0) rows.Add(("bark-generic", genericPct));

        return rows;
    }

    /// <summary>The fresh item path a rolled special bark <paramref name="kind"/> drops as, or null for a kind this
    /// display does not map — defensive only, since <see cref="BarkDropTable"/> validates kinds at load.</summary>
    static string? KindItemPath(string kind)
    {
        foreach ((string itemPath, string entryKind) in BarkSourcePages)
            if (entryKind == kind) return itemPath;
        return null;
    }

    /// <summary>Every wood species this install has a placed LOG for, mapped to the one log variant that stands for it
    /// on the bark pages — the "does this species exist here" filter for the bark lists, the icon drawn there, and
    /// (through its code) the domain the species word is looked up in. Read off the block registry by SHAPE
    /// (<c>log-placed-{wood}-{rot}</c>, the same code the stations accept), never from a list of species, so a wood mod
    /// counts for free (source-conventions.md 2). Built once and kept: a client's registry is the server's and is fixed
    /// for the session.</summary>
    Dictionary<string, Block> WoodLogs()
    {
        if (woodLogs != null) return woodLogs;

        woodLogs = new Dictionary<string, Block>();
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (!CodeParts.Is(block.Code, 0, "log") || !CodeParts.Is(block.Code, 1, "placed")) continue;

            string? wood = CodeParts.At(block.Code, 2);
            if (wood == null) continue;
            if (!woodLogs.TryGetValue(wood, out Block? kept) || LogPageRank(block) > LogPageRank(kept)) woodLogs[wood] = block;
        }
        return woodLogs;
    }

    /// <summary>Which placed-log variant should stand for its species, so the icon clicks through to a page. The
    /// handbook mints a page from <see cref="CollectibleObject.GetHandBookStacks"/>, the very check asked here (a
    /// creative tab or <c>handbook.include</c>, and no <c>handbook.exclude</c>); vanilla's log has it only for the
    /// upright <c>ud</c> rotation, so <c>ns</c> and <c>we</c> have no page, and a wood mod that names its rotations
    /// differently still gets the variant that has one. A click on a variant without a page is dropped by the handbook
    /// without a word, hence the care. <c>ud</c> only breaks a tie, for a species a server hid from the handbook
    /// altogether: its icon is still drawn, it just leads nowhere.</summary>
    int LogPageRank(Block block)
    {
        int rank = block.GetHandBookStacks(capi)?.Count > 0 ? 2 : 0;
        if (CodeParts.Is(block.Code, 3, "ud")) rank++;
        return rank;
    }

    /// <summary>Close up what an empty substitution leaves behind: a doubled space where a sentence used to sit, a
    /// space before a line break, and a blank line that has become two. Everything else — the paragraph breaks the
    /// translator wrote — is left exactly as authored.</summary>
    static string Tidy(string text)
    {
        StringBuilder sb = new StringBuilder(text.Length);
        int blankRun = 0;

        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = CollapseSpaces(rawLine).TrimEnd();
            if (line.Length == 0)
            {
                // One blank line separates paragraphs; a second one only appears because a substitution vanished.
                if (++blankRun > 1) continue;
            }
            else blankRun = 0;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }

        return sb.ToString().Trim('\n');
    }

#if IW_DEBUG
    /// <summary>Sweep every flag combination the composed pages branch on and prove, in one startup line, the three
    /// things composition can silently get wrong: a placeholder left unfilled (a page whose text still shows
    /// <c>{0}</c>), a fragment key that does not exist (Lang answers a miss with the key itself, which would render as
    /// <c>immersivewoodworking:...</c> on the page), and a seam left by an empty substitution (a doubled space).
    /// Cheap enough to run flat out: eight combinations for the block, sixteen for the trestles, eight for the
    /// overview, all string work.
    ///
    /// <para>It checks the COMPOSITION, not the wording — no oracle here can know whether a sentence is true, which is
    /// what the live session is for. Stripped from a shipped build.</para></summary>
    internal void ComposeSelfCheck()
    {
        int cases = 0, bad = 0;
        string firstBad = "";

        void Check(string what, string text)
        {
            cases++;
            // Link targets carry the domain legitimately (handbook://item-immersivewoodworking:halflog), and testing them
            // flagged every sawhorse case and every half-log case as a miss. A missed key comes back as bare text, never
            // inside an href, so the targets go before that test.
            string prose = System.Text.RegularExpressions.Regex.Replace(text, "href=\"[^\"]*\"", "");
            string? fault = text.Contains("{") ? "unfilled placeholder"
                : prose.Contains(Domain + ":") ? "missing lang key"
                : text.Contains("  ") ? "double space"
                : text.Contains(" \n") ? "space before line break"
                : null;
            if (fault == null) return;
            bad++;
            if (firstBad.Length == 0) firstBad = what + ": " + fault;
        }

        // The fragment pickers under test take plain bools straight off the loop variables; only the trestles' three-way
        // tool choice goes through a config object, because RipToolFragment reads one.
        ImmersiveWoodworkingConfig probe = new ImmersiveWoodworkingConfig();
        foreach (bool halfLog in new[] { true, false })
        foreach (bool sticks in new[] { true, false })
        foreach (bool atPlayer in new[] { false, true })
        {
            Check($"block halfLog={halfLog} sticks={sticks} atPlayer={atPlayer}", Tidy(Lang.Get(ChoppingBlockText,
                Lang.Get(ChopSplitFragment(halfLog, atPlayer)),
                sticks ? Lang.Get(Domain + ":choppingblock-handbook-frag-sticks") : "")));
        }

        foreach (bool removePitSaw in new[] { false, true })
        foreach (bool vanillaSaw in new[] { true, false })
        foreach (bool knife in new[] { true, false })
        foreach (bool atPlayer in new[] { false, true })
        {
            probe.RemovePitSaw = removePitSaw;
            probe.AllowVanillaSaw = vanillaSaw;
            Check($"sawhorse noPitSaw={removePitSaw} vanillaSaw={vanillaSaw} knife={knife} atPlayer={atPlayer}", Tidy(Lang.Get(SawhorseText,
                Lang.Get(RipToolFragment(probe)),
                knife ? Lang.Get(Domain + ":sawhorse-handbook-frag-knife") : "",
                Lang.Get(DebarkedDropFragment(atPlayer)))));
        }

        foreach (bool bark in new[] { true, false })
        foreach (bool sawmill in new[] { true, false })
        foreach (bool chopper in new[] { true, false })
        {
            Check($"overview bark={bark} sawmill={sawmill} chopper={chopper}", GuidePageText(bark, sawmill, chopper));
        }

        if (bad == 0)
        {
            capi.Logger.Notification("[immersivewoodworking] Handbook composition self-check: {0} config combination(s) compose cleanly.", cases);
        }
        else
        {
            capi.Logger.Warning("[immersivewoodworking] Handbook composition self-check: {0} of {1} combination(s) are malformed, first: {2}.", bad, cases, firstBad);
        }
    }
#endif

    /// <summary>Runs of spaces down to one — the seam an empty substitution leaves mid-sentence.</summary>
    static string CollapseSpaces(string line)
    {
        if (line.IndexOf("  ", System.StringComparison.Ordinal) < 0) return line;

        StringBuilder sb = new StringBuilder(line.Length);
        bool lastWasSpace = false;
        foreach (char c in line)
        {
            if (c == ' ')
            {
                if (lastWasSpace) continue;
                lastWasSpace = true;
            }
            else lastWasSpace = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
