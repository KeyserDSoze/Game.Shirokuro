using Shirokuro.Api.Lobbies;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Development uses the same cancellation-bearing IAsyncGuestLobby contract
/// as Redis Production. An already-aborted HTTP request or stopped worker
/// must not mutate the in-memory coordinator after it has been cancelled.
/// </summary>
public sealed class LocalGuestLobbyCancellationTests
{
    private static (LocalGuestLobbyRuntime Runtime,
        InMemoryLobbyCoordinator Coordinator) CreateRuntime()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        var reader = new LobbyRosterReader(coordinator);
        var commands = new GuestLobbyCommands(coordinator, reader);
        return (new LocalGuestLobbyRuntime(commands, reader, coordinator),
            coordinator);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("join")]
    [InlineData("hero")]
    [InlineData("team")]
    [InlineData("leave")]
    [InlineData("roster")]
    [InlineData("cleanup")]
    public async Task AlreadyCancelledTokenPreventsEachOperationWithoutMutation(
        string operation)
    {
        var (lobby, coordinator) = CreateRuntime();
        var created = await lobby.CreateAsync(
            "alice", ["white", "black"], "white");
        var joined = await lobby.JoinAsync(
            created.LobbyId, "bob", "black", created.Revision);
        Assert.Equal(2, joined.Revision);
        Assert.Equal(2, joined.Participants.Count);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var token = cancelled.Token;
        Func<Task> action = operation switch
        {
            "create" => async () => await lobby.CreateAsync(
                "charlie", ["white", "black"], "white", token),
            "join" => async () => await lobby.JoinAsync(
                created.LobbyId, "charlie", "white", joined.Revision, token),
            "hero" => async () => await lobby.ChooseHeroAsync(
                created.LobbyId, "alice", "kira", joined.Revision, token),
            "team" => async () => await lobby.ChangeTeamAsync(
                created.LobbyId, "alice", "black", joined.Revision, token),
            "leave" => async () => await lobby.LeaveAsync(
                created.LobbyId, "bob", joined.Revision, token),
            "roster" => async () => await lobby.ForMemberAsync(
                created.LobbyId, "alice", token),
            "cleanup" => async () => await lobby.RemovePlayerFromAllRoomsAsync(
                "alice", token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(action);

        // No new rooms or quota consumption, no revision changes, no hero
        // or team mutation, and no member lost to cancelled worker cleanup.
        Assert.Equal(1, coordinator.StoredRoomCount);
        var snapshot = coordinator.Get(created.LobbyId);
        Assert.Equal(joined.Revision, snapshot.Revision);
        Assert.Equal(2, snapshot.Participants.Count);
        Assert.Equal("alice", snapshot.Participants[0].PlayerId);
        Assert.Equal("white", snapshot.Participants[0].TeamId);
        Assert.Null(snapshot.Participants[0].HeroId);
        Assert.Equal("bob", snapshot.Participants[1].PlayerId);
        Assert.Equal("black", snapshot.Participants[1].TeamId);
        Assert.Null(snapshot.Participants[1].HeroId);

        // Cancellation belongs to the request, not the room: a subsequent
        // legitimate authenticated roster read still works.
        var roster = await lobby.ForMemberAsync(created.LobbyId, "alice");
        Assert.Equal(joined.Revision, roster.Revision);
        Assert.Equal(2, roster.Participants.Count);
    }

    [Fact]
    public async Task StoppedWorkerDoesNotPruneGuestUntilFreshCleanupIsAllowed()
    {
        var (lobby, coordinator) = CreateRuntime();
        var room = await lobby.CreateAsync(
            "alice", ["white", "black"], "white", CancellationToken.None);
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await lobby.RemovePlayerFromAllRoomsAsync(
                "alice", stopped.Token));
        Assert.Equal(1, coordinator.StoredRoomCount);
        Assert.Single((await lobby.ForMemberAsync(room.LobbyId, "alice"))
            .Participants);

        // A new, non-cancelled maintenance pass can still perform cleanup.
        Assert.Equal(1, await lobby.RemovePlayerFromAllRoomsAsync(
            "alice", CancellationToken.None));
        Assert.Equal(0, coordinator.StoredRoomCount);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get(room.LobbyId));
    }

    [Fact]
    public async Task ValidTokenPreservesNormalLocalGuestMutations()
    {
        var (lobby, coordinator) = CreateRuntime();
        using var active = new CancellationTokenSource();
        var token = active.Token;
        var created = await lobby.CreateAsync(
            "alice", ["white", "black"], "white", token);
        var joined = await lobby.JoinAsync(
            created.LobbyId, "bob", "black", created.Revision, token);
        var chosen = await lobby.ChooseHeroAsync(
            created.LobbyId, "alice", "kira", joined.Revision, token);
        var switched = await lobby.ChangeTeamAsync(
            created.LobbyId, "bob", "white", chosen.Revision, token);
        var roster = await lobby.ForMemberAsync(
            created.LobbyId, "alice", token);
        Assert.Equal(switched.Revision, roster.Revision);
        Assert.Equal("kira", roster.Participants[0].HeroId);
        Assert.Equal("white", roster.Participants[1].TeamId);

        await lobby.LeaveAsync(
            created.LobbyId, "bob", roster.Revision, token);
        var afterLeave = await lobby.ForMemberAsync(
            created.LobbyId, "alice", token);
        Assert.Single(afterLeave.Participants);
        Assert.Equal(1, await lobby.RemovePlayerFromAllRoomsAsync(
            "alice", token));
        Assert.Equal(0, coordinator.StoredRoomCount);
    }
}
