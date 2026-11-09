using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestLobbyCommandsTests
{
    private readonly InMemoryLobbyCoordinator _coordinator = new();

    private GuestLobbyCommands CreateCommands() =>
        new(_coordinator, new LobbyRosterReader(_coordinator));

    [Fact]
    public void CreatorIsAdmittedAtomicallyOnRevisionOne()
    {
        var view = CreateCommands().Create("guest-alice", ["white", "black"], "white");
        Assert.Matches("^room-[0-9a-f]{32}$", view.LobbyId);
        Assert.Equal(1, view.Revision);
        Assert.Equal("guest-alice", Assert.Single(view.Participants).PlayerId);
        Assert.Equal("white", Assert.Single(view.Participants).TeamId);
        Assert.Equal(2, view.TeamIds.Count);
    }

    [Fact]
    public void RoomIdentifiersAreDistinctAndRandom()
    {
        var commands = CreateCommands();
        var ids = Enumerable.Range(0, 64)
            .Select(i => commands.Create("guest-" + i, ["white", "black"], "white").LobbyId)
            .ToArray();
        Assert.Equal(64, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^room-[0-9a-f]{32}$", id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unconfigured")]
    public void InvalidInitialTeamNeverPublishesAnEmptyRoom(string? team)
    {
        var commands = CreateCommands();
        Assert.Throws<ArgumentException>(() => commands.Create("alice", ["white", "black"], team));
        // The first successful room is still at revision one and has its owner.
        var valid = commands.Create("alice", ["white", "black"], "white");
        Assert.Equal(1, valid.Revision);
        Assert.Single(valid.Participants);
    }

    [Fact]
    public void InvalidTeamConfigurationsAreRejectedBeforeAllocation()
    {
        var commands = CreateCommands();
        Assert.Throws<ArgumentException>(() => commands.Create("alice", null, "white"));
        Assert.Throws<ArgumentException>(() => commands.Create("alice", ["white"], "white"));
        Assert.Throws<ArgumentException>(() =>
            commands.Create("alice", ["white", "white"], "white"));
        Assert.Throws<ArgumentException>(() =>
            commands.Create("alice", ["white", "bad space"], "white"));
        Assert.Throws<ArgumentException>(() =>
            commands.Create("alice", ["w", "b", "g", "r", "x"], "w"));
        Assert.Throws<ArgumentException>(() =>
            commands.Create("alice", ["White", "black"], "White"));
        Assert.Throws<ArgumentException>(() =>
            commands.Create("alice", ["abcdefghijklmnopqrstuvwxy", "black"], "black"));
    }

    [Fact]
    public void JoinAndSelfOnlyHeroAndTeamChangesUseOptimisticRevisions()
    {
        var commands = CreateCommands();
        var room = commands.Create("alice", ["white", "black", "gold"], "white");
        var joined = commands.Join(room.LobbyId, "bob", "black", room.Revision);
        Assert.Equal(2, joined.Revision);
        Assert.Equal("bob", joined.Participants.Last().PlayerId);
        Assert.Throws<LobbyRevisionConflictException>(() =>
            commands.Join(room.LobbyId, "charlie", "white", 1));

        var picked = commands.ChooseHero(room.LobbyId, "alice", "kira", 2);
        Assert.Equal("kira", picked.Participants.Single(p => p.PlayerId == "alice").HeroId);
        Assert.Null(picked.Participants.Single(p => p.PlayerId == "bob").HeroId);
        Assert.Throws<LobbyRevisionConflictException>(() =>
            commands.ChangeTeam(room.LobbyId, "bob", "gold", 2));

        var switched = commands.ChangeTeam(room.LobbyId, "bob", "gold", 3);
        Assert.Equal("gold", switched.Participants.Single(p => p.PlayerId == "bob").TeamId);
        Assert.Equal("white", switched.Participants.Single(p => p.PlayerId == "alice").TeamId);
    }

    [Fact]
    public void OutsidersCannotChangeOrLeaveExistingMembership()
    {
        var commands = CreateCommands();
        var room = commands.Create("alice", ["white", "black"], "white");
        Assert.Throws<UnauthorizedAccessException>(() =>
            commands.ChooseHero(room.LobbyId, "outsider", "kira", room.Revision));
        Assert.Throws<UnauthorizedAccessException>(() =>
            commands.ChangeTeam(room.LobbyId, "outsider", "black", room.Revision));
        Assert.Throws<UnauthorizedAccessException>(() =>
            commands.Leave(room.LobbyId, "outsider", room.Revision));
        Assert.Equal(1, _coordinator.Get(room.LobbyId).Revision);
    }

    [Fact]
    public void InvalidHeroAndTeamCannotMutateRevision()
    {
        var commands = CreateCommands();
        var room = commands.Create("alice", ["white", "black"], "white");
        Assert.Throws<ArgumentException>(() =>
            commands.ChooseHero(room.LobbyId, "alice", "invented", 1));
        Assert.Throws<ArgumentException>(() =>
            commands.ChangeTeam(room.LobbyId, "alice", "invented", 1));
        Assert.Equal(1, _coordinator.Get(room.LobbyId).Revision);
    }

    [Fact]
    public void CapacityChecksAreAtomicAndNeverLeakExtraParticipant()
    {
        var room = _coordinator.CreateWithOwner(
            "private", ["white", "black"], "owner", "white", maxRooms: 1);
        Assert.Throws<LobbyCapacityException>(() => _coordinator.CreateWithOwner(
            "second", ["white", "black"], "another", "white", maxRooms: 1));
        Assert.Throws<KeyNotFoundException>(() => _coordinator.Get("second"));

        Assert.Throws<LobbyCapacityException>(() =>
            _coordinator.JoinBounded("private", "intruder", "black", room.Revision, maxParticipants: 1));
        Assert.Single(_coordinator.Get("private").Participants);

        _coordinator.LeaveAndPrune("private", "owner", expectedRevision: 1);
        Assert.Throws<KeyNotFoundException>(() => _coordinator.Get("private"));
        Assert.Equal(1, _coordinator.CreateWithOwner(
            "reused-capacity", ["white", "black"], "another", "black", maxRooms: 1).Revision);
    }

    [Fact]
    public async Task SimultaneousJoinWritesCannotOverwriteMembership()
    {
        var commands = CreateCommands();
        var room = commands.Create("alice", ["white", "black"], "white");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            try
            {
                commands.Join(room.LobbyId, "player-" + index, "black", room.Revision);
                return true;
            }
            catch (LobbyRevisionConflictException)
            {
                return false;
            }
        })));
        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(2, _coordinator.Get(room.LobbyId).Revision);
        Assert.Equal(2, _coordinator.Get(room.LobbyId).Participants.Count);
    }

    [Fact]
    public void LastMemberLeavingRemovesRoomButOtherMemberKeepsItAlive()
    {
        var commands = CreateCommands();
        var room = commands.Create("alice", ["white", "black"], "white");
        var joined = commands.Join(room.LobbyId, "bob", "black", room.Revision);
        commands.Leave(room.LobbyId, "alice", joined.Revision);
        Assert.Single(_coordinator.Get(room.LobbyId).Participants);
        Assert.Throws<UnauthorizedAccessException>(() =>
            new LobbyRosterReader(_coordinator).ForMember(room.LobbyId, "alice"));
        commands.Leave(room.LobbyId, "bob", expectedRevision: 3);
        Assert.Throws<KeyNotFoundException>(() => _coordinator.Get(room.LobbyId));
    }

    [Fact]
    public void GuestCannotOccupyMoreThanFourCreatedRoomsButAnotherGuestCan()
    {
        var commands = CreateCommands();
        var ids = Enumerable.Range(0, GuestLobbyCommands.MaxRoomsPerCreator)
            .Select(_ => commands.Create("guest-a", ["white", "black"], "white").LobbyId)
            .ToArray();

        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Throws<LobbyCapacityException>(() =>
            commands.Create("guest-a", ["white", "black"], "white"));
        Assert.Equal(4, _coordinator.StoredRoomCount);
        Assert.All(ids, id => Assert.Equal(1, _coordinator.Get(id).Revision));

        var independent = commands.Create("guest-b", ["white", "black"], "black");
        Assert.Equal(1, independent.Revision);
        Assert.Equal(5, _coordinator.StoredRoomCount);
    }

    [Fact]
    public void CreatorQuotaIsRetainedIfOtherMembersRemainAndReleasedOnFinalLeave()
    {
        var commands = CreateCommands();
        var rooms = Enumerable.Range(0, GuestLobbyCommands.MaxRoomsPerCreator)
            .Select(_ => commands.Create("alice", ["white", "black"], "white"))
            .ToArray();
        var withFriend = commands.Join(rooms[0].LobbyId, "bob", "black", rooms[0].Revision);
        commands.Leave(rooms[0].LobbyId, "alice", withFriend.Revision);

        Assert.Throws<LobbyCapacityException>(() =>
            commands.Create("alice", ["white", "black"], "white"));
        Assert.Single(_coordinator.Get(rooms[0].LobbyId).Participants);
        commands.Leave(rooms[0].LobbyId, "bob", expectedRevision: 3);

        Assert.Equal(1, commands.Create("alice", ["white", "black"], "white").Revision);
        Assert.Equal(4, _coordinator.StoredRoomCount);
    }

    [Fact]
    public async Task CreatorQuotaCannotBeBypassedBySimultaneousRequests()
    {
        var commands = CreateCommands();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            try
            {
                commands.Create("same-guest", ["white", "black"], "white");
                return true;
            }
            catch (LobbyCapacityException)
            {
                return false;
            }
        })));
        Assert.Equal(GuestLobbyCommands.MaxRoomsPerCreator, outcomes.Count(x => x));
        Assert.Equal(GuestLobbyCommands.MaxRoomsPerCreator, _coordinator.StoredRoomCount);
    }

    [Fact]
    public void InvalidCreatorQuotaIsRejectedBeforeCreatingRoom()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _coordinator.CreateWithOwner("room", ["white", "black"], "alice",
                "white", maxRooms: 10, maxRoomsPerCreator: 0));
        Assert.Throws<ArgumentException>(() =>
            _coordinator.CreateWithOwner("room", ["white", "black"], " ",
                "white", maxRooms: 10, maxRoomsPerCreator: 4));
        Assert.Equal(0, _coordinator.StoredRoomCount);
    }
}
