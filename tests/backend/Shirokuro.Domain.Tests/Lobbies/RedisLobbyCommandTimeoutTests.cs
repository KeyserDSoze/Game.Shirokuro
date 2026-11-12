using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
                "StringGetAsync" => ReadSnapshot(),
                "ScriptEvaluateAsync" => CompareExchange(),
                _ => throw new InvalidOperationException(
                    "Unexpected Redis operation in contention fixture.")
            };
        }

        private Task<RedisValue> ReadSnapshot()
        {
            Interlocked.Increment(ref _reads);
            return Task.FromResult((RedisValue)EncodedSnapshot);
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

    public class CorruptSnapshotDatabase : DispatchProxy
    {
        public byte[] Raw { get; set; } = [];
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public int Mutations { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "StringGetAsync")
            {
                Interlocked.Increment(ref _reads);
                return Task.FromResult((RedisValue)Raw);
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
