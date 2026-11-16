using System.IO;
using System.Net;
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
/// A failed Redis read-back of a successful CAS must not leak a fabricated
/// authorized roster. Tests the actual production Lua text and HTTP mapping,
/// without a nondeterministic network/failover fixture.
/// </summary>
public sealed class RedisLobbyCasConfirmationTests
{
    public class FaultyCasDatabase : DispatchProxy
    {
        private byte[]? _saved;
        private double _deadline = 1_900_000_000_000d;
        private double? _global;
        private double? _creator;
        private bool _tracked;
        public byte[] InitialSnapshot { get; set; } = [];
        public string Mode { get; set; } = "healthy";
        public long CasStatus { get; set; } = 1;
        public CancellationTokenSource? CancelAfterCas { get; set; }
        public int CasCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public int Reads { get; private set; }
        public TaskCompletionSource<RedisResult> PendingConfirmation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "ScriptEvaluateAsync")
                throw new InvalidOperationException("Unexpected Redis test operation.");
            var script = Assert.IsType<string>(args![0]);
            var keys = Assert.IsType<RedisKey[]>(args[1]);
            var values = Assert.IsType<RedisValue[]>(args[2]);
            if (script.Contains("if #raw > tonumber(ARGV[1])",
                    StringComparison.Ordinal))
            {
                Reads++;
                return Task.FromResult(RedisResult.Create(
                    (RedisValue)InitialSnapshot, ResultType.BulkString));
            }

            if (script.Contains("if ARGV[6] == '1'",
                    StringComparison.Ordinal))
            {
                CasCalls++;
                Assert.Equal(4, keys.Length);
                Assert.Equal(6, values.Length);
                _tracked = (long)values[4] == 1;
                if (CasStatus == 1)
                {
                    _saved = Mode == "lost" ? null : ((byte[])values[1]!).ToArray();
                    if (Mode == "changed" && _saved is not null)
                        _saved[^1] ^= 0x1e;
                    _deadline = Mode == "expired" ? -1d : 1_900_000_000_000d;
                    _global = Mode == "no-global" ? null
                        : _deadline + (Mode == "stale-global" ? 1 : 0);
                    _creator = Mode == "no-creator" ? null
                        : _deadline + (Mode == "stale-creator" ? 1 : 0);
                }
                CancelAfterCas?.Cancel();
                return Task.FromResult(RedisResult.Create(
                    (RedisValue)CasStatus, ResultType.Integer));
            }

            ConfirmCalls++;
            Assert.Equal(3, keys.Length);
            Assert.Equal(2, values.Length);
            Assert.Contains("redis.call('GET', KEYS[1])", script);
            Assert.Contains("raw ~= ARGV[1]", script);
            Assert.Contains("redis.call('PEXPIRETIME', KEYS[1])", script);
            Assert.Contains("redis.call('ZSCORE', KEYS[2], KEYS[1])", script);
            Assert.Contains("redis.call('ZSCORE', KEYS[3], KEYS[1])", script);
            Assert.Contains("ARGV[2] == '1'", script);
            Assert.Contains("tonumber(global) ~= deadline", script);
            Assert.Contains("tonumber(creator) ~= deadline", script);
            Assert.DoesNotContain("redis.call('PSETEX'", script);
            Assert.DoesNotContain("redis.call('ZADD'", script);
            Assert.DoesNotContain("redis.call('PEXPIRE'", script);
            Assert.Equal(_tracked, (long)values[1] == 1);

