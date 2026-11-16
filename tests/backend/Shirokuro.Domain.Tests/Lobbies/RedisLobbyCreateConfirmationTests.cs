
using System.IO;
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
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Injection of silently lost or partially persisted bounded room creation.
/// No 201/authorized roster may be acknowledged until the same snapshot and
/// both quota index members are revalidated on the Redis connector.
/// </summary>
public sealed class RedisLobbyCreateConfirmationTests
{
    public class FaultyCreateDatabase : DispatchProxy
    {
        private byte[]? _raw;
        private double? _globalScore;
        private double? _creatorScore;
        private double _deadline;
        private RedisKey[]? _creationKeys;

        public string Mode { get; set; } = "healthy";
        public long CreateResult { get; set; } = 1;
        public CancellationTokenSource? CancelAfterCreate { get; set; }
        public int CreateCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public TaskCompletionSource<RedisResult> PendingConfirmation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "ScriptEvaluateAsync")
                throw new InvalidOperationException("Unexpected fake Redis call.");

            var script = Assert.IsType<string>(args![0]);
            var keys = Assert.IsType<RedisKey[]>(args[1]);
            var values = Assert.IsType<RedisValue[]>(args[2]);
            if (script.Contains("ZREMRANGEBYSCORE", StringComparison.Ordinal))
            {
                CreateCalls++;
                Assert.Equal(4, keys.Length);
                Assert.Equal(5, values.Length);
                _creationKeys = keys;
                if (CreateResult == 1)
                {
                    _raw = Mode == "missing-room"
                        ? null : ((byte[])values[0]!).ToArray();
                    if (Mode == "corrupt-room")
                        _raw![^1] ^= 0x5a;
                    _deadline = Mode == "expired-room" ? -1 : 1_900_000_000_000d;
                    _globalScore = Mode == "missing-global"
                        ? null : _deadline + (Mode == "stale-global" ? 1 : 0);
                    _creatorScore = Mode == "missing-creator"
                        ? null : _deadline + (Mode == "stale-creator" ? 1 : 0);
                }
                CancelAfterCreate?.Cancel();
                return Task.FromResult(RedisResult.Create(
                    (RedisValue)CreateResult, ResultType.Integer));
            }

            ConfirmCalls++;
            Assert.NotNull(_creationKeys);
            Assert.Equal(3, keys.Length);
            Assert.Equal(_creationKeys!.Take(3), keys);
            Assert.Single(values);

            // The verification script MUST actually check these conditions
            // inside Redis rather than fetching any raw roster data to .NET.
            Assert.Contains("redis.call('GET', KEYS[1])", script);
            Assert.Contains("raw ~= ARGV[1]", script);
            Assert.Contains("redis.call('PEXPIRETIME', KEYS[1])", script);
            Assert.Contains("redis.call('ZSCORE', KEYS[2], KEYS[1])", script);
            Assert.Contains("redis.call('ZSCORE', KEYS[3], KEYS[1])", script);
            Assert.Contains("tonumber(global) ~= deadline", script);
            Assert.Contains("tonumber(creator) ~= deadline", script);
            Assert.DoesNotContain("redis.call('PSETEX'", script);
            Assert.DoesNotContain("redis.call('ZADD'", script);
            Assert.DoesNotContain("redis.call('PEXPIRE'", script);

            if (Mode == "blocked-confirm") return PendingConfirmation.Task;
            if (Mode == "throw-confirm")
                return Task.FromException<RedisResult>(
                    new IOException("private Redis connection and password"));

