using System;
using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>
/// The typed, gate-checked FACADE over <see cref="XSkillsCompat"/> — the only surface the rest of the mod
/// (XSkillsIntegrationPlan E02–E04) touches for xSkills. Every method here hides the reflection bridge AND all the
/// gates behind a single verb: is the integration installed and enabled, does the player have the ability at tier
/// &gt; 0, are we on the right side. When xSkills is absent, the master flag is off, or the fork changed its API,
/// every method is a no-op / returns false — so callers write the bonus-application path unconditionally and it
/// simply does nothing without xSkills (the same soft-dep shape as the mod's VSRoofing / Toolsmith integrations, one
/// layer deeper because it also reads the API).
///
/// Split by side, matching the underlying XLib guarantees:
/// <list type="bullet">
/// <item><see cref="GrantForestryXp"/> is SERVER-only — XLib's <c>AddExperience</c> no-ops on the client, so this
///   hard-gates to the server itself (a client call is a guaranteed no-op, per the task DoD) rather than trusting
///   every caller to be in a server branch.</item>
/// <item>The <c>TryGet*</c> reads are SIDE-AGNOSTIC for the OWN player — a player's skill set is synced to its owner
///   through WatchedAttributes, so the client can read its own tier. This is the linchpin of client-side swiftsaw
///   chop animation (T04.02), which speeds the LOCAL swing off the player's own swiftsaw tier. Reading ANOTHER
///   player's ability on the client hits an unpopulated skill set ⇒ tier 0 ⇒ false, so it degrades safely.</item>
/// </list>
/// The bridge instance is built once per side in <see cref="InitXSkills"/> (called from <see cref="Start"/>);
/// resolution inside <see cref="XSkillsCompat"/> is LAZY (first real call), so this construction costs nothing on a
/// world without xSkills. The per-player forestry skill also resolves lazily — a call before the player's skill set
/// exists (before OnPlayerNowPlaying) returns false with no exception.
/// </summary>
public partial class ImmersiveWoodworkingModSystem
{
    // The single reflection bridge to XLib.XLeveling. Non-null after InitXSkills (both sides); XSkillsCompat.Available
    // stays false — and every call here a no-op — when xSkills is absent or its API surface couldn't be resolved.
    XSkillsCompat? xskills;

    // The core API captured at Start, kept only so GrantForestryXp can hard-gate to the server without threading an
    // api/side argument through every gameplay caller. Null before Start (no facade call happens that early).
    ICoreAPI? xskillsApi;

    /// <summary>Construct the xSkills reflection bridge. Called once from <see cref="Start"/> on BOTH sides (the
    /// server needs it for XP grants and yield/wear application; the client needs it for own-player swiftsaw reads).
    /// Free when xSkills is absent — the bridge resolves nothing until its first real use.</summary>
    void InitXSkills(ICoreAPI api)
    {
        xskillsApi = api;
        xskills = new XSkillsCompat(api);
    }

    // ------------------------------------------------------------------------------------------------------
    // Diagnostics (IW_DEBUG only — test.bat builds -p:IwDebug=true; a shipped Release strips every call here,
    // arguments included, so there is zero runtime cost and zero log noise for players). Grep the run logs for
    // "[iw-xskills]". Answers three questions in ONE run: did our 4 abilities register, did their names localize
    // in BOTH lang domains (immersivewoodworking for the base GUI, xskills for the Gilded GUI), and what tier /
    // bonus fraction does swifthands actually resolve to when a player saws or chops (the speed question).
    // ------------------------------------------------------------------------------------------------------

    // Remembers the last swifthands read line so a per-tick read (sawing/chopping polls the factor every tick)
    // only logs when the value CHANGES — one line when you start, not a flood. Per-side (each side its own instance).
    string? lastSwiftDbg;

    [Conditional("IW_DEBUG")]
    void DbgX(string msg) => xskillsApi?.Logger.Notification("[iw-xskills] [{0}] {1}", xskillsApi.Side, msg);

