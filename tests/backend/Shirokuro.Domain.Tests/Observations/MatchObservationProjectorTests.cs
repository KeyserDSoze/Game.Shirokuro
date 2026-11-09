using System.Text.Json;
using Shirokuro.Application.Observations;
using Shirokuro.Domain.Geometry;
using Shirokuro.Domain.Matches;
using Xunit;

namespace Shirokuro.Domain.Tests.Observations;

public sealed class MatchObservationProjectorTests
{
    private static readonly MatchObservationProjector Projector = new();

    private static MatchStateSnapshot Example() => new(
        "match-001",
        [
            new AuthoritativeHeroState("alice", "white", "kira", new HexCoordinate(0, 0), new HexCoordinate(2, -1)),
            new AuthoritativeHeroState("bob", "black", "krugg", new HexCoordinate(1, 0), new HexCoordinate(-3, 2)),
            new AuthoritativeHeroState("cara", "white", "adsiz", new HexCoordinate(0, 1), new HexCoordinate(1, 2))
        ]);

    [Theory]
    [InlineData("alice", "kira", 2, -1)]
    [InlineData("bob", "krugg", -3, 2)]
    [InlineData("cara", "adsiz", 1, 2)]
    public void NoGrants_OnlyOwnStateIsVisible(string viewer, string hero, int q, int r)
    {
        var view = Projector.ForObserver(Example(), viewer, []);

        Assert.Equal(viewer, view.Self.PlayerId);
        Assert.Equal(hero, view.Self.HeroId);
        Assert.Equal(new HexCoordinate(q, r), view.Self.PlannedDestination);
        Assert.Empty(view.Others);
    }

    [Theory]
    [InlineData("alice", "bob")]
    [InlineData("alice", "cara")]
    [InlineData("bob", "cara")]
    public void NoGrants_HidesEnemyAndTeammateIdentitiesRegardlessOfTeam(string viewer, string other)
    {
        var view = Projector.ForObserver(Example(), viewer, []);
        Assert.DoesNotContain(view.Others, hero => hero.PlayerId == other);
    }

    [Fact]
    public void IdentityReveal_DoesNotRevealPositionWithoutExplicitPermission()
    {
        var view = Projector.ForObserver(Example(), "alice", [
            new AuthorizedHeroReveal("bob", RevealPosition: false)
        ]);

        var revealed = Assert.Single(view.Others);
        Assert.Equal("bob", revealed.PlayerId);
        Assert.Equal("black", revealed.TeamId);
        Assert.Null(revealed.Position);
        Assert.False(typeof(OtherHeroObservation).GetProperties().Any(
            property => property.Name == nameof(OwnHeroObservation.PlannedDestination)));
    }

    [Fact]
    public void PositionReveal_NeverExposesOtherPlayersPlannedDestination()
    {
        var view = Projector.ForObserver(Example(), "alice", [
            new AuthorizedHeroReveal("bob", RevealPosition: true),
            new AuthorizedHeroReveal("cara", RevealPosition: false)
        ]);

        Assert.Equal(2, view.Others.Count);
        Assert.Equal(new HexCoordinate(1, 0), view.Others[0].Position);
        Assert.Null(view.Others[1].Position);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(view));
        var others = json.RootElement.GetProperty("Others");
        Assert.Equal(2, others.GetArrayLength());
        foreach (var other in others.EnumerateArray())
        {
            Assert.False(other.TryGetProperty("PlannedDestination", out _));
            Assert.False(other.TryGetProperty("PrivateRoute", out _));
        }

        Assert.Equal(-1, view.Self.PlannedDestination!.Value.R);
    }

    [Fact]
    public void DifferentObserversNeverReceiveEachOthersHiddenDestination()
    {
        var state = Example();
        var aliceView = Projector.ForObserver(state, "alice", [
            new AuthorizedHeroReveal("bob", true)
        ]);
        var bobView = Projector.ForObserver(state, "bob", [
            new AuthorizedHeroReveal("alice", true)
        ]);

        Assert.Equal(new HexCoordinate(2, -1), aliceView.Self.PlannedDestination);
        Assert.Equal(new HexCoordinate(-3, 2), bobView.Self.PlannedDestination);
        Assert.Null(aliceView.Others[0].GetType().GetProperty("PlannedDestination"));
        Assert.Null(bobView.Others[0].GetType().GetProperty("PlannedDestination"));
    }

    [Fact]
    public void NonParticipantCannotAskForMatchObservation()
    {
        Assert.Throws<UnauthorizedAccessException>(() => Projector.ForObserver(Example(), "outsider", []));
    }

    [Theory]
    [InlineData("ghost")]
    [InlineData("BOB")]
    public void UnknownAndNonCanonicalRevealIdsAreRejected(string playerId)
    {
        Assert.Throws<ArgumentException>(() => Projector.ForObserver(
            Example(), "alice", [new AuthorizedHeroReveal(playerId, true)]));
    }

    [Fact]
    public void DuplicateGrantsAreRejectedInsteadOfInconsistentlyApplied()
    {
        Assert.Throws<ArgumentException>(() => Projector.ForObserver(Example(), "alice", [
            new AuthorizedHeroReveal("bob", true),
            new AuthorizedHeroReveal("bob", false)
        ]));
    }

    [Fact]
    public void OwnGrantDoesNotDuplicatePlayerInOthers()
    {
        var view = Projector.ForObserver(Example(), "alice", [
            new AuthorizedHeroReveal("alice", true)
        ]);

        Assert.Empty(view.Others);
    }

    [Fact]
    public void MatchSnapshotRejectsDuplicatePlayers()
    {
        var hero = new AuthoritativeHeroState("alice", "white", "kira", default, null);

        Assert.Throws<ArgumentException>(() => new MatchStateSnapshot("match-001", [hero, hero]));
    }

    [Fact]
    public void SnapshotCopiesInputSoCallersCannotAppendSecretPlayersLater()
    {
        var list = new List<AuthoritativeHeroState> {
            new("alice", "white", "kira", default, new HexCoordinate(1, 1))
        };
        var snapshot = new MatchStateSnapshot("match-001", list);
        list.Add(new AuthoritativeHeroState("bob", "black", "krugg", default, new HexCoordinate(3, 3)));

        Assert.Single(snapshot.Heroes);
        Assert.Empty(Projector.ForObserver(snapshot, "alice", []).Others);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" alice")]
    [InlineData("alice ")]
    public void InvalidPlayerIdentifiersAreRejected(string playerId)
    {
        Assert.Throws<ArgumentException>(() => new AuthoritativeHeroState(
            playerId, "white", "kira", default, null));
    }
}

