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
using Shirokuro.Domain.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Silent room DEL/ZREM loss must not let final-member leave or expiry
/// cleanup falsely claim a deleted Redis room or released creator quota.
/// </summary>
public sealed class RedisLobbyDeleteConfirmationTests
{
    public class FaultyDeleteDatabase : DispatchProxy
    {
        private byte[]? _room;
        private bool _global;
        private bool _creator;
        private bool _quotaTracked;
        private RedisKey[]? _deleteKeys;

        public string Mode { get; set; } = "healthy";
        public long DeleteResult { get; set; } = 1;
        public CancellationTokenSource? CancelAfterDelete { get; set; }
        public int Reads { get; private set; }
        public int Deletes { get; private set; }
        public int Confirms { get; private set; }
        public bool RoomPresent => _room is not null;
        public bool GlobalPresent => _global;
        public bool CreatorPresent => _creator;
        public TaskCompletionSource<RedisResult> BlockedConfirmation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Seed(byte[] room, bool tracked)
        {
            _room = room.ToArray();
            _quotaTracked = tracked;
            _global = tracked;
            _creator = tracked;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "ScriptEvaluateAsync")
                throw new InvalidOperationException("Unexpected fake Redis command.");
            var script = Assert.IsType<string>(args![0]);
            var keys = Assert.IsType<RedisKey[]>(args[1]);
            var values = Assert.IsType<RedisValue[]>(args[2]);

            if (script.Contains("if #raw > tonumber(ARGV[1])",
                    StringComparison.Ordinal))
            {
                Reads++;
                return Task.FromResult(_room is null
                    ? RedisResult.Create(RedisValue.Null, ResultType.BulkString)
                    : RedisResult.Create((RedisValue)_room, ResultType.BulkString));
            }

            if (script.Contains("redis.call('DEL', KEYS[1])",
                    StringComparison.Ordinal))
            {
                Deletes++;
                Assert.Equal(4, keys.Length);
                Assert.Equal(3, values.Length);
                Assert.Equal(_quotaTracked ? 1L : 0L, (long)values[1]);
                _deleteKeys = keys;
                if (DeleteResult == 1)
                {
                    if (Mode is not ("ignored" or "room-left"))
                        _room = null;
                    if (Mode is not ("ignored" or "global-left"))
                        _global = false;
                    if (Mode is not ("ignored" or "creator-left"))
                        _creator = false;
                }
                CancelAfterDelete?.Cancel();
                return Task.FromResult(RedisResult.Create(
                    (RedisValue)DeleteResult, ResultType.Integer));
            }

            Confirms++;
            Assert.Equal(3, keys.Length);
            Assert.NotNull(_deleteKeys);
            Assert.Equal(_deleteKeys!.Take(3), keys);
            Assert.Single(values);
            Assert.Equal(_quotaTracked ? 1L : 0L, (long)values[0]);
            // Prove verification itself only reads, and checks all three keys.
            Assert.Contains("redis.call('GET', KEYS[1])", script);
            Assert.Contains("redis.call('ZSCORE', KEYS[2], KEYS[1])", script);
            Assert.Contains("redis.call('ZSCORE', KEYS[3], KEYS[1])", script);
            Assert.Contains("ARGV[1] == '1'", script);
            Assert.DoesNotContain("redis.call('DEL'", script);
            Assert.DoesNotContain("redis.call('ZREM'", script);
            Assert.DoesNotContain("redis.call('PEXPIRE'", script);

