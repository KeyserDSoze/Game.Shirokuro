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

    Task<int> RemovePlayerFromAllRoomsAsync(string trustedPlayerId,
        CancellationToken cancellationToken = default);
}
