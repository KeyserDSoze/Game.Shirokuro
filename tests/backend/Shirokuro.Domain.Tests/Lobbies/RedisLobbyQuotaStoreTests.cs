using System.Text;
using Shirokuro.Api.Lobbies;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class RedisLobbyQuotaStoreTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now = _now.Add(d);
    }

    private static string? Address =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private static async Task<(ConnectionMultiplexer A, ConnectionMultiplexer B)> Connect()
    {
        if (string.IsNullOrWhiteSpace(Address))
            throw new InvalidOperationException("Real Redis tests require SHIROKURO_TEST_REDIS.");
        return (await ConnectionMultiplexer.ConnectAsync(Address),
                await ConnectionMultiplexer.ConnectAsync(Address));
    }

    private static LobbySnapshot Room(
        string id, string creator, DateTimeOffset lastActivity)
    {
        var state = LobbyState.Create(new LobbyDefinition(
            id, ["white", "black"], ["kira", "krugg"]))
            .Join(creator, "white", 0);
        return new LobbySnapshot(state, lastActivity, creator);
    }

    private static RedisLobbySnapshotStore Store(
        ConnectionMultiplexer redis, TimeProvider clock, string ns,
        TimeSpan? timeout = null) =>
        new(redis.GetDatabase(), clock, timeout, ns);

    [Fact]
    public async Task RedisGlobalAndOwnerLimitsAreAtomicAcrossTwoIndependentClients()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var ns = "quota-" + Guid.NewGuid().ToString("N");
            var one = Store(a, clock, ns);
            var two = Store(b, clock, ns);
            LobbySnapshot Fresh(string creator) =>
                Room("room-" + Guid.NewGuid().ToString("N"), creator, clock.GetUtcNow());

            var alice1 = Fresh("guest-alice");
            var alice2 = Fresh("guest-alice");
            Assert.True(await one.CreateBoundedAsync(alice1, 3, 2));
            Assert.True(await two.CreateBoundedAsync(alice2, 3, 2));
            await Assert.ThrowsAsync<LobbyCapacityException>(() =>
                two.CreateBoundedAsync(Fresh("guest-alice"), 3, 2));

            var bob1 = Fresh("guest-bob");
            Assert.True(await two.CreateBoundedAsync(bob1, 3, 2));
            await Assert.ThrowsAsync<LobbyCapacityException>(() =>
                one.CreateBoundedAsync(Fresh("guest-bob"), 3, 2));

            // The same creator still occupies their original quota after a
            // failed global-capacity insertion. Only a current delete frees it.
            var room1 = (await two.ReadAsync(alice1.State.Definition.LobbyId))!;
            Assert.True(room1.Snapshot.QuotaTracked);
            Assert.False(await two.CreateBoundedAsync(alice1, 3, 2));
            Assert.True(await two.DeleteIfCurrentAsync(room1));
            Assert.True(await one.CreateBoundedAsync(Fresh("guest-bob"), 3, 2));
            await Assert.ThrowsAsync<LobbyCapacityException>(() =>
                one.CreateBoundedAsync(Fresh("guest-alice"), 3, 2));
        }
    }

    [Fact]
    public async Task ParallelRoomCreationsCannotExceedAnyQuota()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var ns = "race-" + Guid.NewGuid().ToString("N");
            var stores = new[] { Store(a, clock, ns), Store(b, clock, ns) };
            async Task<bool> TryCreate(int i, string creator, int maxRooms, int maxByOwner)
            {
                try
                {
                    return await stores[i % 2].CreateBoundedAsync(
                        Room("room-" + Guid.NewGuid().ToString("N"),
                            creator, clock.GetUtcNow()), maxRooms, maxByOwner);
                }
                catch (LobbyCapacityException)
                {
                    return false;
                }
            }

            var sameOwner = await Task.WhenAll(Enumerable.Range(0, 48)
                .Select(i => TryCreate(i, "guest-one", 6, 4)));
            Assert.Equal(4, sameOwner.Count(x => x));

            var secondOwner = await Task.WhenAll(Enumerable.Range(0, 48)
                .Select(i => TryCreate(i, "guest-two", 6, 4)));
            Assert.Equal(2, secondOwner.Count(x => x));

            Assert.False(await TryCreate(0, "guest-three", 6, 4));
        }
    }

    [Fact]
    public async Task OwnerLeavingDoesNotFreeCreatorQuotaUntilRoomIsDeleted()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var ns = "owner-" + Guid.NewGuid().ToString("N");
            var one = Store(a, clock, ns);
            var two = Store(b, clock, ns);
            var first = Room("room-" + Guid.NewGuid().ToString("N"),
                "guest-alice", clock.GetUtcNow());
            Assert.True(await one.CreateBoundedAsync(first, 3, 1));
            var read = (await two.ReadAsync(first.State.Definition.LobbyId))!;
            var withBob = read.Snapshot.State.Join("guest-bob",
                "black", read.Snapshot.State.Revision);
            Assert.True(await two.CompareExchangeAsync(read,
                read.Snapshot with { State = withBob }));
            var joined = (await one.ReadAsync(first.State.Definition.LobbyId))!;
            var withoutOwner = joined.Snapshot.State.Leave(
                "guest-alice", joined.Snapshot.State.Revision);
            Assert.True(await one.CompareExchangeAsync(joined,
                joined.Snapshot with { State = withoutOwner }));
            Assert.False(await one.DeleteIfCurrentAsync(read));

            var candidate = Room("room-" + Guid.NewGuid().ToString("N"),
                "guest-alice", clock.GetUtcNow());
            await Assert.ThrowsAsync<LobbyCapacityException>(() =>
                two.CreateBoundedAsync(candidate, 3, 1));
            var current = (await two.ReadAsync(first.State.Definition.LobbyId))!;
            Assert.Equal("guest-alice", current.Snapshot.CreatorId);
            Assert.Equal("guest-bob", Assert.Single(current.Snapshot.State.Participants).PlayerId);
            Assert.True(await two.DeleteIfCurrentAsync(current));
            Assert.True(await one.CreateBoundedAsync(candidate, 3, 1));
        }
    }

    [Fact]
    public async Task HeartbeatUpdatesIndexedDeadlineAndRejectsOldSnapshot()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var ns = "heartbeat-" + Guid.NewGuid().ToString("N");
            var one = Store(a, clock, ns);
            var two = Store(b, clock, ns);
            var id = "room-" + Guid.NewGuid().ToString("N");
            var original = Room(id, "guest-alice", clock.GetUtcNow());
            Assert.True(await one.CreateBoundedAsync(original, 1, 1));
            var aRead = (await one.ReadAsync(id))!;
            var stale = (await two.ReadAsync(id))!;
            clock.Advance(TimeSpan.FromMinutes(10));
            Assert.True(await one.CompareExchangeAsync(aRead,
                aRead.Snapshot with { LastActivityUtc = clock.GetUtcNow() }));
            Assert.False(await two.DeleteIfCurrentAsync(stale));
            Assert.False(await two.CompareExchangeAsync(stale,
                stale.Snapshot with { LastActivityUtc = clock.GetUtcNow() }));
            await Assert.ThrowsAsync<LobbyCapacityException>(() =>
                one.CreateBoundedAsync(Room(
                    "room-" + Guid.NewGuid().ToString("N"),
                    "guest-bob", clock.GetUtcNow()), 1, 1));
        }
    }

    [Fact]
    public async Task ExpirationReclaimsBothQuotasWithoutSweepOrWorker()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var ns = "expiry-" + Guid.NewGuid().ToString("N");
            var timeout = TimeSpan.FromMilliseconds(300);
            var one = Store(a, clock, ns, timeout);
            var two = Store(b, clock, ns, timeout);
            var first = Room("room-" + Guid.NewGuid().ToString("N"),
                "guest-alice", clock.GetUtcNow());
            Assert.True(await one.CreateBoundedAsync(first, 1, 1));
            await Task.Delay(650); // Physical Redis TTL, no clock adjustment.
            Assert.Null(await two.ReadAsync(first.State.Definition.LobbyId));
            var newRoom = Room("room-" + Guid.NewGuid().ToString("N"),
                "guest-alice", clock.GetUtcNow());
            Assert.True(await two.CreateBoundedAsync(newRoom, 1, 1));
        }
    }

    [Fact]
    public void SnapshotVersionAndQuotaTrackingAreValidated()
    {
        var at = DateTimeOffset.UtcNow;
        var room = Room("room-opaque", "guest-signer", at);
        var encoded = RedisLobbySnapshotCodec.Encode(room with { QuotaTracked = true });
        var restored = RedisLobbySnapshotCodec.Decode(encoded,
            room.State.Definition.LobbyId);
        Assert.True(restored.QuotaTracked);
        Assert.Equal("guest-signer", restored.CreatorId);
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Encode(room with
            {
                QuotaTracked = true,
                CreatorId = null
            }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../bad")]
    [InlineData("hello:{other}")]
    public void UntrustedRedisNamespaceIsRejected(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            new RedisLobbySnapshotStore(null!, TimeProvider.System,
                namespaceName: name));
    }
}
