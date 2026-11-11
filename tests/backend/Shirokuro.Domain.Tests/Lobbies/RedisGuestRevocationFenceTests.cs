using System.Security.Cryptography;
using System.Text;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Actual Redis Lua scripts must serialize fence installation against stale
/// in-flight mutations across two independent API/node connections.
/// </summary>
public sealed class RedisGuestRevocationFenceTests
{
    private static string? Address =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task StaleReadCannotCommitAfterRevocationFenceAndOtherGuestsSurvive()
    {
        if (Address is null) return;
        using var redisA = await ConnectionMultiplexer.ConnectAsync(Address);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(Address);
        var clock = new ManualClock();
        var ns = "fence-" + Guid.NewGuid().ToString("N");
        var a = new RedisLobbySnapshotStore(redisA.GetDatabase(), clock, namespaceName: ns);
        var b = new RedisLobbySnapshotStore(redisB.GetDatabase(), clock, namespaceName: ns);
        var apiA = new RedisGuestLobbyRuntime(a, clock);
        var apiB = new RedisGuestLobbyRuntime(b, clock);
        var alice = GuestSessions.NewPlayerId();
        var bob = GuestSessions.NewPlayerId();

        var room = await apiA.CreateAsync(alice, ["white", "black"], "white");
        await apiB.JoinAsync(room.LobbyId, bob, "black", room.Revision);

        // Simulate an already-authenticated HTTP request reading before
        // another node revokes its cookie and installs the tombstone.
        var stale = (await a.ReadAsync(room.LobbyId))!;
        var mutation = stale.Snapshot.State.ChooseHero(
            alice, "kira", stale.Snapshot.State.Revision);

        await b.FenceGuestAsync(alice);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            a.FenceGuestAsync("guest-client-chosen-or-invalid"));
        Assert.Equal(RedisLobbySnapshotStore.RevocationFenceTtl,
            TimeSpan.FromHours(8));

        // Same Lua operation sees fence even though the old snapshot and
        // revision still match; no update or TTL renewal is permitted.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            a.CompareExchangeAsync(stale, stale.Snapshot with { State = mutation }, alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            a.DeleteIfCurrentAsync(stale, alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiA.ChooseHeroAsync(room.LobbyId, alice, "kira", stale.Snapshot.State.Revision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiA.ForMemberAsync(room.LobbyId, alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiB.JoinAsync(room.LobbyId, alice, "white", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiA.CreateAsync(alice, ["white", "black"], "white"));

        var untouched = (await b.ReadAsync(room.LobbyId))!;
        Assert.Equal(stale.Snapshot.State.Revision, untouched.Snapshot.State.Revision);
        Assert.Equal(stale.Snapshot.LastActivityUtc, untouched.Snapshot.LastActivityUtc);
        Assert.Null(untouched.Snapshot.State.Participants.Single(
            p => p.PlayerId == alice).HeroId);

        // Other players still have normal access after a guest is fenced.
        var otherView = await apiB.ForMemberAsync(room.LobbyId, bob);
        Assert.Equal(room.LobbyId, otherView.LobbyId);
        var beforeCleanup = (await a.ReadAsync(room.LobbyId))!.Snapshot;
        Assert.Equal(1, await apiA.RemovePlayerFromAllRoomsAsync(alice));
        var afterCleanup = (await b.ReadAsync(room.LobbyId))!.Snapshot;
        Assert.Equal(beforeCleanup.State.Revision + 1, afterCleanup.State.Revision);
        Assert.Equal(bob, Assert.Single(afterCleanup.State.Participants).PlayerId);
        Assert.Equal(beforeCleanup.LastActivityUtc, afterCleanup.LastActivityUtc);

        // Fencing must not store the plaintext player or token in any Redis
        // key, and must have a bounded expiration, even after a retry.
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(alice)));
        var fenceKey = "shirokuro:lobby:{" + ns + "}:v2:revoked:" + digest;
        Assert.DoesNotContain(alice, fenceKey, StringComparison.Ordinal);
        Assert.DoesNotContain("guest-", fenceKey, StringComparison.Ordinal);
        Assert.Equal("1", (await redisB.GetDatabase().StringGetAsync(fenceKey)).ToString());
        var initialTtl = (await redisA.GetDatabase().KeyTimeToLiveAsync(fenceKey))!.Value;
        Assert.True(initialTtl > TimeSpan.Zero);
        Assert.True(initialTtl <= RedisLobbySnapshotStore.RevocationFenceTtl);
        await a.FenceGuestAsync(alice); // SET NX must not slide the fence TTL.
        var afterRetryTtl = (await redisB.GetDatabase().KeyTimeToLiveAsync(fenceKey))!.Value;
        Assert.True(afterRetryTtl <= initialTtl);
    }

    [Fact]
    public async Task AtomicFenceCheckPreventsLateCreationEvenWhenQuotaIsAvailable()
    {
        if (Address is null) return;
        using var redisA = await ConnectionMultiplexer.ConnectAsync(Address);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(Address);
        var clock = new ManualClock();
        var ns = "fence-quota-" + Guid.NewGuid().ToString("N");
        var first = new RedisGuestLobbyRuntime(
            new RedisLobbySnapshotStore(redisA.GetDatabase(), clock, namespaceName: ns), clock);
        var index = new RedisLobbySnapshotStore(
            redisB.GetDatabase(), clock, namespaceName: ns);
        var owner = GuestSessions.NewPlayerId();
        var survivor = GuestSessions.NewPlayerId();
        var initial = await first.CreateAsync(owner, ["white", "black"], "white");

        await index.FenceGuestAsync(owner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            first.CreateAsync(owner, ["white", "black"], "white"));

        // The failure was before room insertion or quota index mutation.
        var other = await first.CreateAsync(survivor, ["white", "black"], "black");
        Assert.NotEqual(initial.LobbyId, other.LobbyId);
        Assert.NotNull(await index.ReadAsync(initial.LobbyId));
        Assert.NotNull(await index.ReadAsync(other.LobbyId));
        Assert.Equal(1, await first.RemovePlayerFromAllRoomsAsync(owner));
        Assert.Null(await index.ReadAsync(initial.LobbyId));
        Assert.NotNull(await index.ReadAsync(other.LobbyId));
    }

    [Fact]
    public async Task FenceRejectsInvalidGuestBeforeAnyKeyIsCreated()
    {
        if (Address is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(Address);
        var clock = new ManualClock();
        var ns = "fence-validation-" + Guid.NewGuid().ToString("N");
        var store = new RedisLobbySnapshotStore(redis.GetDatabase(), clock,
            namespaceName: ns);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.FenceGuestAsync("attacker-selected-id"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.FenceGuestAsync("guest-1111111111111111111111111111111Z"));
        var keys = redis.GetServer(redis.GetEndPoints().First())
            .Keys(pattern: "shirokuro:lobby:{" + ns + "}:v2:revoked:*")
            .ToArray();
        Assert.Empty(keys);
    }
}
