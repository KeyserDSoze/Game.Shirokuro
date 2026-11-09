using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Tests.Support;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class BackgroundExpiryWorkerTests
{
    [Fact]
    public async Task LobbyWorkerActuallyRemovesInactiveRoomsOnTimerTick()
    {
        var clock = new ManualTickTimeProvider();
        var coordinator = new InMemoryLobbyCoordinator(clock, TimeSpan.FromMinutes(30));
        coordinator.CreateWithOwner("expired", ["white", "black"], "alice", "white", 2);
        using var worker = new LobbyExpiryWorker(coordinator, clock);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => clock.CreatedTimerCount > 0,
            TimeSpan.FromSeconds(3)), "The background worker never created its timer.");
        Assert.Equal(1, coordinator.StoredRoomCount);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(1, coordinator.StoredRoomCount); // No lookup/sweep yet.
        clock.Tick();

        Assert.True(SpinWait.SpinUntil(() => coordinator.StoredRoomCount == 0,
            TimeSpan.FromSeconds(3)), "The periodic lobby worker did not sweep.");
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("expired"));
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RegistryWorkerActuallyRemovesExpiredTicketsOnTimerTick()
    {
        var clock = new ManualTickTimeProvider();
        var registry = new GuestSessionRegistry(clock);
        var id = "guest-" + new string('a', 32);
        var token = registry.Register(id, clock.GetUtcNow().AddHours(8));
        var principal = GuestSessions.CreatePrincipal(id, token);
        // Keep the room alive past the guest's 8h ticket so we can verify
        // the real timer also purges the expired guest's stale membership.
        var coordinator = new InMemoryLobbyCoordinator(clock, TimeSpan.FromHours(9));
        coordinator.CreateWithOwner("expired-room", ["white", "black"], id, "white", 2);
        using var worker = new GuestSessionExpiryWorker(registry, coordinator, clock);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => clock.CreatedTimerCount > 0,
            TimeSpan.FromSeconds(3)), "The background worker never created its timer.");
        Assert.Equal(1, registry.StoredSessionCount);
        Assert.Equal(1, coordinator.StoredRoomCount);
        clock.Advance(TimeSpan.FromHours(8));
        Assert.False(registry.IsActive(principal));
        Assert.Equal(1, registry.StoredSessionCount); // Still waiting for timer.
        clock.Tick();

        Assert.True(SpinWait.SpinUntil(() => registry.StoredSessionCount == 0 &&
            coordinator.StoredRoomCount == 0, TimeSpan.FromSeconds(3)),
            "The periodic session worker did not sweep both tickets and rooms.");
        Assert.False(registry.IsActive(principal));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("expired-room"));
        await worker.StopAsync(CancellationToken.None);
    }
}