    // swifthands-only, log-on-change: the decisive trace for "does the speed bonus apply". Filters out the other
    // three abilities' reads (they poll the same path) and dedups identical consecutive states so it never spams.
    [Conditional("IW_DEBUG")]
    void DbgSwift(string code, IPlayer? player, string detail)
    {
        if (code != "swifthands") return;
        string line = (player?.PlayerName ?? "?") + ": " + detail;
        if (line == lastSwiftDbg) return;
        lastSwiftDbg = line;
        xskillsApi?.Logger.Notification("[iw-xskills] [{0}] swifthands {1}", xskillsApi.Side, line);
    }

    /// <summary>
    /// The XP-grant trace (IW_DEBUG only). Every path that can END a grant — each early return in
    /// <see cref="GrantForestryXp"/> and the successful hand-off — logs exactly one line, tagged with the CALL SITE
    /// (<c>saw</c> / <c>debark</c> / <c>chop</c>), so a run answers "did the grant reach XLib, and did XLib take it"
    /// without any guessing. NOT deduplicated (unlike <see cref="DbgSwift"/>): grants are discrete, per-workpiece
    /// events, a handful per log, so every one of them must show — a dedup would hide exactly the "the second one
    /// silently did nothing" case this exists to catch. Grep the run logs for <c>[iw-xskills] xp</c>.
    /// </summary>
    [Conditional("IW_DEBUG")]
    internal void DbgXp(string site, IPlayer? player, string detail)
        => xskillsApi?.Logger.Notification("[iw-xskills] [{0}] xp {1} {2}: {3}",
            xskillsApi.Side, site, player?.PlayerName ?? "?", detail);

    // ------------------------------------------------------------------------------------------------------
    // Registration (XSkillsIntegrationPlan T01.04) — inject OUR 4 abilities into the existing forestry skill. Called
    // from Start().
    // ------------------------------------------------------------------------------------------------------

    // The 4 abilities in their FIXED registration order — this order is load-bearing. XLib assigns each ability an
    // int id by APPEND ORDER (Skill.AddAbility), and multiplayer ability packets travel by that INDEX, so the server
    // and every client must inject the SAME abilities in the SAME order for indices to line up. Same IW build on both
    // sides + this static array guarantees it. (Saves persist by NAME, not index, so a save made with the mod loads
    // fine without it and vice-versa — see the task's save/MP notes.) Values are flat int[] per XLib's model: index =
    // (tier-1)*3 + {0:start, 1:+perLevel, 2:cap}; all maxTier 3. sawyer/carefulsawyer intentionally mirror vanilla
    // lumberjack/carefullumberjack value curves (verified against XSkills Forestry.cs).
    //
    // Per-ability gate fields (2026-07-16 rebalance, user):
    //   tierLevels[3]   — the forestry level required to reach each tier {T1, T2, T3}. The engine's base required level
    //                     is a UNIFORM minLevel + tier - 1; where a tier is authored to need a HIGHER level than that,
    //                     RegisterForestryAbilities adds a same-skill SkillRequirement to raise it (only where it exceeds
    //                     the base, so a uniform curve like carefulsawyer's {1,2,3} stays requirement-free / tooltip-clean).
    //   foresterMinTier — the tier from which the `forester` profession (T1) is required: 2 = tier 1 free, tiers 2-3 need
    //                     the profession; splitter is 1 = even its tier 1 needs forester.
    // NOTE: tierLevels[0] (the ability minLevel) + values serialize into xSkills' auto ModConfig/XLeveling/forestry.json;
    // the forester + per-tier level REQUIREMENTS are code-added every load (not serialized). So a tierLevels[0] change
    // needs that forestry.json regenerated (delete it); requirement changes apply on every load regardless of the config.
    static readonly (string name, int[] tierLevels, int foresterMinTier, int[] values)[] ForestryAbilities =
        new (string, int[], int, int[])[]
    {
        ("sawyer",        new[] { 5, 7,  9 }, 2, new[] { 10, 1, 20, 20, 2, 40, 20, 2, 60 }), // +plank/beam yield
        ("carefulsawyer", new[] { 1, 2,  3 }, 2, new[] {  5, 1, 15,  5, 2, 25,  5, 2, 45 }), // chance to spend no saw/bark-spud durability (uniform +1/tier)
        ("splitter",      new[] { 7, 9, 12 }, 1, new[] {  8, 1, 16, 12, 2, 25, 12, 2, 35 }), // +firewood only; forester from tier 1 (mining gate REMOVED 2026-07-16, user)
        ("swifthands",    new[] { 3, 5,  7 }, 2, new[] {  3, 1, 10,  5, 1, 15, 10, 1, 35 }), // chop + rip speed; T3 caps at 35% for BOTH (chop cap XSkillsSwiftsawMaxChopSpeedBonus + rip cap XSkillsSwiftsawMaxSawSpeedBonus, both 0.35)
    };

