using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Fake Redis calls never complete, proving bounded waits and propagation of
/// an already-supplied cancellation token without a real network dependency.
/// </summary>
public sealed class RedisLobbyCommandTimeoutTests
{
    public class NeverRespondingDatabase : DispatchProxy
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<RedisResult> Lua { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RedisValue> Get { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Set { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RedisValue[]> Scan { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Interlocked.Increment(ref _calls);
            return method?.Name switch
            {
                "ScriptEvaluateAsync" => Lua.Task,
                "StringGetAsync" => Get.Task,
                "StringSetAsync" => Set.Task,
                "SortedSetRangeByRankAsync" => Scan.Task,
                _ => throw new InvalidOperationException(
                    "Unexpected test Redis operation.")
            };
        }

        public bool NoRedisCompletion =>
            !Lua.Task.IsCompleted && !Get.Task.IsCompleted &&
            !Set.Task.IsCompleted && !Scan.Task.IsCompleted;
    }

    public class RepeatedlyContendedRosterDatabase : DispatchProxy
    {
        public byte[] EncodedSnapshot { get; set; } = [];
        public int ConflictsBeforeSuccess { get; set; } = int.MaxValue;
        private int _reads;
        private int _casCalls;
        public int Reads => Volatile.Read(ref _reads);
        public int CasCalls => Volatile.Read(ref _casCalls);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name switch
            {
                "ScriptEvaluateAsync" => IsBoundedRead(args)
                    ? ReadSnapshot() : CompareExchange(),
                _ => throw new InvalidOperationException(
                    "Unexpected Redis operation in contention fixture.")
            };
        }

        private static bool IsBoundedRead(object?[]? args)
            => Assert.IsType<string>(args![0]).Contains(
                "if #raw > tonumber(ARGV[1])", StringComparison.Ordinal);

        private Task<RedisResult> ReadSnapshot()
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult(RedisResult.Create(
                (RedisValue)EncodedSnapshot, ResultType.BulkString));
        }

        private Task<RedisResult> CompareExchange()
        {
            var count = Interlocked.Increment(ref _casCalls);
            // Simulate an atomic Lua CAS defeated by another member read
            // heartbeat. No non-deterministic threads or production Redis
            // sleeps are involved in exercising the 32-attempt boundary.
            return Task.FromResult(RedisResult.Create(
                (RedisValue)(count <= ConflictsBeforeSuccess ? 0 : 1),
                ResultType.Integer));
        }
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public async Task RosterReadCasRetriesAreBoundedAndNeverReturnStaleData(
        int conflicts, bool shouldSucceed)
    {
        var database = DispatchProxy.Create<IDatabase, RepeatedlyContendedRosterDatabase>();
        var controller = (RepeatedlyContendedRosterDatabase)(object)database;
        var room = "room-" + Guid.NewGuid().ToString("N");
        var actor = GuestSessions.NewPlayerId();
        var state = LobbyState.Create(
            new LobbyDefinition(room, ["white", "black"], ["kira"]))
            .Join(actor, "white", 0);
        controller.EncodedSnapshot = RedisLobbySnapshotCodec.Encode(
            new LobbySnapshot(state, DateTimeOffset.UtcNow, actor, QuotaTracked: true));
        controller.ConflictsBeforeSuccess = conflicts;
        var clock = TimeProvider.System;
        var runtime = new RedisGuestLobbyRuntime(
            new RedisLobbySnapshotStore(database, clock), clock);

        if (shouldSucceed)
        {
            var verified = await runtime.ForMemberAsync(room, actor);
            Assert.Equal(room, verified.LobbyId);
            Assert.Single(verified.Participants);
            Assert.Equal(actor, verified.Participants[0].PlayerId);
        }
        else
        {
            var failed = await Assert.ThrowsAsync<LobbyReadContentionException>(
                () => runtime.ForMemberAsync(room, actor));
            Assert.DoesNotContain(room, failed.Message);
            Assert.DoesNotContain(actor, failed.Message);
        }

        Assert.Equal(32, controller.Reads);
        Assert.Equal(32, controller.CasCalls);
    }

    [Theory]
    [InlineData(31, true)]
    [InlineData(32, false)]
    public async Task UnversionedInvitationJoinCasRetriesReturnOnlyConfirmedMembership(
        int conflicts, bool shouldSucceed)
    {
        var database = DispatchProxy.Create<IDatabase, RepeatedlyContendedRosterDatabase>();
        var controller = (RepeatedlyContendedRosterDatabase)(object)database;
        var room = "room-" + Guid.NewGuid().ToString("N");
        var creator = GuestSessions.NewPlayerId();
        var joiner = GuestSessions.NewPlayerId();
        var state = LobbyState.Create(
            new LobbyDefinition(room, ["white", "black"], ["kira"]))
            .Join(creator, "white", 0);
        controller.EncodedSnapshot = RedisLobbySnapshotCodec.Encode(
            new LobbySnapshot(state, DateTimeOffset.UtcNow, creator, QuotaTracked: true));
        controller.ConflictsBeforeSuccess = conflicts;
        var clock = TimeProvider.System;
        var runtime = new RedisGuestLobbyRuntime(
            new RedisLobbySnapshotStore(database, clock), clock);

        if (shouldSucceed)
        {
            var roster = await runtime.JoinAsync(room, joiner, "black", null);
            Assert.Equal(2, roster.Revision);
            Assert.Equal(2, roster.Participants.Count);
            Assert.Contains(roster.Participants, p => p.PlayerId == joiner &&
                p.TeamId == "black");
        }
        else
        {
            var failure = await Assert.ThrowsAsync<LobbyJoinContentionException>(
                () => runtime.JoinAsync(room, joiner, "black", null));
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain(room, failure.Message);
            Assert.DoesNotContain(creator, failure.Message);
            Assert.DoesNotContain(joiner, failure.Message);
        }

        Assert.Equal(32, controller.Reads);
        Assert.Equal(32, controller.CasCalls);
    }

    [Fact]
    public async Task ProductionInvitationJoinContentionReturnsSanitized503ButRevisionConflictRemains409()
    {
        var database = DispatchProxy.Create<IDatabase, RepeatedlyContendedRosterDatabase>();
        var controller = (RepeatedlyContendedRosterDatabase)(object)database;
        var room = "room-" + Guid.NewGuid().ToString("N");
        var creator = GuestSessions.NewPlayerId();
        var state = LobbyState.Create(
            new LobbyDefinition(room, ["white", "black"], ["kira"]))
            .Join(creator, "white", 0);
        controller.EncodedSnapshot = RedisLobbySnapshotCodec.Encode(
            new LobbySnapshot(state, DateTimeOffset.UtcNow, creator, QuotaTracked: true));

        using var host = Production(database);
        using var visitor = Client(host);
        var cookie = await Login(visitor);
        using var guest = Client(host, cookie);

        using (var request = new HttpRequestMessage(HttpMethod.Post,
                   "/api/lobbies/" + room + "/join"))
        {
            request.Headers.Add("Origin", "https://game.example");
            request.Headers.Add("X-Shirokuro-Request", "1");
            request.Content = JsonContent.Create(new { teamId = "black" });
            using var response = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(room, body);
            Assert.DoesNotContain(creator, body);
            Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(32, controller.Reads);
        Assert.Equal(32, controller.CasCalls);

        using (var request = new HttpRequestMessage(HttpMethod.Post,
                   "/api/lobbies/" + room + "/join"))
        {
            request.Headers.Add("Origin", "https://game.example");
            request.Headers.Add("X-Shirokuro-Request", "1");
            request.Content = JsonContent.Create(new
                { teamId = "black", expectedRevision = 1L });
            using var response = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Contains("revision_conflict", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(34, controller.Reads);
        Assert.Equal(33, controller.CasCalls);
    }

    public class CorruptSnapshotDatabase : DispatchProxy
    {
        public byte[] Raw { get; set; } = [];
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public int Mutations { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "ScriptEvaluateAsync" &&
                Assert.IsType<string>(args![0]).Contains(
                    "if #raw > tonumber(ARGV[1])", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _reads);
                var values = Assert.IsType<RedisValue[]>(args[2]);
                Assert.Equal(RedisLobbySnapshotCodec.MaxBytes, (long)Assert.Single(values));
                // Emulate the server-side byte guard; no oversized reply
                // ever crosses the simulated Redis/.NET boundary.
                return Task.FromResult(RedisResult.Create(
                    (RedisValue)(Raw.Length > RedisLobbySnapshotCodec.MaxBytes
                        ? [] : Raw), ResultType.BulkString));
            }
            Mutations++;
            throw new InvalidOperationException(
                "The corrupt record must never trigger a Redis mutation.");
        }
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("bad-version")]
    [InlineData("oversized")]
    public async Task UndecodableRedisRoomFailsClosedWithoutChangingItsSnapshot(
        string failure)
    {
        var database = DispatchProxy.Create<IDatabase, CorruptSnapshotDatabase>();
        var controller = (CorruptSnapshotDatabase)(object)database;
        var roomId = "room-" + Guid.NewGuid().ToString("N");
        var playerId = GuestSessions.NewPlayerId();
        var valid = LobbyState.Create(
            new LobbyDefinition(roomId, ["white", "black"], ["kira"]))
            .Join(playerId, "white", 0);
        var encoded = RedisLobbySnapshotCodec.Encode(
            new LobbySnapshot(valid, DateTimeOffset.UtcNow, playerId, true));

        controller.Raw = failure switch
        {
            "truncated" => Encoding.UTF8.GetBytes("{\"Version\":2,\"LobbyId\":"),
            "oversized" => new byte[RedisLobbySnapshotCodec.MaxBytes + 1],
            "bad-version" => BadVersion(encoded),
            _ => throw new ArgumentException("Invalid test case.", nameof(failure))
        };

        static byte[] BadVersion(byte[] bytes)
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            json["Version"] = 999;
            return Encoding.UTF8.GetBytes(json.ToJsonString());
        }

        var clock = TimeProvider.System;
        var store = new RedisLobbySnapshotStore(database, clock);
        var error = await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.ReadAsync(roomId));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(roomId, error.Message);
        Assert.DoesNotContain(playerId, error.Message);
        Assert.DoesNotContain("Redis", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, controller.Reads);
        Assert.Equal(0, controller.Mutations);

        var runtime = new RedisGuestLobbyRuntime(store, clock);
        await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => runtime.ForMemberAsync(roomId, playerId));
        Assert.Equal(2, controller.Reads);
        Assert.Equal(0, controller.Mutations);
    }


