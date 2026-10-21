using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ImmersiveWoodworking;

/// <summary>
/// The BARK DROP TABLE (BarkPlan/E02): which SPECIAL bark, if any, a fully-stripped log yields on the sawhorse,
/// keyed by wood species. Parsed once from the mod asset <c>config/barkdrops.json</c> in
/// <see cref="ImmersiveWoodworkingModSystem.AssetsLoaded"/> on BOTH sides — the <c>config</c> asset category is
/// <c>EnumAppSide.Universal</c>, so the file loads on each; the server rolls it (T02.03), the client builds the
/// handbook's "which woods drop this" list out of it (<see cref="WoodworkingHandbookInfo"/>, AuditFixPlan T09.01,
/// which is where that promise stopped being one). No network sync: the asset is identical on both sides.
///
/// Every stripped log gets ONE kind-roll here. How MANY bark of that kind drop is not this table's business: the count
/// lives in the config (<see cref="ImmersiveWoodworkingConfig.BarkPerLogFor"/>) since the asset's <c>countBySpecies</c>
/// map was retired (see <see cref="Load"/>). The roll (T02.03) walks a species' entries as CUMULATIVE intervals and
/// whatever probability is left over (<c>1.0 - sum(chance)</c>) is the ordinary <c>bark-generic</c>. So <c>generic</c>
/// is never stored here — it is the implicit REMAINDER — and a species with no entry (any foreign wood, or the
/// deliberately generic-only walnut/purpleheart/aged) resolves to an EMPTY list and thus always generic. That is why
/// unknown woods "just work": <see cref="ResolveDrops"/> hands back nothing special and never throws.
///
/// This type is the MODEL + PARSER + RESOLVER only (T02.02). The actual roll and the <c>kind → item</c> mapping are
/// T02.03. It NEVER throws on load: a missing/broken/empty asset degrades to <see cref="Empty"/> (all generic) with a
/// warning, the same "one bad part doesn't sink the mod" stance as the config-folder loader.
/// </summary>
public sealed class BarkDropTable
{
    // The special bark KINDS the table understands, as a case-insensitive set for the load-time membership check.
    // `generic` is deliberately NOT here — it is the implicit remainder, never an entry.
    static readonly HashSet<string> validKinds =
        new HashSet<string>(new[] { "tan", "birch", "bast", "medicinal" }, System.StringComparer.OrdinalIgnoreCase);

    /// <summary>The special bark KINDS the table understands (case-insensitive). <c>generic</c> is deliberately NOT
    /// here — it is the implicit remainder, never an entry. An entry naming any other kind is dropped at load with a
    /// warning, so the resolver only ever hands T02.03 a kind it can map to a bark item.</summary>
    public static IReadOnlyCollection<string> ValidKinds => validKinds;

    /// <summary>One special-bark possibility for a species: the <see cref="Kind"/> to drop and the fractional
    /// <see cref="Chance"/> (0..1) it is rolled. Immutable — the table is built once at load and only read afterwards.</summary>
    public readonly struct BarkDropEntry
    {
        /// <summary>The special bark kind, always lowercase and one of <see cref="ValidKinds"/>.</summary>
        public string Kind { get; }

        /// <summary>Probability this kind is rolled, in [0, 1] (after load-time clamping).</summary>
        public float Chance { get; }

        public BarkDropEntry(string kind, float chance) { Kind = kind; Chance = chance; }
    }

    // Species code (lowercase) -> its cleaned/validated special-bark entries, ORDER PRESERVED for the cumulative roll.
    // A species present with an EMPTY array is generic-only (walnut/purpleheart/aged) — kept so the loaded count and the
    // asset's on-record intent match; behaviourally identical to an absent species (both resolve to no special bark).
    readonly Dictionary<string, BarkDropEntry[]> bySpecies;

    static readonly BarkDropEntry[] NoEntries = new BarkDropEntry[0];

    /// <summary>An empty table — every species resolves to generic bark. The mod system's field is initialised to this
    /// so it is never null before <c>AssetsLoaded</c>, and <see cref="Load"/> returns it on any load failure.</summary>
    public static readonly BarkDropTable Empty = new BarkDropTable(new Dictionary<string, BarkDropEntry[]>());

