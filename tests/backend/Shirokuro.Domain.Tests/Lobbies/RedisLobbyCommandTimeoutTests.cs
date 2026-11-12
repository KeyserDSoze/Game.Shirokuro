using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
