using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ImmersiveWoodworking;

/// <summary>
/// The mod's ONLY point of contact with the xSkills fork's <c>XLib.XLeveling</c> API, reached entirely by
/// REFLECTION. There is deliberately no compile-time <c>&lt;Reference&gt;</c> to xskills.dll / xlibfork.dll and no
/// dependency entry in modinfo.json: a hard reference would make .NET throw TypeLoad/FileNotFound the instant IW
/// loads on a world WITHOUT xSkills installed. Instead every XLib type/member is resolved by string name from the
/// assembly of the live <c>XLib.XLeveling.XLeveling</c> ModSystem, the handles are cached once, and if ANYTHING
/// fails to resolve (mod absent, or the fork renamed a member) the whole integration switches itself OFF
/// (<see cref="Available"/> = false) and every method degrades to a null / -1 / no-op — IW keeps running exactly as
/// it does today. This mirrors the mod's other soft integrations (VSRoofing / Toolsmith, gated by
/// <c>IsModEnabled</c>), just one layer deeper because we also touch the API surface, not only its presence.
///
/// One instance lives on the ModSystem (wired in XSkillsIntegrationPlan T01.03). Resolution is LAZY: the first call to any
/// method (or to <see cref="Available"/>) runs <see cref="EnsureInit"/> once, behind a lock so a client render-thread read
/// (swiftsaw, T04.02) can't race the registration path. After that, calls are plain cached-handle invokes.
///
/// Verified against the fork source at <c>reference-mods/xskills-fork_xlib-fork-master/mods/{xlib,xskills}/src</c>
/// (adversarial file:line pass). Notes that shaped this file:
/// <list type="bullet">
/// <item><c>XLeveling.GetSkill</c> has BOTH a <c>(string,bool)</c> and an <c>(int)</c> overload — the int one is
///   off-by-one/buggy — so we bind the method with an explicit <c>Type[]{string,bool}</c> to never hit it.</item>
/// <item><c>Ability</c> has TWO public ctors (an <c>int valuesPerTier</c> one and the <c>int[] values</c> one we
///   want); <c>GetConstructor</c> is bound by the exact <c>Type[]</c> so we always pick the array overload.</item>
/// <item>Requirement ctors are 4-arg (<c>SkillRequirement(Skill,int,int,bool)</c> /
///   <c>AbilityRequirement(Ability,int,int,bool)</c>); C# optional-parameter defaults are compile-time only, so
///   reflection must pass ALL four arguments explicitly — we do.</item>
/// <item><c>PlayerSkillSet.PropertyName()</c> is <c>"SkillSet"</c>, so a player's skill set is fetched with the
///   engine's own <c>Entity.GetBehavior(string)</c> (which matches on PropertyName, as xSkills itself relies on for
///   <c>GetBehavior("health")</c>) — no reflection hop needed for that lookup.</item>
/// <item><c>PlayerAbility.SkillDependentFValue(skillLevel)</c> computes <c>Min(v0 + v1*level, v2) * 0.01</c> reading
///   the current tier internally — i.e. exactly the <c>{start, +perLevel, cap}</c> percent model the plan uses, so
///   one call yields the finished bonus fraction for an ability at the player's tier+level.</item>
/// </list>
/// </summary>
public sealed class XSkillsCompat
{
    // The mod ids we accept as "xSkills is here". The fork ships as "xskillsfork" (its XLib dependency is
    // "xlibfork"); we also accept plain "xskills" because the fork's ModidSpoofSystem answers true for the vanilla
    // id, and a player might run the non-fork build. Either present is enough to attempt resolution.
    const string ForkModId = "xskillsfork";
    const string BaseModId = "xskills";

    // The XLib skill we hang our abilities on and grant XP to. Case-sensitive (XLib compares Skill.Name == name).
    public const string ForestrySkill = "forestry";

    readonly ICoreAPI api;
    readonly object initLock = new object();

    volatile bool initialized; // volatile: makes the double-checked EnsureInit correct — its true write publishes every handle below
    bool available;
    bool warned; // one-shot guard so a resolve/invoke failure logs a single warning, never a per-call spam

    // The live XLib.XLeveling.XLeveling ModSystem instance (its assembly is where every other XLib type is resolved).
    object? xLeveling;