    BarkDropTable(Dictionary<string, BarkDropEntry[]> bySpecies)
    {
        this.bySpecies = bySpecies;
    }

    /// <summary>Number of species keys loaded from the table (including generic-only ones that carry no special entry).</summary>
    public int SpeciesCount => bySpecies.Count;

    /// <summary>The (lowercase) species codes the table lists, for diagnostics — e.g. cross-checking which listed woods
    /// are actually present in this install. Enumeration only; the entries themselves are read via <see cref="ResolveDrops"/>.</summary>
    public IReadOnlyCollection<string> Species => bySpecies.Keys;

    /// <summary>The special-bark entries for <paramref name="species"/> (the lowercase wood code
    /// <see cref="BlockEntitySawhorse.GetWoodSpecies"/> returns), or an EMPTY list for an unknown / generic-only species.
    /// Never null. The remainder up to 1.0 across the returned entries is <c>bark-generic</c> (applied by the T02.03
    /// roll). The lookup is case-insensitive (input lowercased) to match the lowercased keys built at load.</summary>
    public IReadOnlyList<BarkDropEntry> ResolveDrops(string? species)
    {
        if (species != null && bySpecies.TryGetValue(species.ToLowerInvariant(), out BarkDropEntry[]? entries)) return entries;
        return NoEntries;
    }

    // ── Roll (T02.03) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>immersivewoodworking:</c> item CODE (path only, no domain) a rolled bark <paramref name="kind"/>
    /// drops as — the fixed <c>kind → item</c> mapping the <c>barkdrops.json</c> header documents. FRESH bark is always
    /// the GREEN state where one exists (tan and birch dry over 120h, T01.04), so <c>tan → bark-tan-green</c> and
    /// <c>birch → bark-birch-green</c>; bast and medicinal have a single state. A null kind — the generic REMAINDER of
    /// the roll — or, defensively, any unrecognised kind, maps to the ordinary <c>bark-generic</c>, so EVERY strip
    /// yields a bark. All bark items register in the mod's own domain, so the caller wraps this in an
    /// <c>immersivewoodworking:</c> <see cref="AssetLocation"/>.</summary>
    public static string ItemCodeForKind(string? kind) => kind switch
    {
        "tan"       => "bark-tan-green",
        "birch"     => "bark-birch-green",
        "bast"      => "bark-bast",
        "medicinal" => "bark-medicinal",
        _           => "bark-generic", // null = the generic remainder; any other value defensively → generic
    };

    /// <summary>Roll ONE bark KIND for a fully-stripped log of <paramref name="species"/> at the BASE table odds — the
    /// unbiased pick (a debark tool at the spud reference). A thin overload of
    /// <see cref="Roll(string?, System.Random, double)"/> with a <c>chanceMultiplier</c> of 1.0; kept as the two-arg
    /// entry point so the T02.03 behaviour (and the standalone roll harness) are unchanged.</summary>
    public string? Roll(string? species, System.Random rand) => Roll(species, rand, 1.0);