            var requested = (byte[])values[0]!;
            var confirmed = _raw is not null &&
                _raw.AsSpan().SequenceEqual(requested) &&
                _deadline > 0 &&
                _globalScore == _deadline && _creatorScore == _deadline;
            return Task.FromResult(RedisResult.Create(
                (RedisValue)(confirmed ? 1 : 0), ResultType.Integer));
        }
    }

    private static (IDatabase Database, FaultyCreateDatabase Fake) Database()
    {
        var database = DispatchProxy.Create<IDatabase, FaultyCreateDatabase>();
        return (database, (FaultyCreateDatabase)(object)database);
    }

    private static (RedisLobbySnapshotStore Store, LobbySnapshot Snapshot)
        Scenario(IDatabase database)
    {
        var owner = GuestSessions.NewPlayerId();
        var id = "room-" + Guid.NewGuid().ToString("N");
        var state = LobbyState.Create(new LobbyDefinition(
            id, ["white", "black"], ["kira"])).Join(owner, "white", 0);
        var snapshot = new LobbySnapshot(state, DateTimeOffset.UtcNow, owner);
        return (new RedisLobbySnapshotStore(database, TimeProvider.System), snapshot);
    }

    [Theory]
    [InlineData("missing-room")]
    [InlineData("corrupt-room")]
    [InlineData("missing-global")]
    [InlineData("missing-creator")]
    [InlineData("stale-global")]
    [InlineData("stale-creator")]
    [InlineData("expired-room")]
    [InlineData("throw-confirm")]
    [InlineData("blocked-confirm")]
    public async Task PartiallyPersistedCreateNeverConfirmsRoomOrQuota(
        string mode)
    {
        var (database, fake) = Database();
        fake.Mode = mode;
        var (store, room) = Scenario(database);
        var pending = store.CreateBoundedAsync(room, 256, 8);
        Assert.Same(pending, await Task.WhenAny(pending,
            Task.Delay(TimeSpan.FromSeconds(8))));
        var failed = await Record.ExceptionAsync(() => pending);
        Assert.NotNull(failed);
        if (mode == "blocked-confirm")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failed);
            Assert.False(fake.PendingConfirmation.Task.IsCompleted);
        }
        else if (mode != "throw-confirm")
        {
            Assert.IsType<LobbyCreationUnavailableException>(failed);
            Assert.Equal("Lobby creation was not confirmed.", failed.Message);
            Assert.DoesNotContain(room.CreatorId!, failed.ToString(),
                StringComparison.Ordinal);
        }
        Assert.Equal(1, fake.CreateCalls);
        Assert.Equal(1, fake.ConfirmCalls);

        fake.Mode = "healthy";
        Assert.True(await store.CreateBoundedAsync(room with
        {
            State = LobbyState.Create(new LobbyDefinition(
                "room-" + Guid.NewGuid().ToString("N"),
                ["white", "black"], ["kira"])).Join(room.CreatorId!, "white", 0)
        }, 256, 8));
        Assert.Equal(2, fake.CreateCalls);
        Assert.Equal(2, fake.ConfirmCalls);
    }

    [Fact]
    public async Task ConfirmedCreateReturnsSuccessWithNoExtraWrites()
    {
        var (database, fake) = Database();
        var (store, room) = Scenario(database);
        Assert.True(await store.CreateBoundedAsync(room, 256, 8));
        Assert.Equal(1, fake.CreateCalls);
        Assert.Equal(1, fake.ConfirmCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    public async Task NonCreatedOrCapacityRejectedRoomDoesNotReadBack(long result)
    {
        var (database, fake) = Database();
        fake.CreateResult = result;
        var (store, room) = Scenario(database);
        if (result == 0)
            Assert.False(await store.CreateBoundedAsync(room, 256, 8));
        else if (result == -3)
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => store.CreateBoundedAsync(room, 256, 8));
        else
            await Assert.ThrowsAsync<LobbyCapacityException>(
                () => store.CreateBoundedAsync(room, 256, 8));
        Assert.Equal(1, fake.CreateCalls);
        Assert.Equal(0, fake.ConfirmCalls);
    }

    [Fact]
    public async Task CancelledBetweenCreateAndConfirmationSendsNoSecondCommand()
    {
        var (database, fake) = Database();
        var (store, room) = Scenario(database);
        using var cancelled = new CancellationTokenSource();
        fake.CancelAfterCreate = cancelled;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CreateBoundedAsync(room, 256, 8, cancelled.Token));
        Assert.Equal(1, fake.CreateCalls);
        Assert.Equal(0, fake.ConfirmCalls);
    }

    [Fact]
    public async Task PreCancelledCreateDoesNotSendAnyRedisCommand()
    {
        var (database, fake) = Database();
        var (store, room) = Scenario(database);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.CreateBoundedAsync(room, 256, 8, cancelled.Token));
        Assert.Equal(0, fake.CreateCalls);
        Assert.Equal(0, fake.ConfirmCalls);
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

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        if (cookie is not null)
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Write(string route,
        object? payload = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (payload is not null) request.Content = JsonContent.Create(payload);
        return request;
    }

    [Fact]
    public async Task ProductionRejectsLostCreatedRoomWithoutReturningInvitation()
    {
        var (database, fake) = Database();
        fake.Mode = "missing-global";
        using var server = Production(database);
        using var anonymous = Client(server);
        using var issue = Write("/api/sessions/guest");
        using var granted = await anonymous.SendAsync(issue);
        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        var cookie = Assert.Single(granted.Headers.GetValues("Set-Cookie")).Split(';')[0];

        using var member = Client(server, cookie);
        using (var rejectedRequest = Write("/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var denied = await member.SendAsync(rejectedRequest)
            .WaitAsync(TimeSpan.FromSeconds(8)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl?.NoStore == true);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            var content = await denied.Content.ReadAsStringAsync();
            Assert.DoesNotContain("room-", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Redis", content, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, fake.CreateCalls);
        Assert.Equal(1, fake.ConfirmCalls);

        fake.Mode = "healthy";
        using var freshRequest = Write("/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var created = await member.SendAsync(freshRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(2, fake.CreateCalls);
        Assert.Equal(2, fake.ConfirmCalls);
    }
}
