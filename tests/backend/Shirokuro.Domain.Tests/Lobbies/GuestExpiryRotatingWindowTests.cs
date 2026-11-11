using System.IO;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// The durable Redis window must advance across independent API nodes even
/// when an entire earlier page contains permanently unprocessable work.
/// Never acknowledge or infer an identity from failed Data Protection.
/// </summary>
public sealed class GuestExpiryRotatingWindowTests
{
    private static string? RedisAddress =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now = _now.Add(value);
    }

    private static ServiceProvider KeyRing(string folder)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("Shirokuro")
            .PersistKeysToFileSystem(new DirectoryInfo(folder));
        return services.BuildServiceProvider();
    }

    private static GuestSessionExpiryWorker Worker(
        TimeProvider clock, IAsyncGuestLobby lobby, IGuestExpiryIndex index,
        IGuestRevocationFence fence) =>
        new(new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
            clock, lobby, index, fence);

    [Fact]
    public async Task MoreThan128PoisonItemsDoNotStarveNextGuestAcrossRedisNodes()
    {
        if (RedisAddress is null) return;
        using var issuerRedis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var workerRedis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var folder = Path.Combine(Path.GetTempPath(), "shirokuro-window-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var keys = KeyRing(folder);
            var protector = keys.GetRequiredService<IDataProtectionProvider>();
            var clock = new ManualClock();
            var ns = "window-" + Guid.NewGuid().ToString("N");
            var issuer = new RedisGuestExpiryIndex(
                issuerRedis.GetDatabase(), protector, clock, ns);
            var workerIndex = new RedisGuestExpiryIndex(
                workerRedis.GetDatabase(), protector, clock, ns);
            var store = new RedisLobbySnapshotStore(
                workerRedis.GetDatabase(), clock, namespaceName: ns);
            var lobby = new RedisGuestLobbyRuntime(store, clock);
            var expired = GuestSessions.NewPlayerId();
            var survivor = GuestSessions.NewPlayerId();
            var created = await lobby.CreateAsync(
                expired, ["white", "black"], "white");
            await lobby.JoinAsync(created.LobbyId, survivor, "black", created.Revision);
            var before = (await store.ReadAsync(created.LobbyId))!.Snapshot;
            var queue = "shirokuro:guest-expiry:{" + ns + "}:v1:pending";
            var cursor = "shirokuro:guest-expiry:{" + ns + "}:v1:scan-offset";

            // 130 older, unreadable entries precede the only healthy guest.
            // Neither a bare member nor a corrupt payload can claim identity.
            for (var i = 0; i < 130; i++)
                await issuerRedis.GetDatabase().SortedSetAddAsync(
                    queue, "damaged-protected-member-" + i.ToString("D4"),
                    clock.GetUtcNow().ToUnixTimeMilliseconds() - 10_000);
            await issuer.ScheduleRevokedAsync(expired);

            using var capture = new MultiplayerMetricCapture();
            using var workerA = Worker(clock, lobby, issuer, store);
            var firstFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => workerA.SweepOnceAsync());
            Assert.Equal("Guest expiry maintenance has retryable items.",
                firstFailure.Message);
            Assert.DoesNotContain(expired, firstFailure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("damaged-protected-member", firstFailure.ToString(),
                StringComparison.Ordinal);
            Assert.Equal(2, (await store.ReadAsync(created.LobbyId))!
                .Snapshot.State.Participants.Count);

            // Another node sees the next atomically chosen window, including
            // the guest after the first 128 permanently broken items.
            using var workerB = Worker(clock, lobby, workerIndex, store);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => workerB.SweepOnceAsync());
            var after = (await store.ReadAsync(created.LobbyId))!.Snapshot;
            Assert.Equal(before.State.Revision + 1, after.State.Revision);
            Assert.Equal(survivor, Assert.Single(after.State.Participants).PlayerId);
            Assert.Equal(before.LastActivityUtc, after.LastActivityUtc);
            Assert.Equal(130, await issuerRedis.GetDatabase()
                .SortedSetLengthAsync(queue));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                lobby.JoinAsync(created.LobbyId, expired, "white", null));

            var cursorTtl = await workerRedis.GetDatabase().KeyTimeToLiveAsync(cursor);
            Assert.NotNull(cursorTtl);
            Assert.InRange(cursorTtl!.Value,
                TimeSpan.FromSeconds(1), RedisGuestExpiryIndex.CursorLifetime);
            var cursorContent = (await workerRedis.GetDatabase()
                .StringGetAsync(cursor)).ToString();
            Assert.DoesNotContain(expired, cursorContent, StringComparison.Ordinal);
            Assert.DoesNotContain("guest-", cursorContent, StringComparison.Ordinal);
            Assert.True(long.TryParse(cursorContent, out _));

            var invalid = capture.For(MultiplayerMetrics.ExpiryUnreadableItemsName);
            Assert.True(invalid.Length >= RedisGuestExpiryIndex.BatchSize);
            Assert.All(invalid, e =>
            {
                Assert.Empty(e.Tags);
                Assert.Equal(1, e.Value);
            });
            Assert.Contains(capture.For(MultiplayerMetrics.ExpiryMembershipsRemovedName),
                e => e.Value == 1 && e.Tags["mode"] == "redis");

            // Operational cleanup cannot silently delete unreadable work.
            // Operators restore the old keyring; the items remain durable.
            Assert.Equal(130, await workerRedis.GetDatabase()
                .SortedSetLengthAsync(queue));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task WrongDataProtectionRingKeepsWorkUntilRestoredWithoutFencingPlayer()
    {
        if (RedisAddress is null) return;
        using var a = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var b = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var folderA = Path.Combine(Path.GetTempPath(), "shirokuro-ring-a-"
            + Guid.NewGuid().ToString("N"));
        var folderB = Path.Combine(Path.GetTempPath(), "shirokuro-ring-b-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        try
        {
            using var right = KeyRing(folderA);
            using var wrong = KeyRing(folderB);
            var clock = new ManualClock();
            var ns = "wrong-" + Guid.NewGuid().ToString("N");
            var correct = new RedisGuestExpiryIndex(
                a.GetDatabase(), right.GetRequiredService<IDataProtectionProvider>(),
                clock, ns);
            var mismatched = new RedisGuestExpiryIndex(
                b.GetDatabase(), wrong.GetRequiredService<IDataProtectionProvider>(),
                clock, ns);
            var store = new RedisLobbySnapshotStore(b.GetDatabase(), clock,
                namespaceName: ns);
            var lobby = new RedisGuestLobbyRuntime(store, clock);
            var guest = GuestSessions.NewPlayerId();
            var room = await lobby.CreateAsync(guest,
                ["white", "black"], "white");
            await correct.ScheduleRevokedAsync(guest);

            var due = Assert.Single(await mismatched.ReadDueAsync());
            Assert.True(due.IsUnreadable);
            Assert.Empty(due.PlayerId);
            Assert.NotEmpty(due.ProtectedMember);

            using var wrongWorker = Worker(clock, lobby, mismatched, store);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => wrongWorker.SweepOnceAsync());
            Assert.DoesNotContain(guest, failure.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("cryptographic", failure.ToString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(await store.ReadAsync(room.LobbyId));
            Assert.Single(await correct.ReadDueAsync());

            // An operator restores the original keyring; an independent
            // worker can recover and ack the unchanged encrypted entry.
            using var goodWorker = Worker(clock, lobby, correct, store);
            Assert.Equal(1, await goodWorker.SweepOnceAsync());
            Assert.Empty(await mismatched.ReadDueAsync());
            Assert.Null(await store.ReadAsync(room.LobbyId));
        }
        finally
        {
            foreach (var folder in new[] { folderA, folderB })
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [Fact]
    public async Task StructurallyInvalidButAuthenticatedPayloadIsNeverAcknowledged()
    {
        if (RedisAddress is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var folder = Path.Combine(Path.GetTempPath(), "shirokuro-payload-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var keys = KeyRing(folder);
            var provider = keys.GetRequiredService<IDataProtectionProvider>();
            var clock = new ManualClock();
            var ns = "payload-" + Guid.NewGuid().ToString("N");
            var index = new RedisGuestExpiryIndex(redis.GetDatabase(), provider, clock, ns);
            var store = new RedisLobbySnapshotStore(redis.GetDatabase(), clock,
                namespaceName: ns);
            var lobby = new RedisGuestLobbyRuntime(store, clock);
            var guest = GuestSessions.NewPlayerId();
            var room = await lobby.CreateAsync(guest, ["white", "black"], "white");
            var key = "shirokuro:guest-expiry:{" + ns + "}:v1:pending";

            // Protected by the correct keyring, but payload deliberately
            // fails strict UTC-ticks/guest validation.
            var malicious = provider
                .CreateProtector("Shirokuro.GuestExpiryIndex.v1")
                .Protect("invalid-timestamp:" + guest);
            Assert.DoesNotContain(guest, malicious, StringComparison.Ordinal);
            await redis.GetDatabase().SortedSetAddAsync(key, malicious,
                clock.GetUtcNow().ToUnixTimeMilliseconds() - 1_000);
            await index.ScheduleRevokedAsync(guest);

            var read = await index.ReadDueAsync();
            Assert.Equal(2, read.Count);
            Assert.Contains(read, x => x.IsUnreadable && x.PlayerId.Length == 0
                && x.ProtectedMember == malicious);
            Assert.Contains(read, x => !x.IsUnreadable && x.PlayerId == guest);
            using var worker = Worker(clock, lobby, index, store);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => worker.SweepOnceAsync());
            Assert.Null(await store.ReadAsync(room.LobbyId));
            var remaining = await redis.GetDatabase().SortedSetRangeByRankAsync(key);
            Assert.Equal(malicious, Assert.Single(remaining).ToString());

            // No due entries: cursor is deleted, but future scheduled work
            // is untouched and the queue itself is not deleted.
            await index.AcknowledgeAsync(malicious);
            Assert.Empty(await index.ReadDueAsync());
            Assert.False(await redis.GetDatabase().KeyExistsAsync(
                "shirokuro:guest-expiry:{" + ns + "}:v1:scan-offset"));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
