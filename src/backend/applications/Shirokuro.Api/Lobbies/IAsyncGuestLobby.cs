using Shirokuro.Application.Lobbies;

namespace Shirokuro.Api.Lobbies;

/// <summary>
/// The ONLY API/SignalR/expiry abstraction for pre-match lobby state.
/// Select either local Development or Redis-backed deployment at startup,
/// never combine two independent state authorities for one environment.
/// </summary>
public interface IAsyncGuestLobby
{
    Task<LobbyRosterView> CreateAsync(string trustedPlayerId,
        IReadOnlyList<string>? teamIds, string? initialTeam,
        CancellationToken cancellationToken = default);

    Task<LobbyRosterView> JoinAsync(string roomId, string trustedPlayerId,
        string? teamId, long? expectedRevision,
        CancellationToken cancellationToken = default);

    Task<LobbyRosterView> ChooseHeroAsync(string roomId, string trustedPlayerId,
        string? heroId, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<LobbyRosterView> ChangeTeamAsync(string roomId, string trustedPlayerId,
        string? teamId, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task LeaveAsync(string roomId, string trustedPlayerId, long expectedRevision,
        CancellationToken cancellationToken = default);

    Task<LobbyRosterView> ForMemberAsync(string roomId, string trustedPlayerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-authorized pre-match roster read that never renews inactivity.
    /// The explicit ForMemberAsync remains an authorized heartbeat.
    /// </summary>
    Task<LobbyRosterView> PeekForMemberAsync(string roomId,
        string trustedPlayerId, CancellationToken cancellationToken = default);

    Task<int> RemovePlayerFromAllRoomsAsync(string trustedPlayerId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// No verified roster can be returned when the shared Redis heartbeat CAS
/// loses every bounded retry to concurrent changes. This is availability,
/// not a revision conflict (HTTP 409) and not a valid stale roster.
/// Deliberately contains no guest ID, lobby ID, Redis details or snapshot.
/// </summary>
public sealed class LobbyReadContentionException()
    : Exception("Lobby roster temporarily unavailable under contention.");

/// <summary>
/// An invitation join without an explicit expected revision could not win
/// any of its bounded atomic CAS attempts. Not a user revision conflict;
/// no join was confirmed and nothing from the observed roster is returned.
/// </summary>
public sealed class LobbyJoinContentionException()
    : Exception("Lobby join temporarily unavailable under contention.");

/// <summary>
/// Redis returned persisted lobby bytes that cannot be decoded as an
/// authoritative, versioned room snapshot. Do not attach the decoder
/// exception, key, ID or raw bytes to transport-visible diagnostics.
/// </summary>
public sealed class LobbySnapshotUnavailableException()
    : Exception("Persisted lobby snapshot unavailable.");


/// <summary>
/// The Redis bounded-create Lua returned success but the exact new room
/// snapshot and both quota index entries could not be confirmed. This is
/// transient shared-storage uncertainty, NOT a gameplay/revision conflict.
/// Never attach raw bytes, keys, member IDs or Redis exception details.
/// </summary>
public sealed class LobbyCreationUnavailableException()
    : Exception("Lobby creation was not confirmed.");


/// <summary>
/// A positive Redis compare-and-swap reply without a confirmed persisted
/// snapshot and matching quota indices is shared-storage uncertainty,
/// not a gameplay revision conflict. Never attach private Redis data.
/// </summary>
public sealed class LobbyMutationUnavailableException()
    : Exception("Lobby mutation was not confirmed.");
