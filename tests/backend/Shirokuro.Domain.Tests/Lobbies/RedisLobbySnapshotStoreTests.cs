using System.Text;
using System.Text.Json.Nodes;
using Shirokuro.Api.Lobbies;
using Shirokuro.Domain.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class RedisLobbySnapshotStoreTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private static LobbySnapshot Room(string id, DateTimeOffset utc, string owner = "alice")
    {
        var state = LobbyState.Create(new LobbyDefinition(
            id, ["white", "black"], ["kira", "krugg"])).Join(owner, "white", 0);
        return new LobbySnapshot(state, utc, owner);
    }

    [Fact]
    public void VersionedSnapshotRoundTripsAllFieldsAndImmutableRevision()
    {
        var id = "room-opaque-123";
        var time = DateTimeOffset.Parse("2026-11-10T00:00:00Z");
        var initial = Room(id, time);
        var joined = initial.State.Join("bob", "black", initial.State.Revision)
            .ChooseHero("bob", "kira", 2);
        var snapshot = initial with { State = joined };
        var encoded = RedisLobbySnapshotCodec.Encode(snapshot);
        var restored = RedisLobbySnapshotCodec.Decode(encoded, id);

        Assert.Equal(id, restored.State.Definition.LobbyId);
        Assert.Equal(3, restored.State.Revision);
        Assert.Equal(["white", "black"], restored.State.Definition.TeamIds);
        Assert.Equal(["kira", "krugg"], restored.State.Definition.HeroIds);
        Assert.Equal("alice", restored.CreatorId);
        Assert.Equal(time, restored.LastActivityUtc);
        Assert.Equal(joined.Participants, restored.State.Participants);
        Assert.Throws<InvalidOperationException>(() =>
            restored.State.Join("bob", "white", 3));
    }

    [Fact]
    public void TamperedUnknownVersionIdentityAndInvalidRosterAreRejected()
    {
        var bytes = RedisLobbySnapshotCodec.Encode(
            Room("room-opaque-abc", DateTimeOffset.UtcNow));

        byte[] Change(string field, object? value)
        {
            var obj = JsonNode.Parse(bytes)!.AsObject();
            obj[field] = JsonSerializerNode(value);
            return Encoding.UTF8.GetBytes(obj.ToJsonString());
        }

        static JsonNode? JsonSerializerNode(object? value)
            => value is null ? null : JsonNode.Parse(
                System.Text.Json.JsonSerializer.Serialize(value));

        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(Change("Version", 99), "room-opaque-abc"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(bytes, "other-invite"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(Change("Revision", -1), "room-opaque-abc"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(Change("TeamIds", new[] { "white" }),
                "room-opaque-abc"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(Change("Participants", new[]
            {
                new { PlayerId = "alice", TeamId = "unknown", HeroId = (string?)null }
            }), "room-opaque-abc"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(Change("LastActivityUtcTicks", 0L),
                "room-opaque-abc"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode([], "room-opaque-abc"));
        Assert.Throws<InvalidDataException>(() =>
            RedisLobbySnapshotCodec.Decode(new byte[RedisLobbySnapshotCodec.MaxBytes + 1],
                "room-opaque-abc"));
    }

    [Fact]
    public void RestoredDomainMustRejectDuplicateActorsAndInvalidHeroes()
    {
        var definition = new LobbyDefinition("opaque", ["white", "black"], ["kira"]);
        Assert.Throws<ArgumentException>(() => LobbyState.Restore(
            definition, 2, [
                new LobbyParticipant("alice", "white", null),
                new LobbyParticipant("alice", "black", null)
            ]));
        Assert.Throws<ArgumentException>(() => LobbyState.Restore(
            definition, 1, [new LobbyParticipant("alice", "white", "unlisted")]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LobbyState.Restore(definition, -1, []));
    }

    private static string? RedisConnection() =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private static async Task<(ConnectionMultiplexer A, ConnectionMultiplexer B)> Connect()
    {
        var address = RedisConnection();
        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidOperationException(
                "The live Redis integration suite requires SHIROKURO_TEST_REDIS.");

        var a = await ConnectionMultiplexer.ConnectAsync(address);
        var b = await ConnectionMultiplexer.ConnectAsync(address);
        return (a, b);
    }

    [Fact]
    public async Task RedisCasCreateAndConcurrentUpdatesAreAtomicBetweenTwoClients()
    {
        if (RedisConnection() is null) return; // Local runs without Redis; CI always sets it.
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var one = new RedisLobbySnapshotStore(a.GetDatabase(), clock);
            var two = new RedisLobbySnapshotStore(b.GetDatabase(), clock);
            var id = "room-" + Guid.NewGuid().ToString("N");
            var snapshot = Room(id, clock.GetUtcNow());
            Assert.True(await one.CreateAsync(snapshot));
            Assert.False(await two.CreateAsync(snapshot));
            var loaded = (await two.ReadAsync(id))!;
            Assert.Equal(1, loaded.Snapshot.State.Revision);
            var results = await Task.WhenAll(Enumerable.Range(0, 48).Select(i =>
            {
                var state = loaded.Snapshot.State.Join("guest-" + i,
                    "black", loaded.Snapshot.State.Revision);
                return (i % 2 == 0 ? one : two).CompareExchangeAsync(
                    loaded, loaded.Snapshot with { State = state });
            }));
            Assert.Equal(1, results.Count(x => x));
            var latest = (await one.ReadAsync(id))!;
            Assert.Equal(2, latest.Snapshot.State.Revision);
            Assert.Equal(2, latest.Snapshot.State.Participants.Count);
            Assert.False(await two.DeleteIfCurrentAsync(loaded));
            Assert.True(await two.DeleteIfCurrentAsync(latest));
            Assert.Null(await one.ReadAsync(id));
        }
    }

    [Fact]
    public async Task RedisHeartbeatAtSameRevisionRejectsStaleCasAndNeverRevivesExpiredRoom()
    {
        if (RedisConnection() is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var clock = new ManualClock();
            var timeout = TimeSpan.FromMinutes(30);
            var one = new RedisLobbySnapshotStore(a.GetDatabase(), clock, timeout);
            var two = new RedisLobbySnapshotStore(b.GetDatabase(), clock, timeout);
            var id = "room-" + Guid.NewGuid().ToString("N");
            var first = Room(id, clock.GetUtcNow());
            Assert.True(await one.CreateAsync(first));

            var aRead = (await one.ReadAsync(id))!;
            var bRead = (await two.ReadAsync(id))!;
            clock.Advance(TimeSpan.FromMinutes(20));
            var refreshed = aRead.Snapshot with { LastActivityUtc = clock.GetUtcNow() };
            Assert.True(await one.CompareExchangeAsync(aRead, refreshed));
            Assert.False(await two.CompareExchangeAsync(bRead,
                bRead.Snapshot with { LastActivityUtc = clock.GetUtcNow() }));
            Assert.Equal(1, (await two.ReadAsync(id))!.Snapshot.State.Revision);
            clock.Advance(TimeSpan.FromMinutes(30));
            Assert.Null(await one.ReadAsync(id));
            Assert.False(await one.CompareExchangeAsync(aRead, refreshed));
            Assert.False(await two.DeleteIfCurrentAsync(aRead));
        }
    }

    [Fact]
    public async Task RedisPhysicalTtlExpiresWithoutBackgroundWorkerOrReads()
    {
        if (RedisConnection() is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var timeout = TimeSpan.FromMilliseconds(300);
            var one = new RedisLobbySnapshotStore(a.GetDatabase(), TimeProvider.System, timeout);
            var two = new RedisLobbySnapshotStore(b.GetDatabase(), TimeProvider.System, timeout);
            var id = "room-" + Guid.NewGuid().ToString("N");
            Assert.True(await one.CreateAsync(Room(id, DateTimeOffset.UtcNow)));
            Assert.NotNull(await two.ReadAsync(id));
            await Task.Delay(600);
            Assert.Null(await one.ReadAsync(id));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                two.CreateAsync(Room(id, DateTimeOffset.UtcNow.AddMinutes(-1))));
            Assert.True(await two.CreateAsync(Room(id, DateTimeOffset.UtcNow)));
            var current = (await one.ReadAsync(id))!;
            Assert.True(await one.DeleteIfCurrentAsync(current));
        }
    }
}