    /// <summary>
    /// Register OUR 4 abilities into the existing <c>forestry</c> skill (E01/T01.04). Called from <see cref="Start"/>
    /// on BOTH sides, which is the correct window: it runs AFTER every mod's StartPre (so XSkills has already created
    /// forestry) and BEFORE any mod's AssetsLoaded — in particular before XSkills.AssetsLoaded, which Lang-resolves
    /// each ability's DisplayName/Description in place (XSkills.cs:345), so injecting here is what makes our ability
    /// names localize instead of showing raw keys. Both sides register in the same fixed order for MP index alignment.
    ///
    /// Gated on the master <see cref="ImmersiveWoodworkingConfig.XSkillsIntegration"/> flag AND on the bridge being
    /// resolvable (xSkills present + API intact): with either off this is a clean no-op and IW's forestry looks
    /// untouched. (Edge case: a server that disables the flag while a client keeps its default-on local config — the
    /// client never reads the config file, StartPre returns early off-server — will have the client register these 4
    /// locally, and since XLib only appends abilities and its join-time skill-config sync matches by NAME (never trims
    /// a client-only extra), those entries PERSIST for the session, not just until join. They stay harmless though: the
    /// server's client-learn handler null-guards an ability id it doesn't have (OnPlayerAbilityPackage → the
    /// bounds-checked PlayerSkill indexer, XLevelingServer.cs:683-684) and saves are by NAME, so a phantom entry can't
    /// crash or corrupt a save — it simply can't be learned. The real remedy is matching the flag on both sides.) Every
    /// XLib call is inside XSkillsCompat's own try/catch, so a fork API change silently disables the abilities without
    /// touching E02's independent XP path.
    /// </summary>
    void RegisterForestryAbilities()
    {
        if (xskills == null || !Config.XSkillsIntegration) return;
        DbgX("RegisterForestryAbilities: xSkills available=" + xskills.Available + " (config flag on)");
        if (!xskills.Available) return; // xSkills absent or API unresolved → nothing to register, IW forestry untouched

        try
        {
            foreach ((string name, int[] tierLevels, int foresterMinTier, int[] values) in ForestryAbilities)
            {
                int minLevel = tierLevels[0]; // tier-1 level = the ability's serialized minLevel (base for the tier formula)

                // The lang keys follow XSkills' own convention (ability-<name> / abilitydesc-<name>); the strings ship
                // in the mod's lang files and are (re)localized in AssetsLoaded (LocalizeForestryAbilities).
                // AddAbilityToForestry returns the live ability object (or null on a duplicate name / skill-not-ready,
                // already WarnOnce'd inside), which the requirement helpers need.
                object? ability = xskills.AddAbilityToForestry(
                    name,
                    "immersivewoodworking:ability-" + name,
                    "immersivewoodworking:abilitydesc-" + name,
                    minLevel: minLevel, maxTier: 3, values);
                DbgX("  register '" + name + "' levels=" + tierLevels[0] + "/" + tierLevels[1] + "/" + tierLevels[2]
                    + " forester>=T" + foresterMinTier + " -> " + (ability == null ? "NULL (duplicate / skill not ready)" : "ok"));
                if (ability == null) continue;

                // forester (T1) profession gate from `foresterMinTier` up — the same gate vanilla puts on its top
                // forestry perks (lumberjack/axeexpert). minTier:2 = tiers 1-2 free, only tier 2+ checks forester >= 1;
                // minTier:1 (splitter) = every tier checks it. AbilityRequirement auto-passes below minTier, so lower
                // tiers stay ungated. The sibling is resolved by name on the forestry template inside the helper.
                xskills.AddAbilityRequirement(ability, "forester", reqTier: 1, minTier: foresterMinTier);

                // Non-uniform per-tier level gates. Base required level = minLevel + tier - 1 (uniform +1/tier); where a
                // tier is authored higher (e.g. splitter 7/9/12 vs base 7/8/9), raise it with a same-skill forestry
                // SkillRequirement — added ONLY when it exceeds the base, so a uniform curve adds nothing and stays clean.
                for (int tier = 2; tier <= 3; tier++)
                {
                    int wantLevel = tierLevels[tier - 1];
                    if (wantLevel > minLevel + (tier - 1))
                        xskills.AddSkillLevelRequirement(ability, "forestry", level: wantLevel, minTier: tier);
                }
            }
        }
        catch (Exception e)
        {
            // Belt-and-braces: the compat helpers never throw (they degrade internally), but if anything unexpected
            // slips through, disable only the ABILITIES — E02's XP grants use the separate AddExperience path and
            // keep working. Silent-degrade, never crash IW.
            xskillsApi?.Logger.Warning("[immersivewoodworking] xSkills ability registration failed: {0}. Abilities inactive; IW otherwise normal.", e.Message);
        }
    }

