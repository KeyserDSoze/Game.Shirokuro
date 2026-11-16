using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Text;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Actual Redis Lua scripts must serialize fence installation against stale
/// in-flight mutations across two independent API/node connections.
/// </summary>
public sealed class RedisGuestRevocationFenceTests
{
    private static string? Address =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Fact]
    public async Task StaleReadCannotCommitAfterRevocationFenceAndOtherGuestsSurvive()
    {
        if (Address is null) return;
        using var redisA = await ConnectionMultiplexer.ConnectAsync(Address);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(Address);
        var clock = new ManualClock();
        var ns = "fence-" + Guid.NewGuid().ToString("N");
        var a = new RedisLobbySnapshotStore(redisA.GetDatabase(), clock, namespaceName: ns);
        var b = new RedisLobbySnapshotStore(redisB.GetDatabase(), clock, namespaceName: ns);
        var apiA = new RedisGuestLobbyRuntime(a, clock);
        var apiB = new RedisGuestLobbyRuntime(b, clock);
        var alice = GuestSessions.NewPlayerId();
        var bob = GuestSessions.NewPlayerId();

        var room = await apiA.CreateAsync(alice, ["white", "black"], "white");
        await apiB.JoinAsync(room.LobbyId, bob, "black", room.Revision);

        // Simulate an already-authenticated HTTP request reading before
        // another node revokes its cookie and installs the tombstone.
        var stale = (await a.ReadAsync(room.LobbyId))!;
        var mutation = stale.Snapshot.State.ChooseHero(
            alice, "kira", stale.Snapshot.State.Revision);

        await b.FenceGuestAsync(alice);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            a.FenceGuestAsync("guest-client-chosen-or-invalid"));
        Assert.Equal(RedisLobbySnapshotStore.RevocationFenceTtl,
            TimeSpan.FromHours(8));

        // Same Lua operation sees fence even though the old snapshot and
        // revision still match; no update or TTL renewal is permitted.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            a.CompareExchangeAsync(stale, stale.Snapshot with { State = mutation }, alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            a.DeleteIfCurrentAsync(stale, alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiA.ChooseHeroAsync(room.LobbyId, alice, "kira", stale.Snapshot.State.Revision));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiA.ForMemberAsync(room.LobbyId, alice));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiB.JoinAsync(room.LobbyId, alice, "white", null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            apiA.CreateAsync(alice, ["white", "black"], "white"));

        var untouched = (await b.ReadAsync(room.LobbyId))!;
        Assert.Equal(stale.Snapshot.State.Revision, untouched.Snapshot.State.Revision);
        Assert.Equal(stale.Snapshot.LastActivityUtc, untouched.Snapshot.LastActivityUtc);
        Assert.Null(untouched.Snapshot.State.Participants.Single(
            p => p.PlayerId == alice).HeroId);

        // Other players still have normal access after a guest is fenced.
        var otherView = await apiB.ForMemberAsync(room.LobbyId, bob);
        Assert.Equal(room.LobbyId, otherView.LobbyId);
        var beforeCleanup = (await a.ReadAsync(room.LobbyId))!.Snapshot;
        Assert.Equal(1, await apiA.RemovePlayerFromAllRoomsAsync(alice));
        var afterCleanup = (await b.ReadAsync(room.LobbyId))!.Snapshot;
        Assert.Equal(beforeCleanup.State.Revision + 1, afterCleanup.State.Revision);
        Assert.Equal(bob, Assert.Single(afterCleanup.State.Participants).PlayerId);
        Assert.Equal(beforeCleanup.LastActivityUtc, afterCleanup.LastActivityUtc);

        // Fencing must not store the plaintext player or token in any Redis
        // key, and must have a bounded expiration, even after a retry.
        var digest = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(alice)));
        var fenceKey = "shirokuro:lobby:{" + ns + "}:v2:revoked:" + digest;
        Assert.DoesNotContain(alice, fenceKey, StringComparison.Ordinal);
        Assert.DoesNotContain("guest-", fenceKey, StringComparison.Ordinal);
        Assert.Equal("1", (await redisB.GetDatabase().StringGetAsync(fenceKey)).ToString());
        var initialTtl = (await redisA.GetDatabase().KeyTimeToLiveAsync(fenceKey))!.Value;
        Assert.True(initialTtl > TimeSpan.Zero);
        Assert.True(initialTtl <= RedisLobbySnapshotStore.RevocationFenceTtl);
        await a.FenceGuestAsync(alice); // SET NX must not slide the fence TTL.
        var afterRetryTtl = (await redisB.GetDatabase().KeyTimeToLiveAsync(fenceKey))!.Value;
        Assert.True(afterRetryTtl <= initialTtl);
    }

    [Fact]
    public async Task AtomicFenceCheckPreventsLateCreationEvenWhenQuotaIsAvailable()
    {
        if (Address is null) return;
        using var redisA = await ConnectionMultiplexer.ConnectAsync(Address);
        using var redisB = await ConnectionMultiplexer.ConnectAsync(Address);
        var clock = new ManualClock();
        var ns = "fence-quota-" + Guid.NewGuid().ToString("N");
        var first = new RedisGuestLobbyRuntime(
            new RedisLobbySnapshotStore(redisA.GetDatabase(), clock, namespaceName: ns), clock);
        var index = new RedisLobbySnapshotStore(
            redisB.GetDatabase(), clock, namespaceName: ns);
        var owner = GuestSessions.NewPlayerId();
        var survivor = GuestSessions.NewPlayerId();
        var initial = await first.CreateAsync(owner, ["white", "black"], "white");

        await index.FenceGuestAsync(owner);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            first.CreateAsync(owner, ["white", "black"], "white"));

        // The failure was before room insertion or quota index mutation.
        var other = await first.CreateAsync(survivor, ["white", "black"], "black");
        Assert.NotEqual(initial.LobbyId, other.LobbyId);
        Assert.NotNull(await index.ReadAsync(initial.LobbyId));
        Assert.NotNull(await index.ReadAsync(other.LobbyId));
        Assert.Equal(1, await first.RemovePlayerFromAllRoomsAsync(owner));
        Assert.Null(await index.ReadAsync(initial.LobbyId));
        Assert.NotNull(await index.ReadAsync(other.LobbyId));
    }


    /// <summary>
    /// This deterministic Redis double reports completed SET NX even when
    /// the write is discarded. Real healthy Redis cannot model this fault.
    /// No player ID, token or key is ever included in exception text.
    /// </summary>
    public class FaultyFenceDatabase : DispatchProxy
    {
        private readonly Dictionary<string, (string Marker, TimeSpan? Ttl)> _stored =
            new(StringComparer.Ordinal);
        public bool IgnoreWrites { get; set; }
        public bool FalseWithoutKey { get; set; }
        public string ReadFault { get; set; } = "";
        public int SetCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int SuccessfulWrites { get; private set; }
        public string? LastKey { get; private set; }
        public TimeSpan? LastWrittenTtl { get; private set; }
        public TaskCompletionSource<RedisValue> PendingRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "StringSetAsync" &&
                method.ReturnType == typeof(Task<bool>))
            {
                SetCalls++;
                var key = args![0]!.ToString()!;
                var marker = args[1]!.ToString()!;
                var ttl = (TimeSpan?)args[2];
                var when = (When)args[3]!;
                LastKey = key;
                if (FalseWithoutKey)
                    return Task.FromResult(false);
                if (_stored.ContainsKey(key) && when == When.NotExists)
                    return Task.FromResult(false);
                if (!IgnoreWrites)
                {
                    _stored[key] = (marker, ttl);
                    LastWrittenTtl = ttl;
                    SuccessfulWrites++;
                }
                return Task.FromResult(true);
            }
            if (method?.Name == "StringGetAsync" &&
                method.ReturnType == typeof(Task<RedisValue>))
            {
                GetCalls++;
                var key = args![0]!.ToString()!;
                return ReadFault switch
                {
                    "missing" => Task.FromResult(RedisValue.Null),
                    "corrupt" => Task.FromResult((RedisValue)"corrupt"),
                    "throw" => Task.FromException<RedisValue>(
                        new IOException("secret Redis host and credentials")),
                    "blocked" => PendingRead.Task,
                    _ => Task.FromResult(_stored.TryGetValue(key, out var record)
                        ? (RedisValue)record.Marker : RedisValue.Null)
                };
            }
            throw new InvalidOperationException(
                "Unexpected fence test Redis operation.");
        }

        public string? ReadStoredMarker(string key) =>
            _stored.TryGetValue(key, out var record) ? record.Marker : null;
    }

    private static (IDatabase Database, FaultyFenceDatabase Fake)
        SimulatedFenceStore()
    {
        var database = DispatchProxy.Create<IDatabase, FaultyFenceDatabase>();
        return (database, (FaultyFenceDatabase)(object)database);
    }

    [Theory]
    [InlineData("ignored")]
    [InlineData("false-without-key")]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("throw")]
    [InlineData("blocked")]
    public async Task FenceRequiresConfirmedMarkerAndRecoversWithoutSlidingTtl(
        string fault)
    {
        var (database, fake) = SimulatedFenceStore();
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var player = GuestSessions.NewPlayerId();
        fake.IgnoreWrites = fault == "ignored";
        fake.FalseWithoutKey = fault == "false-without-key";
        fake.ReadFault = fault is "ignored" or "false-without-key" ? "" : fault;

        var pending = store.FenceGuestAsync(player);
        Assert.Same(pending, await Task.WhenAny(
            pending, Task.Delay(TimeSpan.FromSeconds(8))));
        var failure = await Record.ExceptionAsync(() => pending);
        Assert.NotNull(failure);
        Assert.DoesNotContain(player, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret Redis", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, fake.SetCalls);
        Assert.Equal(1, fake.GetCalls);
        Assert.StartsWith("shirokuro:lobby:{rooms}:v2:revoked:", fake.LastKey!);
        Assert.DoesNotContain(player, fake.LastKey!, StringComparison.Ordinal);
        if (fault == "blocked")
            Assert.False(fake.PendingRead.Task.IsCompleted);

        fake.IgnoreWrites = false;
        fake.FalseWithoutKey = false;
        fake.ReadFault = "";
        await store.FenceGuestAsync(player);
        Assert.Equal("1", fake.ReadStoredMarker(fake.LastKey!));
        Assert.Equal(RedisLobbySnapshotStore.RevocationFenceTtl,
            fake.LastWrittenTtl);
        var written = fake.SuccessfulWrites;
        await store.FenceGuestAsync(player); // Existing marker: NX=false.
        Assert.Equal(written, fake.SuccessfulWrites);
        Assert.Equal(RedisLobbySnapshotStore.RevocationFenceTtl,
            fake.LastWrittenTtl);
        Assert.Equal(3, fake.SetCalls);
        Assert.Equal(3, fake.GetCalls);
    }

    [Fact]
    public async Task CancelledFenceNeverDispatchesRedisAndCancelledReadCannotSucceed()
    {
        var (database, fake) = SimulatedFenceStore();
        var store = new RedisLobbySnapshotStore(database, TimeProvider.System);
        var guest = GuestSessions.NewPlayerId();
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.FenceGuestAsync(guest, preCancelled.Token));
        Assert.Equal(0, fake.SetCalls);

        fake.ReadFault = "blocked";
        using var requestAbort = new CancellationTokenSource();
        var pending = store.FenceGuestAsync(guest, requestAbort.Token);
        Assert.Equal(1, fake.SetCalls);
        Assert.Equal(1, fake.GetCalls);
        requestAbort.Cancel();
        Assert.Same(pending, await Task.WhenAny(
            pending, Task.Delay(TimeSpan.FromSeconds(5))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(fake.PendingRead.Task.IsCompleted);
        Assert.Equal(1, fake.SetCalls);
        Assert.Equal(1, fake.GetCalls);
    }

    [Fact]
    public async Task ProductionLogoutDoesNotClaimSuccessfulFenceWhenRedisDropsWrite()
    {
        var (database, fake) = SimulatedFenceStore();
        fake.IgnoreWrites = true;
        var fence = new RedisLobbySnapshotStore(database, TimeProvider.System);
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
                    services.RemoveAll<IGuestRevocationFence>();
                    services.AddSingleton<IGuestRevocationFence>(fence);
                });
            });
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        using var issue = new HttpRequestMessage(HttpMethod.Post,
            "/api/sessions/guest");
        issue.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        issue.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        using var issued = await client.SendAsync(issue);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var cookie = Assert.Single(issued.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var member = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        member.DefaultRequestHeaders.Add("Cookie", cookie);

        using var logout = new HttpRequestMessage(HttpMethod.Delete,
            "/api/sessions/guest");
        logout.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        logout.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        using var result = await member.SendAsync(logout);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Equal("no-store", result.Headers.CacheControl?.ToString());
        Assert.Equal(1, fake.SetCalls);
        Assert.Equal(1, fake.GetCalls);
        Assert.False(result.Headers.Contains("Retry-After"));
        var responseBody = await result.Content.ReadAsStringAsync();
        Assert.DoesNotContain("guest-", responseBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Redis", responseBody, StringComparison.OrdinalIgnoreCase);

        // Ticket authority was revoked before fencing was attempted.
        // A copied cookie cannot regain access even on the same API node.
        using var replay = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task FenceRejectsInvalidGuestBeforeAnyKeyIsCreated()
    {
        if (Address is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(Address);
        var clock = new ManualClock();
        var ns = "fval-" + Guid.NewGuid().ToString("N");
        var store = new RedisLobbySnapshotStore(redis.GetDatabase(), clock,
            namespaceName: ns);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.FenceGuestAsync("attacker-selected-id"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.FenceGuestAsync("guest-1111111111111111111111111111111Z"));
        var keys = redis.GetServer(redis.GetEndPoints().First())
            .Keys(pattern: "shirokuro:lobby:{" + ns + "}:v2:revoked:*")
            .ToArray();
        Assert.Empty(keys);
    }
}
