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

    [Fact]
    public void RevokedGuestCleanupRemovesAllMembershipsAndPrunesOnlyEmptyRooms()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.CreateWithOwner("solo", ["white", "black"], "alice", "white", 10);
        coordinator.CreateWithOwner("shared", ["white", "black"], "alice", "white", 10);
        coordinator.JoinBounded("shared", "bob", "black", 1, 16);
        coordinator.CreateWithOwner("independent", ["white", "black"], "bob", "black", 10);

        var oldSnapshot = coordinator.Get("shared");
        Assert.Equal(2, coordinator.RemovePlayerFromAllRooms("alice"));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("solo"));

        var after = coordinator.Get("shared");
        Assert.Equal(3, after.Revision);
        Assert.Equal("bob", Assert.Single(after.Participants).PlayerId);
        Assert.Equal(2, oldSnapshot.Participants.Count); // immutable snapshots
        Assert.Equal(1, coordinator.Get("independent").Revision);
        Assert.Equal(2, coordinator.StoredRoomCount);
        Assert.Equal(0, coordinator.RemovePlayerFromAllRooms("alice"));
        Assert.Equal(3, coordinator.Get("shared").Revision);
        Assert.Throws<ArgumentException>(() => coordinator.RemovePlayerFromAllRooms(" "));
    }

    [Fact]
    public void CleanupKeepsOriginalCreatorQuotaWhileOtherMembersRemain()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.CreateWithOwner("shared", ["white", "black"], "alice",
            "white", maxRooms: 10, maxRoomsPerCreator: 1);
        coordinator.JoinBounded("shared", "bob", "black", 1, 16);
        Assert.Equal(1, coordinator.RemovePlayerFromAllRooms("alice"));

        Assert.Throws<LobbyCapacityException>(() => coordinator.CreateWithOwner(
            "new", ["white", "black"], "alice", "white",
            maxRooms: 10, maxRoomsPerCreator: 1));
        coordinator.LeaveAndPrune("shared", "bob", 3);
        Assert.Equal(1, coordinator.CreateWithOwner(
            "new", ["white", "black"], "alice", "white",
            maxRooms: 10, maxRoomsPerCreator: 1).Revision);
    }

    [Fact]
    public async Task ConcurrentRosterWritesAndAdministrativeCleanupNeverLoseRemoval()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.CreateWithOwner("shared", ["white", "black"], "alice", "white", 10);
        coordinator.JoinBounded("shared", "bob", "black", 1, 16);

        var jobs = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            if (i % 4 == 0)
            {
                coordinator.RemovePlayerFromAllRooms("alice");
                return;
            }

            try
            {
                var currentRevision = coordinator.Get("shared").Revision;
                coordinator.ChooseHero("shared", "alice", "kira", currentRevision);
            }
            catch (Exception ex) when (ex is LobbyRevisionConflictException or
                                         UnauthorizedAccessException)
            {
                // An already signed-out member cannot mutate after cleanup.
            }
        }));
        await Task.WhenAll(jobs);
        Assert.Equal("bob", Assert.Single(coordinator.Get("shared").Participants).PlayerId);
        Assert.Equal(0, coordinator.RemovePlayerFromAllRooms("alice"));
    }
}
