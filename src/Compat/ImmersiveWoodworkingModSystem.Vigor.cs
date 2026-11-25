using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// The gate-checked facade over <see cref="VigorCompat"/> (ModDbFeedbackPlan T06.03). The chopping block and the
/// sawhorses call it unconditionally from their server-side hold handlers; without Vigor, with
/// <see cref="ImmersiveWoodworkingConfig.VigorIntegration"/> off, or on the client, spending is a no-op and nobody is
/// ever too tired. The automated stations never call it: no player works them.
/// </summary>
public partial class ImmersiveWoodworkingModSystem
{
    // Null on the client, until StartServerSide, and whenever the bridge is off.
    VigorCompat? vigor;

    /// <summary>Bind the bridge. Called once from <see cref="StartServerSide"/>, where every mod's ModSystem exists and
    /// Config is final. Exactly one startup line either way, so a server owner can see from the log alone whether hand
    /// work spends stamina.</summary>
    void InitVigor(ICoreServerAPI api)
    {
        if (!api.ModLoader.IsModEnabled(VigorCompat.ModId))
        {
            api.Logger.Notification("[immersivewoodworking] Vigor not installed: hand work spends no stamina.");
            return;
        }
        if (!Config.VigorIntegration)
        {
            api.Logger.Notification("[immersivewoodworking] Vigor found, but VigorIntegration=false: hand work spends no stamina.");
            return;
        }

        var compat = new VigorCompat(api);
        if (!compat.Available) return; // VigorCompat has already logged its one warning
        vigor = compat;
        api.Logger.Notification("[immersivewoodworking] Vigor found: chopping costs {0} stamina per strike, sawing {1} and debarking {2} per second; an exhausted player cannot work the stations.",
            Config.VigorChopStaminaPerStrike, Config.VigorSawStaminaPerSecond, Config.VigorDebarkStaminaPerSecond);
    }

    /// <summary>Charge <paramref name="player"/> for hand work on a station. Server-only; a no-op without the bridge.</summary>
    internal void SpendStamina(string actionId, float amount, IPlayer player) => vigor?.Spend(actionId, amount, player);

    /// <summary>True while Vigor holds <paramref name="player"/> exhausted: the stations then refuse to start or continue
    /// their work. Always false without the bridge, so the stations behave exactly as before.</summary>
    internal bool TooTiredToWork(IPlayer player) => vigor != null && vigor.IsExhausted(player);
}
