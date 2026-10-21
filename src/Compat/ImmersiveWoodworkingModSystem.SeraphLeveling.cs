using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// The gate-checked facade over <see cref="SeraphLevelingCompat"/>. Callers report a finished manual product
/// unconditionally; without Seraph, with <see cref="ImmersiveWoodworkingConfig.SeraphLevelingIntegration"/> off, or on
/// the client, the report is a no-op.
///
/// Only the sawhorse rip reports. It replaces the grid "saw + log" recipes, and those are the only IW-replaced grid
/// outputs any Seraph listener matches (<c>plank-</c> for Carpenter). Firewood, sticks, half-logs, debarked logs and
/// bark match none of them in 0.9.9 or 0.9.10, so the chopping block and the debark side do not report. The automated
/// sawmill has no player at yield time and reports nothing, the same boundary the xSkills XP keeps.
/// </summary>
public partial class ImmersiveWoodworkingModSystem
{
    // Null on the client and until StartServerSide.
    SeraphLevelingCompat? seraph;
    ICoreServerAPI? seraphApi;

    /// <summary>Bind the bridge. Called once from <see cref="StartServerSide"/>: every mod's ModSystem exists by then,
    /// Config is final, and the one startup line tells a server owner whether the integration is live. With the flag
    /// off the bridge is never built, so an owner who switched it off to avoid double counting sees that in the log.</summary>
    void InitSeraphLeveling(ICoreServerAPI api)
    {
        seraphApi = api;
        if (!Config.SeraphLevelingIntegration)
        {
            if (api.ModLoader.IsModEnabled("seraphlevelingreclassed"))
                api.Logger.Notification("[immersivewoodworking] Seraph Leveling found, but SeraphLevelingIntegration=false: sawn boards do not count toward its traits.");
            return;
        }
        seraph = new SeraphLevelingCompat(api);
    }

    // IW_DEBUG only, grep "[iw-seraph]": one line per report or skip, so a run shows whether a batch reached Seraph.
    [Conditional("IW_DEBUG")]
    void DbgSeraph(IPlayer? player, string detail)
        => seraphApi?.Logger.Notification("[iw-seraph] {0}: {1}", player?.PlayerName ?? "?", detail);

    /// <summary>Report that <paramref name="player"/> made <paramref name="quantity"/> of <paramref name="product"/>
    /// on a manual station, as Seraph would have counted the grid craft it replaces. Server-only.</summary>
    internal void ReportCraftToSeraph(IPlayer player, ItemStack product, int quantity)
    {
        if (seraph == null || !seraph.Available) return; // Seraph absent, API changed, or SeraphLevelingIntegration off
        if (quantity <= 0) return;
        // The holder maps can still name a player who left during this session (server PlayerByUid keeps them), and
        // Seraph writes the player's WatchedAttributes without a null check, so an offline holder is skipped. A holder
        // who left and came back is Playing again and does get credited while the old hold lingers: that hold is the
        // sawhorse's own ghost-hold bookkeeping (PruneOfflineSawyers), which wear and xSkills XP share.
        if (player is not IServerPlayer serverPlayer || serverPlayer.ConnectionState != EnumClientState.Playing || serverPlayer.Entity == null)
        {
            DbgSeraph(player, "SKIP: not an in-world server player");
            return;
        }

        seraph.ReportCrafted(serverPlayer, product.Collectible.Code, quantity);
        DbgSeraph(player, "reported " + quantity + " x " + product.Collectible.Code);
    }
}
