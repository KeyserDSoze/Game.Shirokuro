using Shirokuro.Application.Lobbies;

namespace Shirokuro.Api.Lobbies;

/// <summary>Existing lock-safe process-local semantics for Vite/Development.</summary>
public sealed class LocalGuestLobbyRuntime(
    GuestLobbyCommands commands, LobbyRosterReader reader,
    InMemoryLobbyCoordinator coordinator) : IAsyncGuestLobby
{
    public Task<LobbyRosterView> CreateAsync(string actor, IReadOnlyList<string>? teams,
        string? team, CancellationToken cancellationToken = default) =>
        Task.FromResult(commands.Create(actor, teams, team));

    public Task<LobbyRosterView> JoinAsync(string room, string actor, string? team,
        long? revision, CancellationToken cancellationToken = default) =>
        Task.FromResult(commands.Join(room, actor, team, revision));

    public Task<LobbyRosterView> ChooseHeroAsync(string room, string actor, string? hero,
        long revision, CancellationToken cancellationToken = default) =>
        Task.FromResult(commands.ChooseHero(room, actor, hero, revision));

    public Task<LobbyRosterView> ChangeTeamAsync(string room, string actor, string? team,
        long revision, CancellationToken cancellationToken = default) =>
        Task.FromResult(commands.ChangeTeam(room, actor, team, revision));

    public Task LeaveAsync(string room, string actor, long revision,
        CancellationToken cancellationToken = default)
    {
        commands.Leave(room, actor, revision);
        return Task.CompletedTask;
    }

    public Task<LobbyRosterView> ForMemberAsync(string room, string actor,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(reader.ForMember(room, actor));

    public Task<int> RemovePlayerFromAllRoomsAsync(string actor,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(coordinator.RemovePlayerFromAllRooms(actor));
}
