using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace ImmersiveWoodworking;

public partial class ImmersiveWoodworkingModSystem : ModSystem
{
    const string ConfigFile = "immersivewoodworking.json"; // legacy single file (pre-E04); still referenced by the migration (T04.03)
    const string ConfigFolder = "ImmersiveWoodworking";    // per-file config folder under ModConfig/ (ImprovementsPlan E04/T04.02)
    const string ConfigMiscFile = "misc.json";             // fallback file for any field not claimed by a group below
    const string ConfigChannel = "immersivewoodworking";

    // DISK layout for the config (E04/T04.02): the flat config CLASS stays ONE object in memory and ONE ProtoBuf network
    // packet; only its on-disk storage is split into thematic files. Each field is named EXPLICITLY (not taken by
    // declaration order) so re-ordering a field in the class can never silently move it to another file. A field missing
    // from this table falls into misc.json and the startup self-check (VerifyConfigGroups) warns, so a newly added field
    // is never silently lost on the next save. Drop yields (the manual chopping/sawing amounts + the automated-station
    // overrides added in T04.04) live together in drops.json so each manual field sits beside its -1-inherit override;
    // flotsam drops stay in flotsam.json (no automated counterpart). The *Effective accessors are [JsonIgnore], so they
    // never serialize into any file and the self-check never sees them.
    static readonly (string file, string[] fields)[] ConfigGroups = new (string, string[])[]
    {
        ("general.json", new[]
        {
            "EnableSawmill", "EnableChopper", "EnableBarkChain",
            "AutoRestock", "DropsAtPlayer", "ScaleWorkSpeedByToolTier", "DisableVanillaFirewoodCrafting",
            "DisableVanillaPlankCrafting", "DisableVanillaDebarkCrafting", "DisableVanillaLogTannin", "KeepTraitRecipes",
            "AllowVanillaSaw", "RemovePitSaw", "CraftableSawhorse", "LogRecoverThreshold",
        }),
        ("chopping.json", new[]
        {
            "HalfLogChopStage", "AllowFirewoodToSticks", "ChopSeconds", "ChopSpeedMultiplier",
            "FirewoodChopSpeedMultiplier", "HalfLogChopSpeedMultiplier", "MaulChopSpeedMultiplier", "ChopProgressJitter",
            "AxeDurabilityPerLog", "ChopRestockDelaySeconds",
        }),
        ("flotsam.json", new[]
        {
            "FlotsamBreakSeconds", "FlotsamFirewoodLarge", "FlotsamSticksLarge", "FlotsamFirewoodNormal",
            "FlotsamSticksNormal", "FlotsamFirewoodSmall", "FlotsamSticksSmall",
        }),
        ("sawing.json", new[]
        {
            "SawSeconds", "SawDurabilityPerStage", "VanillaSawSpeedMultiplier", "SawRestockDelaySeconds",
        }),
        ("debarking.json", new[]
        {
            "DebarkSeconds", "AllowKnifeDebark", "KnifeDebarkSpeedMultiplier", "AxeHammerDebarkSpeedMultiplier",
            "DebarkDurabilityPerLog", "DebarkRestockDelaySeconds",
        }),
        ("automation.json", new[]
        {
            "SawmillSpeed", "SawmillMinTorque", "SawmillResistance", "SawmillBladeWearMultiplier",
            "ChopperSpeed", "ChopperMinTorque", "ChopperResistance", "ChopperHeadWearMultiplier",
        }),
        // Drop yields for BOTH tiers side by side (ImprovementsPlan E04/T04.04): each manual field immediately followed
        // by its automated override (default -1 = inherit the manual value). The MANUAL fields moved here out of
        // chopping.json / sawing.json so the inherit relationship is visible in one file; their *Effective accessors
        // are [JsonIgnore] and never serialize here. Flotsam drops stay in flotsam.json (no automated counterpart).
        ("drops.json", new[]
        {
            "FirewoodPerLog", "ChopperFirewoodPerLog",
            "FirewoodDropCount", "ChopperFirewoodDropCount",
            "SticksPerFirewood", "ChopperSticksPerFirewood",
            "SticksDropCount", "ChopperSticksDropCount",
            "PlanksPerLog", "SawmillPlanksPerLog",
            "PlankDropCount", "SawmillPlankDropCount",
            "BeamsPerDebarkedLog", "SawmillBeamsPerDebarkedLog",
            "BeamDropCount", "SawmillBeamDropCount",
        }),
        ("integration.json", new[]
        {
            "SharpnessSpeedFloor", "ChopSharpnessCeiling",
            "XSkillsIntegration", "XSkillsSawXpPerSection", "XSkillsDebarkXpPerStrip", "XSkillsChopXpPerWorkpiece",
            "XSkillsFirewoodChopXp", "XSkillsCoopXpFullToBoth", "XSkillsSawyerYield", "XSkillsSplitterYield", "XSkillsCarefulSawyer",
            "XSkillsSwiftsawSpeed", "XSkillsSwiftsawMaxChopSpeedBonus", "XSkillsSwiftsawMaxSawSpeedBonus",
            "SeraphLevelingIntegration",
        }),
        // The bark-system numbers that used to be baked into the code (BarkPlan/E08, T08.01): how many bark a stripped
        // log drops, the debark tool-tier bonus to the special-bark roll, the tar rig's capacity / per-birch fill /
        // opening yield, and the bark drying time. The tannin barrel + poultice recipe numbers stay in their own
        // editable assets (deliberate scope line), so they are NOT config fields and do not appear here.
        ("bark.json", new[]
        {
            "BarkPerLog", "BarkPerLogBySpecies",
            "BarkChanceMultKnife", "BarkChanceMultAxeHammer", "BarkChanceMultSpud", "BarkChanceMultSpudSteelBonus",
            "TarPotCapacityPoints", "TarBirchPointsWet", "TarBirchPointsDry", "TarYield", "BarkDryHours",
        }),
    };

    // The chopping block's grid recipe lives as an enabled:false TEMPLATE (RecipeLoader skips it) that
    // RegisterChoppingBlockRecipes clones once per discovered wood species across every domain (ChoppingBlockRedesignPlan T04.02). The
    // template supplies the tag-based axe ingredient + the 2x1 grid shape; the per-species log input and the
    // output's wood/woodDomain attributes are stamped in code. ChoppingBlockCode is the recipe output (the
    // single, variant-less data-block — its species rides in the stack attributes, not the code).
    static readonly AssetLocation ChoppingBlockRecipeTemplate = new AssetLocation("immersivewoodworking", "recipes/grid/choppingblock.json");
    static readonly AssetLocation ChoppingBlockCode = new AssetLocation("immersivewoodworking", "choppingblock");

    // The OPTIONAL hand-craft for the sawhorse trestle (stick + rope + stick): an enabled:false template the
    // RecipeLoader skips, registered in code (RegisterSawhorseCraftingRecipe) only when Config.CraftableSawhorse
    // is on — the same flag that disables the in-world stick build. See recipes/grid/sawhorse.json.
    static readonly AssetLocation SawhorseRecipeTemplate = new AssetLocation("immersivewoodworking", "recipes/grid/sawhorse.json");
    // The recipe output, set explicitly in code: a bare "sawhorse" in the template would deserialize to game:sawhorse
    // on the manual ToObject+Resolve path (which, unlike the normal RecipeLoader, doesn't resolve bare codes to the
    // asset's own domain) and fail to resolve — exactly why ChoppingBlockCode above is also stamped in code.
    static readonly AssetLocation SawhorseBlockCode = new AssetLocation("immersivewoodworking", "sawhorse");

    // The five plank/beam-consuming sawmill grid recipes (master frame + the four mandatory parts) ship as
    // enabled:false TEMPLATES (RecipeLoader skips them), re-registered per wood DOMAIN in RegisterSawmillRecipes for
    // the SAME reason as the chopping block: a recipe wildcard is domain-pinned (game:plank-* never matches
    // wildcrafttree:plank-azobe), yet the sawhorses drop planks/beams in the LOG'S own domain — so a wood mod's own
    // products couldn't craft the powered tier from a static game:-pinned JSON. (stem → the per-domain recipe Name;
    // asset → the template lookup.)
    static readonly (string stem, string asset)[] SawmillRecipeTemplates = new (string stem, string asset)[]
    {
        ("sawmill_frame",      "recipes/grid/sawmill_frame.json"),
        ("sawmill_sash",       "recipes/grid/sawmill_sash.json"),
        ("sawmill_crankshaft", "recipes/grid/sawmill_crankshaft.json"),
        ("sawmill_levers",     "recipes/grid/sawmill_levers.json"),
        ("sawmill_carriage",   "recipes/grid/sawmill_carriage.json"),
    };

    // The automated chopper's plank/beam-consuming grid recipes — the master frame block + the two WOODEN item-parts
    // (drive/arm) — ship as enabled:false TEMPLATES (RecipeLoader skips them), re-registered per wood DOMAIN in
    // RegisterChopperRecipes for the SAME reason as the sawmill's (a recipe wildcard is domain-pinned, yet the sawhorses
    // drop planks/beams in the LOG'S own domain). The head `chopperhead-{metal}` is deliberately NOT here: it carries no
    // wood, so it ships as a PLAIN enabled recipe the vanilla RecipeLoader loads straight in-domain (exactly like
    // sawmill_blade.json), and metal mods' game-domain ingot/plate variants already work with no per-domain clone. The
    // clamp-bed is NOT a mod part at all — it is the existing hand `choppingblock` (colода), RMB-installed as the bed and
    // crafted by its own recipe (no chopper_bed template), user decision 2026-07-04; the optional flywheel was cut the
    // same day. (stem → the per-domain recipe Name; asset → lookup.)
    static readonly (string stem, string asset)[] ChopperRecipeTemplates = new (string stem, string asset)[]
    {
        ("chopper_frame", "recipes/grid/chopper_frame.json"),
        ("chopper_drive", "recipes/grid/chopper_drive.json"),
        ("chopper_arm",   "recipes/grid/chopper_arm.json"),
    };

    // Bark drop table (BarkPlan/E02): the sawhorse strip's species → special-bark table, loaded from the mod ASSET
    // (not the config) so tree mods patch it with a plain JSON patch and it's immune to the config rebuild
    // (ImprovementsPlan E04). The asset's `config` category is EnumAppSide.Universal, so it loads on both sides — the
    // server rolls it (T02.03), the client reads it for the handbook's "which woods drop this" list
    // (WoodworkingHandbookInfo.OwnPages, AuditFixPlan T09.01 - until then the client's copy was loaded and read by
    // nobody, F-L9-12). See BarkDropTable / LoadBarkDropTable.
    static readonly AssetLocation BarkDropTableAsset = new AssetLocation("immersivewoodworking", "config/barkdrops.json");

    public ImmersiveWoodworkingConfig Config { get; private set; } = new ImmersiveWoodworkingConfig();

    /// <summary>Raised on the CLIENT the moment the server's config has been adopted in place of the local defaults
    /// (the <see cref="ConfigSyncPacket"/> handler in <see cref="StartClientSide"/>). Anything that BAKES a config
    /// value into client-side state has to run again here, or it keeps showing what an unconfigured world would show:
    /// <see cref="ApplyBarkDryHours"/> already did that by hand, and <see cref="WoodworkingHandbookInfo"/> hangs its
    /// handbook prose off it (AuditFixPlan T09.01) because the packet's arrival and <c>LevelFinalize</c> are not
    /// ordered against each other — whichever lands second is the one that gets the live numbers.
    /// <see cref="System.Action"/> spelled out: <c>using System</c> would make <c>Func</c> ambiguous against
    /// <c>Vintagestory.API.Common</c> (memory <c>vs-api-common-shadows-func</c>).</summary>
    public event System.Action? ClientConfigApplied;

    // The parsed bark drop table, populated in AssetsLoaded on both sides. Never null (starts Empty = all-generic), so a
    // caller before AssetsLoaded, or after a broken/missing asset, safely resolves every species to generic bark.
    BarkDropTable barkDropTable = BarkDropTable.Empty;

    /// <summary>The species → special-bark drop table read from <c>config/barkdrops.json</c> (BarkPlan/E02). Empty
    /// (every species → generic bark) until <c>AssetsLoaded</c>; never null. The T02.03 roll reads it on the server, the
    /// handbook on the client.</summary>
    public BarkDropTable BarkDrops => barkDropTable;

    /// <summary>Heat probe behind green bark drying faster near a fire (<see cref="ItemIwGreenBark"/>). An instance
    /// field and not a static: each side has its own mod system, and in single-player both live in one process.</summary>
    internal BarkHeat BarkHeat { get; } = new BarkHeat();

    // Wood species (bare code + its domain) discovered from every worldproperties/block/wood across all domains,
    // populated once in AssetsLoaded (both sides) BEFORE any Block.OnLoaded. BlockChoppingBlock reads this for its
    // per-species creative/handbook stacks (ChoppingBlockRedesignPlan T03.02) and this mod system for the generic
    // grid recipes (T04.02). See DiscoverWoodSpecies.
    List<(string wood, string domain)> woodSpecies = new List<(string wood, string domain)>();

    /// <summary>The all-domain set of chopping-block wood species (species code + its domain) — see
    /// <see cref="DiscoverWoodSpecies"/>. Empty until AssetsLoaded; never null.</summary>
    public IReadOnlyList<(string wood, string domain)> WoodSpecies => woodSpecies;

    // HalfLogBlockPlan T01.02: bare species code -> the ONE domain the half-log slab paints and drops it from.
    // The block variant code (halflogslab-{wood}-...) can carry only the BARE code — a variant part can't hold a
    // colon — so where two domains ship the same species name this index is what resolves it back. Built on BOTH
    // sides (unlike the injection, which is server-only): the client needs it for texturing. See BuildWoodDomainIndex.
    Dictionary<string, string> woodDomains = new Dictionary<string, string>();

    /// <summary>Bare wood species code → its owning domain, for the half-log slab's variant codes
    /// (HalfLogBlockPlan T01.02). Empty until AssetsLoaded; never null. See <see cref="BuildWoodDomainIndex"/>.</summary>
    public IReadOnlyDictionary<string, string> WoodDomains => woodDomains;

    // AuditFixPlan T04.05: species that have a LOG BLOCK but appear in no worldproperties — vanilla writes four of
    // them as literal `states` on its own log assets (`log` adds `aged`; `debarkedlog` adds `aged`, `veryaged`,
    // `rotten`, `veryrotten`), and a wood mod can do the same. They are NOT in woodSpecies/woodDomains and must not
    // be: those two drive the chopping BLOCK's per-species recipes and creative stacks, where `aged` is excluded on
    // purpose (see RegisterGenericChoppingBlockRecipes). But a half-log is stamped from the LOG, so these species DO
    // reach the half-log slab — and without a variant of their own the slab could not be placed at all (F-PT-02).
    // Populated in AssetsLoaded and read by InjectWoodVariants alone. SERVER-ONLY in effect, and deliberately so:
    // blocktypes is a server-side asset category, so on a client this list stays empty — which costs nothing, because
    // the client receives the finished block registry over the wire and the only thing it would have used the list
    // for (the species → domain index) already answers `game` for all four, which is where they live.
    List<(string wood, string domain)> logOnlySpecies = new List<(string wood, string domain)>();

    // Metal-tier speed reference (ManualOpsBalancePlan E02): tool-family FirstCodePart (axe / saw / pitsaw / barkspud / any wood-mod tool)
    // → the MAX MiningSpeed[Wood] across every collectible in that family. Built once in AssetsFinalize (both sides,
    // when api.World.Collectibles is fully resolved) and read live by ToolTierSpeedFactor. Empty until then; never null.
    Dictionary<string, float> woodToolSpeedRef = new Dictionary<string, float>();

    // Metal-tier CHANCE floor anchor (BarkPlan E02/T02.05): the same tool-family FirstCodePart → the MIN MiningSpeed[Wood]
    // across that family. Paired with woodToolSpeedRef (the steel/ceiling anchor) so MetalTierBonusFraction can normalise a
    // tool's metal to [0 at the family's WEAKEST metal, 1 at steel] — the bark spud's drop bonus (copper spud = the 1.0
    // reference, a steel spud adds its full bonus) rides this, distinct from ToolTierSpeedFactor's [floor,1] SPEED factor.
    // Built in the SAME AssetsFinalize scan. Empty until then; never null.
    Dictionary<string, float> woodToolSpeedMin = new Dictionary<string, float>();

    // NON-COMBUSTIBLE wood species (HalfLogBlockPlan T03.01 follow-up): "{domain}:{wood}" for every species whose own
    // LOGS carry no burn at all — the general form of the exception the half-log SLAB hardcodes as
    // `halflogslab-petrified-*: null`. Built once in AssetsFinalize on BOTH sides (the client needs it for the tooltip
    // and the creative stacks, the server for the actual fuel). Empty until then; never null.
    // See BuildNonCombustibleWoodIndex / IsNonCombustibleWood.
    HashSet<string> nonCombustibleWoods = new HashSet<string>();

    public override void StartPre(ICoreAPI api)
    {
        // The config file is owned by the SERVER only (in single-player that's the integrated server,
        // which shares this process and the same ModConfig folder). Clients receive the authoritative
        // config over the network at join (StartServerSide -> PlayerJoin -> StartClientSide handler), so
        // they neither read nor write the file. Doing it on the client too would: (a) leave a stale
        // on-disk copy that differs from the server values actually in effect — confusing for anyone who
        // edits it on a server; and (b) make BOTH app-sides write the same path in single-player, a
        // redundant double-write that can race on the file. Editing the file in single-player still works:
        // the integrated server reads it here and syncs it to the client at join.
        if (api.Side != EnumAppSide.Server) return;

        // One-time migration from the pre-E04 single file (T04.03): if the legacy immersivewoodworking.json is present,
        // fold its values in and (after the folder is written) retire it to .bak. Returns whether the old file should be
        // backed up once the new folder exists. Runs BEFORE the folder load: on a fresh migration it reads the player's
        // old values into Config so StoreConfigFolder writes them out; when both the old file and the folder already
        // exist, the folder wins and the old file is only backed up.
        bool backupLegacy = PrepareLegacyMigration(api);

        try
        {
            LoadConfigFolder(api);
        }
        catch
        {
            api.Logger.Warning("[immersivewoodworking] Could not read config folder {0}, using defaults.", ConfigFolder);
        }

        // Clamp any out-of-range values (e.g. a hand-edited ChopSeconds = 0 that would divide-by-zero) before anything
        // reads them, so the rest of the mod can trust the config. ONE validation point over the fully-merged object,
        // run AFTER every file is loaded (E04/T04.02 — so a value in the last-loaded file is still validated). The
        // logger carries the T01.03 work-multiplier band-clamp corrections to the main log (the client re-sanitizes
        // without a logger, invariant I9, so each correction is reported once here on the server).
        Config.Sanitize(api.Logger);

        // (Re)write the folder so it stays in sync with the current schema: missing fields get their defaults added,
        // existing values are preserved (and any clamped values persisted), each in its own thematic file. Guarded so a
        // disk/IO error here can never abort mod startup.
        bool storeOk = false;
        try
        {
            StoreConfigFolder(api);
            storeOk = true;
        }
        catch
        {
            api.Logger.Warning("[immersivewoodworking] Could not write config folder {0}, keeping in-memory config.", ConfigFolder);
        }

        // Cheap startup self-check: every serialized field must be claimed by exactly one group (else it silently falls
        // to misc.json or is written twice). Warns per offending field; never fatal.
        VerifyConfigGroups(api);

        // Second startup self-check (AuditFixPlan T02.05): every serialized field must survive the CLIENT SYNC intact.
        // Warns per offending field; never fatal.
        VerifyConfigSyncRoundTrip(api);

        // Retire the legacy file to .bak ONLY once the new folder was written successfully — otherwise a failed folder
        // write would leave no primary config at all. On failure the old file stays as the working copy.
        if (backupLegacy && storeOk) BackupLegacyConfig(api);
    }

    // ── Config folder I/O (ImprovementsPlan E04/T04.02) ─────────────────────────────────────────────────────
    // The former single flat file + AddConfigSectionSpacing blank-line hack are replaced by a folder of thematic
    // files (ConfigGroups above). AddConfigSectionSpacing is GONE: each file is now short and self-labelling by its
    // name, so the blank-line-between-sections post-processing bought nothing and only added a fragile re-read/rewrite
    // of the file after StoreModConfig. Newtonsoft's default resolver serializes/populates public FIELDS (the shipped
    // flat file was field-keyed), and this class holds only primitives plus one string→int map
    // (BarkPerLogBySpecies), so no custom serializer settings are needed. That map is MERGED by PopulateObject, not
    // replaced: a key missing from the file keeps its initializer value (see the field's doc for why that is safe).

    /// <summary>Load the config from <c>ModConfig/ImmersiveWoodworking/</c> (E04/T04.02): each thematic file is read
    /// and its fields populated into the ONE shared <see cref="Config"/> object, so the in-memory class and the ProtoBuf
    /// network packet stay a single flat object and no <c>Config.X</c> read site changes. A missing file is not an error
    /// (its fields keep their defaults); a corrupt file is warned and skipped so one bad file can't stop the rest
    /// loading (the <c>StripMatchingRecipes</c> "one bad part doesn't sink the whole" principle). <c>misc.json</c> is
    /// read like any other group so a hand-moved field still loads. Server-side only (its sole caller returned on the
    /// client). An absent FOLDER means a fresh install: nothing loads, everything stays at defaults, and
    /// <see cref="StoreConfigFolder"/> then writes the defaults out. (The legacy single-file migration is T04.03.)</summary>
    void LoadConfigFolder(ICoreAPI api)
    {
        string dir = Path.Combine(GamePaths.ModConfig, ConfigFolder);
        if (!Directory.Exists(dir)) return;

        foreach ((string file, string[] _) in ConfigGroups)
            PopulateFromFile(api, Path.Combine(dir, file));
        PopulateFromFile(api, Path.Combine(dir, ConfigMiscFile));
    }

