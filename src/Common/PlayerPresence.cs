using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace ImmersiveWoodworking;

/// <summary>
/// AuditFix3Plan T01.01: the ONE test for "this player is in the world right now". Every walk over the sawhorse's
/// co-op hold maps (<c>BlockEntitySawhorse.sawEnds</c> / <c>barkSides</c>: the prunes, the owner lookups, the auto-feed
/// peeks, the wear, the yield and bark-chance bests, the XP split) and the two integration facades that act on a holder
/// (<see cref="ImmersiveWoodworkingModSystem.GrantForestryXp"/>, <see cref="ImmersiveWoodworkingModSystem.ReportCraftToSeraph"/>)
/// ask it, so "is this holder still here" has a single answer.
///
/// <para>Why a null check is not that answer: on the SERVER, <c>PlayerByUid</c> keeps returning a player who left during
/// this session (its API doc: "the player does not need to be connected"), and that player still has an
/// <c>Entity</c>, the despawned one their world data keeps. What a disconnect does change is the connection: the engine
/// drops the client from the player object, whose <see cref="IServerPlayer.ConnectionState"/> then reads Offline for
/// good, because a player who reconnects gets a NEW server player object (VintagestoryLib 1.22.3:
/// <c>ServerMain.DisconnectPlayer</c>, <c>ConnectedClient.LoadOrCreatePlayerData</c>). Vanilla tests the same state
/// before it touches a player on the server (<c>BehaviorBodyTemperature</c>, <c>BehaviorTemporalStabilityAffected</c>),
/// which also shows the singleplayer host is Playing: its body temperature runs. One trap: inside the server's own
/// disconnect events (<c>PlayerDisconnect</c>, <c>PlayerLeave</c>, <c>ServerSystem.OnPlayerDisconnect</c>) the leaving
/// player still reads Playing, because the engine detaches the client only after raising them.</para>
///
/// <para>On the CLIENT there is no connection state to read. Its <c>PlayerByUid</c> knows only the players connected
/// now: one who leaves is dropped from it while their old object lives on with a null entity, and one who comes back
/// gets a new object (VintagestoryLib 1.22.3, <c>GeneralPacketHandler</c>; the client's own player is the object that
/// map holds). So the client asks whether <c>PlayerByUid</c> still returns this very object. Nothing client-side walks
/// the hold maps; the branch keeps the answer honest on both sides.</para>
/// </summary>
internal static class PlayerPresence
{
    /// <summary>True when <paramref name="player"/> is in the world: on the server a Playing
    /// <see cref="IServerPlayer"/> with an entity, on the client the object the client's <c>PlayerByUid</c> returns for
    /// that uid.</summary>
    internal static bool IsInWorld(IWorldAccessor world, IPlayer? player)
        => world.Side == EnumAppSide.Server
            ? player is IServerPlayer sp && sp.ConnectionState == EnumClientState.Playing && sp.Entity != null
            : player != null && world.PlayerByUid(player.PlayerUID) == player;
}