            return Mode switch
            {
                "throw" => Task.FromException<RedisResult>(
                    new IOException("private redis password or key")),
                "blocked" => BlockedConfirmation.Task,
                "wrong-reply" => Task.FromResult(RedisResult.Create(
                    (RedisValue)0L, ResultType.Integer)),
                _ => Task.FromResult(RedisResult.Create(
                    (RedisValue)(_room is null &&
                        (!_quotaTracked || (!_global && !_creator)) ? 1L : 0L),
                    ResultType.Integer))
            };
        }
    }

    private static (IDatabase Database, FaultyDeleteDatabase Fake) NewDatabase()
    {
        var db = DispatchProxy.Create<IDatabase, FaultyDeleteDatabase>();
        return (db, (FaultyDeleteDatabase)(object)db);
    }

    private static (RedisLobbySnapshotStore Store,
        RedisLobbySnapshotStore.Stored Expected) Scenario(
            IDatabase database, bool tracked = true)
    {
        var player = GuestSessions.NewPlayerId();
        var id = "room-" + Guid.NewGuid().ToString("N");
        var state = LobbyState.Create(
            new LobbyDefinition(id, ["white", "black"], ["kira"]))
            .Join(player, "white", 0);
        var snapshot = new LobbySnapshot(state,
            DateTimeOffset.UtcNow.AddSeconds(-2), player, QuotaTracked: tracked);
        return (new RedisLobbySnapshotStore(database, TimeProvider.System),
            new RedisLobbySnapshotStore.Stored(snapshot,
                RedisLobbySnapshotCodec.Encode(snapshot)));
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("room-left")]
    [InlineData("global-left")]
    [InlineData("creator-left")]
    [InlineData("throw")]
    [InlineData("blocked")]
    [InlineData("wrong-reply")]
    public async Task FailedConfirmationDoesNotAcknowledgeDeleteAndAllowsRecovery(
        string failure)
    {
        var (db, fake) = NewDatabase();
        var (store, current) = Scenario(db);
        fake.Seed(current.RawBytes, tracked: true);
        fake.Mode = failure;

        var pending = store.DeleteIfCurrentAsync(current);
        Assert.Same(pending, await Task.WhenAny(
            pending, Task.Delay(TimeSpan.FromSeconds(8))));
        var error = await Record.ExceptionAsync(() => pending);
        Assert.NotNull(error);
        Assert.Equal(1, fake.Deletes);
        Assert.Equal(1, fake.Confirms);
        if (failure is "ignored" or "room-left")
            Assert.True(fake.RoomPresent);
        if (failure == "global-left") Assert.True(fake.GlobalPresent);
        if (failure == "creator-left") Assert.True(fake.CreatorPresent);
        if (failure == "blocked")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(fake.BlockedConfirmation.Task.IsCompleted);
        }
        else if (failure is not "throw")
        {
            Assert.IsType<LobbyMutationUnavailableException>(error);
            Assert.DoesNotContain(current.Snapshot.CreatorId!,
                error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(current.Snapshot.State.Definition.LobbyId,
                error.ToString(), StringComparison.Ordinal);
        }

        fake.Mode = "healthy";
        // A previous Lua may or may not have completed before the read-back
        // failure. A healthy retry verifies absence without touching another
        // room; the actual worker owns retry timing, not this method.
        Assert.True(await store.DeleteIfCurrentAsync(current));
        Assert.False(fake.RoomPresent);
        Assert.False(fake.GlobalPresent);
        Assert.False(fake.CreatorPresent);
        Assert.Equal(2, fake.Deletes);
        Assert.Equal(2, fake.Confirms);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulDeleteConfirmsOnlyApplicableKeys(bool tracked)
    {
        var (db, fake) = NewDatabase();
        var (store, current) = Scenario(db, tracked);
        fake.Seed(current.RawBytes, tracked);
        Assert.True(await store.DeleteIfCurrentAsync(current));
        Assert.Equal(1, fake.Deletes);
        Assert.Equal(1, fake.Confirms);
        Assert.False(fake.RoomPresent);
        Assert.False(fake.GlobalPresent);
        Assert.False(fake.CreatorPresent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConflictOrRevocationFenceCannotTriggerReadback(long code)
    {
        var (db, fake) = NewDatabase();
        var (store, current) = Scenario(db);
        fake.Seed(current.RawBytes, tracked: true);
        fake.DeleteResult = code;
        if (code == 0)
            Assert.False(await store.DeleteIfCurrentAsync(current));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => store.DeleteIfCurrentAsync(current, current.Snapshot.CreatorId));
        Assert.Equal(1, fake.Deletes);
        Assert.Equal(0, fake.Confirms);
        Assert.True(fake.RoomPresent);
        Assert.True(fake.GlobalPresent);
        Assert.True(fake.CreatorPresent);
    }

    [Fact]
    public async Task CancellationBetweenDeleteAndReadbackDispatchesNoConfirmation()
    {
        var (db, fake) = NewDatabase();
        var (store, current) = Scenario(db);
        fake.Seed(current.RawBytes, tracked: true);
        using var abort = new CancellationTokenSource();
        fake.CancelAfterDelete = abort;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeleteIfCurrentAsync(
                current, current.Snapshot.CreatorId, abort.Token));
        Assert.Equal(1, fake.Deletes);
        Assert.Equal(0, fake.Confirms);
    }

    [Fact]
    public async Task CancelledBeforeDeleteDoesNotDispatchRedis()
    {
        var (db, fake) = NewDatabase();
        var (store, current) = Scenario(db);
        fake.Seed(current.RawBytes, tracked: true);
        using var abort = new CancellationTokenSource();
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.DeleteIfCurrentAsync(
                current, current.Snapshot.CreatorId, abort.Token));
        Assert.Equal(0, fake.Deletes);
        Assert.Equal(0, fake.Confirms);
        Assert.True(fake.RoomPresent);
    }

    [Fact]
    public async Task ProductionFinalMemberLeaveIs503OnLostDeleteThen204OnRecovery()
    {
        var (db, fake) = NewDatabase();
        fake.Mode = "ignored";
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                TestDataProtectionConfiguration.Apply(builder);
                TestGuestSessionRedisConfiguration.Apply(builder);
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AllowedHosts"] = "game.example",
                        ["Security:AllowedOrigins:0"] = "https://game.example"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IAsyncGuestLobby>();
                    services.AddSingleton<IAsyncGuestLobby>(sp =>
                        new RedisGuestLobbyRuntime(
                            new RedisLobbySnapshotStore(db,
                                sp.GetRequiredService<TimeProvider>()),
                            sp.GetRequiredService<TimeProvider>()));
                });
            });
        using var visitor = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        using var signIn = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/guest");
        signIn.Headers.Add("Origin", "https://game.example");
        signIn.Headers.Add("X-Shirokuro-Request", "1");
        using var issued = await visitor.SendAsync(signIn);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(
            await issued.Content.ReadAsStringAsync());
        var player = document.RootElement.GetProperty("playerId").GetString()!;
        var cookie = Assert.Single(issued.Headers.GetValues("Set-Cookie")).Split(';')[0];

        var id = "room-" + Guid.NewGuid().ToString("N");
        var state = LobbyState.Create(
            new LobbyDefinition(id, ["white", "black"], ["kira"]))
            .Join(player, "white", 0);
        var snapshot = new LobbySnapshot(state, DateTimeOffset.UtcNow.AddSeconds(-2),
            player, QuotaTracked: true);
        fake.Seed(RedisLobbySnapshotCodec.Encode(snapshot), tracked: true);
        using var member = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        member.DefaultRequestHeaders.Add("Cookie", cookie);

        static HttpRequestMessage Leave(string lobby)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Delete, "/api/lobbies/" + lobby + "/membership");
            request.Headers.Add("Origin", "https://game.example");
            request.Headers.Add("X-Shirokuro-Request", "1");
            request.Content = JsonContent.Create(new { expectedRevision = 1 });
            return request;
        }

        using (var request = Leave(id))
        using (var denied = await member.SendAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(8)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            var body = await denied.Content.ReadAsStringAsync();
            Assert.DoesNotContain(id, body);
            Assert.DoesNotContain(player, body);
            Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, fake.Reads);
        Assert.Equal(1, fake.Deletes);
        Assert.Equal(1, fake.Confirms);
        Assert.True(fake.RoomPresent);

        fake.Mode = "healthy";
        using var retry = Leave(id);
        using var removed = await member.SendAsync(retry);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(2, fake.Reads);
        Assert.Equal(2, fake.Deletes);
        Assert.Equal(2, fake.Confirms);
        Assert.False(fake.RoomPresent);
        Assert.False(fake.GlobalPresent);
        Assert.False(fake.CreatorPresent);
    }
}