    /// <summary>Merge one config file's fields into the shared <see cref="Config"/> object (E04/T04.02). Absent file =
    /// no-op (fields keep their current/default values); a parse error is warned and swallowed so the remaining files
    /// still load. Uses <see cref="JsonConvert.PopulateObject(string, object)"/> so only the keys PRESENT in this file
    /// are written — every other field is left untouched.</summary>
    void PopulateFromFile(ICoreAPI api, string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            string text = File.ReadAllText(path);
            JsonConvert.PopulateObject(text, Config);
            WarnRetiredSpeedKeys(api, path, text);
        }
        catch
        {
            api.Logger.Warning("[immersivewoodworking] Could not read config file {0}, its fields keep their previous/default values.", Path.GetFileName(path));
        }
    }

    // Retired config keys (SpeedDecouplingPlan T01.03, extended in the speed-inversion revision): the absolute
    // *StepsPerLog keys AND the intermediate work-direction *WorkMultiplier keys were both replaced by the float
    // *Speed fields (higher = faster). The mod does not migrate keys (mod in development, AuditPlan DECISIONS
    // #6), so an old key is silently ignored by PopulateObject and its field resets to the speed=1 default — then
    // StoreConfigFolder rewrites the file WITHOUT it. This warns ONCE, on that first post-upgrade launch (the only launch
    // the key is still on disk), so an admin who had tuned the old key learns why their setting vanished rather than
    // finding it silently gone. (The *WorkMultiplier entries matter to anyone who ran the brief E01 build that shipped
    // them; a saved WorkMultiplier does NOT auto-invert to the new SawmillSpeed/ChopperSpeed field — it resets, and the
    // admin re-enters the reciprocal.)
    static readonly (string Old, string New)[] RetiredSpeedKeys =
    {
        ("SawmillStepsPerLog", nameof(ImmersiveWoodworkingConfig.SawmillSpeed)),
        ("ChopperStepsPerLog", nameof(ImmersiveWoodworkingConfig.ChopperSpeed)),
        ("SawmillWorkMultiplier", nameof(ImmersiveWoodworkingConfig.SawmillSpeed)),
        ("ChopperWorkMultiplier", nameof(ImmersiveWoodworkingConfig.ChopperSpeed)),
    };

    /// <summary>Warn once per retired speed key present in a just-loaded config file (see <see cref="RetiredSpeedKeys"/>).
    /// Best-effort and defensive: <paramref name="text"/> already parsed via PopulateObject above, so the re-parse here
    /// cannot really fail, but any hiccup is swallowed — a deprecation notice must never break config loading.</summary>
    void WarnRetiredSpeedKeys(ICoreAPI api, string path, string text)
    {
        try
        {
            JObject obj = JObject.Parse(text);
            foreach ((string oldKey, string newKey) in RetiredSpeedKeys)
                if (obj.ContainsKey(oldKey))
                    api.Logger.Warning(
                        "[immersivewoodworking] Config key '{0}' in {1} was retired (SpeedDecouplingPlan) - it no longer binds and is dropped on this save. Its replacement is the SPEED multiplier '{2}' (default 1.0, HIGHER = FASTER); set that instead. NOTE the direction inverted: a retired 'work' value did the opposite, so enter its RECIPROCAL (e.g. an old work multiplier of 2 = slower becomes speed 0.5).",
                        oldKey, Path.GetFileName(path), newKey);
        }
        catch { /* a deprecation notice must never break config loading */ }
    }

    /// <summary>Write the config back out to <c>ModConfig/ImmersiveWoodworking/</c> as thematic files (E04/T04.02):
    /// serialize the whole flat object once, then partition its members into each group's file by the explicit name
    /// table (<see cref="ConfigGroups"/>). Any member NOT claimed by a group is written to <c>misc.json</c> so a newly
    /// added field is never silently dropped (and the self-check warns about it); when nothing is orphaned the stale
    /// <c>misc.json</c> is removed so the folder stays clean. The folder is created if absent. Server-side only.
    /// Newtonsoft's default serializer includes public fields and honours <c>[JsonIgnore]</c>, so the <c>*Effective</c>
    /// accessors added in T04.04 stay out of both the files and the network packet.</summary>
    void StoreConfigFolder(ICoreAPI api)
    {
        string dir = Path.Combine(GamePaths.ModConfig, ConfigFolder);
        Directory.CreateDirectory(dir);

        JObject all = JObject.FromObject(Config);
        var claimed = new HashSet<string>();

        foreach ((string file, string[] fields) in ConfigGroups)
        {
            var group = new JObject();
            foreach (string f in fields)
                if (all.TryGetValue(f, out var v)) { group[f] = v; claimed.Add(f); }
            File.WriteAllText(Path.Combine(dir, file), group.ToString(Formatting.Indented));
        }

        // Fallback: any serialized member not placed by a group (a table gap for a new field). Written only when
        // non-empty so the folder isn't littered with an empty file; when a former orphan has since been tabled, the
        // now-stale misc.json is deleted. Its value survives the transition because LoadConfigFolder read it back in
        // BEFORE this save, so the field's group file now carries the same value.
        var misc = new JObject();
        foreach (JProperty p in all.Properties())
            if (!claimed.Contains(p.Name)) misc[p.Name] = p.Value;

        string miscPath = Path.Combine(dir, ConfigMiscFile);
        if (misc.Count > 0) File.WriteAllText(miscPath, misc.ToString(Formatting.Indented));
        else if (File.Exists(miscPath)) File.Delete(miscPath);
    }

    /// <summary>Startup self-check (E04/T04.02): confirm every serialized config member is claimed by EXACTLY ONE group
    /// in <see cref="ConfigGroups"/>. A member in NO group falls to <c>misc.json</c> (works, but the table should name
    /// it); a member in TWO groups is a table typo (it would be written to two files, the later position winning on the
    /// next load). Also flags a table entry that names no real serialized member (a misspelling in the table). Warns
    /// once per offending name; never fatal. Checks the actual serialized surface (<see cref="JObject"/> keys), so it
    /// catches a stray non-<c>[JsonIgnore]</c> property too, not just fields.</summary>
    void VerifyConfigGroups(ICoreAPI api)
    {
        var counts = new Dictionary<string, int>();
        foreach ((string _, string[] fields) in ConfigGroups)
            foreach (string f in fields)
                counts[f] = counts.TryGetValue(f, out int c) ? c + 1 : 1;

        foreach (KeyValuePair<string, int> kv in counts)
            if (kv.Value > 1)
                api.Logger.Warning("[immersivewoodworking] Config field {0} is listed in {1} groups (must be exactly 1) - fix ConfigGroups.", kv.Key, kv.Value);

        JObject all = JObject.FromObject(Config);
        foreach (JProperty p in all.Properties())
            if (!counts.ContainsKey(p.Name))
                api.Logger.Warning("[immersivewoodworking] Config field {0} is in no group - it will be written to {1}. Add it to ConfigGroups.", p.Name, ConfigMiscFile);

        foreach (string f in counts.Keys)
            if (!all.ContainsKey(f))
                api.Logger.Warning("[immersivewoodworking] ConfigGroups names {0}, which is not a serialized config field - typo?", f);
    }

    /// <summary>Startup self-check (AuditFixPlan T02.05): push the live config through the ACTUAL client-sync transport —
    /// the same <see cref="ConfigSyncPacket"/> envelope, the same ProtoBuf hop, the same JSON on both ends — and compare
    /// the result field by field against what went in. Warns per field that does not survive; never fatal.
    ///
    /// This exists because the transport it replaced failed SILENTLY and per-field: sending the config object itself
    /// under an implicit ProtoBuf contract dropped every member whose value equalled the CLR zero of its type, and the
    /// receiver's initializer supplied a different value in its place (F-L4-02). Nothing in the code said so and no log
    /// line showed it — a server could run for months with 45 settings that never reached a single client. A check that
    /// walks the SERIALIZED SURFACE rather than a hand-written field list is what keeps that from coming back: a field
    /// added to the config later is covered the moment it exists, with nothing to remember.
    ///
    /// Server-side (StartPre has already returned on the client) and once per launch, over ~84 primitives.</summary>
    void VerifyConfigSyncRoundTrip(ICoreAPI api)
    {
        ImmersiveWoodworkingConfig? received;
        try
        {
            // The same envelope and the same two serializers the real send/receive path uses (the engine adds only its
            // own framing around this), so a change to either end is exercised here rather than on someone's server.
            var sent = new ConfigSyncPacket { Json = JsonConvert.SerializeObject(Config) };
            using var wire = new MemoryStream();
            ProtoBuf.Serializer.Serialize(wire, sent);
            wire.Position = 0;
            ConfigSyncPacket back = ProtoBuf.Serializer.Deserialize<ConfigSyncPacket>(wire);
            received = JsonConvert.DeserializeObject<ImmersiveWoodworkingConfig>(back.Json);
        }
        catch (System.Exception e)
        {
            api.Logger.Warning("[immersivewoodworking] Config sync self-check could not run ({0}) - client config sync is UNVERIFIED this launch.", e.Message);
            return;
        }

        if (received == null)
        {
            api.Logger.Warning("[immersivewoodworking] Config sync self-check: the round-trip produced no config at all - clients would keep their local values.");
            return;
        }

        // Compare the serialized surfaces, not a field list: this catches a value that changed, a member that vanished
        // from the wire, and a stray non-[JsonIgnore] property alike — the same reasoning as VerifyConfigGroups.
        JObject before = JObject.FromObject(Config);
        JObject after = JObject.FromObject(received);
        int mismatches = 0;
        foreach (JProperty p in before.Properties())
        {
            JToken? mine = after[p.Name];
            if (mine != null && JToken.DeepEquals(p.Value, mine)) continue;
            mismatches++;
            api.Logger.Warning(
                "[immersivewoodworking] Config field {0} does not survive the client sync: server has {1}, a client would get {2}. Clients will behave as if it were never set.",
                p.Name, p.Value.ToString(), mine == null ? "<nothing - the field is not on the wire>" : mine.ToString());
        }

        // The envelope's SECOND member (T03.03) rides the same hop and is not part of the config's serialized
        // surface, so the loop above cannot see it. Check it here, for the reason this whole method exists: a wire
        // member that silently fails to arrive is exactly the failure mode F-L4-02 was. `sent`/`back` are out of
        // scope by now, so it takes its own tiny round trip — the arrays are three short strings at most.
        if (!BeamSkipWoodsSurvivesSync(out string beamDetail))
            api.Logger.Warning(
                "[immersivewoodworking] The beam skip list does not survive the client sync ({0}). Clients would allow a rip the server refuses. This is a MOD BUG, not a config error - please report it.",
                beamDetail);

        if (!ProductRemapSurvivesSync(out string remapDetail))
            api.Logger.Warning(
                "[immersivewoodworking] The product remap table does not survive the client sync ({0}). Clients would refuse a log the server accepts. This is a MOD BUG, not a config error - please report it.",
                remapDetail);

        if (mismatches > 0)
            api.Logger.Warning("[immersivewoodworking] Config sync self-check: {0} field(s) do not reach clients. This is a MOD BUG, not a config error - please report it.", mismatches);
    }

    /// <summary>Round-trip <see cref="BeamSkipWoodsWire"/> through the real envelope and report whether the same set
    /// comes back, ORDER-INSENSITIVELY (it is a set on both ends; only membership matters). Part of
    /// <see cref="VerifyConfigSyncRoundTrip"/>; true also when there is nothing to send, which is the ordinary answer
    /// on an install whose beam recipe excludes nothing.</summary>
    static bool BeamSkipWoodsSurvivesSync(out string detail)
    {
        detail = "";
        string[]? sentWoods = BeamSkipWoodsWire;
        string[]? backWoods;
        try
        {
            using var wire = new MemoryStream();
            ProtoBuf.Serializer.Serialize(wire, new ConfigSyncPacket { Json = "{}", BeamSkipWoods = sentWoods });
            wire.Position = 0;
            backWoods = ProtoBuf.Serializer.Deserialize<ConfigSyncPacket>(wire).BeamSkipWoods;
        }
        catch (System.Exception e) { detail = "the round trip threw: " + e.Message; return false; }

        HashSet<string> mine = new HashSet<string>(sentWoods ?? System.Array.Empty<string>(), System.StringComparer.Ordinal);
        HashSet<string> theirs = new HashSet<string>(backWoods ?? System.Array.Empty<string>(), System.StringComparer.Ordinal);
        if (mine.SetEquals(theirs)) return true;

        detail = $"server has [{string.Join(", ", mine)}], a client would get [{string.Join(", ", theirs)}]";
        return false;
    }

    /// <summary>Round-trip <see cref="ProductRemapWire"/> through the real envelope and report whether the same
    /// key→value table comes back. Part of <see cref="VerifyConfigSyncRoundTrip"/>; true also when there is nothing to
    /// send, the ordinary answer on an install with no species-renaming wood mod.</summary>
    static bool ProductRemapSurvivesSync(out string detail)
    {
        detail = "";
        string[]? sent = ProductRemapWire;
        string[]? back;
        try
        {
            using var wire = new MemoryStream();
            ProtoBuf.Serializer.Serialize(wire, new ConfigSyncPacket { Json = "{}", ProductRemap = sent });
            wire.Position = 0;
            back = ProtoBuf.Serializer.Deserialize<ConfigSyncPacket>(wire).ProductRemap;
        }
        catch (System.Exception e) { detail = "the round trip threw: " + e.Message; return false; }

        HashSet<string> mine = new HashSet<string>(sent ?? System.Array.Empty<string>(), System.StringComparer.Ordinal);
        HashSet<string> theirs = new HashSet<string>(back ?? System.Array.Empty<string>(), System.StringComparer.Ordinal);
        if (mine.SetEquals(theirs)) return true;

        detail = $"server has {mine.Count} entr(y/ies), a client would get {theirs.Count}";
        return false;
    }

    /// <summary>A short, STABLE fingerprint of the config's compact JSON (FNV-1a 32-bit), logged at client join
    /// (<see cref="StartClientSide"/>) so two runs can be compared for "same config or not?" without printing every
    /// field. Deterministic across processes (unlike <c>string.GetHashCode</c>, which is randomized per run), so a
    /// server and a correctly-synced client print the SAME digest. (E04/T04.06.)</summary>
    static string ConfigDigest(string json)
    {
        uint h = 2166136261u;                                 // FNV-1a 32-bit offset basis
        foreach (char c in json) { h ^= c; h *= 16777619u; }  // xor byte, then multiply by the FNV prime
        return h.ToString("x8");
    }

    // ── Legacy single-file migration (ImprovementsPlan E04/T04.03) ─────────────────────────────────────────
    // Players updating from a pre-E04 version have their whole config in ONE file, ModConfig/immersivewoodworking.json.
    // On the first launch after the update we read it, let StoreConfigFolder lay it out across the new folder, and
    // rename the old file to .bak so it stops shadowing the folder yet stays recoverable. Server-side only (StartPre
    // already returned on the client). What the player loses is any hand-written COMMENTS, which StoreModConfig stripped
    // on every launch anyway — no value regression.

    /// <summary>Decide the legacy-config migration and, when migrating fresh, read the old values into <see cref="Config"/>
    /// so <see cref="StoreConfigFolder"/> writes them into the new folder (E04/T04.03). Returns whether the old file
    /// should be retired to <c>.bak</c> after the folder is written. Cases:
    /// <list type="bullet">
    /// <item>no legacy file → nothing to do (fresh install or already migrated), returns false;</item>
    /// <item>legacy file AND folder both present (a downgrade-then-reupgrade) → the FOLDER is authoritative (newer by
    /// intent); do NOT read the old file, just back it up, returns true;</item>
    /// <item>legacy file, no folder yet (the update's first launch) → read the player's values into Config (missing keys
    /// keep their defaults, standard Newtonsoft), returns true. A read error leaves the old file in place and returns
    /// false so the player can recover it.</item>
    /// </list>
    /// Reading is via <see cref="ICoreAPI.LoadModConfig"/> — the exact deserializer that wrote the file — so the values
    /// round-trip identically. Server-side; guarded so a bad legacy file can't abort startup.</summary>
    bool PrepareLegacyMigration(ICoreAPI api)
    {
        string oldPath = Path.Combine(GamePaths.ModConfig, ConfigFile);
        if (!File.Exists(oldPath)) return false;

        string dir = Path.Combine(GamePaths.ModConfig, ConfigFolder);
        if (Directory.Exists(dir))
        {
            // Both exist: the folder is the newer, authoritative copy (LoadConfigFolder will read it). Don't fold the
            // stale single file back in — that would overwrite fresher folder edits with an older rollback's values.
            api.Logger.Notification("[immersivewoodworking] Both the legacy config {0} and the new folder ModConfig/{1}/ exist; the folder wins and the old file will be kept as .bak.", ConfigFile, ConfigFolder);
            return true;
        }

        try
        {
            ImmersiveWoodworkingConfig loaded = api.LoadModConfig<ImmersiveWoodworkingConfig>(ConfigFile);
            if (loaded != null) Config = loaded;
            api.Logger.Notification("[immersivewoodworking] Migrating config from the single file to the folder ModConfig/{0}/; the old file will be kept as .bak.", ConfigFolder);
            return true;
        }
        catch
        {
            api.Logger.Warning("[immersivewoodworking] Could not read the legacy config {0} for migration; the new folder starts from defaults and the old file is left in place for recovery.", ConfigFile);
            return false;
        }
    }

    /// <summary>Retire the legacy single file to <c>&lt;name&gt;.bak</c> once the new folder has been written (E04/T04.03).
    /// An existing <c>.bak</c> is overwritten (decision: the freshest migration is the useful backup; kept simple, with a
    /// Notification so it isn't silent). A failure here is only a Warning: the folder is already written and in effect,
    /// so the mod runs fine and the stale file is merely left behind. Server-side only.</summary>
    void BackupLegacyConfig(ICoreAPI api)
    {
        try
        {
            string oldPath = Path.Combine(GamePaths.ModConfig, ConfigFile);
            if (!File.Exists(oldPath)) return;

            string bakPath = oldPath + ".bak";
            if (File.Exists(bakPath))
            {
                File.Delete(bakPath);
                api.Logger.Notification("[immersivewoodworking] Overwrote a previous {0}.bak with the freshly migrated legacy config.", ConfigFile);
            }
            File.Move(oldPath, bakPath);
        }
        catch
        {
            api.Logger.Warning("[immersivewoodworking] Could not move the legacy config {0} to .bak; the folder is written and in effect (the mod is fine), the old file is just left in place.", ConfigFile);
        }
    }

    public override void Start(ICoreAPI api)
    {
        api.RegisterBlockClass("iw-choppingblock", typeof(BlockChoppingBlock));
        api.RegisterBlockEntityClass("iw-choppingblock", typeof(BlockEntityChoppingBlock));

        // Migration husk (ChoppingBlockRedesignPlan E05 / T05.01): re-registers the pre-redesign per-species codes `choppingblock-{wood}`
        // (blocktypes/choppingblock_legacy.json, same base code + a wood variantgroup) so old saved stands still
        // resolve on load; the shared ChoppingBlock BE then self-heals them to the live `choppingblock` (T05.02).
        api.RegisterBlockClass("iw-choppingblock-legacy", typeof(BlockChoppingBlockLegacy));

        // Half-log (ManualOpsBalancePlan E04/T04.01) — the carryable intermediate the chopping block splits a log into
        // on the way to firewood (log → 2 half-logs → firewood). A single data-item: the wood species is a stack
        // attribute, re-textured per species at runtime (ItemHalfLog), so one code serves every wood incl. wood mods.
        api.RegisterItemClass("iw-halflog", typeof(ItemHalfLog));

        // Half-log SLAB (HalfLogBlockPlan E03/T03.02) — the same half-log laid as a building block. Unlike the item,
        // the species is a block VARIANT (halflogslab-{wood}-{skin}-{rot}-{run}-{face}, states injected by InjectWoodVariants),
        // so there is no BlockEntity and no per-cell state at all. The class exists purely to point each variant's three
        // textures at the domain that actually ships them; see BlockHalfLogSlab. Must match "class" in
        // blocktypes/halflogslab.json, or the block silently falls back to plain Block and every modded species goes
        // untextured.
        api.RegisterBlockClass("iw-halflogslab", typeof(BlockHalfLogSlab));

        // Sawhorse (one trestle) — the log-ripping station built in LogSawingPlan E03+. Generic wood block (no variants)
        // with a BE that tracks the build stage, pair state, the held log and sawing progress.
        api.RegisterBlockClass("iw-sawhorse", typeof(BlockSawhorse));
        api.RegisterBlockEntityClass("iw-sawhorse", typeof(BlockEntitySawhorse));

        // Patched onto game:stick (patches/sawhorse_build_behavior.json): a plain right-click with a stick on
        // the ground lays the first trestle (stage 1). Advancing an existing trestle is handled by the block
        // itself, so rope needs no behavior. Plain RMB is used (not Shift) so the stick's vanilla GroundStorable
        // "stick pile" (Shift+RMB) is left untouched. See BehaviorSawhorseBuild.
        api.RegisterCollectibleBehaviorClass("iw-sawhorsebuild", typeof(BehaviorSawhorseBuild));

        // Cut-mode tool mode (F) on the pit saw (itemtypes/tool/pitsaw.json) and, for the AllowVanillaSaw / RemovePitSaw
        // configs, on game:saw (patches/saw_sawmode_behavior.json): planks / beams / auto, LOCKED onto the sawhorse pair
        // at rip start. The menu shows only when the saw is aimed at a sawhorse, so a modeless saw stays modeless
        // elsewhere. See CollectibleBehaviorSawMode (SawModePlan).
        api.RegisterCollectibleBehaviorClass("iw-sawmode", typeof(CollectibleBehaviorSawMode));

        // Toolsmith render shim on the forged toolheads (pitsawblade / barkspudhead). It swaps in the
        // floor-origined "-toolsmith" shape ONLY while the head is drawn in a Toolsmith workbench slot (whose
        // hardcoded slot transform ignores every item transform and seats a part purely by its shape geometry),
        // and returns null everywhere else so the normal centred Shape.Base is used for inventory/hand/ground/etc.
        // Replaces the old global shape-swap patch (patches/toolsmith_head_shape.json, deleted), which fixed the
        // bench but shifted every other pose. Inert without Toolsmith (no workbench block ⇒ always null). Attached
        // via behaviorsByType in itemtypes/toolhead/{pitsawblade,barkspudhead}.json. See BehaviorToolsmithBenchMesh.
        api.RegisterCollectibleBehaviorClass("iw-toolsmithbenchmesh", typeof(BehaviorToolsmithBenchMesh));

        // Automated sawmill (AutomatedSawmillPlan E03+) — the mech-powered upgrade tier OVER the hand sawhorses. The player places ONE
        // master frame (BlockSawmill / "iw-sawmill" BE) which captures a multi-cell footprint (currently 19 cells, fitted
        // to the authored model) filled with invisible
        // ghost-fillers (BlockSawmillGhost / "iw-sawmill-multiblock" BE) that route every interaction/break back to the
        // master, then RMB-installs the crafted item-parts into it (T03.07). This is the same shape as vanilla's
        // pulverizer: a placed BlockPulverizer whose footprint is filled by BlockMPMultiblockPulverizer/BEMPMultiblock.
        // The class/entityClass strings here MUST match those in the sawmill blocktypes JSON (frame.json / ghost.json),
        // or the block silently falls back to the base Block/BlockEntity and all routing is lost.
        api.RegisterBlockClass("iw-sawmill", typeof(BlockSawmill));
        api.RegisterBlockEntityClass("iw-sawmill", typeof(BlockEntitySawmill));
        api.RegisterBlockClass("iw-sawmill-ghost", typeof(BlockSawmillGhost));
        api.RegisterBlockEntityClass("iw-sawmill-multiblock", typeof(BEMultiblockSawmill));

        // AutomatedSawmillPlan E04/T04.01 — mechanical power. The sawmill master is interior to its footprint, so the network node can't
        // sit on it; instead one special ghost-filler (BlockSawmillGhostPower, at the exposed -X rotor_default cell)
        // is an IMechanicalPowerBlock that carries the consumer node (BEBehaviorSawmillMP, behavior name "iw-sawmillmp"
        // in sawmill-ghostpower's entityBehaviors). The plain BlockSawmillGhost has no power role. The behavior name
        // must match the entityBehaviors entry in ghostpower.json, the block class the JSON "class".
        api.RegisterBlockClass("iw-sawmill-ghostpower", typeof(BlockSawmillGhostPower));
        api.RegisterBlockEntityBehaviorClass("iw-sawmillmp", typeof(BEBehaviorSawmillMP));

        // Automated CHOPPER (AutoChopperPlan E03+) — the mech-powered upgrade tier OVER the hand chopping block, the exact structural
        // clone of the sawmill above: a placed master frame (BlockChopper / "iw-chopper" BE) captures a multi-cell
        // footprint (T03.03) filled with invisible ghost-fillers (BlockChopperGhost / "iw-chopper-multiblock" BE) that
        // route every interaction/break back to the master, then RMB-installs the crafted item-parts (T03.07). The
        // class/entityClass strings here MUST match those in the chopper blocktypes JSON (frame.json / ghost.json /
        // ghostpower.json), or the block silently falls back to the base Block/BlockEntity and all routing is lost.
        api.RegisterBlockClass("iw-chopper", typeof(BlockChopper));
        api.RegisterBlockEntityClass("iw-chopper", typeof(BlockEntityChopper));
        api.RegisterBlockClass("iw-chopper-ghost", typeof(BlockChopperGhost));
        api.RegisterBlockEntityClass("iw-chopper-multiblock", typeof(BEMultiblockChopper));

        // The chopper master is interior to its footprint too, so its network node can't sit on it; one special
        // ghost-filler (BlockChopperGhostPower, at the exposed input cell) is an IMechanicalPowerBlock that carries
        // the consumer node (BEBehaviorChopperMP, behavior name "iw-choppermp" in chopper-ghostpower's
        // entityBehaviors). The plain BlockChopperGhost has no power role. The behavior name must match that
        // entityBehaviors entry, the block class the JSON "class".
        api.RegisterBlockClass("iw-chopper-ghostpower", typeof(BlockChopperGhostPower));
        api.RegisterBlockEntityBehaviorClass("iw-choppermp", typeof(BEBehaviorChopperMP));

        // Backfills the woodchop keyframes onto whatever body shape a player actually uses, so the swing
        // plays on non-seraph-faceless shapes too (skins / reskin mods built on the seraph skeleton). The
        // JSON patch can only place keyframes on the single seraph-faceless file; this behavior copies them
        // onto the others at shape-build time. Patched into player.json via patches/chopanim_behavior.json.
        api.RegisterEntityBehaviorClass("iw-chopanim", typeof(EntityBehaviorChopAnim));

        // ManualOpsBalancePlan E07: hand-break for beach driftwood. A BlockBehavior bolted onto the vanilla game:flotsam-driftwood-* blocks
        // (via patches/flotsam_break_behavior.json) so holding right-click with an empty hand snaps the driftwood into
        // firewood + sticks by size — the in-world replacement for chopping it on the block (removed T07.01). Attaching
        // an iw- behavior to a foreign vanilla block via an op:add /behaviors/- patch mirrors iw-sawhorsebuild on
        // game:stick — no Harmony. The mod's first BlockBehavior.
        api.RegisterBlockBehaviorClass("iw-flotsambreak", typeof(BlockBehaviorFlotsamBreak));

        // Birch-tar rig (BarkPlan/E04, T04.03) — the fired perforated pot as a ground-stored, packable vessel, and the
        // assembled two-vessel rig it builds onto a ground-stored clay pot. The perforated pot's held Shift+RMB assembles
        // the rig (server-authoritative); a plain empty-hand RMB on the rig takes it apart (perforated pot back to hand,
        // clay pot restored in the same quadrant). Since 2026-07-29 the perforated pot is GroundStorable+Unplaceable like
        // vanilla claypot and has NO BlockEntity: its pack level rides the stack attributes, exactly like the rig's state
        // does. The class string MUST match `class` in blocktypes/perforatedpot.json or the block silently falls back to
        // base Block and all routing is lost. Bark packing + clay sealing are T04.09.
        api.RegisterBlockClass("iw-perforatedpot", typeof(BlockPerforatedPot));
        // The assembled rig is now a ground-storable stack (BlockTarPot implements IContainedInteractable +
        // IContainedMeshSource); its state rides the stack attributes, so it needs NO BlockEntity class (2026-07-28).
        api.RegisterBlockClass("iw-tarpot", typeof(BlockTarPot));
        // The SEALED/FIRED tar pot ITEM needs its own mesh override (2026-07-29): since the D41 model merge folded the
        // birch groups into pot-combined, the class-less default JSON mesh drew a sealed pot wrapped in all four birch
        // stages. ItemTarPot renders it as the shell + lid with NO birch (PotMesh stage 0, withLid) for held/GUI/dropped
        // AND for the ground-storage quadrant it is sealed into — two-tone since the perfcolor axis (2026-07-29): top
        // body + lid in the perforated pot's colour, bottom in the receiver's. Must match `class` in itemtypes/tarpot.json.
        api.RegisterItemClass("iw-tarpot-item", typeof(ItemTarPot));

        // The two GREEN bark kinds (bark.json classByType): Dry runs at pocket speed in a ground pile instead of vanilla's
        // quarter, and up to twice that near any IHeatSource in a block container. The key must stay iw-prefixed: Ancient
        // Tools registers a global "ItemBark", and a second registration of a class key silently replaces the first.
        api.RegisterItemClass("iw-greenbark", typeof(ItemIwGreenBark));

        // BarkPlan/E04, T04.05 — held-item "open the fired tar pot" gesture. A plain RMB on a
        // tarpot-{color}-{perfcolor}-fired instantly pries it open into barktar x8 + a dirty receiver
        // (game:dirtyclaypot-{color}-empty) + the perforated pot back in ITS OWN colour (perforatedpot-{perfcolor}). A
        // coded gesture (not a grid recipe) because opening returns THREE stacks and a grid recipe caps at output + one
        // returned-per-ingredient = two. Attached via behaviorsByType "tarpot-*-fired" in itemtypes/tarpot.json — the
        // SEALED variant never carries it, so only a fired pot opens. See BehaviorTarPotOpen.
        api.RegisterCollectibleBehaviorClass("iw-tarpotopen", typeof(BehaviorTarPotOpen));

        // BarkPlan/E04, D50 — плейн-ПКМ по нашим сосудам показывает ванильную подсказку «зажми Shift, чтобы поставить».
        // Ванильный обожжённый горшок нагоняет её из BlockCookingContainer.TryPlaceBlock; наши горшки такого класса не
        // имели и молчали (Unplaceable глушит сообщение через "__ignore__"). Поведение только ГОВОРИТ, клик не клеймит.
        // Вешается ПОВАРИАНТНО из JSON: обожжённый перфгоршок, риг и ЗАПЕЧАТАННЫЙ дегтярный горшок — но НЕ обожжённый
        // дегтярный (у того плейн-ПКМ уже занят вскрытием, iw-tarpotopen) и НЕ сырой перфгоршок (ванильный сырой горшок
        // тоже молчит). Работает и на блоке, и на предмете — потому и поведение, а не override TryPlaceBlock, которого у
        // предмета нет вовсе. См. BehaviorSneakToPlace.
        api.RegisterCollectibleBehaviorClass("iw-sneaktoplace", typeof(BehaviorSneakToPlace));

        // Register the config-sync channel symmetrically on both sides (same name + message type) so the
        // server can push its authoritative config to clients. ChopSeconds drives BOTH the server chop
        // timer and the client swing-animation speed; without this, a client whose local config differed
        // would see the axe impact drift away from the moment the chop actually resolves. The send/receive
        // ends are wired per side in StartServerSide / StartClientSide.
        //
        // The message type is the JSON envelope ConfigSyncPacket, NOT the config class itself (AuditFixPlan T02.05 /
        // F-L4-02): sent directly, ProtoBuf silently dropped every field whose server value was false/0 and the client
        // fell back to its own initializer. The envelope's rationale lives with the class.
        api.Network.RegisterChannel(ConfigChannel).RegisterMessageType<ConfigSyncPacket>();

        // Wire the optional xSkills (forestry) bridge on BOTH sides: the server grants XP and applies yield/wear
        // bonuses, the client reads its OWN swiftsaw tier to speed the local chop animation. Construction is free
        // when xSkills is absent — the reflection bridge resolves nothing until its first real use. See the facade
        // in ImmersiveWoodworkingModSystem.XSkills.cs.
        InitXSkills(api);

        // Register OUR 4 abilities (sawyer/carefulsawyer/splitter/swiftsaw) into the existing forestry skill — here in
        // Start, which is the one safe window: after every mod's StartPre (forestry already created by XSkills) yet
        // before any AssetsLoaded. No-op without xSkills or with the integration flag off. Both sides register in the
        // same fixed order for MP index alignment. The ability NAMES are (re)localized in AssetsLoaded
        // (LocalizeForestryAbilities) once the mod's lang files are loaded — NOT here (lang isn't ready at Start).
        RegisterForestryAbilities();
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        // Push the server's authoritative config to each player as they join, as JSON inside ConfigSyncPacket. The
        // text is built ONCE here rather than per join: Config is fixed after StartPre (nothing mutates it at
        // runtime), so every joiner gets byte-identical bytes and the same digest in the logs.
        string configJson = JsonConvert.SerializeObject(Config); // compact; [JsonIgnore] keeps the *Effective accessors out
        // The beam skip list rides along (AuditFixPlan T03.03): the client's rip gate needs it and cannot read it
        // itself, since recipe assets never reach a client. Built once here for the same reason as the JSON — the
        // collection pass ran in AssetsLoaded, which the engine completes before this Normal phase.
        string[]? beamSkipWoods = BeamSkipWoodsWire;
        // The recipe-declared product remap table rides along for the same reason as the beam skip list: the client's
        // put/rip gate must accept exactly the logs the server does, and it cannot read recipe assets to build it.
        string[]? productRemap = ProductRemapWire;
        api.Event.PlayerJoin += player =>
            api.Network.GetChannel(ConfigChannel).SendPacket(new ConfigSyncPacket { Json = configJson, BeamSkipWoods = beamSkipWoods, ProductRemap = productRemap }, player);

        // Optional Seraph Leveling ReClassed bridge, server-only: sawn boards count toward its crafting traits. Silent
        // without Seraph. See ImmersiveWoodworkingModSystem.SeraphLeveling.cs.
        InitSeraphLeveling(api);

        // Green bark near a fire finds its container by Inventory.Pos, and a freshly placed crate loses it: see
        // ItemIwGreenBark.RestoreContainerPos.
        api.Event.DidPlaceBlock += (byPlayer, oldBlockId, blockSel, withItemStack) =>
            ItemIwGreenBark.RestoreContainerPos(api.World, blockSel?.Position);

#if IW_DEBUG
        // HalfLogBlockPlan T03.03: round-trip every half-log slab variant through BlockHalfLogSlab's
        // (species, domain) <-> block helpers. Server-side because the server's registry is the authority (a client's
        // is a copy of it received over the wire), and deferred to GameReady because that is the FIRST phase at which
        // the engine promises loaded blocks — at AssetsFinalize the registry is already complete but every Block's own
        // `api` is still null, which made the first version of this check report a false all-clear. See the method's
        // own doc. Stripped from a shipped build.
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => BlockHalfLogSlab.SelfCheck(api));

        // HalfLogBlockPlan T04.01: sweep every click (6 faces x 100 aim points x 8 yaws) through the state-selection
        // rule and check each answer against the block registry. Same phase and same reason as the line above — the
        // check resolves real blocks — and it has to be the SERVER that says so, because the server is the side that
        // will re-derive a placement rather than trust the client's state.
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => HalfLogPlacement.SelfCheck(api));

        // HalfLogBlockPlan T04.02: the one placement input that is CHOSEN rather than read off the click — which
        // species a SPECIES-LESS half-log lays down. Same phase and reason as the two lines above (it resolves a
        // real block through the registry), and it runs the actual ItemHalfLog path rather than restating it.
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => ItemHalfLog.SelfCheck(api));

        // HalfLogBlockPlan T05.01/T05.02: every wrench gesture and every world-edit / schematic transform, swept over
        // the whole registry — that no step lands outside the 24 registered states, that none loses the species, and
        // that the rot walk is one closed cycle covering all 12 legal rot/run pairs. Same phase and reason as the
        // three lines above; the server is also the only side that MUTATES on a wrench turn (BlockHalfLogSlab.Rotate).
        api.Event.ServerRunPhase(EnumServerRunPhase.GameReady, () => BlockHalfLogSlab.WrenchSelfCheck(api));