    /// <summary>
    /// Re-localize OUR 4 ability display names + descriptions IN PLACE, from IW's own lang keys. Called from
    /// <see cref="AssetsLoaded"/> on BOTH sides — the CLIENT pass is the one that matters (the skills GUI reads
    /// <c>Ability.DisplayName</c>), the server pass is harmless. xSkills already runs its own
    /// <c>Lang.GetUnformatted</c> pass over every ability in its AssetsLoaded, so on a healthy load our names localize
    /// through THAT; doing it ourselves too makes it SELF-CONTAINED — IW no longer depends on that pass running, so a
    /// future fork change (or any lang/timing/stale-build edge that left raw keys showing) can't silently revert our
    /// abilities to raw keys. Idempotent: we resolve from the FIXED key (not the current DisplayName), and
    /// <c>GetUnformatted</c> on an already-resolved plain string is a no-op, so it composes with xSkills' pass in
    /// either order. No-op without xSkills / integration off. MUST run in AssetsLoaded, not Start: on the client the
    /// mod's lang is only (re)loaded after the Start phase, so a Start-time resolve would itself read raw keys.
    /// </summary>
    void LocalizeForestryAbilities()
    {
        if (xskills == null || !Config.XSkillsIntegration || !xskills.Available) return;
        DbgX("LocalizeForestryAbilities running (this side renders the skills GUI if it is the client)");
        foreach (var (name, _, _, _) in ForestryAbilities)
        {
            string display = Lang.GetUnformatted("immersivewoodworking:ability-" + name);
            string desc = Lang.GetUnformatted("immersivewoodworking:abilitydesc-" + name);
            xskills.LocalizeForestryAbility(name, display, desc);
            // Log BOTH domains: 'iw' is what we set on Ability.DisplayName (base xLib/xSkills GUI reads this);
            // 'xskills' is what the Gilded GUI looks up. A value equal to the KEY itself (e.g. "xskills:ability-...")
            // means that domain did NOT resolve. If iw resolves but xskills does not, the raw-name bug is Gilded-only.
            DbgX("  '" + name + "': iw=\"" + display + "\"  xskills=\"" + Lang.GetUnformatted("xskills:ability-" + name) + "\"");
        }
    }

