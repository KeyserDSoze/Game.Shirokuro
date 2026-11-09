using System.Text.Json;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class LobbyRosterReaderTests
{
    private readonly InMemoryLobbyCoordinator _coordinator = new();

    private LobbyRosterReader Setup()
    {
        _coordinator.Create("lobby-1", ["white", "black"]);
        _coordinator.Join("lobby-1", "alice", "white", 0);
        _coordinator.Join("lobby-1", "bob", "black", 1);
        _coordinator.ChooseHero("lobby-1", "alice", "kira", 2);
        return new LobbyRosterReader(_coordinator);
    }

    [Fact]
    public void OnlyExistingMembersCanReadRoster()
    {
        var reader = Setup();
        var roster = reader.ForMember("lobby-1", "alice");
        Assert.Equal("lobby-1", roster.LobbyId);
        Assert.Equal(3, roster.Revision);
        Assert.Equal(["white", "black"], roster.TeamIds);
        Assert.Equal(2, roster.Participants.Count);
        Assert.Equal("kira", roster.Participants[0].HeroId);

        Assert.Throws<UnauthorizedAccessException>(() => reader.ForMember("lobby-1", "outsider"));
        Assert.Throws<KeyNotFoundException>(() => reader.ForMember("unknown", "alice"));
        Assert.Throws<ArgumentException>(() => reader.ForMember("lobby-1", " "));
    }

    [Fact]
    public void AnotherRoomDoesNotInheritFirstRoomsMembership()
    {
        var reader = Setup();
        _coordinator.Create("lobby-2", ["white", "black"]);
        _coordinator.Join("lobby-2", "bob", "black", 0);

        Assert.Throws<UnauthorizedAccessException>(() => reader.ForMember("lobby-2", "alice"));
        Assert.Single(reader.ForMember("lobby-2", "bob").Participants);
    }

    [Fact]
    public void RosterIsImmutableSnapshotAndUpdatesHaveDistinctRevision()
    {
        var reader = Setup();
        var first = reader.ForMember("lobby-1", "alice");
        _coordinator.ChooseHero("lobby-1", "bob", "adsiz", 3);
        var second = reader.ForMember("lobby-1", "bob");

        Assert.Equal(3, first.Revision);
        Assert.Equal(4, second.Revision);
        Assert.Null(first.Participants[1].HeroId);
        Assert.Equal("adsiz", second.Participants[1].HeroId);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LobbyParticipantView>)first.Participants)[0] =
                new LobbyParticipantView("evil", "white", null));
    }

    [Fact]
    public void SerializingRosterCannotLeakMatchLocationsOrPrivateRoutes()
    {
        var payload = JsonSerializer.Serialize(Setup().ForMember("lobby-1", "alice"));
        Assert.DoesNotContain("PlannedDestination", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Position", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateRoute", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MatchStateSnapshot", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HeroIds", payload, StringComparison.OrdinalIgnoreCase);
    }
}

