using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class RedisGuestExpiryIndexTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private static string? RedisAddress =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private static ServiceProvider KeyRing(string directory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("Shirokuro")
            .PersistKeysToFileSystem(new DirectoryInfo(directory));
        return services.BuildServiceProvider();
    }

    private sealed class FailingLobby : IAsyncGuestLobby
    {
        public Task<int> RemovePlayerFromAllRoomsAsync(string id,
            CancellationToken cancellationToken = default) =>
            Task.FromException<int>(new IOException("test-only outage"));

        public Task<LobbyRosterView> CreateAsync(string id,
            IReadOnlyList<string>? teams, string? team,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> JoinAsync(string room, string id, string? team,
            long? revision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChooseHeroAsync(string room, string id,
            string? hero, long revision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChangeTeamAsync(string room, string id,
            string? team, long revision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LeaveAsync(string room, string id, long revision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ForMemberAsync(string room, string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task LostIssuerNodeStillCleansExpiredMembershipFromOtherRedisNode()
    {
        if (RedisAddress is null) return;
        using var a = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var b = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var directory = Path.Combine(Path.GetTempPath(), "shirokuro-expiry-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var issuerKeys = KeyRing(directory);
            using var workerKeys = KeyRing(directory);
            var clock = new ManualClock();
            var ns = "expiry-" + Guid.NewGuid().ToString("N");
            var issuer = new RedisGuestExpiryIndex(a.GetDatabase(),
                issuerKeys.GetRequiredService<IDataProtectionProvider>(), clock, ns);
            var workerIndex = new RedisGuestExpiryIndex(b.GetDatabase(),
                workerKeys.GetRequiredService<IDataProtectionProvider>(), clock, ns);
            var alice = GuestSessions.NewPlayerId();
            var bob = GuestSessions.NewPlayerId();
            var expiry = clock.GetUtcNow().AddMinutes(5);
            await issuer.ScheduleAsync(alice, expiry);

            var members = await a.GetDatabase().SortedSetRangeByRankAsync(
                "shirokuro:guest-expiry:{" + ns + "}:v1:pending");
            var encrypted = Assert.Single(members).ToString();
            Assert.DoesNotContain(alice, encrypted, StringComparison.Ordinal);
            Assert.DoesNotContain("guest-", encrypted, StringComparison.Ordinal);
            Assert.DoesNotContain(alice, "shirokuro:guest-expiry:{" + ns + "}:v1:pending");
            Assert.Empty(await workerIndex.ReadDueAsync());

            // Server A could disappear here: no local registry or memory is
            // transferred to B. Lobby and expiry state are both in Redis.
            var store = new RedisLobbySnapshotStore(b.GetDatabase(), clock,
                namespaceName: ns);
            var runtime = new RedisGuestLobbyRuntime(store, clock);
            var shared = await runtime.CreateAsync(alice,
                ["white", "black"], "white");
            await runtime.JoinAsync(shared.LobbyId, bob, "black", 1);
            var initial = (await store.ReadAsync(shared.LobbyId))!.Snapshot.LastActivityUtc;

            var cleanup = new GuestSessionExpiryWorker(
                new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
                clock, runtime, workerIndex);
            clock.Advance(TimeSpan.FromMinutes(4));
            Assert.Equal(0, await cleanup.SweepOnceAsync());
            Assert.Equal(2, (await store.ReadAsync(shared.LobbyId))!.Snapshot
                .State.Participants.Count);

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, await cleanup.SweepOnceAsync());
            Assert.Equal(0, await cleanup.SweepOnceAsync());
            Assert.Empty(await issuer.ReadDueAsync());

            var after = (await store.ReadAsync(shared.LobbyId))!.Snapshot;
            Assert.Equal(3, after.State.Revision);
            Assert.Equal(bob, Assert.Single(after.State.Participants).PlayerId);
            Assert.Equal(initial, after.LastActivityUtc); // Admin cleanup doesn't renew TTL.
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task FailedCleanupKeepsDurableWorkAndTwoWorkersMayAckSameEntry()
    {
        if (RedisAddress is null) return;
        using var a = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var b = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var directory = Path.Combine(Path.GetTempPath(), "shirokuro-expiry-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var keys = KeyRing(directory);
            var clock = new ManualClock();
            var ns = "expiry-" + Guid.NewGuid().ToString("N");
            var first = new RedisGuestExpiryIndex(a.GetDatabase(),
                keys.GetRequiredService<IDataProtectionProvider>(), clock, ns);
            var second = new RedisGuestExpiryIndex(b.GetDatabase(),
                keys.GetRequiredService<IDataProtectionProvider>(), clock, ns);
            var alice = GuestSessions.NewPlayerId();
            await first.ScheduleAsync(alice, clock.GetUtcNow().AddSeconds(3));
            clock.Advance(TimeSpan.FromSeconds(3));

            var failedWorker = new GuestSessionExpiryWorker(
                new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
                clock, new FailingLobby(), second);
            await Assert.ThrowsAsync<IOException>(() => failedWorker.SweepOnceAsync());
            var itemA = Assert.Single(await first.ReadDueAsync());
            var itemB = Assert.Single(await second.ReadDueAsync());
            Assert.Equal(alice, itemA.PlayerId);
            Assert.Equal(itemA.ProtectedMember, itemB.ProtectedMember);

            // Two nodes can see one item before either acknowledges. Repeated
            // acknowledgements are safe, including after a network retry.
            await second.AcknowledgeAsync(itemB.ProtectedMember);
            await first.AcknowledgeAsync(itemA.ProtectedMember);
            Assert.Empty(await first.ReadDueAsync());
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task BoundedDueBatchCannotScanUnboundedGuestWork()
    {
        if (RedisAddress is null) return;
        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var directory = Path.Combine(Path.GetTempPath(), "shirokuro-expiry-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var keys = KeyRing(directory);
            var clock = new ManualClock();
            var index = new RedisGuestExpiryIndex(connection.GetDatabase(),
                keys.GetRequiredService<IDataProtectionProvider>(), clock,
                "expiry-" + Guid.NewGuid().ToString("N"));
            for (var i = 0; i < RedisGuestExpiryIndex.BatchSize + 2; i++)
                await index.ScheduleAsync(GuestSessions.NewPlayerId(),
                    clock.GetUtcNow().AddSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            var batch = await index.ReadDueAsync();
            Assert.Equal(RedisGuestExpiryIndex.BatchSize, batch.Count);
            foreach (var item in batch)
                await index.AcknowledgeAsync(item.ProtectedMember);
            Assert.Equal(2, (await index.ReadDueAsync()).Count);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