    /// <summary>
    /// Grant <paramref name="amount"/> forestry XP to <paramref name="player"/> (XSkillsIntegrationPlan E02, once per
    /// completed manual operation). Effective ONLY on the server and ONLY when the integration is present and enabled —
    /// a client call, a null player, a non-positive amount, or a missing/failed xSkills all resolve to a silent no-op.
    /// Callers grant from their server branch anyway; the side hard-gate here is belt-and-braces so an accidental
    /// client call can never touch skill state.
    /// </summary>
    /// <param name="site">Call-site tag for the IW_DEBUG trace only (<c>saw</c> / <c>debark</c> / <c>chop</c>) — it has
    /// no effect on behaviour, and a Release build strips every <see cref="DbgXp"/> call that reads it.</param>
    public void GrantForestryXp(IPlayer player, float amount, string site = "?")
    {
        if (xskills == null || player?.Entity == null) { DbgXp(site, player, "SKIP: bridge or player entity null"); return; }
        if (!Config.XSkillsIntegration) { DbgXp(site, player, "SKIP: XSkillsIntegration=false"); return; }
        if (amount <= 0f) { DbgXp(site, player, "SKIP: amount=" + amount.ToString("0.####") + " <= 0"); return; }
        if (xskillsApi == null || xskillsApi.Side != EnumAppSide.Server) return; // AddExperience is a client no-op anyway; hard-gate per DoD (silent: the client hits this on EVERY grant)
        if (!xskills.Available) { DbgXp(site, player, "SKIP: xSkills not available"); return; }

        object? skill = xskills.GetForestrySkill(player.Entity);
        if (skill == null) { DbgXp(site, player, "SKIP: no forestry PlayerSkill (skill set not populated yet)"); return; }

        // Read the live skill BEFORE and AFTER, so the line proves whether XLib actually took the experience — a grant
        // that reaches AddExperience but leaves exp unchanged (capped level, disabled skill, a zeroed multiplier) is
        // otherwise indistinguishable from one that was never made. Inside #if, NOT behind [Conditional]: DescribeSkill
        // is a real (reflecting) call whose RESULT is used, so only the preprocessor can keep it out of Release.
#if IW_DEBUG
        string before = xskills.DescribeSkill(skill);
#endif
        xskills.AddExperience(skill, amount, invokeModifiers: true);
#if IW_DEBUG
        DbgXp(site, player, "+" + amount.ToString("0.####") + "  " + before + " -> " + xskills.DescribeSkill(skill));
#endif
    }

    /// <summary>sawyer — extra plank/beam yield FRACTION at the player's current forestry level (E03/T03.01). True with
    /// the fraction only when learned (tier &gt; 0) AND the apply toggle is on; false with 0 otherwise.</summary>
    public bool TryGetSawyerYield(IPlayer player, out float extraFraction)
    {
        extraFraction = 0f;
        if (!Config.XSkillsSawyerYield) return false;
        return TryGetForestryAbilityFraction(player, "sawyer", out extraFraction);
    }

    /// <summary>splitter — extra FIREWOOD yield fraction at the current forestry level (E03/T03.02; firewood only —
    /// sticks stay vanilla). Same contract as <see cref="TryGetSawyerYield"/>.</summary>
    public bool TryGetSplitterYield(IPlayer player, out float extraFraction)
    {
        extraFraction = 0f;
        if (!Config.XSkillsSplitterYield) return false;
        return TryGetForestryAbilityFraction(player, "splitter", out extraFraction);
    }

    /// <summary>carefulsawyer — chance (fraction 0..1) to spend NO durability on a saw / bark-spud use
    /// (E03/T03.03–04). Same contract as the yield reads.</summary>
    public bool TryGetCarefulSawyerSave(IPlayer player, out float saveFraction)
    {
        saveFraction = 0f;
        if (!Config.XSkillsCarefulSawyer) return false;
        return TryGetForestryAbilityFraction(player, "carefulsawyer", out saveFraction);
    }