    /// <summary>Roll ONE bark KIND for a fully-stripped log of <paramref name="species"/> from a single uniform draw of
    /// <paramref name="rand"/> — the pick (T02.03) behind every completed strip — with every special-bark chance scaled
    /// by <paramref name="chanceMultiplier"/> (T02.05: the debark tool tier — <c>&gt; 1</c> lifts the special kinds,
    /// <c>&lt; 1</c> favours the generic remainder, <c>1.0</c> = the base table). Walks the species' entries
    /// (<see cref="ResolveDrops"/>) as CUMULATIVE probability intervals over the SCALED chances: the draw in [0, 1) falls
    /// into at most one entry's slice → that entry's <see cref="BarkDropEntry.Kind"/>; a draw past the last slice (the
    /// leftover generic remainder) returns null = the ordinary <c>bark-generic</c>. The multiplier is applied to the
    /// CHANCES, so a species with no entries (unknown / generic-only) still always returns null regardless of the
    /// multiplier — a better tool never conjures special bark where the table lists none. A scaled cumulative may exceed
    /// 1.0 (a high multiplier over a high base chance): the first slice reaching ≥ 1.0 captures every remaining draw, the
    /// natural TOP clamp (so oak's <c>tan 1.0</c> stays a certain 100% at any multiplier ≥ 1). A non-positive multiplier
    /// collapses all special chances to 0 → always generic. Consumes EXACTLY ONE draw. Order matters (the
    /// authored/preserved order), so multi-kind species like willow keep their intended slices.
    ///
    /// SERVER-ONLY by contract: <see cref="BlockEntitySawhorse.CompleteDebark"/> calls this on
    /// <c>Api.Side == EnumAppSide.Server</c> and the resulting bark travels to clients as a normal ItemEntity, so there
    /// is NO client/server determinism concern (unlike the cross-side visual jitter that must never touch
    /// <c>World.Rand</c>) — <c>Api.World.Rand</c> is the correct, idiomatic RNG here, the very one
    /// <see cref="BlockEntitySawhorse.SpawnEjectedItem"/> already uses for the drop's along-jitter.</summary>
    public string? Roll(string? species, System.Random rand, double chanceMultiplier)
    {
        IReadOnlyList<BarkDropEntry> entries = ResolveDrops(species);
        if (entries.Count == 0) return null; // unknown / generic-only species → the ordinary generic bark (any multiplier)

        double mult = chanceMultiplier > 0.0 ? chanceMultiplier : 0.0; // guard non-positive → special chances collapse to generic
        double roll = rand.NextDouble(); // [0, 1)
        double cumulative = 0.0;
        foreach (BarkDropEntry e in entries)
        {
            cumulative += e.Chance * mult;         // scaled slice; a cumulative reaching ≥ 1.0 captures every remaining draw (top clamp)
            if (roll < cumulative) return e.Kind;  // fell inside this kind's (scaled) slice
        }
        return null; // past every scaled special slice → the generic remainder
    }

    // ── Parsing ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Parse, validate and return the bark drop table from <paramref name="loc"/>
    /// (<c>immersivewoodworking:config/barkdrops.json</c>). NEVER throws and never returns null: a missing asset, a
    /// parse error, or a table with no <c>bySpecies</c> object each yield <see cref="Empty"/> with a warning, so a broken
    /// or absent file degrades to "every log gives generic bark" rather than aborting mod load. Validation per species:
    /// each entry's chance is clamped to [0, 1] and an unknown-kind, kind-less, chance-less or zero-chance entry is
    /// dropped; every drop except the authored zero is WARNED with the species, the entry index and the offending key
    /// (AuditFixPlan T08.07 — no build configuration validates this asset, so the log is the only place a typo can
    /// surface); if the surviving chances sum to more than 1.0 they are scaled down proportionally (warned) so the
    /// generic remainder can't go negative. Logs the loaded species count on success. Called on BOTH sides (the asset is
    /// universal); parses via <see cref="IAsset.ToObject{T}"/>, the same lenient JSON path the mod's recipe templates
    /// use — the authored asset's <c>//</c> comments and unquoted keys, and the comment-stripped shipped copy, both parse.</summary>
    public static BarkDropTable Load(ICoreAPI api, AssetLocation loc)
    {
        IAsset? asset = api.Assets.TryGet(loc);
        if (asset == null)
        {
            api.Logger.Warning("[immersivewoodworking] Bark drop table {0} not found; every stripped log will yield generic bark.", loc);
            return Empty;
        }

        BarkDropTableJson? data;
        try
        {
            data = asset.ToObject<BarkDropTableJson>();
        }
        catch (System.Exception e)
        {
            api.Logger.Warning("[immersivewoodworking] Could not parse bark drop table {0}: {1}; every stripped log will yield generic bark.", loc, e.Message);
            return Empty;
        }

        // countBySpecies (1.3.2) is retired: the per-species bark COUNT moved to the config (bark.json,
        // BarkPerLogBySpecies), because this asset listed every vanilla species at an explicit 3 that beat the global
        // BarkPerLog, and raising that setting did nothing for oak. A stale copy of the asset that still carries the map
        // is told where it went instead of being lumped in with the typos below.
        if (data?.Unknown != null && data.Unknown.Remove("countBySpecies"))
            api.Logger.Warning("[immersivewoodworking] Bark drop table {0}: 'countBySpecies' is no longer read from this file. Per-species bark counts moved to the config: BarkPerLogBySpecies in ModConfig/ImmersiveWoodworking/bark.json.", loc);

        // Name every key the DTO did not recognise BEFORE the bySpecies check, so a misspelled ROOT key ('bySpecie') is
        // reported by its own name and not merely as "no bySpecies object" (AuditFixPlan T08.07, F-L7-23).
        WarnUnknownKeys(api, loc, "the table root", data?.Unknown);

        if (data?.BySpecies == null)
        {
            api.Logger.Warning("[immersivewoodworking] Bark drop table {0} has no 'bySpecies' object; every stripped log will yield generic bark.", loc);
            return Empty;
        }

        Dictionary<string, BarkDropEntry[]> table = new Dictionary<string, BarkDropEntry[]>(data.BySpecies.Count);
        int withEntries = 0;
        foreach (KeyValuePair<string, BarkDropEntryJson[]?> kv in data.BySpecies)
        {
            string species = kv.Key?.Trim().ToLowerInvariant() ?? "";
            if (species.Length == 0) continue; // a blank/whitespace species key is meaningless — skip it (Trim matches the entry-kind handling)

            if (table.ContainsKey(species))
            {
                api.Logger.Warning("[immersivewoodworking] Bark drop table {0} lists species '{1}' more than once; keeping the first.", loc, species);
                continue;
            }

            BarkDropEntry[] entries = CleanEntries(api, loc, species, kv.Value);
            table[species] = entries;
            if (entries.Length > 0) withEntries++;
        }

        // Loaded-count line on BOTH sides (DoD: the species count is visible in the log at startup). Kept at
        // Notification level — one concise line, mirroring the mod's other startup summaries.
        api.Logger.Notification(
            "[immersivewoodworking] Loaded bark drop table: {0} wood species ({1} with special-bark chances, {2} generic-only).",
            table.Count, withEntries, table.Count - withEntries);

        return new BarkDropTable(table);
    }