#endif
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // Adopt the server's config in place of our local one (re-clamped defensively). BlockChoppingBlock
        // reads ChopSeconds live through the mod system, so this swap takes effect on the next chop.
        // The payload is JSON, not the object (AuditFixPlan T02.05 / F-L4-02) — see ConfigSyncPacket.
        api.Network.GetChannel(ConfigChannel).SetMessageHandler<ConfigSyncPacket>(packet =>
        {
            // A packet we cannot read must NOT leave the client half-configured or crash it at join: keep whatever
            // config is in effect and say so once in the log. Unreachable in a matched pair (the server writes this
            // text with the same serializer that reads it here); reachable only on a truncated or corrupted payload.
            ImmersiveWoodworkingConfig? serverConfig = null;
            if (!string.IsNullOrEmpty(packet?.Json))
            {
                try { serverConfig = JsonConvert.DeserializeObject<ImmersiveWoodworkingConfig>(packet!.Json); }
                catch { serverConfig = null; }
            }
            if (serverConfig == null)
            {
                api.Logger.Warning("[immersivewoodworking] Server config packet could not be read - keeping the local config. Client and server settings may differ.");
                return;
            }

            serverConfig.Sanitize();
            Config = serverConfig;

            // The beam skip list travels beside the config (AuditFixPlan T03.03) and is adopted the same way: it is
            // server-owned DATA the client cannot derive, and its rip gate must answer exactly as the server's does.
            // A packet the client could not read returns above, so this only runs on a payload that arrived intact.
            AdoptBeamSkipWoods(packet?.BeamSkipWoods);

            // The product remap table travels beside the beam skip list and is adopted the same way — server-owned DATA
            // the client cannot derive from its own (recipe-less) asset view, and its put/rip gate must resolve exactly
            // as the server's does or it refuses a log the server accepts (T03.03).
            AdoptProductRemaps(packet?.ProductRemap);

            // Re-apply the (now authoritative) bark drying time to the client's bark items: at AssetsFinalize the client
            // had only its default config, so the tooltip/progress bar would otherwise show the wrong hours (T08.02).
            ApplyBarkDryHours(api);

            // Everything else that baked a config value into client state gets the same second chance (T09.01) — today
            // that is the handbook prose, which says what this server actually does only if it is composed from THIS
            // config rather than from the defaults the client booted with.
            ClientConfigApplied?.Invoke();

            // One line to client-main.log confirming the server's config reached the client — the cheap "did it sync?"
            // check for the smoke tests (LogSawingPlan T07.04 / AutomatedSawmillPlan+AutoChopperPlan T08.04) and MP
            // diagnostics if a player's local file ever differed.
            // Notification-level (log file, not on-screen), once per join. The former 29-argument positional format
            // string (indices out of order, unreadable, and growing with every new field — ImprovementsPlan E04/E05/E06) is replaced by
            // a compact-JSON DIGEST plus a handful of representative fields spanning the sections (a manual, a sawing,
            // an automation drive, and one E04 override from each auto station): the digest answers "same config or
            // not?" at a glance, and under IW_DEBUG the FULL config is dumped as compact JSON so a desync can be diffed
            // field-by-field without cluttering the normal log.
            // The text is the SERVER'S OWN (the packet payload), not a re-serialization of the adopted object: the
            // digest then answers "did the server's bytes arrive intact?" rather than "does my copy re-serialize the
            // same?". The two are identical in a healthy pair — the server sanitizes before it serializes, so the
            // client's defensive Sanitize above is a no-op.
            string configJson = packet!.Json;
            api.Logger.Notification(
                "[immersivewoodworking] Received server config (digest {0}): ChopSeconds={1}, FirewoodPerLog={2}, PlanksPerLog={3}, SawmillSpeed={4}, ChopperFirewoodPerLog={5}, SawmillPlanksPerLog={6}.",
                ConfigDigest(configJson), Config.ChopSeconds, Config.FirewoodPerLog, Config.PlanksPerLog,
                Config.SawmillSpeed, Config.ChopperFirewoodPerLog, Config.SawmillPlanksPerLog);
#if IW_DEBUG
            api.Logger.Notification("[immersivewoodworking] Full server config (IW_DEBUG): {0}", configJson);
#endif
        });

        // HalfLogBlockPlan T05.01: re-tesselate the NEIGHBOURING chunk when a half-log slab changes. A wrench turn goes
        // through ExchangeBlock, which marks only the block's OWN chunk dirty, and `rot` is what decides which face of
        // the slab is solid — so a turn on a chunk boundary would leave the block across that boundary culling against
        // a solidity that is gone. Registered here rather than in Block.OnLoaded because that runs once per VARIANT
        // and would add 1368 handlers; the reasoning lives with the feature, in BlockHalfLogSlab.RegisterEdgeRedraw.
        BlockHalfLogSlab.RegisterEdgeRedraw(api);

#if IW_DEBUG
        // The client half of the T03.03 round-trip (see the server registration in StartServerSide). Worth running on
        // BOTH sides even though the block registry is the server's: the species -> domain index the pair depends on
        // is NOT sent over the wire, each side builds its own from its own DiscoverWoodSpecies pass, so only a client
        // run can show that the client agrees. LevelFinalize rather than a run phase — that enum is server-side, and
        // by LevelFinalize the block types have arrived and been loaded.
        api.Event.LevelFinalize += () => BlockHalfLogSlab.SelfCheck(api);

        // The client half of T04.01. The rule itself is side-independent (a pure function of face, hit point and yaw),
        // so what a client run adds is the RESOLUTION step: the client's registry is the server's, received over the
        // wire, and a state that resolves there must resolve here or a placement would preview one block and get
        // another.
        api.Event.LevelFinalize += () => HalfLogPlacement.SelfCheck(api);

        // The client half of T04.02's default-species check. Worth both sides for the same reason as T03.03's: the
        // species index behind the default is built per side from that side's own assets and never sent over the
        // wire, so only a client run can show that the client would place the same block the server does.
        api.Event.LevelFinalize += () => ItemHalfLog.SelfCheck(api);

        // The client half of T05.01/T05.02. The wrench itself never mutates on the client, but the CLIENT is where the
        // interaction help and the turned block's mesh live, so this is the side that proves the state a turn names
        // resolves in the registry the client actually received.
        api.Event.LevelFinalize += () => BlockHalfLogSlab.WrenchSelfCheck(api);