    // The forestry Skill TEMPLATE (not per-player) — cached lazily on first successful GetForestrySkillTemplate,
    // because it doesn't exist until XSkills.StartPre has created it. Never cached as null (a too-early call retries).
    object? forestrySkillTemplate;

    // --- cached reflection handles (all resolved once in TryResolve; non-null whenever available == true) ---
    MethodInfo? miGetSkill;                 // XLeveling.GetSkill(string, bool)          -> Skill
    MethodInfo? miAddAbility;               // Skill.AddAbility(Ability)                 -> int id
    MethodInfo? miSkillFindAbility;         // Skill.FindAbility(string, bool)           -> Ability   (sibling lookup)
    MethodInfo? miAbilityAddRequirement;    // Ability.AddRequirement(Requirement)       -> void
    ConstructorInfo? ctorAbility;           // Ability(string,string,string,int,int,int[],bool)
    ConstructorInfo? ctorSkillReq;          // SkillRequirement(Skill,int,int,bool)
    ConstructorInfo? ctorAbilityReq;        // AbilityRequirement(Ability,int,int,bool)
    MethodInfo? miPlayerSetFindSkill;       // PlayerSkillSet.FindSkill(string, bool)    -> PlayerSkill
    MethodInfo? miPlayerSkillAddExp;        // PlayerSkill.AddExperience(float, bool)    -> void
    MethodInfo? miPlayerSkillFindAbility;   // PlayerSkill.FindAbility(string, bool)     -> PlayerAbility
    PropertyInfo? piPlayerAbilityTier;      // PlayerAbility.Tier                        -> int
    MethodInfo? miPlayerAbilitySkillDependentF; // PlayerAbility.SkillDependentFValue(int) -> float
    PropertyInfo? piAbilityDisplayName;     // Ability.DisplayName {get;set;}            (re-localize our names in place)
    PropertyInfo? piAbilityDescription;     // Ability.Description {get;set;}

    // --- OPTIONAL diagnostic handles (NOT part of the all-or-nothing gate below) ---
    // Read back a PlayerSkill's live level / experience so an IW_DEBUG run can PROVE whether a grant landed
    // (see DescribeSkill). Deliberately resolved AFTER the availability decision and never checked for null there:
    // these are pure telemetry, so a fork that renames one of them must NOT disable the whole integration — it just
    // makes the debug line read "?" instead. Every use is null-guarded.
    PropertyInfo? piPlayerSkillLevel;          // PlayerSkill.Level              -> int
    PropertyInfo? piPlayerSkillExperience;     // PlayerSkill.Experience         -> float
    PropertyInfo? piPlayerSkillRequiredExp;    // PlayerSkill.RequiredExperience -> float

    public XSkillsCompat(ICoreAPI api)
    {
        this.api = api;
    }

    /// <summary>True only when an accepted xSkills mod is installed AND every XLib type/member resolved. When false,
    /// every method below is a no-op. Triggers the one-time lazy resolve on first access.</summary>
    public bool Available
    {
        get { EnsureInit(); return available; }
    }

    /// <summary>The cached XLib.XLeveling.XLeveling ModSystem object, or null when the integration is off. Callers
    /// use it only to prove presence; all real work goes through the typed methods here.</summary>
    public object? GetXLeveling()
    {
        EnsureInit();
        return available ? xLeveling : null;
    }

    // ------------------------------------------------------------------------------------------------------
    // Lazy one-time resolution
    // ------------------------------------------------------------------------------------------------------

    void EnsureInit()
    {
        if (initialized) return;
        lock (initLock)
        {
            if (initialized) return;
            try
            {
                available = TryResolve();
            }
            catch (Exception e)
            {
                available = false;
                WarnOnce("resolve threw " + e.GetType().Name + ": " + e.Message);
            }
            initialized = true;
        }
    }

