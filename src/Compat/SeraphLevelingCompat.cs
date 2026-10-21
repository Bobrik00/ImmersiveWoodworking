using System;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// The mod's only point of contact with Seraph Leveling ReClassed (modid <c>seraphlevelingreclassed</c>), reached by
/// REFLECTION for the same reason as <see cref="XSkillsCompat"/>: no compile-time reference to SeraphLeveling.dll and
/// no modinfo entry, so IW loads on a world without it. The whole surface is ONE public static method,
/// <c>SeraphLeveling.Patches.CraftingPatches.TriggerGridCraftingResult(IServerPlayer, AssetLocation, int)</c>. Seraph
/// credits its crafting traits from a Harmony postfix on <c>GridRecipe.ConsumeInput</c>, and this method raises the
/// same event for crafts made outside the grid; Seraph calls it itself for Immersive Fibercraft's loom. Which output
/// earns what stays Seraph's decision: every listener matches the code's PATH by prefix and ignores the domain, and the
/// one that matters here is Carpenter (<c>plank-</c>, 120 boards), a trait of Sacred Classes' Woodsman.
///
/// Checked against the 0.9.10 source and a decompile of the shipped 0.9.9 DLL: same type, signature and one-line body
/// in both. The method raises the event without a try/catch, so an exception inside any listener reaches the caller;
/// <see cref="ReportCrafted"/> swallows it after one warning. SERVER-only: built in StartServerSide, and Seraph's
/// credit path writes WatchedAttributes, sends chat and persists progress, all of which belong on the server main
/// thread, which is where the sawhorse rip runs.
/// </summary>
public sealed class SeraphLevelingCompat
{
    const string ModId = "seraphlevelingreclassed";

    readonly ICoreServerAPI api;
    readonly Action<IServerPlayer, AssetLocation, int>? triggerGridCrafted;
    bool warned;

    /// <summary>Resolve the entry point once. Seraph absent is the normal case and stays silent; Seraph present but
    /// its API changed logs one warning and leaves the bridge off.</summary>
    public SeraphLevelingCompat(ICoreServerAPI api)
    {
        this.api = api;
        if (!api.ModLoader.IsModEnabled(ModId)) return;

        try
        {
            triggerGridCrafted = Resolve();
        }
        catch (Exception e)
        {
            WarnOnce("resolve threw " + e.GetType().Name + ": " + e.Message);
        }

        if (triggerGridCrafted != null)
            api.Logger.Notification("[immersivewoodworking] Seraph Leveling found: boards sawn on the sawhorses count toward its crafting traits.");
    }

    /// <summary>True when Seraph is installed and its entry point bound.</summary>
    public bool Available => triggerGridCrafted != null;

    Action<IServerPlayer, AssetLocation, int>? Resolve()
    {
        // Same ModSystem type name in 0.9.9 and 0.9.10; its assembly is where CraftingPatches lives. No ShouldLoad
        // override, so the server has an instance.
        ModSystem? system = api.ModLoader.GetModSystem("SeraphLeveling.SeraphLevelingModSystem");
        if (system == null)
        {
            WarnOnce("SeraphLeveling.SeraphLevelingModSystem not found");
            return null;
        }

        Type? craftingPatches = system.GetType().Assembly.GetType("SeraphLeveling.Patches.CraftingPatches");
        // Explicit parameter types, so an overload added later can never rebind this silently.
        MethodInfo? method = craftingPatches?.GetMethod("TriggerGridCraftingResult", BindingFlags.Public | BindingFlags.Static,
            null, new[] { typeof(IServerPlayer), typeof(AssetLocation), typeof(int) }, null);
        if (method == null || method.ReturnType != typeof(void))
        {
            WarnOnce("CraftingPatches.TriggerGridCraftingResult(IServerPlayer, AssetLocation, int) not found (Seraph API changed?)");
            return null;
        }

        return (Action<IServerPlayer, AssetLocation, int>)Delegate.CreateDelegate(typeof(Action<IServerPlayer, AssetLocation, int>), method);
    }

    /// <summary>Tell Seraph that <paramref name="player"/> crafted <paramref name="quantity"/> of
    /// <paramref name="code"/>. No-op when the bridge is off. The caller guarantees an in-world player with a live
    /// entity: Seraph's credit path dereferences <c>player.Entity</c> without a null check.</summary>
    public void ReportCrafted(IServerPlayer player, AssetLocation code, int quantity)
    {
        if (triggerGridCrafted == null) return;
        try
        {
            triggerGridCrafted(player, code, quantity);
        }
        catch (Exception e)
        {
            WarnOnce("TriggerGridCraftingResult threw " + e.GetType().Name + ": " + e.Message);
        }
    }

    void WarnOnce(string reason)
    {
        if (warned) return;
        warned = true;
        api.Logger.Warning("[immersivewoodworking] Seraph Leveling integration disabled/degraded: {0}. IW runs normally without it.", reason);
    }
}
