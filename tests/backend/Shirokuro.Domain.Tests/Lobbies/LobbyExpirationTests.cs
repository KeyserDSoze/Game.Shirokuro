using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Lobbies;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class LobbyExpirationTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds = DateTimeOffset.Parse(
            "2026-11-09T21:00:00+00:00").ToUnixTimeMilliseconds();

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _milliseconds, (long)duration.TotalMilliseconds);
    }

    private static (InMemoryLobbyCoordinator Coordinator, ManualClock Clock) Setup(
        TimeSpan? timeout = null)
    {
        var clock = new ManualClock();
        var coordinator = new InMemoryLobbyCoordinator(clock,
            timeout ?? TimeSpan.FromMinutes(30));
        return (coordinator, clock);
    }

    [Fact]
    public void DefaultTimeoutIsTechnicalAndInvalidTimeoutsAreRejected()
    {
        Assert.Equal(TimeSpan.FromMinutes(30),
            InMemoryLobbyCoordinator.DefaultInactivityTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryLobbyCoordinator(new ManualClock(), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new InMemoryLobbyCoordinator(new ManualClock(), TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ExpiryAtExactTtlImmediatelyHidesAndDeletesAbandonedRoom()
    {
        var (coordinator, clock) = Setup();
        var created = coordinator.CreateWithOwner(
            "private-room", ["white", "black"], "alice", "white", maxRooms: 1);
        Assert.Equal(1, created.Revision);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(1, coordinator.Get("private-room").Revision);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("private-room"));
        Assert.Throws<KeyNotFoundException>(() =>
            coordinator.JoinBounded("private-room", "bob", "black", null, 16));
        Assert.Equal(0, coordinator.PruneExpired()); // Read already purged.
    }

    [Fact]
    public void AuthorizedReadRefreshesTtlWithoutIncrementingRevision()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("private", ["white", "black"], "alice", "white", 3);
        clock.Advance(TimeSpan.FromMinutes(25));
        var visible = new LobbyRosterReader(coordinator).ForMember("private", "alice");
        Assert.Equal(1, visible.Revision);
        clock.Advance(TimeSpan.FromMinutes(25));
        Assert.Equal(1, coordinator.Get("private").Revision);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("private"));
    }

    [Fact]
    public void NonmemberReadsCannotRefreshTtlOrRevealState()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("secret", ["white", "black"], "owner", "white", 3);
        var reader = new LobbyRosterReader(coordinator);
        clock.Advance(TimeSpan.FromMinutes(25));
        Assert.Throws<UnauthorizedAccessException>(() =>
            reader.ForMember("secret", "outsider"));
        Assert.Throws<UnauthorizedAccessException>(() =>
            reader.ForMember("secret", "outsider"));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Throws<KeyNotFoundException>(() => reader.ForMember("secret", "outsider"));
    }

    [Fact]
    public void FailedChangesDoNotRefreshTtlOrModifyRoomRevision()
    {
        var (coordinator, clock) = Setup();
        var commands = new GuestLobbyCommands(
            coordinator, new LobbyRosterReader(coordinator));
        var room = commands.Create("alice", ["white", "black"], "white");
        clock.Advance(TimeSpan.FromMinutes(25));

        Assert.Throws<UnauthorizedAccessException>(() =>
            commands.ChooseHero(room.LobbyId, "outsider", "kira", 1));
        Assert.Throws<UnauthorizedAccessException>(() =>
            commands.ChangeTeam(room.LobbyId, "outsider", "black", 1));
        Assert.Throws<UnauthorizedAccessException>(() =>
            commands.Leave(room.LobbyId, "outsider", 1));
        Assert.Throws<LobbyRevisionConflictException>(() =>
            commands.ChooseHero(room.LobbyId, "alice", "kira", 0));
        Assert.Throws<ArgumentException>(() =>
            commands.ChooseHero(room.LobbyId, "alice", "unknown-hero", 1));
        Assert.Throws<ArgumentException>(() =>
            commands.ChangeTeam(room.LobbyId, "alice", "unknown-team", 1));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get(room.LobbyId));
    }

    [Fact]
    public void SuccessfulIdempotentReconnectCountsAsActivityWithoutChangingRevision()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("room", ["white", "black"], "alice", "white", 3);
        clock.Advance(TimeSpan.FromMinutes(20));
        var joined = coordinator.JoinBounded("room", "alice", "white", null, 16);
        Assert.Equal(1, joined.Revision);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(1, coordinator.Get("room").Revision);
    }

    [Fact]
    public void BackgroundSweepRemovesOnlyExpiredRoomsAndIsIdempotent()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("stale", ["white", "black"], "alice", "white", 4);
        clock.Advance(TimeSpan.FromMinutes(20));
        coordinator.CreateWithOwner("active", ["white", "black"], "bob", "black", 4);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(1, coordinator.PruneExpired());
        Assert.Equal(0, coordinator.PruneExpired());
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("stale"));
        Assert.Equal(1, coordinator.Get("active").Revision);
    }

    [Fact]
    public void ExpiredRoomsReleaseCapacityEvenWithoutBackgroundSweep()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("first", ["white", "black"], "alice", "white", maxRooms: 1);
        Assert.Throws<LobbyCapacityException>(() =>
            coordinator.CreateWithOwner("second", ["white", "black"], "bob", "black", maxRooms: 1));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, coordinator.CreateWithOwner(
            "second", ["white", "black"], "bob", "black", maxRooms: 1).Revision);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("first"));
    }

    [Fact]
    public void SuccessfulWritesRefreshOnlyTheirOwnRoom()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("one", ["white", "black"], "alice", "white", 2);
        coordinator.CreateWithOwner("two", ["white", "black"], "bob", "black", 2);
        clock.Advance(TimeSpan.FromMinutes(20));
        coordinator.ChooseHero("one", "alice", "kira", 1);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, coordinator.PruneExpired());
        Assert.Equal("kira", Assert.Single(coordinator.Get("one").Participants).HeroId);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("two"));
    }

    [Fact]
    public async Task ParallelExpiryAndWritesCannotResurrectExpiredRoom()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("room", ["white", "black"], "alice", "white", 3);
        clock.Advance(TimeSpan.FromMinutes(30));
        var operations = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            try
            {
                if (i % 2 == 0)
                    coordinator.ChooseHero("room", "alice", "kira", 1);
                else
                    coordinator.PruneExpired();
                return true;
            }
            catch (KeyNotFoundException)
            {
                return false;
            }
        }));
        await Task.WhenAll(operations);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("room"));
        Assert.Equal(0, coordinator.PruneExpired());
    }

    [Fact]
    public void HostRegistersPeriodicExpirationService()
    {
        using var server = new WebApplicationFactory<Program>();
        using var client = server.CreateClient();
        var hostedServices = server.Services.GetServices<IHostedService>();
        Assert.Contains(hostedServices, service => service is LobbyExpiryWorker);
        Assert.Same(TimeProvider.System, server.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void ExpiredCreatorRoomsReleasePerGuestQuotaBeforeNextCreate()
    {
        var (coordinator, clock) = Setup();
        var commands = new GuestLobbyCommands(
            coordinator, new LobbyRosterReader(coordinator));
        var oldIds = Enumerable.Range(0, GuestLobbyCommands.MaxRoomsPerCreator)
            .Select(_ => commands.Create("alice", ["white", "black"], "white").LobbyId)
            .ToArray();
        Assert.Throws<LobbyCapacityException>(() =>
            commands.Create("alice", ["white", "black"], "white"));

        clock.Advance(TimeSpan.FromMinutes(30));
        var fresh = commands.Create("alice", ["white", "black"], "white");

        Assert.Equal(1, fresh.Revision);
        Assert.Equal(1, coordinator.StoredRoomCount);
        Assert.All(oldIds, id => Assert.Throws<KeyNotFoundException>(() => coordinator.Get(id)));
    }

    [Fact]
    public void LogoutCleanupDoesNotExtendSurvivingRoomsInactivityDeadline()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("room", ["white", "black"], "alice", "white", 10);
        coordinator.JoinBounded("room", "bob", "black", 1, 16);
        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(1, coordinator.RemovePlayerFromAllRooms("alice"));
        Assert.Equal(3, coordinator.Get("room").Revision);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("room"));
    }

    [Fact]
    public void ExpiredMembershipsCannotBeRevivedByLogoutCleanup()
    {
        var (coordinator, clock) = Setup();
        coordinator.CreateWithOwner("abandoned", ["white", "black"], "alice", "white", 10);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, coordinator.RemovePlayerFromAllRooms("alice"));
        Assert.Equal(0, coordinator.StoredRoomCount);
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("abandoned"));
    }
}
