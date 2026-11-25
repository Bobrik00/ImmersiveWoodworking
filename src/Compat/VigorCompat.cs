using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// The mod's only point of contact with Vigor (modid <c>vigor</c>, a stamina system), reached by REFLECTION for the same
/// reason as <see cref="XSkillsCompat"/> and <see cref="SeraphLevelingCompat"/>: no compile-time reference to Vigor.dll
/// and no modinfo entry, so IW loads on a world without it (ModDbFeedbackPlan T06.03).
///
/// The surface is three members of <c>Vigor.API.IVigorAPI</c> on the live <c>Vigor.VigorModSystem.API</c>:
/// <c>RegisterActionType(string, string)</c>, <c>ConsumeStamina(string, float, EntityPlayer, bool)</c> and
/// <c>IsExhausted(EntityPlayer)</c>. All-or-nothing: if the ModSystem, its <c>API</c> object or any of the three members
/// does not resolve, or the registration throws, the bridge stays off and logs ONE warning.
///
/// Checked against a decompile of Vigor 122.7.5 (memory entities-combat/vs-vigor-*). <c>API</c> and not <c>ServerAPI</c>:
/// the ModSystem builds separate VigorAPI objects per side, and the action types, modifiers and CalculatingStaminaCost
/// listeners other mods register live per object; <c>API</c> is the one Vigor documents. SERVER-only: Vigor changes
/// stamina only on the server (its client copy just logs a warning), and it skips creative players in its own tick
/// but not in ConsumeStamina, so this class skips them itself.
/// </summary>
public sealed class VigorCompat
{
    public const string ModId = "vigor";

    // Vigor stamps its OWN modid on every registered type, so the domain prefix is the only way another mod's stamina
    // modifier can tell these actions are IW's.
    public const string ChopAction = "immersivewoodworking:chop";
    public const string SawAction = "immersivewoodworking:saw";
    public const string DebarkAction = "immersivewoodworking:debark";

    readonly ICoreServerAPI api;
    System.Func<string, float, EntityPlayer, bool, bool>? consume;
    System.Func<EntityPlayer, bool>? isExhausted;
    bool warned;

    /// <summary>Resolve the three members once. The caller has already checked that Vigor is installed.</summary>
    public VigorCompat(ICoreServerAPI api)
    {
        this.api = api;
        try
        {
            Resolve();
        }
        catch (Exception e)
        {
            consume = null;
            isExhausted = null;
            Exception root = (e as TargetInvocationException)?.InnerException ?? e;
            WarnOnce("resolve threw " + root.GetType().Name + ": " + root.Message);
        }
    }

    /// <summary>True when every member resolved and the action types are registered.</summary>
    public bool Available => consume != null && isExhausted != null;

    void Resolve()
    {
        ModSystem? system = api.ModLoader.GetModSystem("Vigor.VigorModSystem");
        if (system == null)
        {
            WarnOnce("Vigor.VigorModSystem not found");
            return;
        }

        Type? iface = system.GetType().Assembly.GetType("Vigor.API.IVigorAPI");
        object? vigorApi = system.GetType().GetProperty("API", BindingFlags.Public | BindingFlags.Instance)?.GetValue(system);
        if (iface == null || vigorApi == null || !iface.IsInstanceOfType(vigorApi))
        {
            WarnOnce("VigorModSystem.API (Vigor.API.IVigorAPI) not found or not set");
            return;
        }

        // Explicit parameter types pick the four-argument ConsumeStamina out of IVigorAPI's two overloads. A signature
        // changed later (float to double, say) can still bind here through the default binder, but CreateDelegate below
        // then throws and the constructor's catch turns the bridge off. The bool is ignoreFatigue, which reflection
        // cannot default, so Spend passes it.
        MethodInfo? register = iface.GetMethod("RegisterActionType", new[] { typeof(string), typeof(string) });
        MethodInfo? consumeMethod = iface.GetMethod("ConsumeStamina", new[] { typeof(string), typeof(float), typeof(EntityPlayer), typeof(bool) });
        MethodInfo? exhaustedMethod = iface.GetMethod("IsExhausted", new[] { typeof(EntityPlayer) });
        if (register == null || consumeMethod?.ReturnType != typeof(bool) || exhaustedMethod?.ReturnType != typeof(bool))
        {
            WarnOnce("IVigorAPI.RegisterActionType / ConsumeStamina / IsExhausted not found (Vigor API changed?)");
            return;
        }

        var boundConsume = (System.Func<string, float, EntityPlayer, bool, bool>)Delegate.CreateDelegate(
            typeof(System.Func<string, float, EntityPlayer, bool, bool>), vigorApi, consumeMethod);
        var boundExhausted = (System.Func<EntityPlayer, bool>)Delegate.CreateDelegate(typeof(System.Func<EntityPlayer, bool>), vigorApi, exhaustedMethod);

        // ConsumeStamina never checks the registry: registering only lets other mods' stamina modifiers and
        // GetAllActionTypes see these ids. A throw here still means the API is not the one this file was checked
        // against, so it counts as a failed resolve (the catch in the constructor).
        register.Invoke(vigorApi, new object[] { ChopAction, "Chopping on the chopping block" });
        register.Invoke(vigorApi, new object[] { SawAction, "Sawing on the sawhorses" });
        register.Invoke(vigorApi, new object[] { DebarkAction, "Debarking on the sawhorses" });

        consume = boundConsume;
        isExhausted = boundExhausted;
    }

    /// <summary>Charge <paramref name="amount"/> stamina to <paramref name="player"/> for <paramref name="actionId"/>.
    /// No-op when the bridge is off, for an amount that is not above 0, and for a creative player. A throw from Vigor or from
    /// another mod's CalculatingStaminaCost listener is swallowed after one warning, so it never breaks a station.</summary>
    public void Spend(string actionId, float amount, IPlayer player)
    {
        // !(amount > 0) also turns away NaN, which Vigor's Math.Max(0, current - amount) would store as the stamina.
        if (consume == null || !(amount > 0f) || !TryGetWorker(player, out EntityPlayer? entity)) return;
        try
        {
            // ignoreFatigue false: the work counts as a fatiguing action, so Vigor holds regeneration for 1 s after it,
            // exactly as it does for its own sprint and jump.
            consume(actionId, amount, entity, false);
        }
        catch (Exception e)
        {
            WarnOnce("ConsumeStamina threw " + e.GetType().Name + ": " + e.Message);
        }
    }

    /// <summary>True while Vigor holds <paramref name="player"/> exhausted, which it clears once the bar is back to 40%.
    /// False when the bridge is off, for a creative player, and if Vigor throws.</summary>
    public bool IsExhausted(IPlayer player)
    {
        if (isExhausted == null || !TryGetWorker(player, out EntityPlayer? entity)) return false;
        try
        {
            return isExhausted(entity);
        }
        catch (Exception e)
        {
            WarnOnce("IsExhausted threw " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    static bool TryGetWorker(IPlayer player, [NotNullWhen(true)] out EntityPlayer? entity)
    {
        entity = player.Entity;
        return entity != null && player.WorldData?.CurrentGameMode != EnumGameMode.Creative;
    }

    void WarnOnce(string reason)
    {
        if (warned) return;
        warned = true;
        api.Logger.Warning("[immersivewoodworking] Vigor integration disabled/degraded: {0}. IW runs normally without it.", reason);
    }
}