    /// <summary>carefulsawyer — the special-bark CHANCE bonus FRACTION (E02/T02.06): a careful stripper rolls the
    /// special bark kinds more often. Deliberately the SAME read as <see cref="TryGetCarefulSawyerSave"/> — one
    /// carefulsawyer tier, one <see cref="ImmersiveWoodworkingConfig.XSkillsCarefulSawyer"/> apply toggle — exposed under
    /// a distinct name for a distinct EFFECT (bark quality here, tool wear there), exactly the way the single
    /// <c>swiftsaw</c> ability drives BOTH the chop and the rip speed. The caller folds this fraction in as a
    /// <c>×(1 + fraction)</c> factor on the debarker's own tool-tier bark-chance multiplier BEFORE the co-op max
    /// (<see cref="BlockEntitySawhorse.BestActiveDebarkerBarkChanceMult"/>); the natural top clamp still lives in
    /// <see cref="BarkDropTable.Roll"/>'s cumulative walk, so a big combined multiplier can only saturate a species' odds
    /// at 100%, never overflow. True with the fraction only when learned (tier &gt; 0) AND the toggle is on; false with 0
    /// otherwise (→ <c>×1.0</c>, so the drop distribution is unchanged without xSkills / an unlearned perk). Same
    /// contract as the yield reads; server-side in practice (the debark roll is), side-agnostic for the own player.</summary>
    public bool TryGetCarefulSawyerBarkBonus(IPlayer player, out float bonusFraction)
    {
        bonusFraction = 0f;
        if (!Config.XSkillsCarefulSawyer) return false;
        return TryGetForestryAbilityFraction(player, "carefulsawyer", out bonusFraction);
    }

    /// <summary>swiftsaw — chop/saw speed bonus fraction at the current forestry level (E04). SIDE-AGNOSTIC for the
    /// OWN player: the chop animation reads this on the CLIENT off the local player's own tier (the swiftsaw linchpin).
    /// Same contract as the yield reads.</summary>
    public bool TryGetSwiftsawFactor(IPlayer player, out float speedFraction)
    {
        speedFraction = 0f;
        if (!Config.XSkillsSwiftsawSpeed) return false;
        return TryGetForestryAbilityFraction(player, "swifthands", out speedFraction);
    }

    // ------------------------------------------------------------------------------------------------------
    // swiftsaw application helpers (XSkillsIntegrationPlan T04.01) — turn the raw swiftsaw FRACTION into the two
    // numbers the chop and rip sites actually consume, with the hard code-caps already applied. Both are pure functions
    // of the player's own (synced) tier + the (synced) Config, so client and server compute the SAME value.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The EFFECTIVE per-swing chop seconds for <paramref name="player"/> on the chopping block: <paramref name="baseSeconds"/>
    /// (the caller passes <see cref="ImmersiveWoodworkingConfig.ChopSeconds"/>) shortened by the player's swiftsaw bonus,
    /// hard-capped so the speed-up never exceeds the <see cref="ImmersiveWoodworkingConfig.XSkillsSwiftsawMaxChopSpeedBonus"/>
    /// fraction of the swing (symmetric with the rip cap), then floored at HALF the base so even a pathological config can't
    /// drive the swing toward 0 — a 0-second swing would divide-by-zero <c>StartChopAnimation</c>'s speed (this floor is a
    /// second net beyond the config's own <c>[0, 0.5]</c> clamp). Returns <paramref name="baseSeconds"/> unchanged when the integration is off, the
    /// swiftsaw ability is unlearned (tier 0), or the bonus is 0 — the swing then keeps its normal length.
    ///
    /// SIDE-AGNOSTIC for the OWN player: the chop animation reads this on the CLIENT (the holder's local swing) while the
    /// server reads the same for the 3rd-person observer replica; both compute the SAME number off the same synced swiftsaw
    /// tier + synced Config, so the visual swing and its impact sound stay in lockstep (the impact frame is timed to land on
    /// exactly this value). Capture it ONCE per hold (BeginChopHold) rather than re-reading per tick, so a mid-hold tier
    /// change (a rare level-up mid-swing) can't drift the pacing between client and server.
    /// </summary>
    public float EffectiveChopSeconds(IPlayer player, float baseSeconds)
    {
        if (!TryGetSwiftsawFactor(player, out float f)) return baseSeconds;
        float reduction = baseSeconds * Math.Min(f, Config.XSkillsSwiftsawMaxChopSpeedBonus);
        float result = baseSeconds - reduction;
        return Math.Max(result, 0.5f * baseSeconds); // hard floor: a swing is never faster than half the base (never 0)
    }

