using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class LobbyStateTests
{
    private static LobbyState Empty(params string[] teams)
        => LobbyState.Create(new LobbyDefinition(
            "room-1",
            teams.Length > 0 ? teams : ["white", "black"],
            ["kira", "krugg", "adsiz"]));

    [Fact]
    public void CreateLobby_HasNoParticipantsAndRevisionZero()
    {
        var state = Empty();
        Assert.Equal(0, state.Revision);
        Assert.Empty(state.Participants);
        Assert.Equal(2, state.Definition.TeamIds.Count);
    }

    [Fact]
    public void Join_AddsPlayerWithTeamAndNoSelectedHero()
    {
        var state = Empty().Join("alice", "white", expectedRevision: 0);
        var participant = Assert.Single(state.Participants);
        Assert.Equal("alice", participant.PlayerId);
        Assert.Equal("white", participant.TeamId);
        Assert.Null(participant.HeroId);
        Assert.Equal(1, state.Revision);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void Restore_RejectsFewerRevisionsThanJoinedMembers(
        long revision, int members)
    {
        var definition = Empty().Definition;
        var participants = new[]
        {
            new LobbyParticipant("alice", "white", null),
            new LobbyParticipant("bob", "black", null)
        }.Take(members).ToArray();

        Assert.Throws<ArgumentException>(() =>
            LobbyState.Restore(definition, revision, participants));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    public void Restore_AcceptsRevisionsAtOrAboveMemberCount(
        long revision, int members)
    {
        var definition = Empty().Definition;
        var participants = new[]
        {
            new LobbyParticipant("alice", "white", null),
            new LobbyParticipant("bob", "black", "kira")
        }.Take(members).ToArray();

        var restored = LobbyState.Restore(definition, revision, participants);

        Assert.Equal(revision, restored.Revision);
        Assert.Equal(members, restored.Participants.Count);
    }

    [Fact]
    public void Join_DoesNotModifyOldSnapshot()
    {
        var before = Empty();
        var after = before.Join("alice", "white", 0);
        Assert.Empty(before.Participants);
        Assert.Single(after.Participants);
        Assert.Equal(0, before.Revision);
    }

    [Fact]
    public void LobbySupportsThreeAndMoreTeamsWithoutInventingTeamSizeLimit()
    {
        var state = Empty("white", "black", "gold");
        state = state.Join("alice", "white", state.Revision);
        state = state.Join("bob", "black", state.Revision);
        state = state.Join("cara", "gold", state.Revision);
        state = state.Join("dan", "gold", state.Revision);
        Assert.Equal(4, state.Participants.Count);
        Assert.Equal("gold", state.Participants[3].TeamId);
    }

    [Fact]
    public void IdempotentRejoin_DoesNotIncreaseRevisionOrLoseHeroSelection()
    {
        var state = Empty().Join("alice", "white", 0).ChooseHero("alice", "kira", 1);
        var after = state.Join("alice", "white", state.Revision);
        Assert.Same(state, after);
        Assert.Equal(2, after.Revision);
        Assert.Equal("kira", Assert.Single(after.Participants).HeroId);
    }

    [Fact]
    public void JoiningWithAnotherTeamRequiresExplicitChangeTeam()
    {
        var state = Empty().Join("alice", "white", 0);
        Assert.Throws<InvalidOperationException>(() => state.Join("alice", "black", 1));
    }

    [Fact]
    public void ChangeTeamPreservesHeroAndOtherPlayers()
    {
        var state = Empty()
            .Join("alice", "white", 0)
            .ChooseHero("alice", "kira", 1)
            .Join("bob", "black", 2);
        var after = state.ChangeTeam("alice", "black", 3);
        Assert.Equal("black", after.Participants[0].TeamId);
        Assert.Equal("kira", after.Participants[0].HeroId);
        Assert.Equal("black", after.Participants[1].TeamId);
        Assert.Equal("white", state.Participants[0].TeamId);
    }

    [Fact]
    public void ChooseHero_UpdatesOnlyOnePlayerAndDoesNotRequireUniqueness()
    {
        var state = Empty().Join("alice", "white", 0).Join("bob", "black", 1);
        var chosen = state.ChooseHero("alice", "kira", 2).ChooseHero("bob", "kira", 3);
        Assert.Equal("kira", chosen.Participants[0].HeroId);
        Assert.Equal("kira", chosen.Participants[1].HeroId);
        Assert.Null(state.Participants[0].HeroId);
    }

    [Fact]
    public void SelectingSameHeroOrSameTeamIsIdempotent()
    {
        var first = Empty().Join("alice", "white", 0).ChooseHero("alice", "kira", 1);
        Assert.Same(first, first.ChooseHero("alice", "kira", 2));
        Assert.Same(first, first.ChangeTeam("alice", "white", 2));
    }

    [Fact]
    public void Leave_RemovesOnlyTheActor()
    {
        var state = Empty().Join("alice", "white", 0).Join("bob", "black", 1);
        var after = state.Leave("alice", 2);
        Assert.Equal("bob", Assert.Single(after.Participants).PlayerId);
        Assert.Equal(3, after.Revision);
        Assert.Equal(2, state.Participants.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonexistent")]
    [InlineData("Kira")]
    [InlineData(" kira")]
    public void UnknownOrInvalidHeroCannotBeSelected(string requested)
    {
        var state = Empty().Join("alice", "white", 0);
        Assert.Throws<ArgumentException>(() => state.ChooseHero("alice", requested, 1));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("WHITE")]
    [InlineData(" white")]
    public void UnknownTeamCannotBeJoined(string requested)
    {
        Assert.Throws<ArgumentException>(() => Empty().Join("alice", requested, 0));
    }

    [Fact]
    public void NonMemberCannotMutateAnotherPlayer()
    {
        var state = Empty().Join("alice", "white", 0);
        Assert.Throws<InvalidOperationException>(() => state.ChooseHero("bob", "kira", 1));
        Assert.Throws<InvalidOperationException>(() => state.ChangeTeam("bob", "black", 1));
        Assert.Throws<InvalidOperationException>(() => state.Leave("bob", 1));
    }

    [Fact]
    public void StaleRevisionIsRejectedWithoutModifyingState()
    {
        var state = Empty().Join("alice", "white", 0);
        var error = Assert.Throws<LobbyRevisionConflictException>(() =>
            state.ChooseHero("alice", "kira", expectedRevision: 0));
        Assert.Equal(0, error.Expected);
        Assert.Equal(1, error.Actual);
        Assert.Null(state.Participants[0].HeroId);
    }

    [Fact]
    public void DifferentPlayersCannotJoinWithTheSamePlayerId()
    {
        var state = Empty().Join("alice", "white", 0);
        Assert.Single(state.Join("alice", "white", 1).Participants);
        Assert.Throws<InvalidOperationException>(() => state.Join("alice", "black", 1));
    }

    [Fact]
    public void DefinitionDefensivelyCopiesMutableConfigurationLists()
    {
        var teams = new List<string> { "white", "black" };
        var heroes = new List<string> { "kira" };
        var definition = new LobbyDefinition("r", teams, heroes);
        teams.Add("gold");
        heroes.Add("krugg");
        Assert.Equal(2, definition.TeamIds.Count);
        Assert.Single(definition.HeroIds);
        Assert.False(definition.AllowsTeam("gold"));
        Assert.False(definition.AllowsHero("krugg"));
    }

    [Fact]
    public void DefinitionRejectsInvalidAndRepeatedConfiguration()
    {
        Assert.Throws<ArgumentException>(() => new LobbyDefinition("r", ["only"], ["kira"]));
        Assert.Throws<ArgumentException>(() => new LobbyDefinition("r", ["white", "white"], ["kira"]));
        Assert.Throws<ArgumentException>(() => new LobbyDefinition("r", ["white", "black"], []));
        Assert.Throws<ArgumentException>(() => new LobbyDefinition("r", ["white", "black"], ["kira", "kira"]));
        Assert.Throws<ArgumentException>(() => new LobbyDefinition(" ", ["white", "black"], ["kira"]));
    }
}