    /// <summary>Resolve and cache every handle. Returns false (integration off) if xSkills is absent — SILENTLY, that
    /// is the normal "not installed" case — or logs one warning and returns false if the mod IS present but a type or
    /// member could not be found (the fork changed its API shape). All-or-nothing: a partial resolve is treated as off.</summary>
    bool TryResolve()
    {
        // Gate on presence first — never touch reflection when neither mod is loaded (the overwhelmingly common case).
        if (!api.ModLoader.IsModEnabled(ForkModId) && !api.ModLoader.IsModEnabled(BaseModId)) return false;

        // The XLeveling ModSystem is resolved by its exact modid string (same call XLeveling.Instance uses internally).
        // If it's null despite the mod being "enabled", the install is odd (xskills without xlib) — stay silently off.
        object? xlev = api.ModLoader.GetModSystem("XLib.XLeveling.XLeveling");
        if (xlev == null) return false;

        Assembly asm = xlev.GetType().Assembly;
        Type tXlev = xlev.GetType();
        Type? tSkill = asm.GetType("XLib.XLeveling.Skill");
        Type? tAbility = asm.GetType("XLib.XLeveling.Ability");
        Type? tRequirement = asm.GetType("XLib.XLeveling.Requirement");
        Type? tSkillReq = asm.GetType("XLib.XLeveling.SkillRequirement");
        Type? tAbilityReq = asm.GetType("XLib.XLeveling.AbilityRequirement");
        Type? tPlayerSet = asm.GetType("XLib.XLeveling.PlayerSkillSet");
        Type? tPlayerSkill = asm.GetType("XLib.XLeveling.PlayerSkill");
        Type? tPlayerAbility = asm.GetType("XLib.XLeveling.PlayerAbility");

        if (tSkill == null || tAbility == null || tRequirement == null || tSkillReq == null ||
            tAbilityReq == null || tPlayerSet == null || tPlayerSkill == null || tPlayerAbility == null)
        {
            WarnOnce("one or more XLib.XLeveling types not found (fork API changed?)");
            return false;
        }

        // Method/ctor/property binding. Every Type[] is explicit so an added overload can never silently rebind us:
        //  - GetSkill(string,bool) dodges the buggy GetSkill(int) overload.
        //  - the Ability(...int[]...) ctor is picked over the Ability(...int valuesPerTier...) ctor by its 6th arg type.
        //  - the requirement ctors are the FULL 4-arg forms (reflection won't fill C# optional defaults).
        miGetSkill = tXlev.GetMethod("GetSkill", new[] { typeof(string), typeof(bool) });
        miAddAbility = tSkill.GetMethod("AddAbility", new[] { tAbility });
        miSkillFindAbility = tSkill.GetMethod("FindAbility", new[] { typeof(string), typeof(bool) });
        miAbilityAddRequirement = tAbility.GetMethod("AddRequirement", new[] { tRequirement });
        ctorAbility = tAbility.GetConstructor(new[]
        {
            typeof(string), typeof(string), typeof(string), typeof(int), typeof(int), typeof(int[]), typeof(bool)
        });
        ctorSkillReq = tSkillReq.GetConstructor(new[] { tSkill, typeof(int), typeof(int), typeof(bool) });
        ctorAbilityReq = tAbilityReq.GetConstructor(new[] { tAbility, typeof(int), typeof(int), typeof(bool) });
        miPlayerSetFindSkill = tPlayerSet.GetMethod("FindSkill", new[] { typeof(string), typeof(bool) });
        miPlayerSkillAddExp = tPlayerSkill.GetMethod("AddExperience", new[] { typeof(float), typeof(bool) });
        miPlayerSkillFindAbility = tPlayerSkill.GetMethod("FindAbility", new[] { typeof(string), typeof(bool) });
        piPlayerAbilityTier = tPlayerAbility.GetProperty("Tier");
        miPlayerAbilitySkillDependentF = tPlayerAbility.GetMethod("SkillDependentFValue", new[] { typeof(int) });
        piAbilityDisplayName = tAbility.GetProperty("DisplayName");
        piAbilityDescription = tAbility.GetProperty("Description");

        if (miGetSkill == null || miAddAbility == null || miSkillFindAbility == null || miAbilityAddRequirement == null ||
            ctorAbility == null || ctorSkillReq == null || ctorAbilityReq == null || miPlayerSetFindSkill == null ||
            miPlayerSkillAddExp == null || miPlayerSkillFindAbility == null || piPlayerAbilityTier == null ||
            miPlayerAbilitySkillDependentF == null || piAbilityDisplayName == null || piAbilityDescription == null)
        {
            WarnOnce("one or more XLib.XLeveling members not found (fork API changed?)");
            return false;
        }

        // Diagnostics only, resolved AFTER the gate above so a missing/renamed member can never turn the integration
        // off — DescribeSkill degrades to "?" instead. See the field comments.
        piPlayerSkillLevel = tPlayerSkill.GetProperty("Level");
        piPlayerSkillExperience = tPlayerSkill.GetProperty("Experience");
        piPlayerSkillRequiredExp = tPlayerSkill.GetProperty("RequiredExperience");

        xLeveling = xlev;
        return true;
    }