            if (Mode == "blocked") return PendingConfirmation.Task;
            if (Mode == "throw")
                return Task.FromException<RedisResult>(
                    new IOException("private Redis password and address"));
            var expected = (byte[])values[0]!;
            var confirmed = _saved is not null &&
                _saved.AsSpan().SequenceEqual(expected) &&
                _deadline > 0 && (!_tracked ||
                    _global == _deadline && _creator == _deadline);
            return Task.FromResult(RedisResult.Create(
                (RedisValue)(confirmed ? 1 : 0), ResultType.Integer));
        }
    }

    private static (IDatabase Database, FaultyCasDatabase Fake) Fake()
    {
        var db = DispatchProxy.Create<IDatabase, FaultyCasDatabase>();
        return (db, (FaultyCasDatabase)(object)db);
    }

    private static (RedisLobbySnapshotStore Store, RedisLobbySnapshotStore.Stored Before,
        LobbySnapshot Updated) Scenario(IDatabase database, bool tracked = true)
    {
        var actor = GuestSessions.NewPlayerId();
        var room = "room-" + Guid.NewGuid().ToString("N");
        var state = LobbyState.Create(
            new LobbyDefinition(room, ["white", "black"], ["kira"]))
            .Join(actor, "white", 0);
        var snapshot = new LobbySnapshot(state, DateTimeOffset.UtcNow,
            actor, QuotaTracked: tracked);
        var bytes = RedisLobbySnapshotCodec.Encode(snapshot);
        return (new RedisLobbySnapshotStore(database, TimeProvider.System),
            new RedisLobbySnapshotStore.Stored(snapshot, bytes),
            snapshot with { LastActivityUtc = snapshot.LastActivityUtc.AddSeconds(1) });
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("changed")]
    [InlineData("no-global")]
    [InlineData("no-creator")]
    [InlineData("stale-global")]
    [InlineData("stale-creator")]
    [InlineData("expired")]
    [InlineData("throw")]
    [InlineData("blocked")]
    public async Task SuccessfulCasIsNotEnoughWhenPersistenceCannotBeConfirmed(
        string mode)
    {
        var (database, fake) = Fake();
        var (store, before, updated) = Scenario(database);
        fake.Mode = mode;
        var pending = store.CompareExchangeAsync(
            before, updated, before.Snapshot.CreatorId);
        Assert.Same(pending, await Task.WhenAny(
            pending, Task.Delay(TimeSpan.FromSeconds(8))));
        var error = await Record.ExceptionAsync(() => pending);
        Assert.NotNull(error);
        Assert.Equal(1, fake.CasCalls);
        Assert.Equal(1, fake.ConfirmCalls);
        if (mode == "blocked")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(fake.PendingConfirmation.Task.IsCompleted);
        }
        else if (mode != "throw")
        {
            Assert.IsType<LobbyMutationUnavailableException>(error);
            Assert.DoesNotContain(before.Snapshot.CreatorId!, error.ToString());
            Assert.DoesNotContain(before.Snapshot.State.Definition.LobbyId,
                error.ToString());
        }
        fake.Mode = "healthy";
        Assert.True(await store.CompareExchangeAsync(
            before, updated, before.Snapshot.CreatorId));
        Assert.Equal(2, fake.CasCalls);
        Assert.Equal(2, fake.ConfirmCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyCasConfirmsTrackedOrUntrackedRoom(bool tracked)
    {
        var (database, fake) = Fake();
        var (store, before, updated) = Scenario(database, tracked);
        Assert.True(await store.CompareExchangeAsync(
            before, updated, before.Snapshot.CreatorId));
        Assert.Equal(1, fake.CasCalls);
        Assert.Equal(1, fake.ConfirmCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ConflictAndRevocationFenceNeverDispatchConfirmation(long result)
    {
        var (database, fake) = Fake();
        fake.CasStatus = result;
        var (store, before, updated) = Scenario(database);
        if (result == 0)
            Assert.False(await store.CompareExchangeAsync(
                before, updated, before.Snapshot.CreatorId));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => store.CompareExchangeAsync(
                    before, updated, before.Snapshot.CreatorId));
        Assert.Equal(1, fake.CasCalls);
        Assert.Equal(0, fake.ConfirmCalls);
    }

    [Fact]
    public async Task CancellationAfterCasDoesNotSendVerification()
    {
        var (database, fake) = Fake();
        var (store, before, updated) = Scenario(database);
        using var aborted = new CancellationTokenSource();
        fake.CancelAfterCas = aborted;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CompareExchangeAsync(
                before, updated, before.Snapshot.CreatorId, aborted.Token));
        Assert.Equal(1, fake.CasCalls);
        Assert.Equal(0, fake.ConfirmCalls);
    }

    [Fact]
    public async Task PreCancelledCasDoesNotTouchRedis()
    {
        var (database, fake) = Fake();
        var (store, before, updated) = Scenario(database);
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.CompareExchangeAsync(
                before, updated, before.Snapshot.CreatorId, aborted.Token));
        Assert.Equal(0, fake.CasCalls);
        Assert.Equal(0, fake.ConfirmCalls);
    }

    [Fact]
    public async Task ProductionHeartbeatReturns503WithoutRosterOnUnconfirmedCasThenRecovers()
    {
        var (database, fake) = Fake();
        var (_, before, _) = Scenario(database);
        fake.InitialSnapshot = before.RawBytes;
        fake.Mode = "lost";
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
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
        using var browser = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        using var login = new HttpRequestMessage(HttpMethod.Post,
            "/api/sessions/guest");
        login.Headers.Add("Origin", "https://game.example");
        login.Headers.Add("X-Shirokuro-Request", "1");
        using var issued = await browser.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        // Rebind the fake room to the authenticated session owner so
        // authorization comes from a real signed cookie, never from a body.
        // The decoded response is only used in this isolated TestServer.
        using var document = System.Text.Json.JsonDocument.Parse(
            await issued.Content.ReadAsStringAsync());
        var user = document.RootElement.GetProperty("playerId").GetString()!;
        var id = before.Snapshot.State.Definition.LobbyId;
        var state = LobbyState.Create(new LobbyDefinition(
            id, ["white", "black"], ["kira"])).Join(user, "white", 0);
        var actual = new LobbySnapshot(state, DateTimeOffset.UtcNow,
            user, QuotaTracked: true);
        fake.InitialSnapshot = RedisLobbySnapshotCodec.Encode(actual);
        var cookie = Assert.Single(issued.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var member = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        member.DefaultRequestHeaders.Add("Cookie", cookie);
        using (var refused = await member.GetAsync("/api/lobbies/" + id + "/roster")
            .WaitAsync(TimeSpan.FromSeconds(8)))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
            Assert.False(refused.Headers.Contains("Set-Cookie"));
            var raw = await refused.Content.ReadAsStringAsync();
            Assert.DoesNotContain(id, raw);
            Assert.DoesNotContain(user, raw);
            Assert.DoesNotContain("Redis", raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, fake.CasCalls);
        Assert.Equal(1, fake.ConfirmCalls);
        fake.Mode = "healthy";
        using var accepted = await member.GetAsync("/api/lobbies/" + id + "/roster");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(2, fake.CasCalls);
        Assert.Equal(2, fake.ConfirmCalls);
    }
}
