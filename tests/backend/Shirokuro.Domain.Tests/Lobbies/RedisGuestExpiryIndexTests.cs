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
                clock, runtime, workerIndex, store);
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
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                runtime.JoinAsync(shared.LobbyId, alice, "white", null));
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
            await Assert.ThrowsAsync<InvalidOperationException>(() => failedWorker.SweepOnceAsync());
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
    public async Task RevokedSessionSchedulesImmediateEncryptedCrossNodeCleanup()
    {
        if (RedisAddress is null) return;
        using var issuerRedis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var recoveryRedis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var directory = Path.Combine(Path.GetTempPath(), "shirokuro-revoked-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var issuerKeys = KeyRing(directory);
            using var recoveryKeys = KeyRing(directory);
            var clock = new ManualClock();
            var ns = "revoke-" + Guid.NewGuid().ToString("N");
            var issuerIndex = new RedisGuestExpiryIndex(
                issuerRedis.GetDatabase(),
                issuerKeys.GetRequiredService<IDataProtectionProvider>(),
                clock, ns);
            var recoveryIndex = new RedisGuestExpiryIndex(
                recoveryRedis.GetDatabase(),
                recoveryKeys.GetRequiredService<IDataProtectionProvider>(),
                clock, ns);
            var alice = GuestSessions.NewPlayerId();
            var bob = GuestSessions.NewPlayerId();

            // The original natural expiry remains in the queue: immediate
            // revocation adds a separate already-due encrypted record.
            await issuerIndex.ScheduleAsync(alice, clock.GetUtcNow().AddHours(8));
            Assert.Empty(await recoveryIndex.ReadDueAsync());

            var storage = new RedisLobbySnapshotStore(recoveryRedis.GetDatabase(),
                clock, namespaceName: ns);
            var lobby = new RedisGuestLobbyRuntime(storage, clock);
            var room = await lobby.CreateAsync(alice,
                ["white", "black"], "white");
            await lobby.JoinAsync(room.LobbyId, bob, "black", room.Revision);
            var before = (await storage.ReadAsync(room.LobbyId))!.Snapshot;

            // The caller may enqueue ONLY after revoking the ticket.
            // Redis sees an opaque member and a due score, not a guest ID.
            var opaque = await issuerIndex.ScheduleRevokedAsync(alice);
            Assert.DoesNotContain(alice, opaque, StringComparison.Ordinal);
            Assert.DoesNotContain("guest-", opaque, StringComparison.Ordinal);
            var members = await issuerRedis.GetDatabase().SortedSetRangeByRankAsync(
                "shirokuro:guest-expiry:{" + ns + "}:v1:pending");
            Assert.Equal(2, members.Length);
            Assert.All(members, member =>
                Assert.DoesNotContain(alice, member.ToString(), StringComparison.Ordinal));
            var due = Assert.Single(await recoveryIndex.ReadDueAsync());
            Assert.Equal(opaque, due.ProtectedMember);
            Assert.Equal(alice, due.PlayerId);

            // Issuer dies or its synchronous lobby cleanup fails.
            var brokenWorker = new GuestSessionExpiryWorker(
                new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
                clock, new FailingLobby(), issuerIndex);
            await Assert.ThrowsAsync<InvalidOperationException>(() => brokenWorker.SweepOnceAsync());
            Assert.Single(await recoveryIndex.ReadDueAsync());

            var recoveredWorker = new GuestSessionExpiryWorker(
                new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
                clock, lobby, recoveryIndex);
            Assert.Equal(1, await recoveredWorker.SweepOnceAsync());
            Assert.Equal(0, await recoveredWorker.SweepOnceAsync());
            Assert.Empty(await issuerIndex.ReadDueAsync()); // only 8h item remains

            var after = (await storage.ReadAsync(room.LobbyId))!.Snapshot;
            Assert.Equal(before.State.Revision + 1, after.State.Revision);
            Assert.Equal(bob, Assert.Single(after.State.Participants).PlayerId);
            Assert.Equal(before.LastActivityUtc, after.LastActivityUtc);
            Assert.Equal(1, (await issuerRedis.GetDatabase().SortedSetLengthAsync(
                "shirokuro:guest-expiry:{" + ns + "}:v1:pending")));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task RevokedCleanupRejectsForgedIdWithoutWritingToRedis()
    {
        if (RedisAddress is null) return;
        using var connection = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var services = new ServiceCollection();
        services.AddDataProtection();
        using var provider = services.BuildServiceProvider();
        var clock = new ManualClock();
        var ns = "rev-" + Guid.NewGuid().ToString("N");
        var index = new RedisGuestExpiryIndex(connection.GetDatabase(),
            provider.GetRequiredService<IDataProtectionProvider>(), clock, ns);
        await Assert.ThrowsAsync<ArgumentException>(
            () => index.ScheduleRevokedAsync("attacker-raw-client-id"));
        Assert.False(await connection.GetDatabase().KeyExistsAsync(
            "shirokuro:guest-expiry:{" + ns + "}:v1:pending"));
    }

    [Fact]
    public async Task RecoveryCutoffFencesOldMembershipEarlyButNeverRemovesNewGuestOrExtendsRoomTtl()
    {
        if (RedisAddress is null) return;
        using var issuerRedis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var recoveryRedis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var directory = Path.Combine(Path.GetTempPath(), "shirokuro-recovery-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var issuerKeys = KeyRing(directory);
            using var recoveryKeys = KeyRing(directory);
            var clock = new ManualClock();
            var cutoff = clock.GetUtcNow().AddMinutes(2);
            var ns = "recovery-" + Guid.NewGuid().ToString("N");
            var issuer = new RedisGuestExpiryIndex(
                issuerRedis.GetDatabase(),
                issuerKeys.GetRequiredService<IDataProtectionProvider>(), clock, ns);
            var recovery = new RedisGuestExpiryIndex(
                recoveryRedis.GetDatabase(),
                recoveryKeys.GetRequiredService<IDataProtectionProvider>(), clock, ns,
                recoveryCutoffUtc: cutoff);
            var oldGuest = GuestSessions.NewPlayerId();
            var freshGuest = GuestSessions.NewPlayerId();
            var oldExpiry = clock.GetUtcNow().AddHours(8);
            await issuer.ScheduleAsync(oldGuest, oldExpiry);
            var store = new RedisLobbySnapshotStore(
                recoveryRedis.GetDatabase(), clock, namespaceName: ns);
            var lobby = new RedisGuestLobbyRuntime(store, clock);
            var roster = await lobby.CreateAsync(oldGuest,
                ["white", "black"], "white");

            // An issuance strictly AFTER cutover has a natural expiry
            // beyond cutoff+8h. An older natural expiry is *not yet due*
            // in normal mode, but reconciliation treats it as due.
            clock.Advance(TimeSpan.FromMinutes(3));
            await issuer.ScheduleAsync(freshGuest, clock.GetUtcNow().AddHours(8));
            await lobby.JoinAsync(roster.LobbyId, freshGuest, "black", roster.Revision);
            var before = (await store.ReadAsync(roster.LobbyId))!.Snapshot;
            Assert.Empty(await issuer.ReadDueAsync());
            var early = Assert.Single(await recovery.ReadDueAsync());
            Assert.Equal(oldGuest, early.PlayerId);

            var worker = new GuestSessionExpiryWorker(
                new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
                clock, lobby, recovery, store);
            Assert.Equal(1, await worker.SweepOnceAsync());
            Assert.Equal(0, await worker.SweepOnceAsync());
            var after = (await store.ReadAsync(roster.LobbyId))!.Snapshot;
            Assert.Equal(before.State.Revision + 1, after.State.Revision);
            Assert.Equal(freshGuest, Assert.Single(after.State.Participants).PlayerId);
            Assert.Equal(before.LastActivityUtc, after.LastActivityUtc);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                lobby.JoinAsync(roster.LobbyId, oldGuest, "white", null));
            Assert.Empty(await issuer.ReadDueAsync());
            var key = "shirokuro:guest-expiry:{" + ns + "}:v1:pending";
            var remaining = Assert.Single(await issuerRedis.GetDatabase()
                .SortedSetRangeByRankAsync(key)).ToString();
            Assert.DoesNotContain(oldGuest, remaining, StringComparison.Ordinal);

            // Deliberately tamper with the ZSET score for the healthy
            // future entry: Redis scores alone may not authorize cleanup.
            // Its authenticated encrypted natural expiry still lies
            // after cutoff+8h and therefore MUST NOT be acknowledged.
            await issuerRedis.GetDatabase().SortedSetAddAsync(
                key, remaining, cutoff.ToUnixTimeMilliseconds());
            Assert.Empty(await recovery.ReadDueAsync());
            Assert.Equal(0, await worker.SweepOnceAsync());
            Assert.NotNull(await store.ReadAsync(roster.LobbyId));
            Assert.Equal(1, await recoveryRedis.GetDatabase().SortedSetLengthAsync(key));
            Assert.Equal(freshGuest,
                Assert.Single((await store.ReadAsync(roster.LobbyId))!
                    .Snapshot.State.Participants).PlayerId);
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
