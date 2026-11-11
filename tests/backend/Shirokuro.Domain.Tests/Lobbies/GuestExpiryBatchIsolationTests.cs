using System.IO;
using System.Threading;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Fault injection for one guest must not stop independent due entries in
/// the same encrypted expiry batch. Successful items ack exactly once;
/// failed items stay durable and are retried on the next worker pass.
/// </summary>
public sealed class GuestExpiryBatchIsolationTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class Index : IGuestExpiryIndex
    {
        private readonly Dictionary<string, string> _due = new();
        private int _next;
        public string? FailAckFor { get; set; }
        public bool FailRead { get; set; }
        public int AckCount { get; private set; }

        public IReadOnlyList<DueGuestExpiry> Remaining =>
            _due.Select(x => new DueGuestExpiry(x.Key, x.Value)).ToArray();

        public Task ScheduleAsync(string id, DateTimeOffset due,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _due.Add("opaque-" + Interlocked.Increment(ref _next), id);
            return Task.CompletedTask;
        }

        public Task<string> ScheduleRevokedAsync(string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = "opaque-" + Interlocked.Increment(ref _next);
            _due.Add(member, id);
            return Task.FromResult(member);
        }

        public Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailRead)
                throw new IOException("private-broken-decryption-key");
            return Task.FromResult(Remaining);
        }

        public Task AcknowledgeAsync(string member,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_due.TryGetValue(member, out var id) && id == FailAckFor)
                throw new IOException("secret-ack-failure");
            Assert.True(_due.Remove(member));
            AckCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class Fence : IGuestRevocationFence
    {
        public string? FailFor { get; set; }
        public List<string> Calls { get; } = new();

        public Task FenceGuestAsync(string id,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(id);
            if (id == FailFor)
                throw new IOException("secret-fence-identity-" + id);
            return Task.CompletedTask;
        }
    }

    private sealed class SelectiveLobby(IAsyncGuestLobby? real = null) : IAsyncGuestLobby
    {
        public string? FailFor { get; set; }
        public CancellationTokenSource? CancelAfterFirst { get; set; }
        public List<string> Calls { get; } = new();

        public async Task<int> RemovePlayerFromAllRoomsAsync(string id,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(id);
            if (id == FailFor)
                throw new IOException("secret-lobby-id-" + id);

            if (CancelAfterFirst is not null)
            {
                CancelAfterFirst.Cancel();
                // Simulate a storage operation that completes just before
                // the request observes cancellation, without its ack.
            }

            return real is null ? 1
                : await real.RemovePlayerFromAllRoomsAsync(id, token);
        }

        public Task<LobbyRosterView> CreateAsync(string id, IReadOnlyList<string>? teams,
            string? team, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> JoinAsync(string room, string id, string? team,
            long? revision, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChooseHeroAsync(string room, string id,
            string? hero, long revision, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChangeTeamAsync(string room, string id,
            string? team, long revision, CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task LeaveAsync(string room, string id, long revision,
            CancellationToken token = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ForMemberAsync(string room, string id,
            CancellationToken token = default) =>
            throw new NotSupportedException();
    }

    private static GuestSessionExpiryWorker Worker(
        TimeProvider clock, Index index, IAsyncGuestLobby lobby, IGuestRevocationFence fence) =>
        new(new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
            clock, lobby, index, fence);

    [Fact]
    public async Task FailedCleanupKeepsOnlyBadItemWhileLaterGuestsAreProcessed()
    {
        var clock = new ManualClock();
        var bad = GuestSessions.NewPlayerId();
        var good = GuestSessions.NewPlayerId();
        var index = new Index();
        await index.ScheduleRevokedAsync(bad);
        await index.ScheduleRevokedAsync(good);
        var lobby = new SelectiveLobby { FailFor = bad };
        var fence = new Fence();
        using var capture = new MultiplayerMetricCapture();
        using var worker = Worker(clock, index, lobby, fence);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.SweepOnceAsync());
        Assert.Equal("Guest expiry maintenance has retryable items.", error.Message);
        Assert.DoesNotContain(bad, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Equal([bad, good], lobby.Calls);
        Assert.Equal([bad, good], fence.Calls);
        Assert.DoesNotContain(index.Remaining, x => x.PlayerId == good);
        Assert.Equal(bad, Assert.Single(index.Remaining).PlayerId);
        Assert.Equal(1, index.AckCount);
        Assert.Contains(capture.For(MultiplayerMetrics.ExpirySweepsName),
            x => x.Tags["mode"] == "redis" && x.Tags["outcome"] == "failure");
        // Memberships cleaned in a partially failed sweep must still be
        // counted once, never re-counted after the retry.
        Assert.Contains(capture.For(MultiplayerMetrics.ExpiryMembershipsRemovedName),
            x => x.Tags["mode"] == "redis" && x.Value == 1);

        lobby.FailFor = null;
        Assert.Equal(1, await worker.SweepOnceAsync());
        Assert.Empty(index.Remaining);
        Assert.Equal(2, index.AckCount);
        Assert.Equal([bad, good, bad], lobby.Calls);
    }

    [Fact]
    public async Task FailedFenceCannotAckBadGuestButDoesNotBlockHealthyGuest()
    {
        var clock = new ManualClock();
        var bad = GuestSessions.NewPlayerId();
        var good = GuestSessions.NewPlayerId();
        var index = new Index();
        await index.ScheduleRevokedAsync(bad);
        await index.ScheduleRevokedAsync(good);
        var fence = new Fence { FailFor = bad };
        var lobby = new SelectiveLobby();
        using var worker = Worker(clock, index, lobby, fence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SweepOnceAsync());
        Assert.Equal([good], lobby.Calls);
        Assert.Equal(bad, Assert.Single(index.Remaining).PlayerId);
        Assert.Equal(1, index.AckCount);
        fence.FailFor = null;
        Assert.Equal(1, await worker.SweepOnceAsync());
        Assert.Empty(index.Remaining);
    }

    [Fact]
    public async Task FailedAcknowledgementRetainsIdempotentRetryWithoutBlockingOtherItems()
    {
        var clock = new ManualClock();
        var bad = GuestSessions.NewPlayerId();
        var good = GuestSessions.NewPlayerId();
        var index = new Index { FailAckFor = bad };
        await index.ScheduleRevokedAsync(bad);
        await index.ScheduleRevokedAsync(good);
        var lobby = new SelectiveLobby();
        var fence = new Fence();
        using var worker = Worker(clock, index, lobby, fence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SweepOnceAsync());
        Assert.Equal([bad, good], lobby.Calls);
        Assert.Equal(bad, Assert.Single(index.Remaining).PlayerId);
        Assert.Equal(1, index.AckCount);
        index.FailAckFor = null;
        // Real Redis cleanup is idempotent; the fixture here records the
        // repeated administrative invocation but does not mutate game state.
        Assert.Equal(1, await worker.SweepOnceAsync());
        Assert.Empty(index.Remaining);
        Assert.Equal([bad, good, bad], lobby.Calls);
    }

    [Fact]
    public async Task HostCancellationStopsBatchWithoutSwallowingOrAcknowledging()
    {
        var clock = new ManualClock();
        var first = GuestSessions.NewPlayerId();
        var second = GuestSessions.NewPlayerId();
        var index = new Index();
        await index.ScheduleRevokedAsync(first);
        await index.ScheduleRevokedAsync(second);
        using var cancel = new CancellationTokenSource();
        var lobby = new SelectiveLobby { CancelAfterFirst = cancel };
        using var worker = Worker(clock, index, lobby, new Fence());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.SweepOnceAsync(cancel.Token));
        Assert.Equal([first], lobby.Calls);
        Assert.Equal(0, index.AckCount);
        Assert.Equal(2, index.Remaining.Count);
    }

    [Fact]
    public async Task FailedEncryptedIndexReadFailsClosedBeforeAnyCleanupOrAck()
    {
        var clock = new ManualClock();
        var index = new Index { FailRead = true };
        await index.ScheduleRevokedAsync(GuestSessions.NewPlayerId());
        var lobby = new SelectiveLobby();
        using var worker = Worker(clock, index, lobby, new Fence());
        using var capture = new MultiplayerMetricCapture();

        await Assert.ThrowsAsync<IOException>(() => worker.SweepOnceAsync());
        Assert.Empty(lobby.Calls);
        Assert.Equal(0, index.AckCount);
        Assert.Single(index.Remaining);
        Assert.Contains(capture.For(MultiplayerMetrics.ExpirySweepsName),
            x => x.Tags["mode"] == "redis" && x.Tags["outcome"] == "failure");
    }

    [Fact]
    public async Task RealRedisTwoNodesContinuePastOneBadGuestAndRetryWithoutTtlRefresh()
    {
        var endpoint = Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        using var a = await ConnectionMultiplexer.ConnectAsync(endpoint);
        using var b = await ConnectionMultiplexer.ConnectAsync(endpoint);
        // Both independent Redis clients use a shared Data Protection key
        // provider, as with the actual encrypted cross-node expiry queue.
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Shirokuro");
        using var provider = services.BuildServiceProvider();
        var protection = provider.GetRequiredService<IDataProtectionProvider>();
        var clock = new ManualClock();
        var ns = "batch-" + Guid.NewGuid().ToString("N");
        var issuerIndex = new RedisGuestExpiryIndex(a.GetDatabase(), protection, clock, ns);
        var otherIndex = new RedisGuestExpiryIndex(b.GetDatabase(), protection, clock, ns);
        var storage = new RedisLobbySnapshotStore(b.GetDatabase(), clock, namespaceName: ns);
        var runtime = new RedisGuestLobbyRuntime(storage, clock);
        var bad = GuestSessions.NewPlayerId();
        var good = GuestSessions.NewPlayerId();
        var survivor = GuestSessions.NewPlayerId();

        var badRoom = await runtime.CreateAsync(bad, ["white", "black"], "white");
        await runtime.JoinAsync(badRoom.LobbyId, survivor, "black", badRoom.Revision);
        var goodRoom = await runtime.CreateAsync(good, ["white", "black"], "white");
        await runtime.JoinAsync(goodRoom.LobbyId, survivor, "black", goodRoom.Revision);
        var beforeGood = (await storage.ReadAsync(goodRoom.LobbyId))!.Snapshot.LastActivityUtc;

        await issuerIndex.ScheduleRevokedAsync(bad);
        await issuerIndex.ScheduleRevokedAsync(good);
        var blocked = new SelectiveLobby(runtime) { FailFor = bad };
        using var firstWorker = new GuestSessionExpiryWorker(
            new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
            clock, blocked, otherIndex, storage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => firstWorker.SweepOnceAsync());
        Assert.Equal(bad, Assert.Single(await issuerIndex.ReadDueAsync()).PlayerId);
        var otherAfterFailure = (await storage.ReadAsync(goodRoom.LobbyId))!.Snapshot;
        Assert.Equal(survivor, Assert.Single(otherAfterFailure.State.Participants).PlayerId);
        Assert.Equal(beforeGood, otherAfterFailure.LastActivityUtc);
        Assert.Equal(2, (await storage.ReadAsync(badRoom.LobbyId))!.Snapshot
            .State.Participants.Count);

        using var recoveryWorker = new GuestSessionExpiryWorker(
            new GuestSessionRegistry(clock), new InMemoryLobbyCoordinator(clock),
            clock, runtime, issuerIndex, storage);
        Assert.Equal(1, await recoveryWorker.SweepOnceAsync());
        Assert.Equal(0, await recoveryWorker.SweepOnceAsync());
        Assert.Empty(await otherIndex.ReadDueAsync());
        var afterBad = (await storage.ReadAsync(badRoom.LobbyId))!.Snapshot;
        Assert.Equal(survivor, Assert.Single(afterBad.State.Participants).PlayerId);
        Assert.Equal((await storage.ReadAsync(goodRoom.LobbyId))!.Snapshot.LastActivityUtc,
            beforeGood);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            runtime.JoinAsync(badRoom.LobbyId, bad, "white", null));
    }
}