    /// <summary>A one-line snapshot of a player's live forestry <c>PlayerSkill</c> — <c>"lvl=3 exp=12.35/532"</c> — for
    /// the IW_DEBUG XP trace, or <c>"?"</c> when the integration is off / the skill is null / any of the optional
    /// diagnostic handles didn't resolve. Pure telemetry: never throws, never warns (a failed read here must not
    /// consume the one-shot <see cref="WarnOnce"/> budget that real failures need), and never affects behaviour.
    /// Reading it BEFORE and AFTER <see cref="AddExperience"/> is what proves whether a grant actually landed.</summary>
    public string DescribeSkill(object? playerSkill)
    {
        if (!available || playerSkill == null) return "?";
        if (piPlayerSkillLevel == null || piPlayerSkillExperience == null) return "?";
        try
        {
            object? lvl = piPlayerSkillLevel.GetValue(playerSkill);
            object? exp = piPlayerSkillExperience.GetValue(playerSkill);
            object? req = piPlayerSkillRequiredExp?.GetValue(playerSkill);
            return "lvl=" + (lvl is int l ? l.ToString() : "?")
                 + " exp=" + (exp is float e ? e.ToString("0.####") : "?")
                 + "/" + (req is float r ? r.ToString("0.#") : "?");
        }
        catch
        {
            return "?"; // telemetry must never disturb the run
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Registration side (used in Start()/AssetsLoaded() by XSkillsIntegrationPlan T01.04) — operates on the
    // forestry skill TEMPLATE
    // ------------------------------------------------------------------------------------------------------

    /// <summary>The shared forestry Skill template (for registering abilities / building requirements), or null if
    /// the integration is off OR the skill doesn't exist yet (called before XSkills created it). Cached once resolved.</summary>
    public object? GetForestrySkillTemplate()
    {
        EnsureInit();
        if (!available) return null;
        if (forestrySkillTemplate != null) return forestrySkillTemplate;
        try
        {
            // Cache only a non-null result: an early call (skill not yet built) returns null without poisoning the cache.
            forestrySkillTemplate = miGetSkill!.Invoke(xLeveling, new object[] { ForestrySkill, false });
        }
        catch (Exception e)
        {
            WarnOnce("GetSkill(\"forestry\") threw: " + e.Message);
            return null;
        }
        return forestrySkillTemplate;
    }

    /// <summary>
    /// Construct one of OUR abilities (the <c>int[] values</c> ctor: {start,+perLevel,cap} per tier) and register it
    /// into the forestry skill, returning the live ABILITY OBJECT (or null on any failure — mod off, skill not ready,
    /// or AddAbility rejected it as a duplicate/already-owned). The object is what the requirement helpers below need;
    /// see the deviation note in the task file on why this returns the object rather than the raw int id.
    /// </summary>
    /// <param name="values">Flattened tier values, length = maxTier * valuesPerTier (3 here: start, perLevel, cap).</param>
    public object? AddAbilityToForestry(string name, string? displayName, string? description, int minLevel, int maxTier, int[] values)
    {
        EnsureInit();
        if (!available) return null;
        object? skill = GetForestrySkillTemplate();
        if (skill == null) return null;
        try
        {
            object ability = ctorAbility!.Invoke(new object?[] { name, displayName, description, minLevel, maxTier, values, false })!;
            object? idObj = miAddAbility!.Invoke(skill, new object?[] { ability });
            int id = idObj is int i ? i : -1;
            if (id < 0)
            {
                // -1 = duplicate name or the ability already belongs to a skill. Treat as "not registered" so callers
                // (and the requirement helpers) don't act on a half-attached ability. Not fatal; integration stays on.
                WarnOnce("AddAbility rejected '" + name + "' (already registered?)");
                return null;
            }
            return ability;
        }
        catch (Exception e)
        {
            WarnOnce("AddAbility('" + name + "') threw: " + e.Message);
            return null;
        }
    }

    /// <summary>Attach a "requires forestry level <paramref name="level"/>" gate to <paramref name="ability"/>,
    /// effective once the ability reaches tier <paramref name="minTier"/>. No-op when the integration is off or the
    /// ability object is null.</summary>
    public void AddSkillRequirement(object? ability, int level, int minTier)
    {
        EnsureInit();
        if (!available || ability == null) return;
        object? skill = GetForestrySkillTemplate();
        if (skill == null) return;
        try
        {
            object req = ctorSkillReq!.Invoke(new object?[] { skill, level, minTier, false })!;
            miAbilityAddRequirement!.Invoke(ability, new object?[] { req });
        }
        catch (Exception e)
        {
            WarnOnce("AddSkillRequirement threw: " + e.Message);
        }
    }

    /// <summary>Attach a "requires sibling ability <paramref name="otherCode"/> at tier <paramref name="reqTier"/>"
    /// gate to <paramref name="ability"/> (e.g. forester T1), effective once the ability reaches tier
    /// <paramref name="minTier"/>. The sibling is resolved on the forestry template by name. No-op when the
    /// integration is off, the ability is null, or the sibling isn't found.</summary>
    public void AddAbilityRequirement(object? ability, string otherCode, int reqTier, int minTier)
    {
        EnsureInit();
        if (!available || ability == null) return;
        object? skill = GetForestrySkillTemplate();
        if (skill == null) return;
        try
        {
            object? other = miSkillFindAbility!.Invoke(skill, new object[] { otherCode, false });
            if (other == null)
            {
                WarnOnce("sibling ability '" + otherCode + "' not found on forestry — requirement skipped");
                return;
            }
            object req = ctorAbilityReq!.Invoke(new object?[] { other, reqTier, minTier, false })!;
            miAbilityAddRequirement!.Invoke(ability, new object?[] { req });
        }
        catch (Exception e)
        {
            WarnOnce("AddAbilityRequirement threw: " + e.Message);
        }
    }

    /// <summary>Attach a "requires level <paramref name="level"/> in skill <paramref name="skillName"/>" gate to
    /// <paramref name="ability"/>, effective from tier <paramref name="minTier"/> up. <paramref name="skillName"/> may be
    /// a DIFFERENT skill (a cross-skill gate) OR the ability's own skill <c>forestry</c> — the latter raises a specific
    /// tier's required level above the engine's uniform <c>minLevel + tier - 1</c> base (how we author non-uniform tier
    /// levels like 7/9/12). Resolves the skill's TEMPLATE by name via <c>GetSkill(string,bool)</c> (never the buggy
    /// <c>GetSkill(int)</c>). No-op when the integration is off, the ability is null, or that skill isn't loaded — in
    /// which case the requirement is simply skipped.</summary>
    public void AddSkillLevelRequirement(object? ability, string skillName, int level, int minTier)
    {
        EnsureInit();
        if (!available || ability == null) return;
        try
        {
            object? skill = miGetSkill!.Invoke(xLeveling, new object[] { skillName, false });
            if (skill == null)
            {
                WarnOnce("skill '" + skillName + "' not found for a skill-level requirement — requirement skipped");
                return;
            }
            object req = ctorSkillReq!.Invoke(new object?[] { skill, level, minTier, false })!;
            miAbilityAddRequirement!.Invoke(ability, new object?[] { req });
        }
        catch (Exception e)
        {
            WarnOnce("AddSkillLevelRequirement('" + skillName + "') threw: " + e.Message);
        }
    }

    /// <summary>Set OUR ability's <c>DisplayName</c> + <c>Description</c> (already Lang-resolved by the caller) IN PLACE
    /// on the forestry template, found by <paramref name="code"/>. Lets IW localize its own ability names self-
    /// containedly (see the facade's <c>LocalizeForestryAbilities</c>) instead of relying on xSkills' AssetsLoaded
    /// Lang pass. No-op when the integration is off or the ability isn't found.</summary>
    public void LocalizeForestryAbility(string code, string displayName, string description)
    {
        EnsureInit();
        if (!available) return;
        object? skill = GetForestrySkillTemplate();
        if (skill == null) return;
        try
        {
            object? ability = miSkillFindAbility!.Invoke(skill, new object[] { code, false });
            if (ability == null) return;
            piAbilityDisplayName!.SetValue(ability, displayName);
            piAbilityDescription!.SetValue(ability, description);
        }
        catch (Exception e)
        {
            WarnOnce("LocalizeForestryAbility('" + code + "') threw: " + e.Message);
        }
    }

    // ------------------------------------------------------------------------------------------------------
    // Per-player side (used at operation time by XSkillsIntegrationPlan E02–E04) — resolves the player's live
    // PlayerSkill / PlayerAbility
    // ------------------------------------------------------------------------------------------------------

    /// <summary>The player's live forestry PlayerSkill (for XP grants and ability reads), or null when the
    /// integration is off or the player has no skill set. The skill set is found by the engine's own
    /// <c>GetBehavior("SkillSet")</c> (PlayerSkillSet.PropertyName() == "SkillSet").</summary>
    public object? GetForestrySkill(Entity entity)
    {
        EnsureInit();
        if (!available || entity == null) return null;
        try
        {
            EntityBehavior? skillSet = entity.GetBehavior("SkillSet");
            if (skillSet == null) return null;
            return miPlayerSetFindSkill!.Invoke(skillSet, new object[] { ForestrySkill, false });
        }
        catch (Exception e)
        {
            WarnOnce("GetForestrySkill threw: " + e.Message);
            return null;
        }
    }

    /// <summary>Grant forestry XP to a player's PlayerSkill. Effective server-side only (the XLib API no-ops the grant
    /// on the client), so callers must invoke this from server branches — this wrapper does not gate the side itself.</summary>
    public void AddExperience(object? playerSkill, float amount, bool invokeModifiers = true)
    {
        EnsureInit();
        if (!available || playerSkill == null) return;
        try
        {
            miPlayerSkillAddExp!.Invoke(playerSkill, new object[] { amount, invokeModifiers });
        }
        catch (Exception e)
        {
            WarnOnce("AddExperience threw: " + e.Message);
        }
    }

    /// <summary>The player's PlayerAbility for <paramref name="code"/> on the given forestry PlayerSkill, or null.
    /// Side-agnostic — the own player's skill set is populated on the client too (needed by swiftsaw, T04.02).</summary>
    public object? FindAbility(object? playerSkill, string code)
    {
        EnsureInit();
        if (!available || playerSkill == null) return null;
        try
        {
            return miPlayerSkillFindAbility!.Invoke(playerSkill, new object[] { code, false });
        }
        catch (Exception e)
        {
            WarnOnce("FindAbility('" + code + "') threw: " + e.Message);
            return null;
        }
    }

    /// <summary>The learned tier of a PlayerAbility (0 = not learned / off). Side-agnostic read.</summary>
    public int AbilityTier(object? playerAbility)
    {
        EnsureInit();
        if (!available || playerAbility == null) return 0;
        try
        {
            object? t = piPlayerAbilityTier!.GetValue(playerAbility);
            return t is int tier ? tier : 0;
        }
        catch (Exception e)
        {
            WarnOnce("AbilityTier threw: " + e.Message);
            return 0;
        }
    }

    /// <summary>The finished bonus FRACTION of a PlayerAbility at forestry <paramref name="skillLevel"/> — XLib's
    /// <c>SkillDependentFValue</c>, i.e. <c>Min(start + perLevel*level, cap) * 0.01</c> read at the ability's current
    /// tier. Pass 0 to use the player's current level. Returns 0 when off / tier 0. Side-agnostic (own-player read
    /// works client-side, needed by swiftsaw).</summary>
    public float SkillDependentF(object? playerAbility, int skillLevel = 0)
    {
        EnsureInit();
        if (!available || playerAbility == null) return 0f;
        try
        {
            object? v = miPlayerAbilitySkillDependentF!.Invoke(playerAbility, new object[] { skillLevel });
            return v is float f ? f : 0f;
        }
        catch (Exception e)
        {
            WarnOnce("SkillDependentF threw: " + e.Message);
            return 0f;
        }
    }

    // ------------------------------------------------------------------------------------------------------

    void WarnOnce(string reason)
    {
        if (warned) return;
        warned = true;
        api.Logger.Warning("[immersivewoodworking] xSkills integration disabled/degraded: {0}. IW runs normally without it.", reason);
    }
}