    /// <summary>Turn one species' raw JSON entries into a clean, validated array (see <see cref="Load"/> for the rules).
    /// Returns an empty array for a null/empty input or when every entry was dropped — that species is then generic-only.
    /// Order is preserved so the T02.03 roll's cumulative intervals match the authored sequence.
    ///
    /// <para>Every entry DROPPED here is named in the log (AuditFixPlan T08.07, F-L7-23), with the species and the
    /// entry's index: no build configuration validates this file, so a typo in a key is only ever catchable at load, and
    /// a silent drop is indistinguishable from a species the author meant to leave generic. The single deliberate
    /// silence is an authored <c>chance: 0</c> — zeroing a kind is how a patch switches it off, and
    /// <see cref="BarkDropEntryJson.Chance"/> is nullable precisely so a MISSING (or misspelled) <c>chance</c> key is
    /// distinguishable from that.</para></summary>
    static BarkDropEntry[] CleanEntries(ICoreAPI api, AssetLocation loc, string species, BarkDropEntryJson[]? raw)
    {
        if (raw == null || raw.Length == 0) return NoEntries;

        List<BarkDropEntry> kept = new List<BarkDropEntry>(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            BarkDropEntryJson? entry = raw[i];
            if (entry == null)
            {
                api.Logger.Warning("[immersivewoodworking] Bark drop table {0}: species '{1}' entry #{2} is null; ignoring it.", loc, species, i);
                continue;
            }

            // Unrecognised keys first: they are the likeliest reason whatever this entry is missing went missing.
            WarnUnknownKeys(api, loc, "species '" + species + "' entry #" + i, entry.Unknown);

            string kind = entry.Kind?.Trim() ?? "";
            if (kind.Length == 0)
            {
                api.Logger.Warning("[immersivewoodworking] Bark drop table {0}: species '{1}' entry #{2} names no bark kind; ignoring that entry (an entry needs both keys: kind and chance).", loc, species, i);
                continue;
            }

            if (!validKinds.Contains(kind))
            {
                api.Logger.Warning("[immersivewoodworking] Bark drop table {0}: species '{1}' entry #{2} names unknown bark kind '{3}'; ignoring that entry (valid kinds: tan, birch, bast, medicinal).", loc, species, i, kind);
                continue;
            }

            if (entry.Chance == null)
            {
                api.Logger.Warning("[immersivewoodworking] Bark drop table {0}: species '{1}' entry #{2} ('{3}') carries no 'chance'; ignoring that entry (chance is a fraction 0.0-1.0).", loc, species, i, kind);
                continue;
            }

            float chance = GameMath.Clamp(entry.Chance.Value, 0f, 1f);
            // Drop anything that never meaningfully fires so the T02.03 cumulative roll stays clean: a zero/negative
            // chance (negatives were clamped to 0), or a NaN (unreachable via the default JSON parser, which throws on a
            // NaN token → Empty table, but guarded so no float value can ever survive into the interval math). SILENT,
            // unlike the drops above: an authored 0 is a patch turning a kind off, not a mistake (T08.07).
            if (float.IsNaN(chance) || chance <= 0f) continue;
            kept.Add(new BarkDropEntry(kind.ToLowerInvariant(), chance));
        }

        if (kept.Count == 0) return NoEntries;

        // Guard the generic remainder: if the special chances sum to more than 1.0 (a mis-authored or over-patched
        // table) scale them down proportionally so they sum to exactly 1.0 and generic bark is 0 — never a NEGATIVE
        // remainder that the roll couldn't represent. Proportional scaling keeps the special kinds' relative odds intact.
        float sum = 0f;
        foreach (BarkDropEntry e in kept) sum += e.Chance;
        if (sum > 1f + 1e-4f)
        {
            api.Logger.Warning("[immersivewoodworking] Bark drop table {0}: species '{1}' special-bark chances sum to {2:0.###} (>1.0); clamping proportionally so generic bark is 0.", loc, species, sum);
            float scale = 1f / sum;
            for (int i = 0; i < kept.Count; i++)
                kept[i] = new BarkDropEntry(kept[i].Kind, kept[i].Chance * scale);
        }

        return kept.ToArray();
    }