    [Fact]
    public async Task BoundedSnapshotReadsDistinguishAbsentFromOversizedDataBeforeTransport()
    {
        var database = DispatchProxy.Create<IDatabase, CorruptSnapshotDatabase>();
        var fixture = (CorruptSnapshotDatabase)(object)database;
        var roomId = "room-" + Guid.NewGuid().ToString("N");
        var actor = GuestSessions.NewPlayerId();
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        fixture.Raw = new byte[RedisLobbySnapshotCodec.MaxBytes + 1];
        await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.ReadAsync(roomId));
        await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.ReadForGuestWithoutHeartbeatAsync(roomId, actor));
        Assert.Equal(2, fixture.Reads);
        Assert.Equal(0, fixture.Mutations);
    }

    [Fact]
    public async Task CorruptedRedisRoomReturnsUncached503OnReadAndAllMutations()
    {
        var database = DispatchProxy.Create<IDatabase, CorruptSnapshotDatabase>();
        var controller = (CorruptSnapshotDatabase)(object)database;
        controller.Raw = Encoding.UTF8.GetBytes("{not valid json}");
        using var host = Production(database);
        using var visitor = Client(host);
        var cookie = await Login(visitor);
        using var guest = Client(host, cookie);

        const string id = "room-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        using (var roster = await guest.GetAsync("/api/lobbies/" + id + "/roster"))
            AssertSanitized503(roster);

        foreach (var (method, suffix, body) in new[]
                 {
                     (HttpMethod.Post, "/join", (object)new
                         { teamId = "white", expectedRevision = 1 }),
                     (HttpMethod.Patch, "/hero", (object)new
                         { heroId = "kira", expectedRevision = 1 }),
                     (HttpMethod.Patch, "/team", (object)new
                         { teamId = "black", expectedRevision = 1 }),
                     (HttpMethod.Delete, "/membership", (object)new
                         { expectedRevision = 1 })
                 })
        {
            using var request = new HttpRequestMessage(
                method, "/api/lobbies/" + id + suffix);
            request.Headers.Add("Origin", "https://game.example");
            request.Headers.Add("X-Shirokuro-Request", "1");
            request.Content = JsonContent.Create(body);
            using var response = await guest.SendAsync(request);
            AssertSanitized503(response);
        }

        Assert.Equal(5, controller.Reads);
        Assert.Equal(0, controller.Mutations);

        static void AssertSanitized503(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    /// <summary>
    /// The capacity index is persisted Redis input, not permission to fetch
    /// keys in other keyspaces. No actual Redis commands are issued by tests.
    /// </summary>
    public class IndexedRoomDatabase : DispatchProxy
    {
        public RedisValue[] Members { get; set; } = [];
        public byte[]? Raw { get; set; }
        public Dictionary<string, byte[]?>? RawByKey { get; set; }
        public bool HoldReads { get; set; }
        public TaskCompletionSource<bool> InitialWindowFilled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReads { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlightReads;
        private int _maxInFlightReads;
        public int InFlightReads => Volatile.Read(ref _inFlightReads);
        public int MaxInFlightReads => Volatile.Read(ref _maxInFlightReads);
        public List<string> ReadKeys { get; } = [];
        public int Writes { get; private set; }
        public int ScanCalls { get; private set; }
        public long? ScanStart { get; private set; }
        public long? ScanStop { get; private set; }
        public long? MaxMemberBytes { get; private set; }
        // A fake server may violate the request's bound. The application
        // must still reject its oversized response before any room GET.
        public bool ReturnUnboundedResult { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            return method?.Name switch
            {
                "ScriptEvaluateAsync" => IsIndexScan(args)
                    ? Scan(args) : Read(args),
                _ => RejectWrite()
            };
        }

        private static bool IsIndexScan(object?[]? args)
            => Assert.IsType<string>(args![0]).Contains(
                "'ZRANGE', KEYS[1]", StringComparison.Ordinal);

        private Task<RedisResult> Scan(object?[]? args)
        {
            ScanCalls++;
            var script = Assert.IsType<string>(args![0]);
            Assert.Contains("'ZRANGE', KEYS[1], 0, tonumber(ARGV[1])",
                script, StringComparison.Ordinal);
            var keys = Assert.IsType<RedisKey[]>(args[1]);
            Assert.EndsWith(":capacity:all", Assert.Single(keys).ToString());
            var values = Assert.IsType<RedisValue[]>(args[2]);
            ScanStart = 0;
            ScanStop = (long)values[0];
            MaxMemberBytes = (long)values[1];
            var batch = ReturnUnboundedResult
                ? Members
                : Members.Take(checked((int)ScanStop.Value + 1)).ToArray();
            // Emulate the Lua-side byte bound before returning to .NET.
            var bounded = batch.Select(member =>
                Encoding.UTF8.GetByteCount(member.ToString()) > MaxMemberBytes.Value
                    ? (RedisValue)string.Empty : member).ToArray();
            return Task.FromResult(RedisResult.Create(bounded));
        }

        private async Task<RedisResult> Read(object?[]? args)
        {
            var script = Assert.IsType<string>(args![0]);
            Assert.Contains("if #raw > tonumber(ARGV[1])", script,
                StringComparison.Ordinal);
            var keys = Assert.IsType<RedisKey[]>(args[1]);
            var key = Assert.Single(keys).ToString();
            ReadKeys.Add(key);
            var limit = Assert.IsType<RedisValue[]>(args[2]);
            Assert.Equal(RedisLobbySnapshotCodec.MaxBytes, (long)Assert.Single(limit));
            var inFlight = Interlocked.Increment(ref _inFlightReads);
            // Concurrent reads race the maximum counter, not the key list.
            int previous;
            do
            {
                previous = Volatile.Read(ref _maxInFlightReads);
            } while (inFlight > previous &&
                     Interlocked.CompareExchange(
                         ref _maxInFlightReads, inFlight, previous) != previous);
            if (inFlight == 8)
                InitialWindowFilled.TrySetResult(true);
            try
            {
                if (HoldReads)
                    await ReleaseReads.Task;
                var bytes = RawByKey is null ? Raw :
                    RawByKey.TryGetValue(key, out var value) ? value : null;
                return bytes is null
                    ? RedisResult.Create(RedisValue.Null, ResultType.BulkString)
                    : RedisResult.Create((RedisValue)(bytes.Length >
                        RedisLobbySnapshotCodec.MaxBytes ? [] : bytes),
                        ResultType.BulkString);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightReads);
            }
        }

        private object RejectWrite()
        {
            Writes++;
            throw new InvalidOperationException(
                "Administrative index scan must never mutate Redis.");
        }
    }

    public static string IndexedKeyForRoom(string roomId) =>
        "shirokuro:lobby:{rooms}:v2:room:" +
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(roomId)));

    private static (string RoomId, string PlayerId, byte[] Encoded)
        ValidIndexedRoom()
    {
        var roomId = "room-" + Guid.NewGuid().ToString("N");
        var playerId = GuestSessions.NewPlayerId();
        var state = LobbyState.Create(
            new LobbyDefinition(roomId, ["white", "black"], ["kira"]))
            .Join(playerId, "white", 0);
        return (roomId, playerId, RedisLobbySnapshotCodec.Encode(
            new LobbySnapshot(state, DateTimeOffset.UtcNow,
                playerId, QuotaTracked: true)));
    }

    [Fact]
    public async Task AdministrativeScanIsBoundedEvenForAnEmptyIndex()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        Assert.Empty(await store.GetIndexedRoomsAsync());
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(0L, fixture.ScanStart);
        Assert.Equal(GuestLobbyCommands.MaxRooms, fixture.ScanStop);
        Assert.Equal(IndexedKeyForRoom("room-0").Length,
            fixture.MaxMemberBytes);
        Assert.Empty(fixture.ReadKeys);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task ExactlyMaxRoomsInIndexMayBeScannedWithoutTruncation()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        fixture.Members = Enumerable.Range(0, GuestLobbyCommands.MaxRooms)
            .Select(i => (RedisValue)IndexedKeyForRoom(
                "room-" + i.ToString("x32")))
            .ToArray();
        // The indexed keys may disappear physically just after ZRANGE.
        fixture.Raw = null;
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        Assert.Empty(await store.GetIndexedRoomsAsync());
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(0L, fixture.ScanStart);
        Assert.Equal(GuestLobbyCommands.MaxRooms, fixture.ScanStop);
        Assert.Equal(GuestLobbyCommands.MaxRooms, fixture.ReadKeys.Count);
        Assert.Equal(0, fixture.Writes);
    }


    [Fact]
    public async Task IndexedRoomReadsUseAtMostEightConcurrentRedisCommands()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        fixture.Members = Enumerable.Range(0, 19)
            .Select(i => (RedisValue)IndexedKeyForRoom("room-" + i))
            .ToArray();
        fixture.HoldReads = true;
        fixture.Raw = null; // Expired Redis keys remain harmless omissions.
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var pending = store.GetIndexedRoomsAsync();
        try
        {
            // The first window can be launched without waiting on any GET.
            await fixture.InitialWindowFilled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(8, fixture.InFlightReads);
            Assert.Equal(8, fixture.ReadKeys.Count);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            fixture.ReleaseReads.TrySetResult(true);
        }
        Assert.Empty(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(19, fixture.ReadKeys.Count);
        Assert.Equal(fixture.Members.Select(x => x.ToString()), fixture.ReadKeys);
        Assert.Equal(8, fixture.MaxInFlightReads);
        Assert.Equal(0, fixture.InFlightReads);
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task CancelledAdministrativeReadCannotScheduleLaterWindows()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        fixture.Members = Enumerable.Range(0, 20)
            .Select(i => (RedisValue)IndexedKeyForRoom("room-" + i))
            .ToArray();
        fixture.HoldReads = true;
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var pending = store.GetIndexedRoomsAsync(cancellation.Token);
        try
        {
            await fixture.InitialWindowFilled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(8, fixture.ReadKeys.Count);
            Assert.Equal(8, fixture.MaxInFlightReads);
            Assert.Equal(0, fixture.Writes);
        }
        finally
        {
            fixture.ReleaseReads.TrySetResult(true);
        }
    }

    [Fact]
    public async Task IndexedScanKeepsOrderAndFailsClosedForCorruptLastWindow()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        var rooms = Enumerable.Range(0, 19).Select(_ => ValidIndexedRoom())
            .ToArray();
        fixture.Members = rooms.Select(room =>
            (RedisValue)IndexedKeyForRoom(room.RoomId)).ToArray();
        fixture.RawByKey = rooms.ToDictionary(room => IndexedKeyForRoom(room.RoomId),
            room => (byte[]?)room.Encoded);
        // The middle room expired after index enumeration; the caller
        // receives only the surviving rooms, in index order.
        fixture.RawByKey[IndexedKeyForRoom(rooms[4].RoomId)] = null;
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var verified = await store.GetIndexedRoomsAsync();
        Assert.Equal(rooms.Where((_, i) => i != 4).Select(x => x.RoomId),
            verified.Select(x => x.Snapshot.State.Definition.LobbyId));
        Assert.Equal(19, fixture.ReadKeys.Count);
        Assert.InRange(fixture.MaxInFlightReads, 1, 8);

        // A later corrupted snapshot invalidates the entire scan, not just
        // the last room. The caller cannot perform partial cleanup.
        fixture.ReadKeys.Clear();
        fixture.RawByKey[IndexedKeyForRoom(rooms[18].RoomId)] =
            new byte[RedisLobbySnapshotCodec.MaxBytes + 1];
        var error = await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.GetIndexedRoomsAsync());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(rooms[18].RoomId, error.Message);
        Assert.Equal(19, fixture.ReadKeys.Count);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData(257)]
    [InlineData(600)]
    public async Task OversizedCapacityIndexFailsBeforeRoomReadsOrAnyCleanup(int count)
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        // Simulate an invalid response from a fake Redis that ignores the
        // rank bound, in addition to the legitimate 257-item response.
        fixture.ReturnUnboundedResult = count > GuestLobbyCommands.MaxRooms + 1;
        fixture.Members = Enumerable.Range(0, count)
            .Select(i => (RedisValue)IndexedKeyForRoom(
                "room-" + i.ToString("x32")))
            .ToArray();
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var error = await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.GetIndexedRoomsAsync());
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("room-", error.Message);
        Assert.DoesNotContain("Redis", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(0L, fixture.ScanStart);
        Assert.Equal(GuestLobbyCommands.MaxRooms, fixture.ScanStop);
        Assert.Empty(fixture.ReadKeys);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("another-authority")]
    [InlineData("capacity-index")]
    [InlineData("short-hash")]
    [InlineData("uppercase-hash")]
    [InlineData("other-namespace")]
    [InlineData("extra-suffix")]
    public async Task CorruptCapacityIndexCannotReadArbitraryRedisKeys(
        string caseName)
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        var prefix = "shirokuro:lobby:{rooms}:v2:";
        var hash = new string('a', 64);
        var key = caseName switch
        {
            "another-authority" => "guest-session:" + hash,
            "capacity-index" => prefix + "capacity:all",
            "short-hash" => prefix + "room:abc",
            "uppercase-hash" => prefix + "room:" + new string('A', 64),
            "other-namespace" => "shirokuro:lobby:{else}:v2:room:" + hash,
            "extra-suffix" => prefix + "room:" + hash + ":other",
            _ => throw new ArgumentOutOfRangeException(nameof(caseName))
        };
        fixture.Members = [(RedisValue)key];
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.GetIndexedRoomsAsync());

        Assert.Null(ex.InnerException);
        Assert.DoesNotContain(key, ex.Message);
        Assert.DoesNotContain("Redis", ex.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.ReadKeys);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task OversizedIndexMemberIsStrippedBeforeTransportAndNoRoomIsRead()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        var valid = IndexedKeyForRoom("room-healthy");
        fixture.Members = [
            (RedisValue)valid,
            (RedisValue)new string('Z', 64 * 1024)
        ];
        fixture.Raw = new byte[] { 1, 2, 3 };
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var error = await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.GetIndexedRoomsAsync());

        Assert.Null(error.InnerException);
        Assert.DoesNotContain("ZZZZ", error.Message);
        Assert.Empty(fixture.ReadKeys);
        Assert.Equal(1, fixture.ScanCalls);
        Assert.Equal(GuestLobbyCommands.MaxRooms, fixture.ScanStop);
        Assert.Equal(valid.Length, fixture.MaxMemberBytes);
        Assert.Equal(0, fixture.Writes);
        Assert.Equal(2, fixture.Members.Length); // No implicit repair.
    }

    [Fact]
    public async Task MixedIndexWithLaterMalformedShortKeyFailsBeforeAnyRoomRead()
    {
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        fixture.Members = [
            (RedisValue)IndexedKeyForRoom("room-healthy"),
            (RedisValue)"guest-session:untrusted"
        ];
        fixture.Raw = new byte[] { 1, 2, 3 };
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.GetIndexedRoomsAsync());

        Assert.Empty(fixture.ReadKeys);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("missing-lobby-id")]
    [InlineData("mismatched-lobby-id")]
    [InlineData("bad-version")]
    [InlineData("oversized")]
    [InlineData("untracked")]
    public async Task CorruptIndexedRoomCannotAuthorizeAdministrativeCleanup(
        string caseName)
    {
        var (roomId, playerId, encoded) = ValidIndexedRoom();
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        fixture.Members = [(RedisValue)IndexedKeyForRoom(roomId)];
        var json = JsonNode.Parse(Encoding.UTF8.GetString(encoded))!.AsObject();
        fixture.Raw = caseName switch
        {
            "truncated" => Encoding.UTF8.GetBytes("{\"LobbyId\":"),
            "missing-lobby-id" => Encoding.UTF8.GetBytes("{}"),
            "mismatched-lobby-id" => Modified("LobbyId",
                "room-" + Guid.NewGuid().ToString("N")),
            "bad-version" => Modified("Version", 999),
            "oversized" => new byte[RedisLobbySnapshotCodec.MaxBytes + 1],
            "untracked" => Modified("QuotaTracked", false),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName))
        };

        byte[] Modified(string property, object value)
        {
            json[property] = JsonValue.Create(value);
            return Encoding.UTF8.GetBytes(json.ToJsonString());
        }

        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<LobbySnapshotUnavailableException>(
            () => store.GetIndexedRoomsAsync());
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(roomId, failure.Message);
        Assert.DoesNotContain(playerId, failure.Message);
        Assert.DoesNotContain("Redis", failure.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal([IndexedKeyForRoom(roomId)], fixture.ReadKeys);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task ValidIndexedRoomsAndMissingExpiredKeysKeepExistingSemantics()
    {
        var (roomId, playerId, encoded) = ValidIndexedRoom();
        var database = DispatchProxy.Create<IDatabase, IndexedRoomDatabase>();
        var fixture = (IndexedRoomDatabase)(object)database;
        fixture.Members = [(RedisValue)IndexedKeyForRoom(roomId)];
        fixture.Raw = encoded;
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var indexed = Assert.Single(await store.GetIndexedRoomsAsync());
        Assert.Equal(roomId, indexed.Snapshot.State.Definition.LobbyId);
        Assert.Equal(playerId, Assert.Single(
            indexed.Snapshot.State.Participants).PlayerId);
        Assert.True(indexed.Snapshot.QuotaTracked);
        Assert.Equal([IndexedKeyForRoom(roomId)], fixture.ReadKeys);

        fixture.ReadKeys.Clear();
        fixture.Raw = null; // A room may expire between ZRANGE and GET.
        Assert.Empty(await store.GetIndexedRoomsAsync());
        Assert.Equal([IndexedKeyForRoom(roomId)], fixture.ReadKeys);
        Assert.Equal(0, fixture.Writes);
    }

    private static (IDatabase Database, NeverRespondingDatabase Controller)
        Stalled()
    {
        var database = DispatchProxy.Create<IDatabase, NeverRespondingDatabase>();
        return (database, (NeverRespondingDatabase)(object)database);
    }

    private sealed class Scenario
    {
        public RedisLobbySnapshotStore Store { get; }
        public RedisLobbySnapshotStore.Stored Stored { get; }
        public LobbySnapshot NewRoom { get; }
        public string Actor { get; }

        public Scenario(IDatabase database)
        {
            Actor = GuestSessions.NewPlayerId();
            var id = "room-" + Guid.NewGuid().ToString("N");
            var state = LobbyState.Create(new LobbyDefinition(
                id, ["white", "black"], ["kira"])).Join(Actor, "white", 0);
            NewRoom = new LobbySnapshot(state, DateTimeOffset.UtcNow, Actor);
            Stored = new RedisLobbySnapshotStore.Stored(
                NewRoom, RedisLobbySnapshotCodec.Encode(NewRoom));
            Store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        }

        public Task Invoke(string operation, CancellationToken token)
            => operation switch
            {
                "read" => Store.ReadAsync(
                    NewRoom.State.Definition.LobbyId, token),
                "fence" => Store.FenceGuestAsync(Actor, token),
                "create" => Store.CreateAsync(NewRoom, token),
                "bounded-create" => Store.CreateBoundedAsync(
                    NewRoom, 256, 8, token),
                "cas" => Store.CompareExchangeAsync(
                    Stored, NewRoom, Actor, token),
                "delete" => Store.DeleteIfCurrentAsync(Stored, Actor, token),
                "scan" => Store.GetIndexedRoomsAsync(token),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
    }

    [Theory]
    [InlineData("read")]
    [InlineData("fence")]
    [InlineData("create")]
    [InlineData("bounded-create")]
    [InlineData("cas")]
    [InlineData("delete")]
    [InlineData("scan")]
    public async Task CancelledBeforeDispatchDoesNotSendRedisCommand(string operation)
    {
        var (database, redis) = Stalled();
        var scenario = new Scenario(database);
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scenario.Invoke(operation, aborted.Token));
        Assert.Equal(0, redis.Calls);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("fence")]
    [InlineData("create")]
    [InlineData("bounded-create")]
    [InlineData("cas")]
    [InlineData("delete")]
    [InlineData("scan")]
    public async Task InFlightAbortStopsWaitWithOneRedisDispatch(string operation)
    {
        var (database, redis) = Stalled();
        var scenario = new Scenario(database);
        using var aborted = new CancellationTokenSource();
        var pending = scenario.Invoke(operation, aborted.Token);
        Assert.Equal(1, redis.Calls);
        aborted.Cancel();
        Assert.Same(pending, await Task.WhenAny(
            pending, Task.Delay(TimeSpan.FromSeconds(5))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, redis.Calls);
        Assert.True(redis.NoRedisCompletion);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("fence")]
    [InlineData("create")]
    [InlineData("bounded-create")]
    [InlineData("cas")]
    [InlineData("delete")]
    [InlineData("scan")]
    public async Task CommandDeadlineCannotInventSuccessfulMutationOrRoster(
        string operation)
    {
        var (database, redis) = Stalled();
        var scenario = new Scenario(database);
        var pending = scenario.Invoke(operation, CancellationToken.None);
        Assert.Equal(1, redis.Calls);
        Assert.Same(pending, await Task.WhenAny(
            pending, Task.Delay(TimeSpan.FromSeconds(8))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, redis.Calls);
        Assert.True(redis.NoRedisCompletion);
    }

    [Fact]
    public async Task RuntimeCancelledCreateDoesNotWaitForOrRepeatLua()
    {
        var (database, redis) = Stalled();
        var clock = TimeProvider.System;
        var runtime = new RedisGuestLobbyRuntime(
            new RedisLobbySnapshotStore(database, clock), clock);
        using var aborted = new CancellationTokenSource();
        var pending = runtime.CreateAsync(GuestSessions.NewPlayerId(),
            ["white", "black"], "white", aborted.Token);
        Assert.Equal(1, redis.Calls);
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, redis.Calls);
        Assert.True(redis.NoRedisCompletion);
    }

    private static WebApplicationFactory<Program> Production(IDatabase database) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAsyncGuestLobby>();
                services.AddSingleton<IAsyncGuestLobby>(sp =>
                {
                    var clock = sp.GetRequiredService<TimeProvider>();
                    return new RedisGuestLobbyRuntime(
                        new RedisLobbySnapshotStore(database, clock), clock);
                });
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> host,
        string? cookie = null)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        if (cookie is not null)
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task<string> Login(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/sessions/guest");
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        using var issued = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        return Assert.Single(issued.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    [Fact]
    public async Task ProductionCreateReturnsSanitized503OnUncertainLuaMutation()
    {
        var (database, redis) = Stalled();
        using var host = Production(database);
        using var visitor = Client(host);
        var cookie = await Login(visitor);
        using var client = Client(host, cookie);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/lobbies");
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        request.Content = JsonContent.Create(new {
            teamIds = new[] { "white", "black" }, teamId = "white"
        });
        using var response = await client.SendAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, redis.Calls);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guest-", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProductionAuthorizedRosterReadReturnsSanitized503OnStalledRedis()
    {
        var (database, redis) = Stalled();
        using var host = Production(database);
        using var visitor = Client(host);
        var cookie = await Login(visitor);
        using var client = Client(host, cookie);
        using var response = await client.GetAsync("/api/lobbies/room-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee/roster")
            .WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(1, redis.Calls);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}
