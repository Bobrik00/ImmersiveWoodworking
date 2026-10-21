using Vintagestory.API.Config;

namespace ImmersiveWoodworking;

/// <summary>Build stage of a single trestle. Built up stick → stick → rope (see <see cref="BlockEntitySawhorse.TryBuildStep"/>).</summary>
public enum SawhorseStage
{
    Stage1 = 0,   // one stick laid
    Stage2 = 1,   // two sticks
    Complete = 2  // finished trestle (rope added)
}

/// <summary>What kind of sawable a stack is, from its block code (see <see cref="BlockEntitySawhorse.GetSawInput"/>).
/// Drives the accept filter (LogSawingPlan E05) and, in <see cref="SawMode.Auto"/>, the saw-result mapping (E06), so "what
/// can be laid on the pair" and "what it saws into by default" share one classification: <see cref="Log"/> and
/// <see cref="LogSection"/> → planks, <see cref="DebarkedLog"/> → beams (see <see cref="BlockEntitySawhorse.SawsIntoPlanks"/>).
/// The Auto default is only a default — a chosen <see cref="SawMode"/> overrides it, so ANY input can rip to EITHER product
/// (SawModePlan).</summary>
public enum SawInput
{
    None = 0,        // not a sawable input — rejected
    Log = 1,         // log-placed-{wood}-{rot}          → plank-{wood} in Auto
    DebarkedLog = 2, // debarkedlog-{wood}-{rot}         → supportbeam-{wood} in Auto
    LogSection = 3   // logsection-placed-{wood}-{seg}-{rot} → plank-{wood} in Auto (ManualOpsBalancePlan E08; redwood in vanilla, quarter of a 2×2 trunk)
}

/// <summary>Which product a rip yields, decoupled from the input kind (SawModePlan). The mode lives on the pit saw as a
/// vanilla tool mode (F, <see cref="CollectibleBehaviorSawMode"/>) and, at the moment a rip starts, is LOCKED onto the
/// sawhorse pair (<c>BlockEntitySawhorse.sawMode</c>, like the directional-cut lock) so a two-man rip takes the first
/// sawyer's choice; on the mechanical sawmill it lives on the machine and is cycled with Shift+RMB. Read by every
/// saw-result site next to <see cref="SawInput"/>: <see cref="BlockEntitySawhorse.GetSawProduct"/>,
/// <see cref="BlockEntitySawhorse.SawProductResolves(Vintagestory.API.Common.IWorldAccessor, Vintagestory.API.Common.ItemStack, SawMode)"/>
/// and the sawmill's <c>GetPassBatch</c>.</summary>
public enum SawMode
{
    Auto = 0,   // input decides: log/section → planks, debarked → beams (backward-compatible default)
    Planks = 1, // force plank-{wood} regardless of input
    Beams = 2   // force supportbeam-{wood} regardless of input (still blocked for a BeamSkippedWood species)
}

/// <summary>Lang glue for <see cref="SawMode"/>, one source for the code suffix and the localized name so the tool-mode
/// menu (<see cref="CollectibleBehaviorSawMode"/>) and the sawmill's Shift+RMB confirmation line read the same keys.</summary>
public static class SawModeExt
{
    /// <summary>The lang-key suffix / SkillItem code: "auto" / "planks" / "beams".</summary>
    public static string Code(this SawMode m) => m switch
    {
        SawMode.Planks => "planks",
        SawMode.Beams  => "beams",
        _              => "auto",
    };

    /// <summary>The lang KEY for the mode name. Full literal keys (not a domain-prefixed concatenation) so
    /// tools/check-lang.py resolves each statically instead of tripping on a phantom <c>sawmode-</c> stem.</summary>
    static string NameKey(this SawMode m) => m switch
    {
        SawMode.Planks => "immersivewoodworking:sawmode-planks",
        SawMode.Beams  => "immersivewoodworking:sawmode-beams",
        _              => "immersivewoodworking:sawmode-auto",
    };

    /// <summary>The localized mode name in the CALLER's language — the client-side default (hover info, the tool-mode
    /// menu), where <see cref="Lang.Get"/> uses the local player's language.</summary>
    public static string LangName(this SawMode m) => Lang.Get(m.NameKey());

    /// <summary>The localized mode name in a SPECIFIC language — for a server sending a line to one player, which must
    /// use the RECIPIENT's language, not the server's (the mod's <c>SendIngameError</c> idiom, e.g. ItemHalfLog).</summary>
    public static string LangName(this SawMode m, string langCode) => Lang.GetL(langCode, m.NameKey());
}