    /// <summary>Name, in one warning, every JSON property the DTO did not recognise at <paramref name="where"/>
    /// (AuditFixPlan T08.07, F-L7-23). Newtonsoft DISCARDS an unknown property without a word, so without the
    /// <c>[JsonExtensionData]</c> bag that feeds this, a misspelled key (<c>chanse</c>) is indistinguishable from an
    /// absent one and the whole entry disappears unexplained. A no-op on a healthy file: the bag stays empty, and the
    /// asset's <c>//</c> comments never reach it (the reader drops comments, and the shipped copy is stripped anyway).</summary>
    static void WarnUnknownKeys(ICoreAPI api, AssetLocation loc, string where, IDictionary<string, JToken>? unknown)
    {
        if (unknown == null || unknown.Count == 0) return;
        api.Logger.Warning(
            "[immersivewoodworking] Bark drop table {0}: {1} carries unrecognised key(s): {2}. Ignored - check the spelling (an entry takes kind and chance; the root takes bySpecies).",
            loc, where, string.Join(", ", unknown.Keys));
    }

    // Newtonsoft DTO mirroring config/barkdrops.json ({ bySpecies: { <species>: [ {kind, chance} ] } }). A JSON null
    // array value deserializes to a null entry list (CleanEntries treats it as generic-only). Property names bind
    // case-insensitively; annotated explicitly for clarity. The retired countBySpecies has no property on purpose: it
    // lands in the root's Unknown bag, where Load picks it out by name.
    //
    // Both halves carry a [JsonExtensionData] bag (AuditFixPlan T08.07, F-L7-23): it is the only place a key the DTO
    // does not know survives long enough to be NAMED in the log, since Newtonsoft otherwise throws it away in silence.
    // Empty on a healthy file, so it costs nothing to keep.
    sealed class BarkDropTableJson
    {
        [JsonProperty("bySpecies")]
        public Dictionary<string, BarkDropEntryJson[]?>? BySpecies { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken>? Unknown { get; set; }
    }

    sealed class BarkDropEntryJson
    {
        [JsonProperty("kind")]
        public string? Kind { get; set; }

        /// <summary>NULLABLE on purpose (T08.07): null is what separates "the <c>chance</c> key is missing or
        /// misspelled" — a mistake <see cref="CleanEntries"/> reports — from an authored <c>chance: 0</c>, a patch
        /// switching a kind off, which is dropped in silence. A non-nullable float delivered both as 0.</summary>
        [JsonProperty("chance")]
        public float? Chance { get; set; }

        [JsonExtensionData]
        public IDictionary<string, JToken>? Unknown { get; set; }
    }
}
