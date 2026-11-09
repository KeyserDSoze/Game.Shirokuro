using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class InMemoryLobbyCoordinatorTests
{
    [Fact]
    public void CoordinatorUsesExactlyTheEmbeddedOriginalRoster()
    {
        var ids = OriginalHeroRoster.Ids;
        Assert.Equal(14, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("kira", ids);
        Assert.Contains("gordunhao", ids);
        Assert.Contains("magjistar", ids);
        Assert.Contains("vlko-dlak", ids);

        var coordinator = new InMemoryLobbyCoordinator();
        var lobby = coordinator.Create("r", ["white", "black", "gold"]);
        Assert.Equal(ids, lobby.Definition.HeroIds);
    }

    [Fact]
    public void CoordinatorJoinChangeTeamAndSelectHeroAreAuthoritativeInMemory()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create("r", ["white", "black", "gold"]);

        var joined = coordinator.Join("r", "alice", "gold", 0);
        Assert.Equal("gold", Assert.Single(joined.Participants).TeamId);
        var selected = coordinator.ChooseHero("r", "alice", "kira", 1);
        Assert.Equal("kira", Assert.Single(selected.Participants).HeroId);
        var moved = coordinator.ChangeTeam("r", "alice", "black", 2);
        Assert.Equal("black", Assert.Single(moved.Participants).TeamId);
        var left = coordinator.Leave("r", "alice", 3);
        Assert.Empty(left.Participants);
        Assert.Equal(4, coordinator.Get("r").Revision);
    }

    [Fact]
    public void DuplicateLobbyIdsAreRejected()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create("r", ["white", "black"]);
        Assert.Throws<InvalidOperationException>(() => coordinator.Create("r", ["white", "black"]));
    }

    [Fact]
    public void UnknownLobbyIdIsRejected()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("missing"));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Join("missing", "alice", "white", 0));
    }

    [Fact]
    public void OldSnapshotsAreUnaffectedByLaterCoordinatorMutations()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        var before = coordinator.Create("r", ["white", "black"]);
        var after = coordinator.Join("r", "alice", "white", 0);
        Assert.Empty(before.Participants);
        Assert.Single(after.Participants);
        Assert.Equal(0, before.Revision);
    }

    [Fact]
    public async Task ConcurrentWritesOnSameRevisionCannotOverwriteEachOther()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create("r", ["white", "black"]);
        var attempts = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            try
            {
                coordinator.Join("r", "p-" + i, "white", expectedRevision: 0);
                return true;
            }
            catch (LobbyRevisionConflictException)
            {
                return false;
            }
        }));

        var results = await Task.WhenAll(attempts);
        Assert.Equal(1, results.Count(won => won));
        Assert.Equal(1, coordinator.Get("r").Revision);
        Assert.Single(coordinator.Get("r").Participants);
    }

    [Fact]
    public void SameLobbyDoesNotAffectAnotherLobby()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create("a", ["white", "black"]);
        coordinator.Create("b", ["white", "black"]);
        coordinator.Join("a", "alice", "white", 0);
        Assert.Empty(coordinator.Get("b").Participants);
        Assert.Equal(0, coordinator.Get("b").Revision);
    }
}