    /// <summary>
    /// The ripping-tempo MULTIPLIER for <paramref name="player"/> on the sawhorses: <c>1 + swiftsaw bonus</c>, hard-capped
    /// so it never exceeds <c>×(1 + </c><see cref="ImmersiveWoodworkingConfig.XSkillsSwiftsawMaxSawSpeedBonus"/><c>)</c>.
    /// Returns 1 (no change) when the integration is off, the ability is unlearned (tier 0), or the bonus is 0 — so the
    /// caller multiplies <c>SawSpeedFor</c> by this UNCONDITIONALLY and it simply does nothing without swiftsaw.
    ///
    /// Applied on the SERVER-authoritative rip-progress path only (the client sees the synced <c>sawProgress</c>, so it must
    /// not scale independently — that would desync progress). Only the rip TEMPO is scaled; the saw animation/sound loop is
    /// never touched — its sound is baked into fixed loop frames, so changing its <c>AnimationSpeed</c> would desync the
    /// audio. Unlike the chop read this is not needed on the client, but it stays side-agnostic for symmetry.
    /// </summary>
    public float SwiftsawSawFactor(IPlayer player)
    {
        if (!TryGetSwiftsawFactor(player, out float f)) return 1f;
        return 1f + Math.Min(f, Config.XSkillsSwiftsawMaxSawSpeedBonus);
    }

    /// <summary>
    /// Shared read path behind every ability wrapper: resolve the player's forestry skill, find <paramref name="code"/>,
    /// and return its finished bonus FRACTION at the player's CURRENT forestry level — XLib
    /// <c>SkillDependentFValue(0)</c> = <c>Min(start + perLevel*level, cap) * 0.01</c> (0 → use the skill's own level;
    /// verified at PlayerAbility.cs:137). Returns false with 0 whenever the integration is off, the player/skill set
    /// isn't ready, the ability is unlearned (tier 0), or the bonus computes to 0 — so a caller applies a real bonus
    /// only when this is true. Side-agnostic (own-player skill set is client-synced). The per-ability apply toggle is
    /// checked by each public wrapper BEFORE this, so a disabled ability never even resolves.
    /// </summary>
    bool TryGetForestryAbilityFraction(IPlayer player, string code, out float fraction)
    {
        fraction = 0f;
        if (xskills == null || player?.Entity == null) return false;
        if (!Config.XSkillsIntegration) return false;
        if (!xskills.Available) return false;

        object? skill = xskills.GetForestrySkill(player.Entity);
        if (skill == null) { DbgSwift(code, player, "no forestry skill (skillset not synced yet)"); return false; }
        object? ability = xskills.FindAbility(skill, code);
        if (ability == null) { DbgSwift(code, player, "ability NOT FOUND on forestry (registration/name mismatch)"); return false; }
        int tier = xskills.AbilityTier(ability);
        if (tier <= 0) { DbgSwift(code, player, "tier=0 -> factor 1.0 (ability not invested; leveling forestry is NOT investing the point)"); return false; }

        float f = xskills.SkillDependentF(ability, 0); // 0 → use the player's current forestry level
        DbgSwift(code, player, "tier=" + tier + " fraction=" + f.ToString("0.###") + " -> saw x" + (1f + Math.Min(f, Config.XSkillsSwiftsawMaxSawSpeedBonus)).ToString("0.###"));
        if (f <= 0f) return false;
        fraction = f;
        return true;
    }
}