#endif
    }

    /// <summary>
    /// First discovers the chopping block's wood species across all domains (<see cref="DiscoverWoodSpecies"/>, both
    /// sides); then the SERVER-side recipe strip described below.
    /// BEFORE recipes are registered (runs at our default ExecuteOrder 0.1, ahead of the vanilla RecipeLoader at
    /// 1), SERVER-side: scan EVERY recipe asset in EVERY domain and surgically remove the recipes the mod's
    /// stations/chains replace, so those become the only hand path. The rules are STRUCTURAL (matched by a
    /// recipe's output + input shape, never by file name), so the same pass covers vanilla AND any wood-adding mod
    /// — e.g. Wildcraft: Trees, whose log/plank/beam/tannin recipes live in its own <c>wildcrafttree:</c> domain — with
    /// no per-mod patch. Most families are grid recipes under <c>recipes/grid/</c>; the pit-saw blade adds a
    /// <c>recipes/smithing/</c> pass and the log→tannin family a <c>recipes/barrel/</c> pass (own paths, own predicates).
    /// Five families, each gated on its own config flag:
    ///   • firewood ("axe + a log the chopping block accepts → firewood", <see cref="IsLogToFirewood"/>) — matched
    ///     by OUTPUT (firewood) AND an input whose code-base is one the stand takes
    ///     (<see cref="BlockEntityChoppingBlock.LogCodeParts"/>: log / logsection / debarkedlog / choppingblock). This
    ///     deliberately SPARES narrow-trunk logs the stand can't process (e.g. wildcrafttree's <c>lognarrow-*</c>):
    ///     they keep their hand recipe instead of being orphaned. A species-specific firewood counts as firewood, so
    ///     vanilla's separate aged recipe (output "agedfirewood") goes too, now that the stand makes that item itself
    ///     (AuditFixPlan T04.05). The same flag also strips the driftwood→firewood grid recipe
    ///     (<see cref="IsFlotsamToFirewood"/>, a <c>flotsam-*</c> input → firewood), the grid echo of the in-world
    ///     flotsam hand-break.
    ///   • planks &amp; beams ("saw + log/debarked log → plank/supportbeam", <see cref="IsLogToPlankOrBeam"/>) —
    ///     matched by OUTPUT (plank-* / supportbeam-*) AND a log-placed-/logsection-placed-/debarkedlog- input, so
    ///     only the rip recipes go while the recycling recipes (planks/stairs/slab/ladder → planks — same output,
    ///     non-log input) stay.
    ///   • debarking ("axe + hammer + log → debarked log", <see cref="IsLogToDebarkedLog"/>, gated on
    ///     <see cref="ImmersiveWoodworkingConfig.DisableVanillaDebarkCrafting"/>) — matched by OUTPUT (debarkedlog-*)
    ///     AND a log-placed-* input, the in-world echo the sawhorse SIDE action replaces; the output gate keeps it
    ///     distinct from the beam rip (which CONSUMES a debarked log), and it covers vanilla and any wood mod's own
    ///     debark recipe alike.
    ///   • pit saw (<see cref="IsPitSawRecipe"/>, gated on <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/>) —
    ///     the mod's OWN <c>pitsaw-{metal}</c> craft (asset under <c>recipes/grid/tool/</c>, reached by this same
    ///     all-domain scan), matched by OUTPUT first-part "pitsaw". Removing it makes the pit saw uncraftable so only
    ///     ordinary saws remain; the tool itself is also gated out of the sawing logic (BlockEntitySawhorse.SawIsEffective).
    ///     The same flag also drives a SECOND pass over <c>recipes/smithing/</c> stripping the pit-saw BLADE's anvil
    ///     recipe (<see cref="IsPitSawBladeRecipe"/>, ManualOpsBalancePlan E09) — the blade is the sole gating part of BOTH assembly paths
    ///     (grid blade+stick+rope AND, with Toolsmith, head→tool tinkering), so removing it kills the whole chain.
    ///   • log→tannin (<see cref="IsLogToTannin"/>, gated on
    ///     <see cref="ImmersiveWoodworkingConfig.DisableVanillaLogTannin"/>, BarkPlan E03/T03.03) — a pass over
    ///     <c>recipes/barrel/</c> (its own path, since these are BARREL not grid recipes) removing "whole/debarked log
    ///     → weak/strong tannin" — matched by OUTPUT (<c>weaktanninportion</c>/<c>strongtanninportion</c>) AND a
    ///     <c>log-placed-*</c> / <c>debarkedlog-*</c> input. The bark chain (E03/T03.02) replaces it, so the log
    ///     survives for lumber; the mod's OWN bark→tannin recipes are spared (tags-only bark input, no log code), as are
    ///     <c>lognarrow-placed-*</c> (undebarkable) and leaf routes — the same lognarrow discipline as the firewood strip.
    ///
    /// Why server-only is the whole story (unchanged from the original per-file approach, verified against the
    /// engine): VSSurvivalMod's RecipeLoader registers grid recipes ONLY on the server (its AssetsLoaded loads only
    /// when api is ICoreServerAPI), and the server syncs its whole recipe registry to every client on join
    /// (RecipeRegistryBase.ToBytes/FromBytes → Packet_Recipes). So editing these assets on the server, before
    /// RecipeLoader reads them, is sufficient and complete — the removal propagates to all clients and there is no
    /// client/server set to reconcile. Editing the asset (so the recipe never registers into either the handbook
    /// list OR the crafting-grid match index) rather than World.GridRecipes.RemoveAll (which only hides from the
    /// handbook, leaving it craftable) is the other thing this gets right. Each asset is parsed and only its
    /// matched nodes removed — never blind-blanked — so everything else in the file survives, the count is logged,
    /// and a re-run is a no-op. Gated on config so vanilla hand-crafting can be turned back on; every other source
    /// (firepit, pit kiln, the stations themselves) is untouched.
    ///
    /// This method ALSO runs the ImprovementsPlan E03 saw-tool relax pass (<see cref="RelaxSawToolIngredients"/>) — UNCONDITIONAL (a
    /// compat fix, not config-gated), placed before the strip's config early-return. Both the strip and the relax read
    /// each asset's FULLY-PATCHED data: JSON patches (the engine's ModJsonPatchLoader) are applied during asset load,
    /// ahead of this 0.1 pass, so a mod that patches a recipe is already reflected here. That order is the premise the
    /// strip has always relied on; T03.01 reconfirms it empirically in the test run (the relax pass's own "Relaxed N
    /// slot(s)" notification is the instrument — a base-game run must report the 6 vanilla cabinet slots).
    /// </summary>
    public override void AssetsLoaded(ICoreAPI api)
    {
        // Wood-species discovery runs on BOTH sides (client → the chopping block's creative/handbook stacks,
        // server → its generic recipes) and BEFORE the server-only recipe strip below. It precedes every
        // Block.OnLoaded because our ExecuteOrder (0.1) is below the 0.2 block loader. See DiscoverWoodSpecies.
        DiscoverWoodSpecies(api);

        // AuditFixPlan T04.05 — the species that have a log but no worldproperties entry (vanilla's aged / veryaged /
        // rotten / veryrotten). Kept OUT of woodSpecies on purpose and folded in only by the slab injection below.
        DiscoverLogOnlySpecies(api);

        // HalfLogBlockPlan T01.02 — resolve each bare species code to one domain (both sides), then stamp the species
        // list into the half-log slab's `wood` variant group (server only — see InjectWoodVariants). Both must precede
        // the 0.2 block loader, hence here and not in AssetsFinalize.
        BuildWoodDomainIndex();
        InjectWoodVariants(api);

        // Bark drop table (BarkPlan/E02, T02.02): load the species → special-bark asset here too, on BOTH sides and in
        // the same window as the species discovery. Never throws — a missing/broken asset leaves an Empty table (all
        // generic) with a warning. The server rolls it at strip completion (T02.03); the client reads it for the handbook.
        barkDropTable = BarkDropTable.Load(api, BarkDropTableAsset);

#if IW_DEBUG
        // Diagnostic (debug builds only): how many LISTED species aren't actually present in this install — the honest
        // "unknown species" count. This is EXPECTED, not an error: barkdrops.json lists Wildcraft woods (willow, linden,
        // …) that don't exist on a vanilla run, so they simply never key the resolver. Kept off normal runs so it never
        // reads as a warning; useful when a tree mod's own patch should have added its species.
        {
            HashSet<string> installed = new HashSet<string>();
            foreach ((string wood, string _) in woodSpecies) installed.Add(wood);
            int notInstalled = 0;
            foreach (string sp in barkDropTable.Species) if (!installed.Contains(sp)) notInstalled++;
            api.Logger.Notification("[immersivewoodworking] Bark drop table: {0} of {1} listed species are not present in this install (expected for Wildcraft woods on a vanilla run).", notInstalled, barkDropTable.SpeciesCount);
        }
#endif

        // Localize OUR forestry ability names/descriptions from IW's own lang keys, now that the mod's lang files are
        // loaded (they aren't yet during Start). Runs on BOTH sides — the client pass is what the skills GUI reads.
        // Self-contained fallback for xSkills' own AssetsLoaded Lang pass; a no-op without xSkills. See the facade.
        LocalizeForestryAbilities();

        if (api.Side != EnumAppSide.Server) return;

        // ImprovementsPlan E03: replace the fragile index-pinned JSON patch (the removed patches/pitsaw_cabinet_compat.json, which did
        // op:Remove on /0/ingredients/S/code … /5/…) with a STRUCTURAL pass — relax any grid-recipe slot that gates a
        // saw by BOTH a tool-saw tag AND a concrete code (vanilla's cabinet is the only such recipe) so the pit saw and
        // any modded tool-saw craft it. Unconditional compat fix (no config gate), so it runs BEFORE the config-gated
        // strip early-return below. Same server-only, before-RecipeLoader, mutate-asset.Data approach as the strip
        // (see RelaxSawToolIngredients / StripMatchingRecipes).
        int sawFilesChanged = 0, sawSlotsRelaxed = 0;
        RelaxSawToolIngredients(api, "recipes/grid/", ref sawFilesChanged, ref sawSlotsRelaxed);
        if (sawSlotsRelaxed > 0)
            api.Logger.Notification(
                "[immersivewoodworking] Relaxed {0} saw-tool ingredient slot(s) across {1} grid recipe asset(s) so any tool-saw (incl. the pit saw) can craft them; synced to clients.",
                sawSlotsRelaxed, sawFilesChanged);

        // AuditFixPlan T03.03 (F-L9-10): harvest the beam recipe's own skipVariants BEFORE the strip below deletes
        // those recipes, and UNCONDITIONALLY — the "no beams from rotten wood" rule is the game's, so the rip must
        // honour it whether or not we removed the grid craft that carried it. Read-only: nothing is written back.
        CollectBeamSkipVariants(api, "recipes/grid/");
        if (beamSkipWoodsServer.Count > 0)
            api.Logger.Notification(
                "[immersivewoodworking] Beams stay unavailable for {0} wood state(s) the game's own beam recipe excluded ({1}); the rip honours the same list.",
                beamSkipWoodsServer.Count, string.Join(", ", beamSkipWoodsServer));

        // AuditFixPlan T03.03: harvest the recipe-declared product REMAPS (a wood mod that RENAMES the species on the
        // way to its plank/beam, e.g. FloralZones' pinussibirica log → game:plank-pine) BEFORE the strip below deletes
        // those recipes, and UNCONDITIONALLY — the log must reach its plank whether or not we removed the grid craft.
        // Read-only: nothing is written back. Synced to clients on the config packet (ProductRemapWire).
        CollectProductRemaps(api, "recipes/grid/");
        if (productRemapServer.Count > 0)
            api.Logger.Notification(
                "[immersivewoodworking] {0} sawn product(s) resolve through a wood mod's own grid-recipe rename (e.g. a Siberian pine log → vanilla pine planks); the stations accept those logs and the rename is synced to clients.",
                productRemapServer.Count);

        bool stripFirewood = Config.DisableVanillaFirewoodCrafting;
        bool stripSaw = Config.DisableVanillaPlankCrafting;
        bool stripPitsaw = Config.RemovePitSaw; // also remove the mod's OWN pit saw recipe (its asset is under recipes/grid/tool/, so this same all-domain scan reaches it)
        bool stripDebark = Config.DisableVanillaDebarkCrafting; // the axe+hammer log→debarked-log grid recipe the sawhorse SIDE action replaces
        // The BARREL log→tannin chain the bark chain replaces (BarkPlan/E03, T03.03) — a different asset path, its own
        // pass below. Kept whenever the bark chain is switched off: its bark tannin recipes are not loaded then
        // (FeatureGate), and stripping the vanilla routes as well would leave no tannin at all.
        bool stripLogTannin = Config.DisableVanillaLogTannin && Config.EnableBarkChain;
        if (!stripFirewood && !stripSaw && !stripPitsaw && !stripDebark && !stripLogTannin) return; // every family kept on → nothing to remove

        int filesChanged = 0, totalRemoved = 0;

        // A recipe with requiresTrait is a class mod's reward, not the everyday hand path the stations replace (Sacred
        // Classes' Carpenter is "2 logs + saw → 32 planks" and friends), so it survives unless KeepTraitRecipes is off.
        // Only while the world keeps the lock: with the classExclusiveRecipes world option off (the Exploration preset
        // does that) RecipeLoader nulls every RequiresTrait in this same phase, and a spared recipe would become a
        // shortcut past the stations for every player. Same read RecipeLoader makes. Counted per trait for the log.
        bool keepTraitRecipes = Config.KeepTraitRecipes && api.World.Config.GetBool("classExclusiveRecipes", true);
        SortedDictionary<string, int> keptByTrait = new SortedDictionary<string, int>(System.StringComparer.Ordinal);

        // Grid recipes — all four families the stations replace live under recipes/grid/, so one scan with a
        // combined predicate covers them (firewood, saw log→plank/beam, the mod's own pit-saw craft, axe+hammer
        // debark). See StripMatchingRecipes for the file-shape / malformed-JSON / write-only-on-change handling.
        StripMatchingRecipes(api, "recipes/grid/", recipe =>
        {
            bool replaced =
                (stripFirewood && (IsLogToFirewood(recipe) || IsFlotsamToFirewood(recipe))) ||
                (stripSaw && IsLogToPlankOrBeam(recipe)) ||
                (stripPitsaw && IsPitSawRecipe(recipe)) ||
                (stripDebark && IsLogToDebarkedLog(recipe));
            if (!replaced || !keepTraitRecipes || recipe["requiresTrait"] is not JValue { Type: JTokenType.String } trait) return replaced;
            string traitName = (string)trait!;
            if (traitName.Length == 0) return true;
            keptByTrait[traitName] = keptByTrait.TryGetValue(traitName, out int n) ? n + 1 : 1;
            return false;
        }, ref filesChanged, ref totalRemoved);
        if (keptByTrait.Count > 0)
        {
            List<string> kept = new List<string>();
            int keptTotal = 0;
            foreach (KeyValuePair<string, int> kv in keptByTrait)
            {
                kept.Add(kv.Key + " x" + kv.Value);
                keptTotal += kv.Value;
            }
            api.Logger.Notification(
                "[immersivewoodworking] Kept {0} trait-locked grid recipe(s) the station strip would have removed ({1}); KeepTraitRecipes=false removes them too.",
                keptTotal, string.Join(", ", kept));
        }

        // ManualOpsBalancePlan E09: the pit-saw BLADE's ANVIL recipe lives under recipes/smithing/, which the grid scan above never
        // reaches — so removing only the pit-saw's grid craft left the blade forgeable but the saw unassemblable
        // (a dead-end). The blade is the sole gating part of BOTH assembly paths — the ordinary blade+stick+rope
        // grid craft AND, when Toolsmith is installed, its head→tool tinkering (config/toolsmith/.../tinkerabletools
        // lists "pitsaw") — so stripping the blade's smithing recipe kills the whole pit-saw chain in one move; the
        // Toolsmith list needs no edit, as no blade can be obtained to tinker with. Only when RemovePitSaw is on.
        if (stripPitsaw)
            StripMatchingRecipes(api, "recipes/smithing/", IsPitSawBladeRecipe, ref filesChanged, ref totalRemoved);

        // BarkPlan/E03 (T03.03): the vanilla log→tannin BARREL chain the bark chain replaces lives under
        // recipes/barrel/ — a different asset path than the grid/smithing scans above, so it gets its own pass with a
        // barrel-shaped predicate (IsLogToTannin: tannin output + a log input, ingredients as a JArray). Same server-
        // only, before-RecipeLoader, mutate-asset.Data discipline as the grid strip: barrel recipes also register
        // ONLY on the server via RecipeLoader (ExecuteOrder 1, > our 0.1) into a synced RecipeRegistry
        // (RegisterRecipeRegistry<...BarrelRecipe...>("barrelrecipes")), so editing the asset before it is read removes
        // the recipe from both the craft-match set AND the handbook, and the removal propagates to every client on join.
        // The mod's OWN bark→tannin recipes are spared: their inputs are tags-only bark + water/weak-tannin, no log.
        if (stripLogTannin)
            StripMatchingRecipes(api, "recipes/barrel/", IsLogToTannin, ref filesChanged, ref totalRemoved);

        // BarkPlan/E07 (T07.03): Wildcraft: Trees ships its OWN barrel tannin recipes. The strip above removes its
        // WHOLE-log routes (walnut/willow/beech, log-placed-*), but IsLogToTannin deliberately SPARES its two NON-log
        // routes (leaves-oak, lognarrow-alder) so they aren't orphaned. Left at Wildcraft's numbers those are a cheap
        // bypass of the whole bark chain (a huge hide = 120 free oak leaves, vs ~18 debarked oaks of milled bark), so
        // when the log→tannin strip is ON and Wildcraft is present we NERF (not remove) those two routes: they survive
        // as an EXPENSIVE fallback, bark stays the primary path. Gated on the same DisableVanillaLogTannin flag, so
        // flag-off leaves Wildcraft's original balance untouched. Same server-only, before-RecipeLoader, mutate-asset
        // .Data discipline as the strip (runs after it, so it reads the already-stripped array), keyed structurally so
        // it only ever hits leaves-*/lognarrow-* → tannin (Wildcraft's; the mod's own tags-only bark recipes are inert).
        int nerfFiles = 0, nerfRecipes = 0;
        if (stripLogTannin && api.ModLoader.IsModEnabled("wildcrafttree"))
            NerfWildcraftNonLogTannin(api, "recipes/barrel/", ref nerfFiles, ref nerfRecipes);
        if (nerfRecipes > 0)
            api.Logger.Notification(
                "[immersivewoodworking] Nerfed {0} Wildcraft non-log tannin recipe(s) across {1} asset(s) so bark stays the primary tanning path; synced to clients.",
                nerfRecipes, nerfFiles);

        if (totalRemoved > 0)
        {
            List<string> fam = new List<string>();
            if (stripFirewood) fam.Add("firewood");
            if (stripSaw) fam.Add("saw log→plank/beam");
            if (stripPitsaw) fam.Add("pit saw + blade recipes"); // grid craft AND the blade's smithing recipe (E09)
            if (stripDebark) fam.Add("axe+hammer log→debarked log");
            if (stripLogTannin) fam.Add("barrel log→tannin"); // vanilla/wood-mod whole-log tannin, replaced by the bark chain (E03)
            string families = string.Join(" + ", fam);
            api.Logger.Notification(
                "[immersivewoodworking] Disabled {0} recipe(s) across {1} asset(s) ({2}); synced to clients.",
                totalRemoved, filesChanged, families);
        }
    }

    /// <summary>
    /// Register the chopping block's grid recipes generically — one per wood species discovered across every domain
    /// (<see cref="DiscoverWoodSpecies"/>: vanilla + wildcrafttree + any wood mod), so a log from ANY wood mod crafts
    /// the right-species stand with no per-mod patch (ChoppingBlockRedesignPlan T04.02). This is the final step that makes the redesign's
    /// mod-generic promise real: a single static JSON recipe can't span domains (a wildcard log code is domain-pinned,
    /// e.g. <c>game:log-placed-*-ud</c> never expands to <c>wildcrafttree:</c>), so the recipes are built in code.
    ///
    /// Runs in <see cref="AssetsFinalize"/>, NOT our <see cref="AssetsLoaded"/> (ExecuteOrder 0.1): the ModSystem docs
    /// note blocks/items aren't registered below ExecuteOrder 0.2, so the placed logs and our own block wouldn't yet
    /// resolve there; by AssetsFinalize the block/item REGISTRY is complete (which is all a <c>GetBlock</c> + recipe
    /// Resolve needs) AND the vanilla RecipeLoader (AssetsLoaded, ExecuteOrder 1) has run. Deliberately NOT "after every
    /// <c>Block.OnLoaded</c>" — on the server that is one phase LATER than AssetsFinalize (see the phase note on
    /// <see cref="AttachFlotsamBreakBehavior"/>), so nothing here may read per-block state computed in OnLoaded. It
    /// doesn't: the scan reads block/item CODES and JSON-loaded fields only. SERVER-only, like RecipeLoader — the server's recipe registry is
    /// the authority and syncs whole to every client on join (Packet_Recipes), so registering here reaches all clients
    /// and the handbook with no client-side set to reconcile (the same reasoning as the AssetsLoaded recipe strip).
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        // BOTH sides (unlike the server-only recipe registration below): api.World.Collectibles is now fully resolved
        // with its MiningSpeed dictionaries, and the metal-tier speed factor (ManualOpsBalancePlan E02) is read live on client AND server
        // during a rip/strip, so BOTH need the index — build it here on both. The two sides do NOT see the same numbers,
        // though (AuditFixPlan T02.01): the client never parses itemtypes at all, it gets the registry over the wire,
        // and the wire truncates every collectible float onto a 1/64 grid. Parity comes from QuantizeLikeWire pulling
        // the server onto that same grid inside the scan, NOT from "both sides read the same JSON" — they don't.
        // See BuildWoodToolSpeedIndex / ToolTierSpeedFactor / QuantizeLikeWire.
        BuildWoodToolSpeedIndex(api);

        // Same both-sides reasoning, same fully-resolved registry, same wire caveat (BurnDuration is quantized too, so
        // that scan thresholds on WireQuantum rather than 0): record which wood species carry NO combustion at all,
        // so the half-log ITEM (species in stack attributes, where a JSON byType table is impossible) can refuse to burn
        // for them. See BuildNonCombustibleWoodIndex.
        BuildNonCombustibleWoodIndex(api);

        // Bark drying time (BarkPlan/E08, T08.02): overwrite the two green bark kinds' Dry transition hours from config,
        // on BOTH sides (the server resolves the transition, the client shows the tooltip + progress bar). A multiplayer
        // client still holds only its DEFAULT config here (StartPre is server-only), so it RE-APPLIES with the
        // authoritative value when the server config arrives — see the StartClientSide sync handler.
        ApplyBarkDryHours(api);

        if (api is ICoreServerAPI sapi)
        {
            RegisterChoppingBlockRecipes(sapi);
            RegisterSawhorseCraftingRecipe(sapi);
            // Same generic-per-domain treatment for the automated sawmill's plank/beam-consuming grid recipes (master
            // frame + the 4 mandatory parts): without it they are game:-pinned and a wood mod's own-domain planks/beams
            // (what the sawhorses make from a modded log) can't craft the powered tier. See RegisterSawmillRecipes.
            // Skipped when the sawmill is switched off; its one static recipe, the blade kit, is FeatureGate's.
            if (Config.EnableSawmill) RegisterSawmillRecipes(sapi);
            // And the automated chopper's wooden parts (frame + drive/arm) — the exact same clone-per-domain path via the
            // shared RegisterWoodDomainRecipes. The chopper's metal-only head recipe needs none of this and is loaded
            // straight by the RecipeLoader; the bed is the hand choppingblock (its own recipe). See RegisterChopperRecipes.
            // Skipped when the chopper is switched off; the head and head-half recipes are FeatureGate's.
            if (Config.EnableChopper) RegisterChopperRecipes(sapi);

            // Conflict-proof (re)attach of the driftwood hand-break behavior. Runs LAST, after every JSON patch, so no
            // other mod's patch order can wipe it. Server-only, like the recipes above (the finalized block registry
            // syncs whole to every client on join). See AttachFlotsamBreakBehavior.
            AttachFlotsamBreakBehavior(sapi);
        }
    }

    /// <summary>
    /// Overwrite the DRY transition time of the two green bark kinds (<c>bark-tan-green</c>, <c>bark-birch-green</c>) from
    /// <see cref="ImmersiveWoodworkingConfig.BarkDryHours"/> (BarkPlan/E08, T08.02). The shipped 96h lives in
    /// <c>bark.json</c>'s <c>transitionableProps</c>; making it config-driven means overwriting the loaded item's
    /// <c>TransitionableProperties[i].TransitionHours.avg</c> at runtime, found by <see cref="EnumTransitionType.Dry"/>
    /// (NOT by array position — robust if another transition is ever added). Only the AVERAGE is set, so whatever
    /// distribution the asset declared is preserved (the asset has var 0, so avg IS the value).
    ///
    /// <para>Runs on BOTH sides (the server resolves the transition, the client renders the tooltip + progress bar).
    /// The sides agree here because this method OVERWRITES the JSON number with the SYNCED config one on each of them —
    /// NOT because they read the same asset. They don't: <c>itemtypes</c> is a server-only asset category and the
    /// client's copy of <c>TransitionHours</c> arrived over the wire truncated onto a 1/64 grid (AuditFixPlan T02.01 —
    /// the same wire that forked the metal-tier factor). The overwrite lands both sides on
    /// <see cref="ImmersiveWoodworkingConfig.BarkDryHours"/> exactly, and the asset's other two floats sit on the grid
    /// anyway (<c>freshHours: 0</c>, <c>transitionRatio: 1</c>), so nothing here is left reading a quantized value.
    /// VS then samples a stack's <c>TransitionHours</c>
    /// ONCE (when its transitionstate is first created) and stores it per-stack, so overwriting <c>.avg</c> here (before any
    /// gameplay) is picked up by every bark stack that begins drying afterward, with no per-stack reconciliation. Called from <see cref="AssetsFinalize"/> and AGAIN from the client's config-sync handler
    /// (<see cref="StartClientSide"/>): at AssetsFinalize a multiplayer client holds only its DEFAULT config (StartPre is
    /// server-only), so without the re-apply it would display the wrong drying time. Idempotent (writing the same value
    /// twice is harmless). The value applies to bark that starts drying afterward; bark already mid-dry on an existing save
    /// keeps its originally sampled time (NOT rescaled) — not a break, drying is never a hard gate.</para>
    /// </summary>
    void ApplyBarkDryHours(ICoreAPI api)
    {
        float hours = Config.BarkDryHours;
        foreach (string code in new[] { "bark-tan-green", "bark-birch-green" })
        {
            Item? item = api.World.GetItem(new AssetLocation("immersivewoodworking", code));
            TransitionableProperties[]? props = item?.TransitionableProps;
            if (props == null) continue;
            foreach (TransitionableProperties p in props)
                if (p.Type == EnumTransitionType.Dry && p.TransitionHours != null)
                    p.TransitionHours.avg = hours;
        }
    }

    /// <summary>
    /// Conflict-proof fallback for <c>patches/flotsam_break_behavior.json</c>: bolt <see cref="BlockBehaviorFlotsamBreak"/>
    /// onto every vanilla driftwood block (<c>game:flotsam-*</c>) PROGRAMMATICALLY, here in <see cref="AssetsFinalize"/> —
    /// i.e. AFTER every JSON patch has applied. The JSON patch does <c>op:add /behaviors/-</c>, which another mod's
    /// <c>op:replace /behaviors</c> silently wipes: Wildcraft: Trees' <c>better_driftwood.json</c> replaces the whole array
    /// with <c>[UnstableFalling, RightClickPickup]</c>, and because IW's patch applies earlier in load order,
    /// <c>iw-flotsambreak</c> never survived — no <c>OnLoaded</c>, and driftwood fell back to vanilla placement (the
    /// hand-break did nothing). Appending after every patch cannot be clobbered by patch order. The
    /// <see cref="Block.HasBehavior(System.Type,bool)"/> guard skips blocks that already carry it (the no-conflict case,
    /// where the JSON patch succeeded), so it never double-attaches (which would break/consume the driftwood twice).
    ///
    /// Mirrors vanilla <c>BlockReinforcement</c> / <c>Snowballs</c>, which append the SAME behavior to BOTH
    /// <see cref="Block.BlockBehaviors"/> (placed-block interactions) AND <c>CollectibleBehaviors</c> (held-in-hand — the
    /// PRIMARY break path; <c>Collectible.OnHeldInteractStart</c> iterates ONLY CollectibleBehaviors). Filling both is
    /// load-bearing for the WIRE too, which no engine doc says out loud: <c>BlockTypeNet.GetBlockTypePacket</c>
    /// serialises the behaviour list from <c>CollectibleBehaviors</c> alone, so a behavior appended ONLY to
    /// BlockBehaviors would never reach a client at all. SERVER-only, like BlockReinforcement: the finalized registry
    /// (carrying this behavior) syncs to clients, who re-instantiate it and get <c>OnLoaded</c> via their own block init
    /// (<c>ClientSystemStartup.OnAllAssetsLoaded_Blocks</c>). Note the asymmetry that creates:
    /// <c>ReadBlockTypePacket</c> builds ONE instance per packet entry and puts that SAME instance into both arrays, so
    /// a client holds one where the server holds two. Harmless here — the only mutable per-instance state is the client
    /// break-sound handle, and a client is on at most one break path at a time.
    ///
    /// <para><b>Why the two hand-rolled <c>OnLoaded</c> calls</b> (re-verified against VS 1.22.3 on 2026-07-31; the
    /// reason this comment used to give — "the engine already ran every Block.OnLoaded before AssetsFinalize" — is FALSE
    /// on the server, see the phase note below):
    /// <list type="bullet">
    /// <item><c>placed.OnLoaded(api)</c> is REQUIRED, but because nothing in the engine EVER initialises a BlockBehaviors
    /// entry, at any phase: <c>CollectibleObject.OnLoaded</c> iterates only <c>CollectibleBehaviors</c>,
    /// <c>Block.OnLoaded</c> adds no BlockBehaviors loop of its own, and an IL scan of the whole VintagestoryLib finds
    /// ZERO call sites into <c>CollectibleBehavior.OnLoaded</c> over that array. Drop this line and the placed-path
    /// instance stays uninitialised forever (a null <c>api</c> on the first Shift-right-click).</item>
    /// <item><c>held.OnLoaded(api)</c> is a DOUBLE call, not a rescue: the engine calls it again about a second later,
    /// when <c>ServerSystemBlockSimulation.OnBeginModsAndConfigReady</c> runs <c>OnLoadedNative</c> over every block.
    /// That is harmless ONLY because <c>BlockBehaviorFlotsamBreak.OnLoaded</c> merely caches <c>api</c>/<c>capi</c> and
    /// logs. Keep it idempotent — the day it does non-idempotent init (allocating a handle, registering a listener,
    /// incrementing anything), this line has to go.</item>
    /// </list></para>
    ///
    /// <para><b>Server phase order</b>, which is the OPPOSITE of what the engine's own <c>ModSystem.AssetsFinalize</c>
    /// XML doc claims (that doc is right on the CLIENT, where blocks are loaded before the client's AssetsFinalize, and
    /// wrong on the server): AssetsFinalize (phase 4) → StartServerSide → <c>Block.OnLoadedNative</c> (phase 5,
    /// ModsAndConfigReady) → GameReady (6). So HERE the block registry is already complete, but every Block's own
    /// <c>api</c> field is still null and everything computed in <c>Block.OnLoaded</c> — <c>snowLevel</c>,
    /// <c>notSnowCovered</c>, <c>TextureSubIdForBlockColor</c>, and any derived state in our own block classes — still
    /// reads its default, silently. This method is safe in that window because it touches only <c>Code</c> and the
    /// behavior arrays, both JSON-populated at registration. <c>ExecuteOrder</c> cannot buy its way past this: it orders
    /// mod systems WITHIN a phase, and this is a gap BETWEEN phases. Anything that does need post-OnLoaded block state
    /// must defer, exactly as vanilla <c>vssurvivalmod/Systems/Snowballs.cs</c> defers its otherwise identical
    /// behavior-append to <c>EnumServerRunPhase.WorldReady</c> because it reads <c>snowLevel</c>.</para>
    /// </summary>
    static void AttachFlotsamBreakBehavior(ICoreAPI api)
    {
        foreach (Block block in api.World.Blocks)
        {
            if (block?.Code == null || block.Id == 0) continue;
            if (block.FirstCodePart() != "flotsam") continue;                    // only game:flotsam-driftwood-*
            if (block.HasBehavior(typeof(BlockBehaviorFlotsamBreak))) continue;  // JSON patch already attached it → no double

            // Separate instances per array (as vanilla does): the held path uses the CollectibleBehaviors copy, the
            // placed path the BlockBehaviors copy. Server-side only — a client re-instantiates from the block-type
            // packet and gets ONE shared instance in both arrays (see the doc above), which is equally fine.
            var placed = new BlockBehaviorFlotsamBreak(block);
            var held   = new BlockBehaviorFlotsamBreak(block);
            block.BlockBehaviors       = block.BlockBehaviors.Append(placed);
            block.CollectibleBehaviors = block.CollectibleBehaviors.Append(held);
            placed.OnLoaded(api);
            held.OnLoaded(api);

            api.Logger.Notification("[immersivewoodworking] Re-attached iw-flotsambreak to '{0}' (its behaviors had been replaced by another mod).", block.Code);
        }
    }

    /// <summary>
    /// Build and register one <c>axe + {domain}:log-placed-{wood}-ud → choppingblock</c> grid recipe per discovered
    /// wood species, replacing the old vanilla-only JSON recipe. Clones the enabled:false JSON template
    /// (<see cref="ChoppingBlockRecipeTemplate"/>) — which carries the tag-based "tool-axe" ingredient (parsed by the
    /// engine, not hand-built) and the 2x1 grid shape — then per species pins the log slot to that exact placed log
    /// and bakes the species + its texture domain onto the OUTPUT stack. Baking the output is the one thing a
    /// domain-pinned JSON wildcard can't do, and it's what makes the crafted result AND the handbook preview the right
    /// wood (<see cref="BlockChoppingBlock.OnCreatedByCrafting"/> re-derives the same pair from the crafted log as a
    /// fallback for any non-standard craft path). A species with no real placed-log block (game's decorative-only
    /// charred/petrified, etc.) is skipped silently — "in worldproperties but no craftable log" is an expected gap,
    /// not an error — and "aged" is skipped to preserve the original recipe's deliberate <c>skipVariants:["aged"]</c>.
    /// </summary>
    void RegisterChoppingBlockRecipes(ICoreServerAPI sapi)
    {
        IAsset? templateAsset = sapi.Assets.TryGet(ChoppingBlockRecipeTemplate);
        if (templateAsset == null)
        {
            sapi.Logger.Warning("[immersivewoodworking] Chopping block recipe template {0} not found; no chopping block recipes registered.", ChoppingBlockRecipeTemplate);
            return;
        }

        GridRecipe? template;
        try
        {
            template = templateAsset.ToObject<GridRecipe>();
        }
        catch (System.Exception e)
        {
            sapi.Logger.Warning("[immersivewoodworking] Could not parse chopping block recipe template {0}: {1}", ChoppingBlockRecipeTemplate, e.Message);
            return;
        }

        // Validate the template's shape ONCE (its clones share it): the log slot "L" and the output must be present,
        // or there's nothing meaningful to register.
        if (template?.Ingredients == null || !template.Ingredients.ContainsKey("L") || template.Output == null)
        {
            sapi.Logger.Warning("[immersivewoodworking] Chopping block recipe template {0} is missing its 'L' ingredient or output; no recipes registered.", ChoppingBlockRecipeTemplate);
            return;
        }

        // The per-species recipes below are the real craft path — their baked output makes the crafted stack the right
        // wood on its own — but they are HIDDEN from the handbook's "Created by" section (ShowInCreatedBy=false).
        // Why: the handbook matches a recipe to a page via recipeOutput.Attributes.IsSubSetOf(pageStack.Attributes)
        // (Collectible.Satisfies → ItemStack.Satisfies, vssurvivalmod CollectibleBehaviorHandbookTextAndExtraInfo
        // .addCreatedByInfo). The chopping block shows ONE generic, attribute-less page (BlockChoppingBlock
        // .GetHandBookStacks returns a bare stack), so an output that bakes wood/woodDomain can never be a subset of {}
        // and would never appear there. So instead we register ONE wildcard DISPLAY recipe per domain (bare output →
        // IsSubSetOf({},{})==true) further down, which IS what populates that page — as a clean, species-cycling entry.
        int registered = 0;
        HashSet<string> registeredDomains = new HashSet<string>();
        foreach ((string wood, string domain) in woodSpecies)
        {
            // "aged" is a real placed-log wood but was deliberately excluded from the chopping block recipe (the
            // original recipe's skipVariants:["aged"]); keep it out of the generic registration too.
            if (wood == "aged") continue;

            // The stand is crafted from a barked, upright, placed log. Skip a discovered species that ships no such
            // block (decorative-only woods like game's charred/petrified) so we never register a dead recipe — and
            // silently, because "wood in worldproperties but no placed log" is a normal gap, not an error.
            AssetLocation logCode = new AssetLocation(domain, "log-placed-" + wood + "-ud");
            if (sapi.World.GetBlock(logCode) == null) continue;

            GridRecipe recipe = template.Clone();
            recipe.Enabled = true; // the template ships enabled:false (so RecipeLoader skips it); the clones are live

            // Pin the wildcard/placeholder log slot to this exact species' placed log (clear the name → exact match).
            CraftingRecipeIngredient log = recipe.Ingredients!["L"];
            log.Code = logCode;
            log.Name = null;

            // Output: the variant-less choppingblock with this species + its texture domain baked into the stack, so
            // the result and the handbook preview are the right wood. (Code set explicitly rather than relying on the
            // template's bare "choppingblock" resolving to our domain.)
            recipe.Output!.Code = ChoppingBlockCode.Clone(); // own instance per recipe, not the shared static
            recipe.Output.Attributes = new JsonObject(new JObject { ["wood"] = wood, ["woodDomain"] = domain });
            recipe.Name = new AssetLocation("immersivewoodworking", "choppingblock-" + domain + "-" + wood);

            // Hidden from "Created by": the bare generic handbook page can't match this species-stamped output (see the
            // note above the loop). The one per-domain wildcard recipe below is what shows on that page instead.
            recipe.ShowInCreatedBy = false;

            if (recipe.Resolve(sapi.World, "immersivewoodworking chopping block recipe (" + domain + ":" + wood + ")"))
            {
                sapi.RegisterCraftingRecipe(recipe);
                registered++;
                registeredDomains.Add(domain);
            }
        }

        // Handbook DISPLAY recipe(s): one per domain that registered a species. The log slot is a BASIC (unnamed)
        // wildcard so it stays ONE recipe that the handbook renders as a species-cycling slideshow
        // (SlideshowGridRecipeTextComponent) — a NAMED {wood} wildcard would instead be expanded into one fixed grid per
        // wood and the page would flip whole grids instead of cycling the slot. Note WHO expands it: on this manual
        // registration path the RecipeLoader's GenerateRecipesForAllIngredientCombinations never runs (see
        // RegisterWoodDomainRecipes), so the expansion here is the handbook renderer's own
        // (SlideshowGridRecipeTextComponent.ResolveIngredients clones the recipe per variant for a NAMED ingredient and
        // per-cell shuffles an UNNAMED one) — display only, it changes nothing about what the grid accepts.
        // The output is the BARE choppingblock (no wood/woodDomain) precisely so it satisfies
        // the attribute-less generic page (IsSubSetOf({},{})==true). These ARE craftable, but they overlap the
        // per-species recipes (registered first → a real craft matches one of those and uses its baked output); and if
        // a craft ever resolves through a display recipe instead, BlockChoppingBlock.OnCreatedByCrafting still stamps
        // the species from the consumed log, so the result is correct either way. SkipVariants:["aged"] keeps exact
        // parity with the per-species "aged" skip. The handbook output icon is the default-oak look (the page is
        // generic) — an accepted cosmetic trade for a single clean page.
        int displaysRegistered = 0;
        foreach (string domain in registeredDomains)
        {
            GridRecipe display = template.Clone();
            display.Enabled = true;
            display.ShowInCreatedBy = true; // the default; explicit because THIS is the recipe the handbook page shows

            CraftingRecipeIngredient log = display.Ingredients!["L"];
            log.Code = new AssetLocation(domain, "log-placed-*-ud"); // '*' → basic wildcard; Name stays null (unnamed)
            log.Name = null;
            log.SkipVariants = new[] { "aged" }; // parity with the per-species skip — never offer the aged log

            display.Output!.Code = ChoppingBlockCode.Clone();
            display.Output.Attributes = null; // BARE output → matches the attribute-less generic handbook page
            display.Name = new AssetLocation("immersivewoodworking", "choppingblock-display-" + domain);

            if (display.Resolve(sapi.World, "immersivewoodworking chopping block display recipe (" + domain + ")"))
            {
                sapi.RegisterCraftingRecipe(display);
                displaysRegistered++;
            }
        }

#if IW_DEBUG
        sapi.Logger.Notification("[immersivewoodworking] Registered {0} generic chopping-block grid recipe(s) from {1} discovered wood species, plus {2} handbook display recipe(s).", registered, woodSpecies.Count, displaysRegistered);
#endif
    }

    /// <summary>
    /// Register the OPTIONAL sawhorse hand-craft (stick + rope + stick → one finished trestle) — but ONLY when
    /// <see cref="ImmersiveWoodworkingConfig.CraftableSawhorse"/> is on. That same flag also disables the in-world
    /// stick build (<see cref="BehaviorSawhorseBuild"/>), so the two acquisition paths are mutually exclusive: stick
    /// build by default, craft-and-place when the flag is set (which frees the stick's plain right-click for another
    /// mod). Clones the enabled:false JSON template (<see cref="SawhorseRecipeTemplate"/>) and flips it live, mirroring
    /// the chopping-block registration above; the output is the variant-less <c>sawhorse</c> block (no per-species
    /// stamping needed), which <see cref="BlockSawhorse.OnBlockPlaced"/> places straight to Complete. Server-only, like
    /// the vanilla RecipeLoader — the server's recipe registry is the authority and syncs whole to every client on
    /// join (Packet_Recipes), so registering here reaches all clients and the handbook with no client set to reconcile.
    /// </summary>
    void RegisterSawhorseCraftingRecipe(ICoreServerAPI sapi)
    {
        if (!Config.CraftableSawhorse) return; // off by default → the in-world stick build stays the only path

        IAsset? templateAsset = sapi.Assets.TryGet(SawhorseRecipeTemplate);
        if (templateAsset == null)
        {
            sapi.Logger.Warning("[immersivewoodworking] Sawhorse recipe template {0} not found; no sawhorse craft registered.", SawhorseRecipeTemplate);
            return;
        }

        GridRecipe? recipe;
        try
        {
            recipe = templateAsset.ToObject<GridRecipe>();
        }
        catch (System.Exception e)
        {
            sapi.Logger.Warning("[immersivewoodworking] Could not parse sawhorse recipe template {0}: {1}", SawhorseRecipeTemplate, e.Message);
            return;
        }

        if (recipe?.Ingredients == null || recipe.Output == null)
        {
            sapi.Logger.Warning("[immersivewoodworking] Sawhorse recipe template {0} is missing its ingredients or output; not registered.", SawhorseRecipeTemplate);
            return;
        }

        recipe.Enabled = true; // the template ships enabled:false (so RecipeLoader skips it); this clone is live

        // Stamp the output to the mod-domain block explicitly (own instance, not the shared static) — see the
        // SawhorseBlockCode note above. The game:stick / game:rope inputs are already domain-qualified, so they
        // resolve as-is on this manual path.
        recipe.Output.Code = SawhorseBlockCode.Clone();

        if (recipe.Resolve(sapi.World, "immersivewoodworking sawhorse craft"))
        {
            sapi.RegisterCraftingRecipe(recipe);
#if IW_DEBUG
            sapi.Logger.Notification("[immersivewoodworking] Registered the sawhorse hand-craft (CraftableSawhorse on).");
#endif
        }
    }

    /// <summary>
    /// Re-register the automated SAWMILL's plank/beam-consuming grid recipes (the master frame + the four mandatory
    /// item-parts: sash, crankshaft, levers, carriage) generically across every discovered wood domain. Thin wrapper
    /// over the shared <see cref="RegisterWoodDomainRecipes"/> (the sawmill's blade recipe — metal only — and the pit saw
    /// need none of this, so they load straight from JSON). See that method for the full why.
    /// </summary>
    void RegisterSawmillRecipes(ICoreServerAPI sapi)
        => RegisterWoodDomainRecipes(sapi, SawmillRecipeTemplates, "sawmill");

    /// <summary>
    /// Re-register the automated CHOPPER's plank/beam-consuming grid recipes (the master frame + the two wooden parts
    /// drive/arm) generically across every discovered wood domain — the exact mirror of
    /// <see cref="RegisterSawmillRecipes"/> (AutomatedSawmillPlan T02.03), via the same shared <see cref="RegisterWoodDomainRecipes"/>. The
    /// chopper's METAL-ONLY head recipe (<c>chopperhead-{metal}</c>) is NOT here: it carries no wood, so it ships as a
    /// PLAIN enabled recipe the vanilla RecipeLoader loads in-domain (like <c>sawmill_blade.json</c>), and metal mods'
    /// game-domain ingot/plate variants already work with no per-domain clone. The bed is the hand <c>choppingblock</c>
    /// (its own recipe — no chopper_bed template); the optional flywheel was cut (user decision 2026-07-04).
    /// </summary>
    void RegisterChopperRecipes(ICoreServerAPI sapi)
        => RegisterWoodDomainRecipes(sapi, ChopperRecipeTemplates, "chopper");

    /// <summary>
    /// Shared engine of <see cref="RegisterSawmillRecipes"/> and <see cref="RegisterChopperRecipes"/>: re-register a set
    /// of plank/beam-consuming grid-recipe TEMPLATES generically across every discovered wood domain, so a wood mod's
    /// own-domain planks and support beams craft the powered tiers with no per-mod patch — the same mod-independence the
    /// hand stations already have. WHY this is needed: those recipes pin their wood slots to <c>game:plank-*</c> /
    /// <c>game:supportbeam-*</c>, and a recipe wildcard is DOMAIN-pinned (<c>game:plank-*</c> never matches
    /// <c>wildcrafttree:plank-azobe</c>), while the sawhorses deliberately drop planks/beams in the LOG'S own domain
    /// (<see cref="BlockEntitySawhorse"/>'s ResolveProductItem/Block). So on a wood mod the only planks a player can make
    /// are own-domain, which the <c>game:</c>-pinned recipes reject — the automated tier is uncraftable even though the
    /// hand tier is fully generic. Each template ships <c>enabled:false</c> (RecipeLoader skips it); here we clone it once
    /// per wood domain that actually ships the product, re-point the plank slot to <c>{plankDomain}:plank-*</c> and the
    /// beam slot to <c>{beamDomain}:supportbeam-*</c> — INDEPENDENTLY, so a master frame is registered for every
    /// (plankDomain × beamDomain) pair and a MIXED-wood craft (vanilla planks + a mod's beams) works too; the
    /// single-material parts register once per plank domain. The <c>-*</c> wildcard is kept, so any species in a domain
    /// matches. The templates' <c>name</c> keys do NOT bind their cells on this path and are not meant to: cell binding
    /// exists ONLY as the RecipeLoader's expansion into per-variant sub-recipes
    /// (<c>RecipeBase.GenerateRecipesForAllIngredientCombinations</c>), which nothing here calls, and at match time
    /// <c>CraftingRecipeIngredient.SatisfiesAsIngredient</c> never looks at <c>Name</c>. So a craft mixing woods or
    /// mixing metals across the cells is ACCEPTED — intended, decided by the author 2026-08-29 (AuditFixPlan T10.02,
    /// F-L7-20): these parts bake no species and no metal into their output, so nothing downstream can tell the
    /// difference, and forcing homogeneity would cost thousands of registered sub-recipes for no gameplay effect. See
    /// <c>recipe-conventions.md</c> §"Exceptions" 3. The output's domain is set explicitly (a bare
    /// code resolves to game: on this manual path — see <see cref="SawhorseBlockCode"/>), and register.
    /// <paramref name="label"/> only tags log lines. SERVER-only, like the chopping-block registration and RecipeLoader —
    /// syncs whole to clients via Packet_Recipes.
    /// </summary>
    void RegisterWoodDomainRecipes(ICoreServerAPI sapi, (string stem, string asset)[] templates, string label)
    {
        // Which discovered wood domains actually ship a plank ITEM / support-beam BLOCK (a domain's own products from
        // ripping that wood's logs). A wildcard ingredient Resolves true even with ZERO matching items (the engine
        // returns early for any non-Exact match), so we gate on real existence — otherwise a domain with no planks
        // would register a dead, uncraftable handbook entry. game is always here (vanilla ships planks/beams), so
        // vanilla crafting keeps working now that the static JSON recipes are disabled templates.
        HashSet<string> domainsWithPlank = new HashSet<string>();
        HashSet<string> domainsWithBeam = new HashSet<string>();
        foreach ((string wood, string domain) in woodSpecies)
        {
            if (sapi.World.GetItem(new AssetLocation(domain, "plank-" + wood)) != null) domainsWithPlank.Add(domain);
            if (sapi.World.GetBlock(new AssetLocation(domain, "supportbeam-" + wood)) != null) domainsWithBeam.Add(domain);
        }

        int registered = 0;
        foreach ((string stem, string assetPath) in templates)
        {
            AssetLocation templateLoc = new AssetLocation("immersivewoodworking", assetPath);
            IAsset? templateAsset = sapi.Assets.TryGet(templateLoc);
            if (templateAsset == null)
            {
                sapi.Logger.Warning("[immersivewoodworking] {0} recipe template {1} not found; skipping its recipes.", label, templateLoc);
                continue;
            }

            GridRecipe? template;
            try
            {
                template = templateAsset.ToObject<GridRecipe>();
            }
            catch (System.Exception e)
            {
                sapi.Logger.Warning("[immersivewoodworking] Could not parse {0} recipe template {1}: {2}", label, templateLoc, e.Message);
                continue;
            }
            if (template?.Ingredients == null || template.Output == null) continue;

            bool needsBeam = TemplateNeedsBeam(template);

            // Re-point the plank slot and the beam slot INDEPENDENTLY. A player can hold planks and beams from DIFFERENT
            // wood mods (vanilla planks + a mod's beams, or any mix), so the master frame — the only recipe consuming
            // both — is registered for every (plankDomain × beamDomain) pair; the single-material parts (planks only)
            // register once per plank domain. Without the cross product a MIXED-wood frame craft is rejected even
            // though each material exists ("only vanilla" and "only modded" work, a mix doesn't).
            foreach (string plankDomain in domainsWithPlank)
            {
                if (needsBeam)
                {
                    foreach (string beamDomain in domainsWithBeam)
                        if (BuildAndRegisterWoodDomainRecipe(sapi, template, stem, plankDomain, beamDomain)) registered++;
                }
                else if (BuildAndRegisterWoodDomainRecipe(sapi, template, stem, plankDomain, null))
                {
                    registered++;
                }
            }
        }

#if IW_DEBUG
        sapi.Logger.Notification("[immersivewoodworking] Registered {0} generic {1} grid recipe(s) across {2} plank domain(s).", registered, label, domainsWithPlank.Count);
#endif
    }

    /// <summary>True if this wood-domain recipe template consumes a support beam (only a master frame does) — so its
    /// per-domain registration must also require the domain to ship a <c>supportbeam-*</c> block, not just planks.</summary>
    static bool TemplateNeedsBeam(GridRecipe template)
    {
        if (template.Ingredients == null) return false;
        foreach (CraftingRecipeIngredient ing in template.Ingredients.Values)
            if (ing.Code?.Path?.StartsWith("supportbeam-", System.StringComparison.Ordinal) == true) return true;
        return false;
    }

    /// <summary>Clone one wood-domain recipe template, re-point its plank slot(s) to <paramref name="plankDomain"/> and
    /// (for a frame) its support-beam slot(s) to <paramref name="beamDomain"/> — the two are INDEPENDENT so a mixed-wood
    /// craft (e.g. vanilla planks + a mod's beams) resolves — stamp the in-domain output, and register it. The -* wildcard
    /// is kept, so any species within each domain matches; the templates' <c>name</c> keys are inert on this manual path
    /// and mixing species or metals between the cells is accepted by design (see <see cref="RegisterWoodDomainRecipes"/>
    /// for why). <paramref name="beamDomain"/> is null for the single-material parts (planks only). Returns true if
    /// the recipe resolved and was registered.</summary>
    bool BuildAndRegisterWoodDomainRecipe(ICoreServerAPI sapi, GridRecipe template, string stem, string plankDomain, string? beamDomain)
    {
        GridRecipe recipe = template.Clone();
        recipe.Enabled = true; // the template ships enabled:false (so RecipeLoader skips it); the clones are live

        // Only the DOMAIN is swapped; the plank and beam slots each take their own so they can come from different wood
        // mods. The metal/resin/tool slots are left untouched (mods add those as game-domain variants, not their own).
        foreach (CraftingRecipeIngredient ing in recipe.Ingredients!.Values)
        {
            string? path = ing.Code?.Path;
            if (path == null) continue;
            if (path.StartsWith("plank-", System.StringComparison.Ordinal))
                ing.Code = new AssetLocation(plankDomain, path);
            else if (beamDomain != null && path.StartsWith("supportbeam-", System.StringComparison.Ordinal))
                ing.Code = new AssetLocation(beamDomain, path);
        }

        // Output is always the in-domain mod block/item; stamp its domain explicitly (a bare code resolves to game: on
        // this manual ToObject+Resolve path and would fail to resolve — same reason as SawhorseBlockCode).
        if (recipe.Output?.Code == null) return false;
        recipe.Output.Code = new AssetLocation("immersivewoodworking", recipe.Output.Code.Path);

        // Name must be UNIQUE per (template, plankDomain[, beamDomain]) or a colliding registration is dropped.
        recipe.Name = new AssetLocation("immersivewoodworking",
            beamDomain != null ? stem + "-" + plankDomain + "-" + beamDomain : stem + "-" + plankDomain);

        if (!recipe.Resolve(sapi.World, "immersivewoodworking grid recipe (" + recipe.Name.Path + ")")) return false;
        sapi.RegisterCraftingRecipe(recipe);
        return true;
    }

    /// <summary>
    /// Discover every wood species (bare code + its domain) from all <c>worldproperties/block/wood</c> assets across
    /// EVERY domain — vanilla (<c>game</c>), Wildcraft: Trees (<c>wildcrafttree</c>) and any other wood-adding mod — so
    /// the chopping block's per-species creative stacks (ChoppingBlockRedesignPlan T03.02) and generic grid recipes (T04.02) cover any mod's
    /// woods with no per-mod patch. The species' domain is the worldproperties asset's OWN domain, which is also where
    /// its <c>log-placed-{wood}-ud</c> block lives: a mod ships its own wood.json + its own logs in its domain
    /// (wildcrafttree does), while a mod that instead PATCHES game's wood.json adds game-domain logs — either way the
    /// asset domain equals the log domain, which is what the recipe input (T04.02) needs. Both sides: the client builds
    /// the creative/handbook stacks, the server the recipes; worldproperties are on disk on both. <c>GetMany&lt;T&gt;</c>
    /// (the engine's own RecipeLoader uses it) deserializes each asset and logs+skips a malformed one, so one bad mod
    /// file can't break the set. A variant's <c>Code</c> is an AssetLocation: its <c>.Path</c> is the bare species
    /// ("oak"), independent of however its (usually absent) domain defaulted — the real domain is the file's.
    /// </summary>
    void DiscoverWoodSpecies(ICoreAPI api)
    {
        List<(string wood, string domain)> found = new List<(string wood, string domain)>();
        HashSet<string> seen = new HashSet<string>();

        foreach ((AssetLocation loc, StandardWorldProperty prop) in
                 api.Assets.GetMany<StandardWorldProperty>(api.Logger, "worldproperties/block/wood.json"))
        {
            if (prop?.Variants == null) continue;
            foreach (WorldPropertyVariant variant in prop.Variants)
            {
                string? wood = variant?.Code?.Path;
                if (string.IsNullOrEmpty(wood)) continue;
                if (seen.Add(loc.Domain + ":" + wood)) found.Add((wood!, loc.Domain));
            }
        }

        woodSpecies = found;

#if IW_DEBUG
        HashSet<string> domains = new HashSet<string>();
        foreach ((string _, string domain) in found) domains.Add(domain);
        api.Logger.Notification("[immersivewoodworking] Discovered {0} chopping-block wood species across {1} domain(s).", found.Count, domains.Count);
#endif
    }

    /// <summary>
    /// Discover the wood species that have a LOG BLOCK but no <c>worldproperties/block/wood</c> entry — the ones
    /// <see cref="DiscoverWoodSpecies"/> structurally cannot see (AuditFixPlan T04.05, F-PT-02). Vanilla writes four
    /// of them as literal <c>states</c> on its own log assets: <c>log.json</c> adds <c>aged</c>, and
    /// <c>log-debarked.json</c> adds <c>aged</c>, <c>veryaged</c>, <c>rotten</c> and <c>veryrotten</c>, none of which
    /// appear in any worldproperties file. A half-log is stamped from the LOG it was split off, so those species
    /// reach the half-log slab, whose <c>wood</c> axis was built from worldproperties alone — which is why an aged
    /// half-log had no slab to place and the click did nothing at all.
    ///
    /// <para>Matched by the asset's own <c>code</c> being <c>log</c> or <c>debarkedlog</c> — the SAME pair
    /// <see cref="BlockEntityChoppingBlock.IsFullLogBlock"/> accepts for the split, so this list can never cover more
    /// or less than the set of species a half-log can actually carry. All domains, so a wood mod that writes literal
    /// states gets its variants with no patch from us; the species' domain is the asset's own, exactly as in
    /// <see cref="DiscoverWoodSpecies"/> (a mod ships its logs in the domain its blocktype lives in).
    ///
    /// <para><b>Both halves of the vanilla <c>wood</c> group are read</b> — the literal <c>states</c> AND whatever
    /// <c>loadFromProperties</c> names — because the second is what the engine itself does to expand the group, and a
    /// mod is free to point it at a property file that is not <c>block/wood</c> (which DiscoverWoodSpecies, keyed on
    /// that one filename, would then miss). The overlap with DiscoverWoodSpecies is the normal case and costs
    /// nothing: <see cref="BuildWoodDomainIndex"/> is a set union either way.
    ///
    /// <para><b>Cheap by construction.</b> A full JSON parse of every blocktype asset in every domain would be some
    /// 500 files and several megabytes to find four; instead each asset's raw text is scanned with one regex for a
    /// <c>code</c> key valued <c>log</c> / <c>debarkedlog</c>, and only the handful that match are parsed. The parse
    /// then re-checks the TOP-LEVEL code properly, so a stray nested match costs a parse and nothing else. Measured
    /// offline over vanilla's 543 blocktypes: 3 files match (log, log-debarked, log-withresin), and all 3 parse.
    ///
    /// <para><b>The text scanned is not the file on disk.</b> The engine's JSON patch loader (VSEssentials
    /// <c>ModJsonPatchLoader</c>, ExecuteOrder 0.05, so it runs before this 0.1 pass) writes every asset a mod has
    /// patched back through a default <c>JsonTextWriter</c>: one line, keys quoted, comments gone. The first probe
    /// was anchored at a line start and so matched the disk layout only; it skipped exactly the vanilla log files
    /// that In Dappled Groves and Wildcraft Trees patch, and on those installs the four species had no slab and
    /// their half-logs could not be placed (found 2026-09-10). Anything that scans <c>ToText()</c> has to accept
    /// either layout.
    ///
    /// <para>SERVER-ONLY in effect and without a side guard, the same way <see cref="InjectWoodVariants"/> is:
    /// <c>blocktypes</c> is a server-side asset category, so on a client <c>GetMany</c> finds nothing and this leaves
    /// an empty list. Nothing on the client needs it — the block registry arrives over the wire, and the one index a
    /// client would build from it (species → domain) already answers <c>game</c> for all four vanilla states.
    /// </summary>
    void DiscoverLogOnlySpecies(ICoreAPI api)
    {
        List<(string wood, string domain)> found = new List<(string wood, string domain)>();
        HashSet<string> seen = new HashSet<string>();

        foreach (IAsset asset in api.Assets.GetMany("blocktypes/"))
        {
            string text;
            try { text = asset.ToText(); }
            catch { continue; }                                    // unreadable asset — not ours to report

            if (!FullLogAssetProbe.IsMatch(text)) continue;        // cheap pre-filter, see the doc above

            // The engine's read, not JObject.Parse: a log blocktype the block loader accepts must not be skipped here
            // (see ReadAssetLikeEngine). One it rejects, the block loader reports soon enough.
            if (ReadAssetLikeEngine(asset) is not JObject block) continue;

            string? code = (string?)block["code"];
            if (code != "log" && code != "debarkedlog") continue;  // a nested match — re-checked properly

            if (block["variantgroups"] is not JArray groups) continue;
            foreach (JToken group in groups)
            {
                if ((string?)group["code"] != "wood") continue;

                if (group["states"] is JArray states)
                    foreach (JToken state in states)
                        AddSpecies(found, seen, (string?)state, asset.Location.Domain);

                string? props = (string?)group["loadFromProperties"];
                if (props != null)
                    foreach ((string wood, string domain) in SpeciesFromProperties(api, props, asset.Location.Domain))
                        AddSpecies(found, seen, wood, domain);

                break;                                             // one `wood` group per block
            }
        }

        logOnlySpecies = found;

#if IW_DEBUG
        HashSet<string> fromProperties = new HashSet<string>();
        foreach ((string wood, string _) in woodSpecies) fromProperties.Add(wood);
        int extra = 0;
        foreach ((string wood, string _) in found) if (!fromProperties.Contains(wood)) extra++;
        api.Logger.Notification("[immersivewoodworking] Log blocktypes carry {0} wood state(s), {1} of them absent from every worldproperties (expected: vanilla aged/veryaged/rotten/veryrotten).", found.Count, extra);
#endif
    }

    /// <summary>A <c>code</c> key valued exactly <c>log</c> or <c>debarkedlog</c>, in both spellings VS JSON allows
    /// (bare key and quoted key), WHEREVER it sits on its line. All it asks of the position is that <c>code</c> is a
    /// whole key name (<c>(?&lt;!\w)</c> keeps <c>barkcode</c> out); the closing quote right after the value keeps a
    /// drop's <c>log-placed-{wood}-ud</c> out.
    ///
    /// <para>Deliberately NOT anchored to a line start: a patched asset arrives as one line (see
    /// <see cref="DiscoverLogOnlySpecies"/>), a minified mod file has no line starts to speak of, and a comment may
    /// precede the key. A nested match is possible in principle and costs one parse, because
    /// <see cref="DiscoverLogOnlySpecies"/> re-checks the parsed top-level field. The contract is to be cheap and
    /// never MISS, not to be exact.</para></summary>
    static readonly System.Text.RegularExpressions.Regex FullLogAssetProbe = new System.Text.RegularExpressions.Regex(
        "(?<!\\w)\"?code\"?\\s*:\\s*\"(log|debarkedlog)\"",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Add one (species, domain) pair unless the exact pair is already there. Blank codes are skipped; the
    /// same species from two domains is kept twice, exactly as <see cref="DiscoverWoodSpecies"/> keeps it — the
    /// collapse to one domain per bare code is <see cref="BuildWoodDomainIndex"/>'s job, not this one's.</summary>
    static void AddSpecies(List<(string wood, string domain)> into, HashSet<string> seen, string? wood, string domain)
    {
        if (string.IsNullOrEmpty(wood)) return;
        if (seen.Add(domain + ":" + wood)) into.Add((wood!, domain));
    }

    /// <summary>The species listed by the worldproperties asset a variant group's <c>loadFromProperties</c> names
    /// (e.g. <c>block/wood</c>, or a mod's own <c>wildcrafttree:block/wood</c>). The species' domain is the PROPERTY
    /// asset's domain — an unqualified path resolves in the asking blocktype's own domain, which is what the engine
    /// does and what keeps a mod's own logs and its own species in one domain. Empty on a missing or malformed
    /// asset.</summary>
    static IEnumerable<(string wood, string domain)> SpeciesFromProperties(ICoreAPI api, string path, string askingDomain)
    {
        AssetLocation loc = new AssetLocation(path.Contains(':') ? path : askingDomain + ":" + path)
            .WithPathPrefixOnce("worldproperties/").WithPathAppendixOnce(".json");

        StandardWorldProperty? prop = null;
        try { prop = api.Assets.TryGet(loc)?.ToObject<StandardWorldProperty>(); }
        catch { /* malformed: the engine's own loader reports it */ }

        if (prop?.Variants == null) yield break;
        foreach (WorldPropertyVariant variant in prop.Variants)
        {
            string? wood = variant?.Code?.Path;
            if (!string.IsNullOrEmpty(wood)) yield return (wood!, loc.Domain);
        }
    }

    // Lower bound of the metal-tier factor (ManualOpsBalancePlan E02). No REAL tool lands on it — the weakest vanilla
    // stone axe is 2.6/11 ≈ 0.24, and a speed-less variant of a known family now drops to that weakest indexed tier
    // rather than here (AuditFixPlan T03.02) — so it only guards against a mis-authored wood-mod tool with a near-zero
    // wood speed silently freezing progress, and against a degenerate single-tier family with nothing weaker to point
    // at. Mirrors the [0.05, 1] floor the config's other speed multipliers use.
    const float TierSpeedFloor = 0.05f;

    // The CHOPPING AXE's ceiling: the vanilla steel felling axe's wood mining speed, hard-pinned as a COMPILED constant
    // instead of being read from the runtime scan, so no mod can raise it by any mechanism (a modded "steel" axe with a
    // higher wood speed, a runtime mining-speed buff, a load-order race). Two readers, and they must agree: it is the
    // reference BuildWoodToolSpeedIndex forces onto the "axe" family, and the fallback anchor UnknownFamilyAnchor hands a
    // third-party tool whose own family never entered the index (AuditFixPlan T03.02). It never crosses the wire, so it
    // needs no QuantizeLikeWire — and 11 sits exactly on the 1/64 grid anyway (11×64 = 704). If vanilla ever rebalances
    // the steel felling axe, this one constant is the whole edit.
    const float VanillaSteelAxeWoodSpeed = 11f;

    /// <summary>Round a collectible's JSON float DOWN onto the same 1/64 grid the network puts it on, so the server and
    /// the client arrive at the same number BY CONSTRUCTION rather than by coincidence (AuditFixPlan T02.01).
    ///
    /// <para><b>Why this exists.</b> <c>itemtypes</c> / <c>blocktypes</c> are SERVER-side asset categories: the client
    /// never parses that JSON at all, it receives the whole registry over the wire. And the wire quantizes every float
    /// field of a collectible — <c>Vintagestory.Common.CollectibleNet.SerializeFloat(p) => (int)(p * 64f)</c>, a
    /// TRUNCATION, paired with <c>DeserializeFloat(p) => p / 64f</c>. So an author's <c>6.6</c> reaches the client as
    /// <c>422/64 = 6.59375</c> while the server keeps <c>6.6</c>, and this holds in SINGLE-PLAYER too:
    /// <c>ServerMain.BuildServerAssetsPacket</c> packs the registry unconditionally, with no same-process shortcut, and
    /// the client assigns <c>game.Items</c> in exactly one place — from that packet. The list of quantized fields
    /// includes <c>MiningSpeed[*]</c>, <c>CombustibleProps.BurnDuration</c> / <c>MeltingDuration</c>,
    /// <c>Block.Resistance</c>, <c>AttackPower</c> and the nutrition / transition floats; a collectible's
    /// <c>Attributes</c> JsonObject (which travels as a STRING) and an ItemStack's own attribute tree are IMMUNE.
    ///
    /// <para><b>Why quantize instead of trusting parity.</b> Any quantity both sides derive from the registry
    /// INDEPENDENTLY diverges, and divisions hide it: when the numerator IS the anchor (a steel tool over its own steel
    /// reference) the error cancels exactly, so a test swing with a steel axe shows perfect agreement and proves
    /// nothing. A copper axe does not cancel — 0.4727273 server vs 0.4715909 client — and once that hair seeds a hash
    /// (<see cref="BlockEntityChoppingBlock.ChopJitterFactor"/>) the finalizer scatters the two sides into unrelated
    /// values. Building the index in <c>AssetsFinalize</c> does NOT help: on the client that phase runs out of
    /// <c>SystemModHandler.OnLevelFinalize()</c>, i.e. AFTER <c>LoadItemTypes</c>, so the client indexes the ALREADY
    /// quantized registry. Pulling the server DOWN onto the client's grid is the only direction that converges.
    ///
    /// <para>Idempotent for anything already on the grid (k/64 is exact in float for realistic k, and multiplying by 64
    /// is exact), so the client may safely re-quantize what the wire already truncated. Assumes a non-negative input —
    /// every call site guards <c>&gt; 0</c> first — since C# int truncation rounds toward zero, not down.</para></summary>
    public static float QuantizeLikeWire(float v) => (int)(v * 64f) / 64f;

    /// <summary>The wire's quantization step (1/64). A threshold compared against ZERO is only accidentally safe: a
    /// value inside <c>(0, WireQuantum)</c> survives on the server and truncates to 0 on the client, flipping the test
    /// between the sides. Comparing against THIS instead is safe by construction. See <see cref="QuantizeLikeWire"/>.</summary>
    public const float WireQuantum = 1f / 64f;

    /// <summary>Turn the engine's <c>secondsUsed</c> hold argument into an HONEST hold clock — the real seconds this
    /// right-click has been held — for every timed gesture the mod owns (AuditFixPlan T02.03).
    ///
    /// <para><b>Why this exists.</b> <c>secondsUsed</c> is NOT a clock. The client drives the hold per FRAME but only
    /// packets it every 0.15 s (<c>SystemMouseInWorldInteractions</c>: <c>stepPacketAccum &gt; 0.15</c>), sending its
    /// per-frame <c>Controls.UsingCount</c> along. On arrival the server computes the base ONCE —
    /// <c>secondsPassed = (server.ElapsedMilliseconds - controls.UsingBeginMS) / 1000f</c> — and then REPLAYS the
    /// missed frames in a loop whose body ends with <c>secondsPassed += 0.02f</c>
    /// (<c>ServerSystemBlockSimulation.callOnUsingBlock</c> for blocks, <c>ServerSystemInventory.callOnUsing</c> for
    /// held items). So every call after the first in a burst carries a value LARGER than the time that actually
    /// passed. At a client tick of 20 ms a burst is 8 steps, i.e. the server runs a permanent <b>+0.14…0.16 s</b>
    /// ahead — it does not accumulate, but it never goes away either, and the release burst before
    /// <c>OnBlockInteractStop</c> / <c>OnHeldInteractStop</c> hands those handlers the same inflated number.
    ///
    /// <para><b>What it costs unclamped.</b> Progress summed from these deltas over-credits by that constant (a 1.5 s
    /// strip finishes in 1.36 s), a threshold tested against it fires early (driftwood breaks before the configured
    /// time), and a threshold RE-BASED on it stays permanently ahead of the client's — which is how a chop landed
    /// after the button was already released.
    ///
    /// <para><b>How.</b> The base the engine started from is reconstructible: <c>Controls.UsingBeginMS</c> is public
    /// API and the engine stamps it at <c>StartBlockUse</c> / <c>StartHeldItemUse</c>, so
    /// <c>(world.ElapsedMilliseconds - UsingBeginMS) / 1000</c> IS that un-inflated base. The inflation is only ever
    /// additive, so <c>min</c> of the two recovers the honest value exactly. A whole burst therefore collapses onto
    /// one value: the world clock advances by at most a millisecond or two across the handful of microseconds the
    /// replay loop takes, against the 0.02 s a step adds. That collapse is correct, not a loss — those frames are
    /// already in the past, and the value they share is the client's CURRENT hold time.
    ///
    /// <para>SERVER ONLY. The client's own <c>secondsUsed</c> is already honest — it is recomputed from its own
    /// <c>UsingBeginMS</c> every frame and never replayed — so it is returned untouched rather than min'd against a
    /// clock that is not guaranteed to share the client's origin. Callers therefore get one number that means the same
    /// thing on both sides, which is what makes a client-side prediction and the server's authority agree.
    ///
    /// <para>Apply at the OUTERMOST seam of a gesture (the <c>Block</c>/<c>BlockBehavior</c> override), once, before
    /// anything downstream sees the argument: deltas, thresholds and re-bases all inherit the honest value and need no
    /// arithmetic of their own. Defensive against a hold whose start stamp is missing or stale (a gesture the engine
    /// never opened): a negative reconstruction is ignored and the argument passes through.</para></summary>
    public static float HonestHoldSeconds(IWorldAccessor world, EntityAgent? byEntity, float secondsUsed)
    {
        if (world.Side != EnumAppSide.Server) return secondsUsed; // client clock is per-frame and never replayed
        long beginMs = byEntity?.Controls?.UsingBeginMS ?? 0L;
        if (beginMs <= 0L) return secondsUsed;                    // no hold stamp to measure against
        float real = (world.ElapsedMilliseconds - beginMs) / 1000f;
        return real < 0f ? secondsUsed : GameMath.Min(secondsUsed, real);
    }

    /// <inheritdoc cref="HonestHoldSeconds(IWorldAccessor, EntityAgent, float)"/>
    public static float HonestHoldSeconds(IWorldAccessor world, IPlayer? byPlayer, float secondsUsed)
        => HonestHoldSeconds(world, byPlayer?.Entity, secondsUsed);

    /// <summary>Scan every fully-resolved collectible once and record, per tool FAMILY (its
    /// <see cref="CollectibleObject.FirstCodePart()"/> — "axe", "saw", "pitsaw", "barkspud", or any wood mod's own tool
    /// code), the reference wood mining speed (<c>MiningSpeed[<see cref="EnumBlockMaterial.Wood"/>]</c>) that
    /// <see cref="ToolTierSpeedFactor"/> normalises against. The reference is the family's STEEL variant (metal = the
    /// LAST code part, e.g. <c>axe-steel</c>): STEEL is the CEILING by design — a steel tool lands at exactly 1.0, lesser
    /// metals fall proportionally below it, and anything AT OR ABOVE steel (meteoric iron, a modded super-tool) is capped
    /// to 1.0 by <see cref="ToolTierSpeedFactor"/>'s clamp, i.e. it works EXACTLY like steel with no extra benefit (user
    /// requirement: no tool is faster than steel). We deliberately do NOT use the family MAX as the divisor — a
    /// better-than-steel tool (notably the vanilla meteoric-iron AXE, whose wood speed exceeds the steel axe) would then
    /// raise the bar and drop STEEL below 1.0 (the observed "steel fills to 91%, not 100%" bug). Families with NO steel
    /// variant (the single-item pit saw; a wood-mod tool that stops at bronze) have no steel anchor and fall back to
    /// their own family MAX, so their best available tool is the 1.0. Runs in <see cref="AssetsFinalize"/> on BOTH sides
    /// — the client needs its own copy, since it never parses <c>itemtypes</c> and cannot ask the server per strike. It
    /// does NOT see the server's numbers, though: the registry crosses the wire truncated onto a 1/64 grid, so every
    /// speed entering this scan goes through <see cref="QuantizeLikeWire"/> first and the server ends up on the client's
    /// grid — parity by construction, not by "both sides read the same JSON" (AuditFixPlan T02.01). A tool with
    /// no wood mining speed never enters the index; what that ABSENCE then means is decided per case by
    /// <see cref="ToolTierSpeedFactor"/>, which reads both this ceiling index and the <see cref="woodToolSpeedMin"/>
    /// floor index it fills alongside.</summary>
    void BuildWoodToolSpeedIndex(ICoreAPI api)
    {
        Dictionary<string, float> steelRef = new Dictionary<string, float>(); // steel variant's wood speed per family (the anchor)
        Dictionary<string, float> maxRef = new Dictionary<string, float>();    // family max — the fallback anchor when no steel variant exists
        Dictionary<string, float> minRef = new Dictionary<string, float>();    // family MIN — the [0]-bonus anchor for MetalTierBonusFraction (BarkPlan T02.05)
        foreach (CollectibleObject coll in api.World.Collectibles)
        {
            if (coll?.Code == null || coll.MiningSpeed == null) continue;
            if (!coll.MiningSpeed.TryGetValue(EnumBlockMaterial.Wood, out float ws)) continue;
            // Quantize BEFORE the > 0 test, not after: the client only ever sees the 1/64-truncated value, so a tool
            // authored inside (0, 1/64) is a REAL tool to the server and a zero-speed one to the client. Truncating
            // first makes both sides admit exactly the same tools to the index, then compare the same numbers.
            ws = QuantizeLikeWire(ws);
            if (ws <= 0f) continue;
            // FIXED ceiling: only vanilla ("game") and this mod's own tools may DEFINE the steel reference. A third-party
            // mod that ships a stronger-than-steel axe/saw must NOT be able to raise it — otherwise every lesser metal is
            // divided by an inflated anchor and silently slows down. (A modpack pushed the "axe" reference to ~15.8 wood,
            // so the copper axe fell from 0.47 to 0.33 per strike → a log took 4 hits, the 4th wasted at 99%.) Such tools
            // still GET a factor against this fixed reference, clamped to 1.0 in ToolTierSpeedFactor — at or above steel =
            // chops exactly like steel — they simply can't MOVE the reference.
            string domain = coll.Code.Domain;
            if (domain != "game" && domain != "immersivewoodworking") continue;
            string family = coll.FirstCodePart();
            if (string.IsNullOrEmpty(family)) continue;

            if (!maxRef.TryGetValue(family, out float cur) || ws > cur) maxRef[family] = ws;
            if (!minRef.TryGetValue(family, out float lo) || ws < lo) minRef[family] = ws; // family MIN for the T02.05 chance-bonus [0]-anchor
            // The metal is conventionally the LAST code part (axe-steel / saw-steel / barkspud-steel), so this picks the
            // steel variant without hardcoding any full tool code. One steel variant per family, so a plain set is fine.
            if (coll.LastCodePart() == "steel") steelRef[family] = ws;
        }

        // Reference = the steel variant where one exists (steel is the ceiling), else the family max as a fallback.
        Dictionary<string, float> found = new Dictionary<string, float>(maxRef.Count);
        foreach (string family in maxRef.Keys)
            found[family] = steelRef.TryGetValue(family, out float s) ? s : maxRef[family];

        // The chopping AXE is the tier a player leans on from day one, so its ceiling is HARD-PINNED to the vanilla steel
        // felling axe's wood mining speed — NOT read from the runtime scan at all (rationale and the constant itself:
        // VanillaSteelAxeWoodSpeed at class scope). A steel-or-better axe still lands at 1.0 = ONE strike (the clamp in
        // ToolTierSpeedFactor caps anything ≥11); a copper axe sits at 5.2/11 ≈ 0.47 = ~3 strikes on a whole log. The
        // NUMERATOR divided by it in ToolTierSpeedFactor is quantized, which is where the client/server fork lived.
        found["axe"] = VanillaSteelAxeWoodSpeed;

        woodToolSpeedRef = found;
        woodToolSpeedMin = minRef; // family MIN anchors (BarkPlan T02.05 chance bonus); the axe hard-pin above touches only the REF/ceiling, not this

#if IW_DEBUG
        api.Logger.Notification("[immersivewoodworking] Wood-tool speed index: {0} tool families ({1} anchored on steel).", found.Count, steelRef.Count);
        foreach (var kv in found)
            api.Logger.Notification("[immersivewoodworking]   [iw-tier] reference family '{0}' = {1} wood-speed (steel=1 strike)", kv.Key, kv.Value);
#endif
    }

    /// <summary>
    /// Index the wood species that DON'T BURN, so this mod's species-in-attributes products can honour a wood mod's
    /// deliberate "this material is not fuel" decision (HalfLogBlockPlan T03.01 follow-up).
    ///
    /// <para>The problem it solves: our species list comes from <see cref="DiscoverWoodSpecies"/>, which reads
    /// <c>worldproperties</c> in EVERY domain, and not everything in there is wood. Wildcraft Trees ships
    /// <c>petrified</c> — stone — and systematically strips its combustion in seven of its own assets
    /// (<c>*-petrified-*: null</c> in the log, debarked log, planks, plank slab, plank stairs, support beam and the
    /// plank item), and pointedly leaves it out of its firewood recipe. The half-log SLAB matches that with one
    /// <c>combustiblePropsByType</c> line, because a block carries its species in the variant code. The half-log
    /// ITEM cannot: its species lives in stack attributes (<c>wood</c> / <c>woodDomain</c>), so a byType glob has
    /// nothing to match and the flat <c>combustibleProps</c> in <c>itemtypes/halflog.json</c> applies to every species.
    /// This index is the code half of that fix, read by <see cref="ItemHalfLog.GetCombustibleProperties"/>.
    ///
    /// <para>The rule is GENERAL, not a <c>petrified</c> whitelist: for each species we look at ITS OWN logs — exactly
    /// the blocks a half-log is made from (<see cref="BlockEntityChoppingBlock.IsFullLogBlock"/>) — and mark the species
    /// non-combustible only when at least one such log exists and NOT ONE of them burns. So any wood mod that removes
    /// combustion from a species the way Wildcraft Trees does gets the same treatment with no per-mod patch, while a
    /// species we can't judge (no log block at all, an unreadable code shape) is left alone and keeps burning — the
    /// pre-fix behaviour is always the fallback. "At least one burns ⇒ burns" is the conservative direction on purpose:
    /// a mod with a MIS-AUTHORED byType table (vanilla's own <c>debarkedlog</c> table is dead — its keys are
    /// <c>log-*-…</c> against a <c>debarkedlog-…</c> code — and silently falls through to its <c>*</c> default) must not
    /// be read as an intentional "not fuel".
    ///
    /// <para>Keys are built by <see cref="BlockEntityChoppingBlock.LogBlockSpecies"/>, the same call that STAMPS the
    /// species onto a half-log at chop time, so the index and the stack can only ever agree.
    ///
    /// <para>Runs in <see cref="AssetsFinalize"/> on BOTH sides, alongside <see cref="BuildWoodToolSpeedIndex"/> and over
    /// the same fully-resolved <c>api.World.Collectibles</c> registry: the server decides whether a half-log is fuel, the
    /// client draws the tooltip and the creative stacks, and both must say the same thing. That agreement is NOT free —
    /// the client's copy of the registry arrived over the wire with <c>BurnDuration</c> truncated onto a 1/64 grid, so
    /// the "does it burn" test thresholds on <see cref="WireQuantum"/> instead of 0 (AuditFixPlan T02.01). Reads the block's
    /// <c>CombustibleProps</c> FIELD rather than calling <c>GetCombustibleProperties</c> — that is what vanilla's own
    /// registry scans do (BlockForge / BlockBloomery / BlockClayOven build their fuel lists off the field), and it can't
    /// be tripped by a third-party block class that assumes a non-null world or position.
    /// </summary>
    void BuildNonCombustibleWoodIndex(ICoreAPI api)
    {
        // "{domain}:{wood}" -> does ANY log of that species burn. Absent = no log seen for that species at all.
        Dictionary<string, bool> anyLogBurns = new Dictionary<string, bool>();

        foreach (CollectibleObject coll in api.World.Collectibles)
        {
            if (coll is not Block block) continue;
            if (!BlockEntityChoppingBlock.IsFullLogBlock(block)) continue;

            (string? wood, string domain) = BlockEntityChoppingBlock.LogBlockSpecies(block);
            if (string.IsNullOrEmpty(wood)) continue;

            // BurnDuration > 0 is the engine's own "is this fuel" test (BEBehaviorBurning), so a species whose logs
            // declare a table with a zero duration counts as non-combustible just like an outright null. The threshold
            // is WireQuantum, not a bare 0 (AuditFixPlan T02.01 / F-L4-15): BurnDuration is one of the floats the wire
            // truncates onto the 1/64 grid, so a log declaring anything in (0, 0.015625) would burn on the server and
            // read as non-fuel on the client, and this index has to say the SAME thing on both — the server decides
            // whether a half-log is fuel, the client draws the tooltip and the creative stacks off it. Today no shipped
            // content sits in that window (vanilla's minimum is 1, wood mods use 4), but that is a fact about content,
            // not a property of this code. A sub-quantum duration is unusable as fuel anyway — the client would never
            // see it — so treating it as "doesn't burn" is also the honest reading.
            bool burns = block.CombustibleProps != null && block.CombustibleProps.BurnDuration >= WireQuantum;
            string key = domain + ":" + wood;
            anyLogBurns[key] = anyLogBurns.TryGetValue(key, out bool already) ? already || burns : burns;
        }

        HashSet<string> found = new HashSet<string>();
        foreach (KeyValuePair<string, bool> kv in anyLogBurns)
            if (!kv.Value) found.Add(kv.Key);

        nonCombustibleWoods = found;

#if IW_DEBUG
        api.Logger.Notification("[immersivewoodworking] Non-combustible wood index: judged {0} species from their logs, {1} non-combustible.",
            anyLogBurns.Count, found.Count);
#endif

        // Only speak up when there IS something — a vanilla-only install has no such species and stays silent. Worth a
        // plain Notification (not IW_DEBUG): it is the one line that answers "why doesn't my half-log burn?".
        if (found.Count > 0)
        {
            api.Logger.Notification(
                "[immersivewoodworking] {0} non-combustible wood species (their own logs don't burn, so neither do our half-logs): {1}.",
                found.Count, string.Join(", ", found));
        }
    }

    /// <summary>True if <paramref name="wood"/> in <paramref name="domain"/> is a species whose own logs carry no
    /// combustion — i.e. the source wood mod deliberately made that material non-fuel (Wildcraft Trees' stone
    /// <c>petrified</c> being the shipped case). Products of ours that store the species in stack attributes, where a
    /// JSON <c>combustiblePropsByType</c> is impossible, gate their burn on this. False until
    /// <see cref="AssetsFinalize"/>, and false for any species we couldn't judge — the fallback is always "burns", the
    /// pre-fix behaviour. See <see cref="BuildNonCombustibleWoodIndex"/>.</summary>
    public bool IsNonCombustibleWood(string? wood, string? domain)
    {
        if (string.IsNullOrEmpty(wood)) return false;
        return nonCombustibleWoods.Contains((domain ?? "game") + ":" + wood);
    }

    /// <summary>Metal-tier speed multiplier in [<see cref="TierSpeedFloor"/>, 1] for a manual woodworking tool, from its
    /// OWN wood mining speed over a STEEL-grade anchor: a stone axe or copper saw returns a fraction below 1, a steel one
    /// returns exactly 1.0, and a BETTER-than-steel tool (meteoric iron, a modded super-tool) is CAPPED to 1.0 by the
    /// clamp — steel is the ceiling, so nothing works faster. Callers multiply their base speed by this
    /// (<see cref="BlockEntitySawhorse.SawSpeedFor"/>, <see cref="BlockEntitySawhorse.BarkSpeedFor"/>,
    /// <see cref="BlockEntityChoppingBlock.ChopStrike"/>). Read live on both sides through the mod system (BlockEntities
    /// already reach it for <see cref="Config"/>).
    ///
    /// <para><b>Three answers, not one (AuditFixPlan T03.02).</b> Every question this method could not otherwise answer
    /// used to fall out of it as <c>1.0</c> — and <c>1.0</c> is not "no opinion", it is the speed of STEEL, the ceiling
    /// of the whole system. So anything the mod failed to recognise silently worked as well as the best tool in the game.
    /// The cases are now told apart by ONE test — is this tool's FAMILY (<see cref="CollectibleObject.FirstCodePart()"/>)
    /// in the index (<see cref="BuildWoodToolSpeedIndex"/>)? — and only the third still answers 1.0:
    ///
    /// <list type="number">
    /// <item><b>Has a wood speed, family NOT indexed</b> — a third-party tool. The index deliberately admits only
    /// <c>game</c> and <c>immersivewoodworking</c> as anchors so that no modpack can raise the bar (one pushed the axe
    /// anchor to ~15.8 and dropped the copper axe from 0.47 to 0.33), but being kept out of the ANCHORS never meant a
    /// tool should escape being MEASURED. It now gets an honest tier: its own wood speed over the anchor of the matching
    /// vanilla family (<see cref="UnknownFamilyAnchor"/>). It gets a tier; it still cannot move one.</item>
    /// <item><b>Family IS indexed, but this instance carries no wood speed</b> — a broken variant of a tool we know.
    /// Vanilla ships four: <c>axe-bearded/battle/bardiche/double-ruined</c> are absent from <c>miningspeedbytype</c> yet
    /// still declare <c>tool: "axe"</c>, so every axe gate in this mod accepts them and a rusted stub used to fell a log
    /// in the single strike of a steel felling axe. It drops instead to the family's WEAKEST indexed tier
    /// (<see cref="WeakestIndexedTierFactor"/>).</item>
    /// <item><b>No wood speed AND family not indexed</b> — not a woodworking tool at all: the knife (<c>plant</c> only)
    /// and the plain hammer (no mining speed whatsoever). Flat 1.0, ON PURPOSE — the tier-1 knife and tier-2 axe+hammer
    /// debark tiers are FLAT across metals by design, and the family test is exactly what keeps case 2 from eating
    /// them.</item>
    /// </list>
    ///
    /// <para>A tool whose family IS indexed and that carries a wood speed takes none of the three — it is the ordinary
    /// path, logged as <c>0-indexed-family</c> and untouched by all of this. Returns a flat 1.0 before any of it when the
    /// feature is off (<see cref="ImmersiveWoodworkingConfig.ScaleWorkSpeedByToolTier"/>). Under <c>IW_DEBUG</c> every
    /// tool logs the rule it took, once (<see cref="TraceTier"/>): <c>1-foreign-family-anchor</c>,
    /// <c>2-weakest-tier-of-known-family</c>, <c>3-flat-not-a-wood-tool</c>.</para></summary>
    public float ToolTierSpeedFactor(ItemStack? stack)
    {
        if (!Config.ScaleWorkSpeedByToolTier) return 1f;
        CollectibleObject? coll = stack?.Collectible;
        if (coll?.Code == null) return 1f; // no code = no family to judge by, and FirstCodePart() below would throw

        float ws = WoodSpeedOnWireGrid(coll);

        // The ONE test the answers below hang on. Note it is asked of the FAMILY, not of the domain: a third-party
        // "axe-*" is a KNOWN family and takes the ordinary path against the vanilla axe anchor — the domain filter only
        // ever stopped it from DEFINING that anchor, and rule 1 is for a family nobody in game/iw shares a name with.
        // Read off Code, not off the collectible (AuditFixPlan T05.03): AssetLocation.FirstCodePart() cuts at the first
        // dash, while CollectibleObject.FirstCodePart() splits the WHOLE code and drops every part but this one. Same
        // answer; this runs on every tick of a chop or rip hold, through SawSpeedFor / the chop speed chain.
        string family = coll.Code.FirstCodePart();
        float refMax = 0f;
        bool familyKnown = !string.IsNullOrEmpty(family) && woodToolSpeedRef.TryGetValue(family, out refMax) && refMax > 0f;

        if (ws <= 0f)
        {
            // RULE 3 — not a woodworking tool at all: knife, plain hammer. Flat, and the debark tiers are built on that.
            if (!familyKnown)
            {
                TraceTier(coll, family, "3-flat-not-a-wood-tool", 0f, 0f, 1f);
                return 1f;
            }

            // RULE 2 — a family we know, but THIS variant carries no wood speed: the four vanilla axe-*-ruined stubs.
            float ruined = WeakestIndexedTierFactor(family, refMax);
            TraceTier(coll, family, "2-weakest-tier-of-known-family", 0f, refMax, ruined);
            return ruined;
        }

        // The ORDINARY path, unchanged by T03.02 — this metal against its own family's steel anchor.
        if (familyKnown)
        {
            float indexed = GameMath.Clamp(ws / refMax, TierSpeedFloor, 1f);
            TraceTier(coll, family, "0-indexed-family", ws, refMax, indexed);
            return indexed;
        }

        // RULE 1 — a third-party family: honest tier against the matching vanilla anchor, which it can never move.
        float anchor = UnknownFamilyAnchor(coll);
        float foreign = GameMath.Clamp(ws / anchor, TierSpeedFloor, 1f);
        TraceTier(coll, family, "1-foreign-family-anchor", ws, anchor, foreign);
        return foreign;
    }

    /// <summary>A collectible's <c>MiningSpeed[<see cref="EnumBlockMaterial.Wood"/>]</c> put on the wire's 1/64 grid, or
    /// 0 when it carries none at all. The quantization happens BEFORE any <c>&gt; 0</c> test, exactly as in
    /// <see cref="BuildWoodToolSpeedIndex"/>: a tool authored inside <c>(0, 1/64)</c> is a real tool to the server and a
    /// zero-speed one to the client, so truncating first is what makes both sides admit the same tools and then divide
    /// the same numbers. ONE definition on purpose — <see cref="ToolTierSpeedFactor"/> and
    /// <see cref="MetalTierBonusFraction"/> must not be able to drift onto different grids (AuditFixPlan
    /// T02.01/T03.02). See <see cref="QuantizeLikeWire"/>.</summary>
    static float WoodSpeedOnWireGrid(CollectibleObject coll)
    {
        if (coll.MiningSpeed == null) return 0f;
        if (!coll.MiningSpeed.TryGetValue(EnumBlockMaterial.Wood, out float ws)) return 0f;
        return QuantizeLikeWire(ws);
    }

    /// <summary>The tier handed to a tool of an INDEXED family that carries no wood mining speed of its own — the
    /// family's WEAKEST indexed metal (<see cref="woodToolSpeedMin"/>) over its ceiling anchor, i.e. exactly the slowest
    /// REAL tier that family sells (for axes the plain stone axe, 2.6/11 ≈ 0.24; AuditFixPlan T03.02, rule 2).
    ///
    /// <para>Why the weakest tier and not the bare <see cref="TierSpeedFloor"/>: the floor is a SAFETY value, sized to
    /// stop a mis-authored near-zero tool from freezing progress, not a balance number — at 0.05 a vanilla ruined axe
    /// would need ~20 strikes for one log. The weakest indexed metal is a number the family itself already stands
    /// behind, and it says the right thing about a rusted stub: junk, but not a wall.
    ///
    /// <para>The floor is still the answer in the two shapes where "weakest" means nothing: no min recorded (only
    /// reachable for the force-added <c>"axe"</c> entry in a world whose scan found no axe at all), and a degenerate
    /// single-tier family where the weakest IS the ceiling — handing THAT back would return a speed-less tool the
    /// family's best number, which is the entire bug this rule exists to close.</para></summary>
    float WeakestIndexedTierFactor(string family, float refMax)
    {
        if (!woodToolSpeedMin.TryGetValue(family, out float min) || min <= 0f || min >= refMax) return TierSpeedFloor;
        return GameMath.Clamp(min / refMax, TierSpeedFloor, 1f);
    }

    /// <summary>The steel-grade anchor a THIRD-PARTY tool's wood speed is divided by when its own family never entered
    /// the index (AuditFixPlan T03.02, rule 1) — picked by the tool's <see cref="CollectibleObject.Tool"/> type, which
    /// is the one thing about a foreign tool this mod already trusts everywhere else
    /// (<see cref="BlockEntitySawhorse.IsSawTool"/> and the axe gates key on it precisely so that any mod's tool
    /// qualifies no matter how it is coded).
    ///
    /// <para>A single flat anchor would NOT be neutral: measuring everything against the steel felling axe's 11 scores a
    /// modded steel-grade saw (wood 6, the vanilla steel saw's own number) at 0.55 — slower than a vanilla COPPER saw at
    /// 4/6 ≈ 0.67. So a saw is judged by the vanilla <c>saw</c> anchor, and an axe (and anything else, defensively) by
    /// the hard-pinned <see cref="VanillaSteelAxeWoodSpeed"/>. Both anchors still come only from <c>game</c> /
    /// <c>immersivewoodworking</c> families, so this hands a foreign tool a measurement and never a lever.</para></summary>
    float UnknownFamilyAnchor(CollectibleObject coll)
    {
        if (coll.Tool == EnumTool.Saw && woodToolSpeedRef.TryGetValue("saw", out float sawRef) && sawRef > 0f) return sawRef;
        return VanillaSteelAxeWoodSpeed;
    }

#if IW_DEBUG
    // [iw-tier] dev trace state: ONE line per (tool code, rule taken). ToolTierSpeedFactor is asked on every hold step
    // and every strike, so an ungated line would bury the log within seconds; the set turns the trace into a census of
    // what this world actually contains. The rule is part of the key so a mid-session config flip still shows up.
    // Locked because both sides call the factor and the client also reaches it off its render path.
    readonly HashSet<string> tierTraceSeen = new HashSet<string>();
#endif

    /// <summary>Log, once per tool and rule, which branch of <see cref="ToolTierSpeedFactor"/> a tool took and the
    /// numbers behind it (AuditFixPlan T03.02 DoD). Marked <c>[Conditional("IW_DEBUG")]</c>, so in a release build the
    /// CALLS are erased by the compiler and nothing is evaluated at the call site — the hot path pays nothing. Shares
    /// the <c>[iw-tier]</c> marker with the index dump in <see cref="BuildWoodToolSpeedIndex"/>, so one grep gives both
    /// the anchors and every decision taken against them.</summary>
    [System.Diagnostics.Conditional("IW_DEBUG")]
    void TraceTier(CollectibleObject coll, string family, string rule, float woodSpeed, float anchor, float factor)
    {
#if IW_DEBUG
        string code = coll.Code?.ToString() ?? "<no code>";
        lock (tierTraceSeen)
        {
            if (!tierTraceSeen.Add(code + "|" + rule)) return;
        }
        Mod.Logger.Notification(
            "[immersivewoodworking]   [iw-tier] tool={0} family={1} rule={2} woodspeed={3:0.####} anchor={4:0.####} factor={5:0.####}",
            code, string.IsNullOrEmpty(family) ? "<none>" : family, rule, woodSpeed, anchor, factor);
#endif
    }

    /// <summary>Metal-tier CHANCE bonus fraction in [0, 1] for a manual woodworking tool: 0 at its family's WEAKEST metal
    /// (e.g. the copper bark spud), 1 at STEEL (the ceiling), scaling LINEARLY with the tool's own MiningSpeed[Wood]
    /// between those two family anchors (<see cref="woodToolSpeedMin"/> / <see cref="woodToolSpeedRef"/>). This is the
    /// CHANCE-side counterpart to <see cref="ToolTierSpeedFactor"/> (a [floor, 1] SPEED factor anchored on steel alone):
    /// the bark drop's per-metal spud bonus (BarkPlan T02.05 — a copper spud is the 1.0 reference, a steel spud adds its
    /// full bonus) multiplies THIS in, so a better spud lifts the sub-ceiling woods while oak stays clamped at 100%.
    /// Returns 0 (no bonus → flat baseline) when metal-tier scaling is off
    /// (<see cref="ImmersiveWoodworkingConfig.ScaleWorkSpeedByToolTier"/> — the SAME master switch the speed factor obeys,
    /// so turning metal tiers off flattens BOTH: that switch means "the tool's metal does not matter", and leaving one
    /// metal effect standing would make it a half-truth. F-L6-18 read the shared switch as an oversight; it is the
    /// intent, now stated in the config docstring and in config-reference.md as well as here), when the tool carries no
    /// wood mining speed, when its family isn't indexed, or for a single-metal family (min == ref → no spread).
    ///
    /// <para>Note the ABSENCE of a wood speed needs no three-rule split here, unlike
    /// <see cref="ToolTierSpeedFactor"/>: 0 is this scale's BASELINE, not its ceiling, so a speed-less tool already
    /// falls to "no bonus" — the conservative direction — instead of silently claiming the best one.</para>
    /// Read live on both sides through the mod system.</summary>
    public float MetalTierBonusFraction(ItemStack? stack)
    {
        if (!Config.ScaleWorkSpeedByToolTier) return 0f;
        CollectibleObject? coll = stack?.Collectible;
        if (coll?.Code == null) return 0f;
        float ws = WoodSpeedOnWireGrid(coll); // ONE shared quantization point with the speed factor — a raw numerator here would fork the bark-drop bonus per side
        if (ws <= 0f) return 0f;
        string family = coll.Code.FirstCodePart(); // off Code, like ToolTierSpeedFactor — no whole-code split (T05.03)
        if (string.IsNullOrEmpty(family)) return 0f;
        if (!woodToolSpeedRef.TryGetValue(family, out float refMax) || !woodToolSpeedMin.TryGetValue(family, out float refMin)) return 0f;
        float span = refMax - refMin;
        if (span <= 0f) return 0f; // single-metal family (e.g. the one-piece pit saw) → no metal spread → no bonus
        return GameMath.Clamp((ws - refMin) / span, 0f, 1f);
    }

    // Toolsmith stores a tinkered tool's sharpness as two RAW int attributes on the itemstack's own tree (decompiled
    // Toolsmith 1.2.17 — ToolsmithModSystem's Get/SetToolCurrentSharpness/MaxSharpness read exactly these keys off
    // ItemStack.Attributes; GetToolSharpnessPercent = current / max). We read them raw so the mod has ZERO hard
    // dependency on Toolsmith.dll: absent Toolsmith the attributes simply aren't present and the factor is 1.0.
    const string SharpnessCurrentAttr = "toolSharpnessCurrent";
    const string SharpnessMaxAttr = "toolSharpnessMax";

    /// <summary>Linear sharpness→speed factor for a Toolsmith-tinkered tool, in
    /// [<see cref="ImmersiveWoodworkingConfig.SharpnessSpeedFloor"/>, 1]. A fully sharp tool (current == max) works at
    /// full speed (1.0); a fully blunt one (current == 0) at the configurable floor (default 0.5); in between it scales
    /// LINEARLY with the sharpness percent (current / max). WITHOUT Toolsmith the tool carries no sharpness attributes,
    /// so this returns 1.0 and speed is unchanged — graceful-absent, no Toolsmith.dll reference (the keys are read raw).
    /// Shared by the pit saw (<see cref="BlockEntitySawhorse.SawSpeedFor"/>), the bark spud
    /// (<see cref="BlockEntitySawhorse.BarkSpeedFor"/>) and the chopping-block strike
    /// (<see cref="BlockEntityChoppingBlock.ChopStrike"/>); the ordinary saw, the axe+hammer debark tier and the knife
    /// never route through here (user decision). Read live on both sides through the mod system (BlockEntities already
    /// reach it for <see cref="Config"/>), so client and server scale the per-strike bar by the SAME synced tool sharpness.
    /// NOTE: the factor tops out at 1.0 — sharpness only PENALISES a blunt edge, it never speeds a sharp tool PAST its
    /// metal-tier baseline.</summary>
    public float SharpnessSpeedFactor(ItemStack? stack)
    {
        // No Toolsmith (or a tool it hasn't tinkered) → no max attribute → treat as fully sharp (factor 1.0).
        if (stack?.Attributes == null || !stack.Attributes.HasAttribute(SharpnessMaxAttr)) return 1f;
        int max = stack.Attributes.GetInt(SharpnessMaxAttr, 0);
        if (max <= 0) return 1f;                                            // guard divide-by-zero
        int cur = stack.Attributes.GetInt(SharpnessCurrentAttr, max);      // missing current → assume full edge
        float pct = GameMath.Clamp((float)cur / max, 0f, 1f);
        return GameMath.Lerp(Config.SharpnessSpeedFloor, 1f, pct);         // floor (blunt) … 1.0 (sharp)
    }

    // Sharpness band where a chopping axe behaves EXACTLY like its plain metal tier (vanilla feel). Below the low edge the
    // factor ramps down to the blunt floor; above the high edge it ramps up to the config ceiling. User spec: 35–65%.
    const float ChopSharpNeutralLow = 0.35f;
    const float ChopSharpNeutralHigh = 0.65f;

    /// <summary>Sharpness→speed factor for the CHOPPING axe — a PLATEAU curve, distinct from the pit saw / bark spud's
    /// linear <see cref="SharpnessSpeedFactor"/>: flat 1.0 across the [<see cref="ChopSharpNeutralLow"/>,
    /// <see cref="ChopSharpNeutralHigh"/>] band (so a normally-maintained edge chops at the plain metal-tier speed =
    /// vanilla feel), ramping DOWN to <see cref="ImmersiveWoodworkingConfig.SharpnessSpeedFloor"/> at 0% sharpness and UP
    /// to <see cref="ImmersiveWoodworkingConfig.ChopSharpnessCeiling"/> at 100%. WITHOUT Toolsmith the tool carries no
    /// sharpness attributes, so this returns 1.0 (vanilla) — the same graceful-absent read as
    /// <see cref="SharpnessSpeedFactor"/>. Used only by <see cref="BlockEntityChoppingBlock.ChopStrike"/>.</summary>
    public float ChopSharpnessFactor(ItemStack? stack)
    {
        // No Toolsmith (or an un-tinkered tool) → no max attribute → mid-band 1.0 (vanilla feel), like a maintained edge.
        if (stack?.Attributes == null || !stack.Attributes.HasAttribute(SharpnessMaxAttr)) return 1f;
        int max = stack.Attributes.GetInt(SharpnessMaxAttr, 0);
        if (max <= 0) return 1f;                                            // guard divide-by-zero
        int cur = stack.Attributes.GetInt(SharpnessCurrentAttr, max);      // missing current → assume full edge
        float pct = GameMath.Clamp((float)cur / max, 0f, 1f);

        if (pct <= ChopSharpNeutralLow)                                    // dull side: floor … 1.0
            return GameMath.Lerp(Config.SharpnessSpeedFloor, 1f, pct / ChopSharpNeutralLow);
        if (pct >= ChopSharpNeutralHigh)                                   // sharp side: 1.0 … ceiling
            return GameMath.Lerp(1f, Config.ChopSharpnessCeiling, (pct - ChopSharpNeutralHigh) / (1f - ChopSharpNeutralHigh));
        return 1f;                                                          // neutral band → vanilla
    }

    /// <summary>Shared traversal for the AssetsLoaded recipe passes — the config-gated strip AND the ImprovementsPlan E03 saw-tool
    /// relax. Iterates every recipe asset under <paramref name="basePath"/> across ALL domains, normalizes the two
    /// VALID recipe-file shapes RecipeLoader reads — an ARRAY of recipes, or a single recipe OBJECT — to a JArray so
    /// one path covers both (~half of vanilla's recipes/grid files are single objects, so this is the common, expected
    /// case, NOT an error), hands the array to <paramref name="mutate"/>, and writes the asset back ONLY when mutate
    /// reports a non-zero change count. A file the game itself cannot read is left exactly as-is, silently (see
    /// <see cref="ReadAssetLikeEngine"/>) — a missed edit is safe; blind-editing an unparseable file isn't. Accumulates the
    /// file/element counters, so a re-run (or a file with nothing to change) is a no-op.</summary>
    static void ForEachRecipeAsset(ICoreAPI api, string basePath, System.Func<JArray, int> mutate,
        ref int filesChanged, ref int elementsChanged)
    {
        foreach (IAsset asset in api.Assets.GetMany(basePath, null)) // all domains
        {
            JToken? token = ReadAssetLikeEngine(asset);
            JArray? recipes = token as JArray ?? (token is JObject ? new JArray(token) : null);
            if (recipes == null) continue; // not an array or object — not a shape any recipe file should be; skip

            int changed = mutate(recipes);
            if (changed == 0) continue; // nothing matched here (or already applied on a re-run) → no write

            asset.Data = Encoding.UTF8.GetBytes(recipes.ToString(Formatting.None));
            asset.IsPatched = true; // parity with the engine's own asset writer: without it ServerMain.Launch()'s closing
                                    // UnloadUnpatchedAssets() nulls these bytes back out. Harmless today (every reader
                                    // runs on AssetsLoaded/AssetsFinalize, i.e. earlier — AuditFixPlan T10.01), but a
                                    // later reader would then silently get the ORIGINAL file off disk. Cost is honest:
                                    // the rewritten assets now stay resident for the session, exactly as engine-patched
                                    // ones do. (F-L8-28)
            filesChanged++;
            elementsChanged += changed;
        }
    }

    /// <summary>Read another mod's JSON asset the way the engine's own loaders do, or null when the engine cannot read
    /// it either. Used for the recipe passes (<see cref="ForEachRecipeAsset"/>) and for the log blocktypes
    /// (<see cref="DiscoverLogOnlySpecies"/>).
    ///
    /// <para>The read is the engine's own call, <c>asset.ToObject&lt;JToken&gt;()</c> (what <c>GetMany&lt;JToken&gt;</c>
    /// does for RecipeLoader, and <c>GetMany&lt;JObject&gt;</c> for the block loader), and NOT <c>JToken.Parse</c> /
    /// <c>JObject.Parse</c>: they disagree, and only the engine's verdict decides which recipes and blocks exist.
    /// Measured with the game's own Newtonsoft over 1193 recipe files (vanilla, the reference
    /// mods, ours, Tankards &amp; Goblets 1.4.4), the old read lost two files the game loads:
    /// <list type="bullet">
    /// <item><c>tankardsandgoblets:recipes/grid/alcoholbandage.json</c> ends without its closing <c>]</c>.
    /// <c>DeserializeObject</c> accepts a file cut off before its closing brackets, <c>JToken.Parse</c> throws: the game
    /// loads all eight recipes, the old read skipped the file and warned once per grid pass, four times per start.</item>
    /// <item><c>tankardsandgoblets:recipes/grid/large demijohn.json</c> opens with a comment before the array.
    /// <c>JToken.Parse</c> returns that comment as the whole document, so the old read saw "not a recipe shape" and
    /// skipped the file without a word.</item>
    /// </list>
    /// Every other file yields the same recipes both ways; a key written twice may land at another position.</para>
    ///
    /// <para>Nothing is logged for a file that fails here: RecipeLoader or the block loader reads the same file a
    /// moment later and reports it as "Syntax error in json file" itself, so our line would be a second report of
    /// someone else's file.</para>
    ///
    /// <para>The comments are dropped. <c>DeserializeObject</c> keeps a <c>//</c> or <c>/* */</c> comment that sits
    /// inside an array, at any depth, as a <c>JTokenType.Comment</c> element (inside an object it is dropped), while
    /// <c>JToken.Parse</c> drops them all. Every pass walks the array assuming recipes, and
    /// <see cref="RelaxSawToolSlots"/> would throw on <c>comment["ingredients"]</c>; a comment among a log's wood
    /// <c>states</c> would read as a species; and a written-back file stays free of comments, as it was.</para></summary>
    static JToken? ReadAssetLikeEngine(IAsset asset)
    {
        JToken? token;
        try
        {
            token = asset.ToObject<JToken>();
        }
        catch (System.Exception)
        {
            return null;
        }

        if (token is JContainer container)
        {
            List<JToken> comments = new List<JToken>();
            foreach (JToken t in container.Descendants())
                if (t.Type == JTokenType.Comment) comments.Add(t);
            foreach (JToken comment in comments) comment.Remove();
        }
        return token;
    }

    // HalfLogBlockPlan T01.02 — the half-log slab's own blocktype asset, whose `wood` variant group is filled in at
    // runtime. Kept next to the injection so the two can never drift apart.
    static readonly AssetLocation HalfLogSlabAsset = new AssetLocation("immersivewoodworking", "blocktypes/halflogslab.json");

    /// <summary>HalfLogBlockPlan T01.02 — collapse the discovered (species, domain) pairs to one domain per BARE
    /// species code, because that is all a block variant code can carry (a variant part cannot hold a colon, so
    /// <c>halflogslab-oak-barked-down-we-split</c> has nowhere to say WHICH oak). Runs on BOTH sides — the injection below is
    /// server-only, but the client reads this index to paint the block.
    ///
    /// Tie-break, decided in T01.02: <c>game</c> always wins; between two non-game domains the ordinally smaller one
    /// wins. The point of the second rule is determinism, not correctness — <c>Assets.GetMany</c> is documented as
    /// unsorted, so "first discovered" would silently depend on scan order, and the species→domain choice must be the
    /// same on every launch. Picking the wrong domain degrades gracefully anyway: the mod's texture resolution is
    /// already two-step (species domain, then <c>game</c>), so the worst case is a vanilla-looking texture, not a crash.
    /// A shared species code between two domains means the same tree in practice.</summary>
    void BuildWoodDomainIndex()
    {
        Dictionary<string, string> index = new Dictionary<string, string>();
        foreach ((string wood, string domain) in woodSpecies)
        {
            if (!index.TryGetValue(wood, out string? current)) { index[wood] = domain; continue; }
            if (current == "game") continue;                                   // game already won, nothing beats it
            if (domain == "game" || System.StringComparer.Ordinal.Compare(domain, current) < 0) index[wood] = domain;
        }
        woodDomains = index;
    }

    /// <summary>HalfLogBlockPlan T01.02 — replace the `wood` variant group's on-disk placeholder in
    /// <see cref="HalfLogSlabAsset"/> with the real species list, so every wood mod's species gets its own block
    /// variant with no per-mod patch. The list is the UNION of the worldproperties species
    /// (<see cref="BuildWoodDomainIndex"/>) and the species that only a log blocktype declares
    /// (<see cref="DiscoverLogOnlySpecies"/>) — see the comment at the union below for why the two are kept apart
    /// everywhere else. Same read → parse → mutate → write-back idiom as the recipe passes
    /// (<see cref="ForEachRecipeAsset"/>), and for the same reason it works: our ExecuteOrder is 0.1, the engine's
    /// block loader is 0.2, so what it reads is what we leave here. Proven empirically in T01.01.
    ///
    /// SERVER ONLY in effect, deliberately without a side guard: <c>blocktypes</c> is a server-side asset category
    /// (VSAPI <c>AssetCategory</c>), so on the client <c>TryGet</c> simply returns null and this is a no-op — the client
    /// receives the finished block registry over the wire ("Loaded N block types from server"). There is therefore no
    /// client/server list to reconcile (T01.01 finding; the plan originally assumed the opposite).
    ///
    /// The states are SORTED. Not for cross-side agreement (there is none to keep) but because block ids are written
    /// into the player's savegame: an unstable order would remap ids and log "missing blocks" on every launch.
    ///
    /// Never throws. A missing/unparseable asset or an empty species list leaves the on-disk placeholder alone, which
    /// registers a single inert <c>halflogslab-placeholder-*</c> family instead of taking the block down entirely.</summary>
    void InjectWoodVariants(ICoreAPI api)
    {
        IAsset? asset = api.Assets.TryGet(HalfLogSlabAsset);
        if (asset == null) return;                                             // client side — expected, see the doc above

        JObject block;
        try
        {
            block = JObject.Parse(asset.ToText());
        }
        catch (System.Exception e)
        {
            api.Logger.Warning("[immersivewoodworking] Could not parse {0}; the half-log slab keeps its placeholder wood variant. {1}", asset.Location, e.Message);
            return;
        }

        JArray? groups = block["variantgroups"] as JArray;
        JArray? states = null;
        if (groups != null)
            foreach (JToken g in groups)
                if ((string?)g["code"] == "wood") { states = g["states"] as JArray; break; }

        if (states == null)
        {
            api.Logger.Warning("[immersivewoodworking] {0} has no `wood` variant group with states; the half-log slab will have no species.", asset.Location);
            return;
        }

        // The axis is EVERY species a half-log can be stamped with, which is wider than woodDomains: that index is
        // keyed on worldproperties, and vanilla's own `aged` / `veryaged` / `rotten` / `veryrotten` have logs and no
        // worldproperties entry at all (AuditFixPlan T04.05, DiscoverLogOnlySpecies). Without them the split produced
        // a half-log whose slab did not exist, and laying it did nothing — no block, no message, no sound. The two
        // lists stay SEPARATE on purpose: woodSpecies drives the chopping BLOCK's per-species recipes and creative
        // stacks, where `aged` is deliberately excluded, and folding these four in there would resurrect exactly the
        // `choppingblock-aged` the mod removed.
        HashSet<string> species = new HashSet<string>(woodDomains.Keys);
        foreach ((string wood, string _) in logOnlySpecies) species.Add(wood);

        List<string> woods = new List<string>(species);
        if (woods.Count == 0)
        {
            api.Logger.Warning("[immersivewoodworking] No wood species discovered; the half-log slab keeps its placeholder variant.");
            return;
        }
        woods.Sort(System.StringComparer.Ordinal);                             // stable block ids across launches

        states.Clear();                                                        // REPLACE the placeholder, never append to it
        foreach (string wood in woods) states.Add(wood);

        asset.Data = Encoding.UTF8.GetBytes(block.ToString(Formatting.None));
        asset.IsPatched = true; // same parity as ForEachRecipeAsset above — see the note there (F-L8-28)

#if IW_DEBUG
        HashSet<string> domains = new HashSet<string>();
        foreach (KeyValuePair<string, string> kv in woodDomains) domains.Add(kv.Value);
        foreach ((string _, string domain) in logOnlySpecies) domains.Add(domain);
        api.Logger.Notification("[immersivewoodworking] Half-log slab: injected {0} wood state(s) ({1} of them log-only) from {2} domain(s) into {3}.",
            woods.Count, woods.Count - woodDomains.Count, domains.Count, asset.Location);
#endif
    }

    /// <summary>Scan every recipe asset under <paramref name="basePath"/> (all domains) and remove the recipes for
    /// which <paramref name="shouldRemove"/> holds. Shared by the grid strip (firewood/saw/pit-saw/debark) and the
    /// smithing strip (pit-saw blade, ManualOpsBalancePlan E09). Thin wrapper over <see cref="ForEachRecipeAsset"/> —
    /// the file-shape, malformed-JSON and write-only-on-change handling all live there.</summary>
    static void StripMatchingRecipes(ICoreAPI api, string basePath, System.Func<JToken, bool> shouldRemove,
        ref int filesChanged, ref int totalRemoved)
        => ForEachRecipeAsset(api, basePath, recipes => RemoveRecipesWhere(recipes, shouldRemove), ref filesChanged, ref totalRemoved);

    /// <summary>Removes from <paramref name="recipes"/> every node for which <paramref name="match"/> is true
    /// (iterating back-to-front so indices stay valid); returns the number removed.</summary>
    static int RemoveRecipesWhere(JArray recipes, System.Func<JToken, bool> match)
    {
        int before = recipes.Count;
        for (int i = recipes.Count - 1; i >= 0; i--)
        {
            if (match(recipes[i])) recipes.RemoveAt(i);
        }
        return before - recipes.Count;
    }

    /// <summary>Scan every recipe asset under <paramref name="basePath"/> (all domains) and RELAX any ingredient slot
    /// that gates a saw tool by BOTH a <c>tool-saw</c> tag AND a concrete <c>code</c> — dropping the redundant code so
    /// the slot becomes a tag-only match. Code + tags are an AND-gate, so a present <c>saw-*</c> code excludes the
    /// mod's <c>pitsaw-*</c> (and any other mod's tool-saw) even though it carries the tag; removing the code leaves
    /// the tag, which every saw satisfies. Vanilla's <c>game:recipes/grid/cabinet</c> is the ONLY grid recipe that
    /// still pairs <c>tool-saw</c> with a non-empty code — every other vanilla tool-saw recipe is already tag-only —
    /// so this restores vanilla's own tag-migration intent GENERICALLY, replacing the old index-pinned JSON patch
    /// (patches/pitsaw_cabinet_compat.json, removed in ImprovementsPlan E03/T03.03). Being structural, it catches the recipe in ANY
    /// domain, at ANY array position, and any future or mod-added variant of the same shape — none of the old patch's
    /// failure modes (a 7th variant, a removed entry, a whole-array replace). Uses the shared
    /// <see cref="ForEachRecipeAsset"/> traversal (same server-only, before-RecipeLoader, mutate-asset.Data-then-write
    /// path as the strip); runs at our ExecuteOrder 0.1 ahead of the vanilla RecipeLoader (1) so the recipe registers
    /// already relaxed, and the whole server recipe registry syncs to clients on join.</summary>
    static void RelaxSawToolIngredients(ICoreAPI api, string basePath, ref int filesChanged, ref int slotsRelaxed)
        => ForEachRecipeAsset(api, basePath, recipes =>
        {
            int relaxed = 0;
            foreach (JToken recipe in recipes) relaxed += RelaxSawToolSlots(recipe);
            return relaxed;
        }, ref filesChanged, ref slotsRelaxed);

    /// <summary>Drop the <c>code</c> from every ingredient slot of <paramref name="recipe"/> that carries a
    /// <c>tool-saw</c> tag AND a <c>saw-*</c> code (domain-stripped <see cref="FirstCodePart"/> == "saw"); returns the
    /// number of slots changed. The tag requirement is what makes the tag-only result meaningful — a code-only saw slot
    /// (no tag) would relax to "any item", so it is deliberately left alone. The first-code-part == "saw" test (NOT a
    /// looser StartsWith/Contains) keeps it to the vanilla saw shape and away from a hypothetical <c>sawblade-*</c> /
    /// <c>sawdust-*</c> (FirstCodePart "sawblade"/"sawdust") or the mod's own <c>pitsaw-*</c> ("pitsaw"). Everything
    /// else on the slot (<c>tags</c>, <c>isTool</c>, <c>toolDurabilityCost</c>, <c>allowedVariants</c>) is left intact —
    /// e.g. table.json's metal <c>allowedVariants</c> restriction survives. Only the named-slot dict shape is handled
    /// (grid recipes), matching <see cref="RecipeHasInput"/>; tool slots always carry a code, never a nested variant
    /// list.</summary>
    static int RelaxSawToolSlots(JToken recipe)
    {
        if (recipe["ingredients"] is not JObject ingredients) return 0;
        int relaxed = 0;
        foreach (JProperty slot in ingredients.Properties())
        {
            if (slot.Value is not JObject ing || ing["tags"] is not JArray tags) continue;

            bool hasSawTag = false;
            foreach (JToken t in tags) if ((string?)t == "tool-saw") { hasSawTag = true; break; }
            if (!hasSawTag) continue;

            string? code = StripDomain((string?)ing["code"]);
            if (string.IsNullOrEmpty(code) || FirstCodePart(code) != "saw") continue; // no code, or not a `saw-*` code → leave it

            ing.Remove("code");
            relaxed++;
        }
        return relaxed;
    }

    /// <summary>True if this grid recipe is "axe + a chopping-block-acceptable log → firewood" — matched by OUTPUT
    /// (firewood, any domain) AND an input whose code-base the stand accepts
    /// (<see cref="BlockEntityChoppingBlock.LogCodeParts"/>). Sourcing the input set from the stand's own
    /// accept-list keeps "what the stand can chop" and "what we strip the hand recipe for" from drifting, and
    /// SPARES inputs the stand can't take (e.g. wildcrafttree <c>lognarrow-*</c>), so they aren't orphaned.
    ///
    /// <para>The output test is the stand's own <see cref="BlockEntityChoppingBlock.IsFirewoodCodePart"/>, so a
    /// SPECIES-SPECIFIC firewood counts as firewood here too — vanilla's separate
    /// <c>log-placed-aged-ud → agedfirewood</c> recipe goes with the rest (AuditFixPlan T04.05). It used to be spared,
    /// correctly, for as long as the stand made ordinary firewood out of an aged log: removing it would have left aged
    /// wood with no firewood path of its own. Now the stand makes <c>agedfirewood</c> itself
    /// (<c>BlockEntityChoppingBlock.ResolveFirewood</c>), so sparing it would leave ONE wood with a grid shortcut that
    /// every other wood lost.</summary>
    static bool IsLogToFirewood(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        if (output == null || !BlockEntityChoppingBlock.IsFirewoodCodePart(FirstCodePart(output))) return false;
        return RecipeHasInput(recipe, code =>
            System.Array.IndexOf(BlockEntityChoppingBlock.LogCodeParts, FirstCodePart(code)) >= 0);
    }

    /// <summary>True if this grid recipe is "axe + driftwood → firewood" — the vanilla flotsam recipes
    /// (survival/recipes/grid/firewood.json: flotsam-driftwood-1/2/3). Its OWN predicate because ManualOpsBalancePlan E07 removed "flotsam"
    /// from <see cref="BlockEntityChoppingBlock.LogCodeParts"/> (driftwood is now broken by hand, not chopped on the
    /// block), so <see cref="IsLogToFirewood"/> no longer catches it — but the vanilla grid craft must stay disabled so
    /// driftwood is processed only by the in-world hand break (<see cref="ImmersiveWoodworking.BlockBehaviorFlotsamBreak"/>).
    /// Gated by the same <see cref="ImmersiveWoodworkingConfig.DisableVanillaFirewoodCrafting"/> flag as the other
    /// firewood recipes.</summary>
    static bool IsFlotsamToFirewood(JToken recipe)
    {
        if (OutputBareCode(recipe) != "firewood") return false;
        return RecipeHasInput(recipe, code => FirstCodePart(code) == "flotsam");
    }

    /// <summary>True if this grid recipe is "saw + log/debarked log → plank/support beam" — the rip the sawhorses
    /// replace — matched by OUTPUT (plank-* / supportbeam-*) AND a log-placed-/logsection-placed-/debarkedlog-
    /// input. The output gate is what tells the rip recipes apart from the RECYCLING ones (planks/stairs/slab/
    /// ladder → planks: same plank-* output, but a non-log input), so those stay; it also keeps this from touching
    /// any unrelated recipe that merely consumes a log into something that isn't a plank/beam.</summary>
    static bool IsLogToPlankOrBeam(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        if (output == null) return false;
        if (!output.StartsWith("plank-", System.StringComparison.Ordinal) &&
            !output.StartsWith("supportbeam-", System.StringComparison.Ordinal)) return false;

        return RecipeHasInput(recipe, code =>
            code.StartsWith("log-placed-", System.StringComparison.Ordinal) ||
            code.StartsWith("logsection-placed-", System.StringComparison.Ordinal) ||
            code.StartsWith("debarkedlog-", System.StringComparison.Ordinal));
    }

    /// <summary>True if this grid recipe is "axe + hammer + a placed log → debarked log" — the in-world echo the
    /// sawhorse SIDE action (debarking) replaces — matched by OUTPUT (debarkedlog-*) AND a <c>log-placed-*</c> input.
    /// The output gate tells it apart from the rip recipe that CONSUMES a debarked log into a beam (output
    /// <c>supportbeam-*</c>, caught by <see cref="IsLogToPlankOrBeam"/> instead), so the two never overlap; the
    /// log-placed- input gate keeps it to the bark-strip craft. Structural like the others, so vanilla's
    /// <c>game:debarkedlog</c> AND a wood mod's own (e.g. wildcrafttree's, same shape) are both caught with no per-mod
    /// patch. Used to disable the vanilla debark grid recipe when
    /// <see cref="ImmersiveWoodworkingConfig.DisableVanillaDebarkCrafting"/> is on.</summary>
    static bool IsLogToDebarkedLog(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        if (output == null) return false;
        if (!output.StartsWith("debarkedlog-", System.StringComparison.Ordinal)) return false;

        return RecipeHasInput(recipe, code =>
            code.StartsWith("log-placed-", System.StringComparison.Ordinal));
    }

    /// <summary>True if this BARREL recipe is "whole/debarked log (+ water or weak tannin) → weak/strong tannin" — the
    /// vanilla log-based tanning chain the bark chain (BarkPlan/E03) replaces — matched by OUTPUT
    /// (<c>weaktanninportion</c>/<c>strongtanninportion</c>) AND a <c>log-placed-*</c> or <c>debarkedlog-*</c> input.
    /// The output gate keeps it to tannin; the log-input gate is what tells the vanilla log routes apart from the mod's
    /// OWN bark → tannin recipes (E03/T03.02), whose inputs are tags-only bark + water/weak-tannin with NO log code, so
    /// those survive. It also SPARES narrow-trunk logs (<c>lognarrow-placed-*</c> — <see cref="FirstCodePart"/> is
    /// "lognarrow", so the string never starts with "log-placed-"; the sawhorses can't debark them, so they have no bark
    /// path and stripping their tannin recipe would orphan them) and leaf routes (wildcrafttree's <c>leaves-*</c>
    /// tannin), matching the firewood strip's <c>lognarrow</c> discipline. Structural like the grid predicates, so
    /// vanilla (oak/acacia) AND a wood mod's own whole-log tannin (wildcrafttree walnut/willow/beech) are both caught
    /// with no per-mod patch. Barrel <c>ingredients</c> is a JArray, not the grid's named-slot dict, so it uses
    /// <see cref="BarrelRecipeHasInput"/>. Used when <see cref="ImmersiveWoodworkingConfig.DisableVanillaLogTannin"/>
    /// is on.</summary>
    static bool IsLogToTannin(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        if (output != "weaktanninportion" && output != "strongtanninportion") return false;

        return BarrelRecipeHasInput(recipe, code =>
            code.StartsWith("log-placed-", System.StringComparison.Ordinal) ||
            code.StartsWith("debarkedlog-", System.StringComparison.Ordinal));
    }

    /// <summary>True if this grid recipe outputs the mod's own pit saw (<c>pitsaw-{metal}</c>) — matched by OUTPUT
    /// first-code-part = "pitsaw" (domain-stripped), independent of the metal variant. Used to disable the pit saw's
    /// crafting recipe when <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> is on, so it can no longer be
    /// crafted (only the mod's own pitsaw recipe outputs this code, so the match is exact and mod-internal).</summary>
    static bool IsPitSawRecipe(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        return output != null && FirstCodePart(output) == "pitsaw";
    }

    /// <summary>True if this recipe outputs the mod's pit-saw BLADE (<c>pitsawblade-{metal}</c>) — matched by OUTPUT
    /// first-code-part = "pitsawblade" (domain-stripped), independent of the metal variant. Used to disable the
    /// blade's anvil smithing recipe when <see cref="ImmersiveWoodworkingConfig.RemovePitSaw"/> is on, so the pit
    /// saw's sole gating part can no longer be forged (ManualOpsBalancePlan E09). Distinct from <see cref="IsPitSawRecipe"/> ("pitsaw",
    /// the assembled tool): "pitsawblade-{metal}" splits to "pitsawblade" on its first dash, so the two never
    /// overlap. Only this mod's recipe outputs this code, so the match is exact and mod-internal.</summary>
    static bool IsPitSawBladeRecipe(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        return output != null && FirstCodePart(output) == "pitsawblade";
    }

    // --- AuditFixPlan T03.03 (F-L9-10): the beam recipe's OWN skipVariants, carried over to the rip ----------------
    //
    // Vanilla's "saw + debarkedlog-{wood}-ud -> supportbeam-{wood}" grid recipe excludes veryaged / rotten / veryrotten:
    // the beam BLOCKS exist for those woods (world-gen, creative), so nothing but that list ever said no. The mod
    // STRIPS that recipe and puts the rip in its place, which would quietly turn the stations into a way around a
    // restriction the game deliberately shipped.
    //
    // The list is read off the recipe as DATA, before it is removed, rather than typed into C# as three species. That
    // keeps source-conventions.md 2 intact (accept by the SHAPE of a code, never a species whitelist) and it also
    // covers a wood mod that ships its own beam recipe with its own skipVariants, with no per-mod patch.
    //
    // TWO sets, one per side, because in single player the client and the server are two ModSystem instances in ONE
    // process and only the server can read this: recipe assets are a server-side asset CATEGORY, so a client never
    // sees the file. The server fills its set in AssetsLoaded and pushes a copy on the config packet
    // (ConfigSyncPacket.BeamSkipWoods); the client fills its own set from that packet. Without the client copy the two
    // sides' rip gates would disagree and a client would animate a rip its server refuses. Static so the gate
    // (BlockEntitySawhorse.SawProductResolves) can read it without a per-call ModLoader scan on the per-tick path.
    static readonly HashSet<string> beamSkipWoodsServer = new HashSet<string>(System.StringComparer.Ordinal);
    static readonly HashSet<string> beamSkipWoodsClient = new HashSet<string>(System.StringComparer.Ordinal);

    /// <summary>Whether <paramref name="wood"/> is a state the game's own <c>debarkedlog -> supportbeam</c> recipe
    /// excluded, i.e. one the rip must NOT turn into a beam either (T03.03). Reads the set belonging to
    /// <paramref name="world"/>'s OWN side, so a single-player process cannot answer with the other side's copy.</summary>
    internal static bool BeamSkippedWood(IWorldAccessor world, string wood)
        => (world.Side == EnumAppSide.Client ? beamSkipWoodsClient : beamSkipWoodsServer).Contains(wood);

    /// <summary>The server's collected skip list as it goes on the wire, or null when nothing is skipped (ProtoBuf
    /// drops a zero-length array anyway, and the receiver reads null as "nothing skipped" — the same meaning).
    /// Built once after the collection pass and reused for every join, like the config JSON beside it.</summary>
    internal static string[]? BeamSkipWoodsWire
        => beamSkipWoodsServer.Count == 0 ? null : new List<string>(beamSkipWoodsServer).ToArray();

    /// <summary>Adopt the server's skip list on the CLIENT (from <see cref="ConfigSyncPacket.BeamSkipWoods"/>). Null or
    /// empty means nothing is skipped — a legitimate answer, not a missing one.</summary>
    static void AdoptBeamSkipWoods(string[]? woods)
    {
        beamSkipWoodsClient.Clear();
        if (woods == null) return;
        foreach (string w in woods) if (!string.IsNullOrEmpty(w)) beamSkipWoodsClient.Add(w);
    }

    /// <summary>Read-only pass over every grid recipe asset in every domain, harvesting the wood states that the
    /// <c>debarkedlog -> supportbeam</c> recipes exclude. Runs on the SERVER in <see cref="AssetsLoaded"/>, BEFORE the
    /// strip removes those recipes and regardless of whether the strip is even enabled — the restriction belongs to
    /// the game, not to our replacement of the grid craft, so it holds either way. Uses the shared
    /// <see cref="ForEachRecipeAsset"/> traversal for the parse / file-shape / malformed-JSON handling and always
    /// reports 0 changes, so it never writes an asset back.</summary>
    static void CollectBeamSkipVariants(ICoreAPI api, string basePath)
    {
        // Start from empty every time. AssetsLoaded runs once per WORLD, and in single player the process outlives a
        // quit to the main menu — so without this, a second world would inherit the first world's mod set.
        beamSkipWoodsServer.Clear();

        int noFiles = 0, noElements = 0; // discarded: this pass reports 0 changes, so nothing is ever written
        ForEachRecipeAsset(api, basePath, recipes =>
        {
            foreach (JToken recipe in recipes) CollectBeamSkipVariants(recipe);
            return 0;
        }, ref noFiles, ref noElements);
    }

    /// <summary>Harvest ONE recipe's excluded wood states into <c>beamSkipWoodsServer</c>, if it is a
    /// <c>debarkedlog -> supportbeam</c> recipe. Both JSON shapes are read, because both occur in vanilla's own files:
    /// per-INGREDIENT (<c>"L": { code: "debarkedlog-*-ud", name: "wood", skipVariants: [...] }</c>, as
    /// <c>log-quad.json</c> writes it) and per-RECIPE keyed by the wildcard's name
    /// (<c>skipVariants: { wood: [...] }</c> beside <c>code: "debarkedlog-{wood}-ud"</c>, as <c>supportbeams.json</c>
    /// writes it). The wildcard name comes from the slot's <c>name</c> when it has one, else from the <c>{...}</c>
    /// placeholder in its code.</summary>
    static void CollectBeamSkipVariants(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        if (output == null || !output.StartsWith("supportbeam-", System.StringComparison.Ordinal)) return;
        if (recipe["ingredients"] is not JObject ingredients) return;

        foreach (JProperty slot in ingredients.Properties())
        {
            if (slot.Value is not JObject ing) continue;
            string? code = StripDomain((string?)ing["code"]);
            if (code == null || !code.StartsWith("debarkedlog-", System.StringComparison.Ordinal)) continue;

            AddBeamSkipStates(ing["skipVariants"] as JArray);                       // per-ingredient list

            string? name = (string?)ing["name"] ?? WildcardName(code);              // the {wood} the recipe keys by
            if (name != null && recipe["skipVariants"] is JObject byName)
                AddBeamSkipStates(byName[name] as JArray);                          // per-recipe, keyed list
        }
    }

    /// <summary>Add every non-empty string of <paramref name="states"/> to the server's beam skip set; a null or
    /// non-array token adds nothing.</summary>
    static void AddBeamSkipStates(JArray? states)
    {
        if (states == null) return;
        foreach (JToken t in states)
        {
            string? state = (string?)t;
            if (!string.IsNullOrEmpty(state)) beamSkipWoodsServer.Add(state!);
        }
    }

    // --- Recipe-declared product REMAP table -----------------------------------------------------------------------
    //
    // source-conventions.md 2 resolves a sawn product by SHAPE — plank-{wood}/supportbeam-{wood} in the log's own
    // domain then game — which assumes a wood mod names its plank after its log's species. Some mods DON'T: FloralZones
    // ships Siberian pine as floralzonescircumborealregion:log-placed-pinussibirica-ud but has no plank-pinussibirica,
    // mapping that log to the VANILLA game:plank-pine (and larixlaricina→larch, quercuspetraea→oak, piceamariana→
    // piceaglauca). The rename lives ONLY in the mod's grid recipe (saw + log → the differently-named plank), so the
    // shape resolver resolves nothing and the station refuses the log at the door. Worse, the same recipe is the one
    // our strip removes (IsLogToPlankOrBeam), so with the strip on the log has no plank path at all.
    //
    // The fix reads the mapping off the recipe as DATA, before it is removed — exactly the beamSkip discipline above —
    // and hands it to the ONE resolver as a fallback consulted only when the shape lookup came up empty. No species is
    // named in C# and no second resolver is written: the table is the game's own declared output for that log, keyed by
    // the very AssetLocation the resolver was about to fail on. TWO sides, filled/synced like beamSkip, because the
    // put/rip gate runs on both and recipe assets never reach a client.
    static readonly Dictionary<string, string> productRemapServer = new Dictionary<string, string>(System.StringComparer.Ordinal);
    static readonly Dictionary<string, string> productRemapClient = new Dictionary<string, string>(System.StringComparer.Ordinal);

    /// <summary>The recipe-declared product code for a <paramref name="domain"/>:<paramref name="path"/> lookup the
    /// shape resolver could not satisfy (e.g. <c>floralzonescircumborealregion:plank-pinussibirica</c> →
    /// <c>game:plank-pine</c>), or null when no wood mod remapped it. Reads the set for <paramref name="world"/>'s OWN
    /// side, so a single-player process cannot answer with the other side's copy (like <see cref="BeamSkippedWood"/>).
    /// Called only on the resolver's null path, so the ordinary lookup pays nothing.</summary>
    internal static string? RemappedProductCode(IWorldAccessor world, string domain, string path)
    {
        Dictionary<string, string> map = world.Side == EnumAppSide.Client ? productRemapClient : productRemapServer;
        return map.TryGetValue(domain + ":" + path, out string? code) ? code : null;
    }

    /// <summary>The server's remap table as it goes on the wire — one <c>keyvalue</c> string per entry — or null
    /// when nothing is remapped (ProtoBuf omits a zero-length array; the receiver reads null as "no remaps"). Built once
    /// after the collection pass and reused for every join, like <see cref="BeamSkipWoodsWire"/> beside it.</summary>
    internal static string[]? ProductRemapWire
    {
        get
        {
            if (productRemapServer.Count == 0) return null;
            string[] wire = new string[productRemapServer.Count];
            int i = 0;
            foreach (KeyValuePair<string, string> kv in productRemapServer) wire[i++] = kv.Key + "" + kv.Value;
            return wire;
        }
    }

    /// <summary>Adopt the server's remap table on the CLIENT (from <see cref="ConfigSyncPacket.ProductRemap"/>). Null or
    /// empty means nothing is remapped — a legitimate answer, not a missing one. An entry without the U+0001 delimiter
    /// is skipped rather than trusted.</summary>
    static void AdoptProductRemaps(string[]? wire)
    {
        productRemapClient.Clear();
        if (wire == null) return;
        foreach (string entry in wire)
        {
            int sep = entry.IndexOf('');
            if (sep > 0) productRemapClient[entry.Substring(0, sep)] = entry.Substring(sep + 1);
        }
    }

    /// <summary>Read-only pass over every grid recipe asset in every domain, harvesting each "saw + log → renamed
    /// plank/beam" recipe's declared output into <see cref="productRemapServer"/>. Runs on the SERVER in
    /// <see cref="AssetsLoaded"/>, BEFORE the strip removes those recipes and regardless of whether the strip is enabled
    /// — the log must reach its plank whether we removed the grid craft or not. Uses the shared
    /// <see cref="ForEachRecipeAsset"/> traversal and always reports 0 changes, so it never writes an asset back.</summary>
    static void CollectProductRemaps(ICoreAPI api, string basePath)
    {
        productRemapServer.Clear(); // AssetsLoaded runs once per world; a single-player process outlives a quit, so a second world must not inherit the first's table
        int noFiles = 0, noElements = 0; // discarded: this pass reports 0 changes, so nothing is ever written
        ForEachRecipeAsset(api, basePath, recipes =>
        {
            foreach (JToken recipe in recipes) CollectProductRemap(recipe);
            return 0;
        }, ref noFiles, ref noElements);
    }

    /// <summary>Harvest ONE grid recipe's log→plank/beam output into <see cref="productRemapServer"/>, if it renames the
    /// species. Gate: a LITERAL <c>plank-*</c>/<c>supportbeam-*</c> output (a <c>{wood}</c>-templated one keeps the
    /// input's species, so the shape resolver already finds it — no alias needed) plus a
    /// <c>log-placed-</c>/<c>logsection-placed-</c>/<c>debarkedlog-</c> input carrying the variant(s) it applies to. For
    /// each such variant the key is the exact <c>&lt;log domain&gt;:&lt;product path&gt;</c> the sawhorse resolver builds
    /// and fails on (product path = the output's first part, "plank"/"supportbeam", + the input species), and the value
    /// is the recipe's declared output, domain-qualified against the log's own mod (a rip recipe ships beside the logs
    /// it consumes, so a domain-less output means that same mod). First writer wins on the rare key collision.</summary>
    static void CollectProductRemap(JToken recipe)
    {
        string? outputRaw = (string?)recipe["output"]?["code"];
        string? outputBare = StripDomain(outputRaw);
        if (outputBare == null) return;
        if (!outputBare.StartsWith("plank-", System.StringComparison.Ordinal) &&
            !outputBare.StartsWith("supportbeam-", System.StringComparison.Ordinal)) return;
        if (outputBare.IndexOf('{') >= 0) return; // templated → same species as the input → shape resolver handles it
        if (recipe["ingredients"] is not JObject ingredients) return;

        string keyPrefix = FirstCodePart(outputBare) + "-"; // "plank-" or "supportbeam-" — the resolver's product path prefix
        foreach (JProperty slot in ingredients.Properties())
        {
            if (slot.Value is not JObject ing) continue;
            string? rawCode = (string?)ing["code"];
            string? bare = StripDomain(rawCode);
            if (bare == null) continue;
            if (!bare.StartsWith("log-placed-", System.StringComparison.Ordinal) &&
                !bare.StartsWith("logsection-placed-", System.StringComparison.Ordinal) &&
                !bare.StartsWith("debarkedlog-", System.StringComparison.Ordinal)) continue;

            string inputDomain = CodeDomain(rawCode!);
            string outputQualified = outputRaw!.IndexOf(':') >= 0 ? outputRaw! : inputDomain + ":" + outputBare;
            foreach (string species in InputVariants(recipe, ing))
            {
                if (string.IsNullOrEmpty(species)) continue;
                string key = inputDomain + ":" + keyPrefix + species;
                if (!productRemapServer.ContainsKey(key)) productRemapServer[key] = outputQualified; // first writer wins
            }
        }
    }

    /// <summary>The wood variants a log ingredient applies to: its own <c>allowedVariants</c> list (the FloralZones
    /// shape, one per remapped species) or, failing that, a recipe-level <c>allowedVariants</c> keyed by the wildcard's
    /// name — the same two JSON shapes <see cref="CollectBeamSkipVariants(JToken)"/> reads for <c>skipVariants</c>. A
    /// concrete-code input (no wildcard, no list) yields nothing: the rename idiom every wood mod uses is a wildcard
    /// gated by <c>allowedVariants</c>, so a one-species-per-recipe concrete craft is left to the shape resolver.</summary>
    static IEnumerable<string> InputVariants(JToken recipe, JObject ing)
    {
        if (ing["allowedVariants"] is JArray perIngredient)
        {
            foreach (JToken t in perIngredient) { string? s = (string?)t; if (s != null) yield return s; }
            yield break;
        }
        string? name = (string?)ing["name"] ?? WildcardName(StripDomain((string?)ing["code"]) ?? "");
        if (name != null && recipe["allowedVariants"] is JObject byName && byName[name] is JArray perRecipe)
            foreach (JToken t in perRecipe) { string? s = (string?)t; if (s != null) yield return s; }
    }

    /// <summary>The leading "domain:" of an asset code, or "game" when it carries none (the default the engine assigns).
    /// Complements <see cref="StripDomain"/>, which drops it.</summary>
    static string CodeDomain(string code)
    {
        int colon = code.IndexOf(':');
        return colon > 0 ? code.Substring(0, colon) : "game";
    }

    /// <summary>The name inside the first <c>{...}</c> placeholder of <paramref name="code"/>
    /// ("debarkedlog-{wood}-ud" -> "wood"), or null when the code carries none. That name is the key a recipe-level
    /// <c>skipVariants</c> object uses.</summary>
    static string? WildcardName(string code)
    {
        int open = code.IndexOf('{');
        if (open < 0) return null;
        int close = code.IndexOf('}', open + 1);
        return close > open + 1 ? code.Substring(open + 1, close - open - 1) : null;
    }

    /// <summary>The recipe's output collectible code with any domain prefix stripped (so "game:firewood" and a
    /// bare "firewood" compare equal), or null if the node has no output code.</summary>
    static string? OutputBareCode(JToken recipe) => StripDomain((string?)recipe["output"]?["code"]);

    /// <summary>Strip a leading "domain:" from an asset code, returning the bare path (null in → null out).</summary>
    internal static string? StripDomain(string? code)
    {
        if (code == null) return null;
        int colon = code.IndexOf(':');
        return colon >= 0 ? code.Substring(colon + 1) : code;
    }

    /// <summary>The first code part of a domain-stripped code — the text before the first '-' ("log-placed-oak-ud"
    /// → "log", "flotsam-driftwood-1" → "flotsam") — mirroring CollectibleObject.FirstCodePart() for the JSON
    /// (pre-registration) phase, where we only have the code string.</summary>
    static string FirstCodePart(string bareCode)
    {
        int dash = bareCode.IndexOf('-');
        return dash >= 0 ? bareCode.Substring(0, dash) : bareCode;
    }

    /// <summary>True if any of the recipe's ingredient slots has a code (domain stripped) for which
    /// <paramref name="match"/> holds. Scans all slots rather than assuming a fixed pattern letter, so it doesn't
    /// matter which letter the wood/log slot uses; tool slots carry tags rather than a code, so they never match.
    /// Returns false when "ingredients" isn't the expected object shape (safe: no removal).</summary>
    static bool RecipeHasInput(JToken recipe, System.Func<string, bool> match)
    {
        if (recipe["ingredients"] is not JObject ingredients) return false;
        foreach (JProperty ing in ingredients.Properties())
        {
            string? code = StripDomain((string?)ing.Value["code"]);
            if (code != null && match(code)) return true;
        }
        return false;
    }

    /// <summary>Barrel-recipe counterpart to <see cref="RecipeHasInput"/>: a BARREL recipe's <c>ingredients</c> is a
    /// JArray of ingredient objects (not the grid's named-slot dict), so this iterates the array and tests each
    /// ingredient's domain-stripped <c>code</c>. Tags-only ingredients (no <c>code</c>, e.g. the mod's own bark
    /// ingredient) yield a null code and are skipped, so they never satisfy a log test — that is what spares the mod's
    /// bark → tannin recipes. Returns false when <c>ingredients</c> isn't the expected array shape (safe: no removal).</summary>
    static bool BarrelRecipeHasInput(JToken recipe, System.Func<string, bool> match)
    {
        if (recipe["ingredients"] is not JArray ingredients) return false;
        foreach (JToken ing in ingredients)
        {
            string? code = StripDomain((string?)ing["code"]);
            if (code != null && match(code)) return true;
        }
        return false;
    }

    // --- BarkPlan/E07 (T07.03): Wildcraft: Trees NON-log tannin NERF ---------------------------------------------------
    // DRAFT balance numbers (tuned at the T07.06 modpack playtest), hardcoded like every other BarkPlan scalar until E08.
    // Leaf route: MULTIPLY the leaf count (Wildcraft's 4 → 8 per litre) and a long seal, so leaf tannin is a slow,
    // tedious poor-man's path. Narrow-log route: cut the yield (5 L → 2 L per narrow log) to IW's raw-bark tier, medium
    // seal. Chosen (time model in memory vs-barrel-seal-flat-leather-floor) so bark (milled, ~204 h chain) always beats
    // leaves (120 h seal → ~300 h) and narrow logs, without deleting either — the poor stay served, bark stays worth it.
    const int WildcraftLeafTanninQuantityFactor = 2;   // ×2 leaves (4 → 8) per litre
    const int WildcraftLeafTanninSealHours = 120;
    const int WildcraftNarrowLogTanninLitres = 2;      // 5 L → 2 L per narrow log
    const int WildcraftNarrowLogTanninSealHours = 72;

    /// <summary>NERF (not remove) Wildcraft: Trees' two NON-log barrel tannin routes — leaf steeping and narrow-log
    /// steeping — so bark stays the primary tanning path (BarkPlan/E07, T07.03). A pass over <paramref name="basePath"/>
    /// (<c>recipes/barrel/</c>, all domains) via the shared <see cref="ForEachRecipeAsset"/> traversal (same server-only,
    /// before-RecipeLoader, mutate-asset.Data-then-write path as the strip). Runs only when
    /// <see cref="ImmersiveWoodworkingConfig.DisableVanillaLogTannin"/> is on AND <c>wildcrafttree</c> is loaded (caller
    /// gate); the whole-log routes are already removed by <see cref="IsLogToTannin"/>, and these two are the routes that
    /// pass spares. Structural, so it catches every leaf / narrow-log tannin recipe Wildcraft ships (weak AND strong)
    /// with no per-recipe pinning.</summary>
    static void NerfWildcraftNonLogTannin(ICoreAPI api, string basePath, ref int filesChanged, ref int recipesChanged)
        => ForEachRecipeAsset(api, basePath, recipes =>
        {
            int changed = 0;
            foreach (JToken recipe in recipes) if (NerfNonLogTanninRecipe(recipe)) changed++;
            return changed;
        }, ref filesChanged, ref recipesChanged);

    /// <summary>Rewrite ONE barrel recipe's numbers in place iff it is a NON-log route to tannin — output
    /// <c>weaktanninportion</c>/<c>strongtanninportion</c> AND a <c>leaves-*</c> OR <c>lognarrow-*</c> solid input;
    /// returns true if it changed anything. A <c>leaves-*</c> input → multiply that ingredient's <c>quantity</c> by
    /// <see cref="WildcraftLeafTanninQuantityFactor"/> and set <c>sealHours</c> to <see cref="WildcraftLeafTanninSealHours"/>
    /// (leave the water:tannin litres 1:1). A <c>lognarrow-*</c> input → set the <c>output</c> litres AND the liquid
    /// ingredient's litres to <see cref="WildcraftNarrowLogTanninLitres"/> (keeping Wildcraft's own water:tannin 1:1
    /// idiom) and <c>sealHours</c> to <see cref="WildcraftNarrowLogTanninSealHours"/>. The output gate keeps this to
    /// tannin; the leaf/narrow-log input gate is what tells these apart from the mod's OWN tags-only bark→tannin recipes
    /// (no <c>code</c> on their bark ingredient → neither branch, so they are never touched) and from vanilla/wood-mod
    /// WHOLE-log tannin (<c>log-placed-*</c>/<c>debarkedlog-*</c>, removed by the strip). <c>FirstCodePart</c> matches
    /// <c>lognarrow</c> exactly (not <c>log</c>), so a wide log never trips the narrow branch.</summary>
    static bool NerfNonLogTanninRecipe(JToken recipe)
    {
        string? output = OutputBareCode(recipe);
        if (output != "weaktanninportion" && output != "strongtanninportion") return false;
        if (recipe["ingredients"] is not JArray ingredients) return false;

        JObject? leaf = null, lognarrow = null;
        foreach (JToken ing in ingredients)
        {
            string? code = StripDomain((string?)ing["code"]);
            if (code == null) continue;
            string part = FirstCodePart(code);
            if (part == "leaves") leaf = ing as JObject;
            else if (part == "lognarrow") lognarrow = ing as JObject;
        }

        if (leaf != null)
        {
            int qty = (int?)leaf["quantity"] ?? 1;
            leaf["quantity"] = qty * WildcraftLeafTanninQuantityFactor;
            recipe["sealHours"] = WildcraftLeafTanninSealHours;
            return true;
        }
        if (lognarrow != null)
        {
            if (recipe["output"] is JObject outp && outp["litres"] != null)
                outp["litres"] = WildcraftNarrowLogTanninLitres;
            foreach (JToken ing in ingredients)                        // the water / weak-tannin liquid input carries litres
                if (ing is JObject o && o["litres"] != null) o["litres"] = WildcraftNarrowLogTanninLitres;
            recipe["sealHours"] = WildcraftNarrowLogTanninSealHours;
            return true;
        }
        return false;
    }
}
