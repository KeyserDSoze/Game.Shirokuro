using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class DeploymentReadinessTests
{
    private static string? RedisAddress =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));
        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _milliseconds, (long)duration.TotalMilliseconds);
    }

    private sealed class ToggleSessionStore : IDistributedCache
    {
        private int _reads;
        private int _writes;
        private byte[]? _stored;
        public int Reads => Volatile.Read(ref _reads);
        public int Writes => Volatile.Read(ref _writes);
        public bool Fail { get; set; }
        public bool FailWrites { get; set; }
        public TaskCompletionSource<bool>? ReadStarted { get; set; }
        public Task? ReadBarrier { get; set; }

        public byte[]? Get(string key) => _stored;
        public async Task<byte[]?> GetAsync(string key,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reads);
            ReadStarted?.TrySetResult(true);
            if (ReadBarrier is { } barrier)
                await barrier.WaitAsync(token);
            if (Fail)
                throw new IOException("sensitive guest-session Redis error");
            return _stored;
        }
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;
        public void Remove(string key) { }
        public Task RemoveAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) =>
            _stored = value.ToArray();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options,
            CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _writes);
            if (FailWrites)
                throw new IOException("sensitive guest-session write Redis error");
            Assert.True(options.AbsoluteExpirationRelativeToNow is { } expiry &&
                expiry > TimeSpan.Zero && expiry <= TimeSpan.FromSeconds(10));
            _stored = value.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class BrokenReadinessKeys : IReadinessKeyVerifier
    {
        public void Verify() =>
            throw new IOException("secret certificate path and password");
    }

    private static WebApplicationFactory<Program> Production(
        IConnectionMultiplexer redis, ToggleSessionStore sessions,
        ManualClock clock, bool brokenKeys = false, bool realSessions = false,
        string? realSessionConnection = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, sessions);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton(redis);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                services.RemoveAll<IDistributedCache>();
                if (realSessions)
                    services.AddStackExchangeRedisCache(options =>
                    {
                        // Override the earlier Production test TLS options,
                        // not just Configuration: ConfigurationOptions takes
                        // precedence in StackExchangeRedisCache.
                        options.ConfigurationOptions =
                            ConfigurationOptions.Parse(realSessionConnection
                                ?? throw new InvalidOperationException(
                                    "Dedicated test Redis TLS connection is required."));
                        // The isolated ACL fixture permits only shirokuro:* keys.
                        options.InstanceName = "shirokuro:readiness-test:";
                    });
                else
                    services.AddSingleton<IDistributedCache>(sessions);

                if (brokenKeys)
                {
                    services.RemoveAll<IReadinessKeyVerifier>();
                    services.AddSingleton<IReadinessKeyVerifier>(new BrokenReadinessKeys());
                }
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string baseAddress = "https://game.example") =>
        server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(baseAddress),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static async Task AssertStatus(
        HttpResponseMessage response, HttpStatusCode code, string value)
    {
        Assert.Equal(code, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var raw = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(raw);
        Assert.Equal(value, json.RootElement.GetProperty("status").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.DoesNotContain("Redis", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guest-", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DevelopmentReadinessAndLivenessWorkWithoutRedisOrCredentials()
    {
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(server, "http://localhost");
        using var ready = await client.GetAsync("/api/ready");
        await AssertStatus(ready, HttpStatusCode.OK, "ready");
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ProductionChecksBothStoresCoalescesCallsAndRecoversAfterOutage()
    {
        if (RedisAddress is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var sessionStore = new ToggleSessionStore();
        var clock = new ManualClock();
        using var server = Production(redis, sessionStore, clock);
        using var client = Client(server);
        using var metrics = new MultiplayerMetricCapture();

        // Repeated and concurrent public probes must not run an unbounded
        // number of session-cache/Redis commands.
        var responses = await Task.WhenAll(
            Enumerable.Range(0, 24).Select(_ => client.GetAsync("/api/ready")));
        foreach (var response in responses)
        {
            using (response)
                await AssertStatus(response, HttpStatusCode.OK, "ready");
        }
        Assert.Equal(1, sessionStore.Reads);
        Assert.Equal(1, sessionStore.Writes);

        sessionStore.Fail = true;
        using (var cached = await client.GetAsync("/api/ready"))
            await AssertStatus(cached, HttpStatusCode.OK, "ready");
        Assert.Equal(1, sessionStore.Reads);
        Assert.Equal(1, sessionStore.Writes);

        clock.Advance(TimeSpan.FromSeconds(4));
        using (var unavailable = await client.GetAsync("/api/ready"))
            await AssertStatus(unavailable, HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(2, sessionStore.Reads);
        Assert.Equal(2, sessionStore.Writes);

        using (var stillAlive = await client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, stillAlive.StatusCode);

        sessionStore.Fail = false;
        clock.Advance(TimeSpan.FromSeconds(4));
        using (var recovered = await client.GetAsync("/api/ready"))
            await AssertStatus(recovered, HttpStatusCode.OK, "ready");
        Assert.Equal(3, sessionStore.Reads);
        Assert.Equal(3, sessionStore.Writes);

        // A replica or misconfigured cache endpoint may respond to GET but
        // reject SET; that must never declare the Production node ready.
        sessionStore.FailWrites = true;
        clock.Advance(TimeSpan.FromSeconds(4));
        using (var readOnly = await client.GetAsync("/api/ready"))
            await AssertStatus(readOnly,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(4, sessionStore.Writes);
        Assert.Equal(3, sessionStore.Reads); // Failed before read verification.

        sessionStore.FailWrites = false;
        clock.Advance(TimeSpan.FromSeconds(4));
        using (var recoveredWrite = await client.GetAsync("/api/ready"))
            await AssertStatus(recoveredWrite, HttpStatusCode.OK, "ready");
        Assert.Equal(5, sessionStore.Writes);
        Assert.Equal(4, sessionStore.Reads);

        // The failure phase is finite and diagnostic-only. It must not
        // expose which key, cookie, Redis endpoint or exception was used.
        var durations = metrics.ForDurations(
            MultiplayerMetrics.ReadinessDurationSecondsName);
        Assert.Contains(durations, x => x.Tags["stage"] == "session-read" &&
            x.Tags["outcome"] == "unavailable");
        Assert.Contains(durations, x => x.Tags["stage"] == "redis-write" &&
            x.Tags["outcome"] == "unavailable");
        Assert.Contains(durations, x => x.Tags["stage"] == "ok" &&
            x.Tags["outcome"] == "ready");
        Assert.All(durations, x => Assert.All(x.Tags.Values, tag =>
        {
            Assert.DoesNotContain("sensitive", tag, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-", tag, StringComparison.OrdinalIgnoreCase);
        }));
    }

    [Fact]
    public async Task ProductionRefusesUnhealthyRoomRedisAndDoesNotBypassIngressPolicy()
    {
        if (RedisAddress is null) return;
        var redis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var clock = new ManualClock();
        var sessionStore = new ToggleSessionStore();
        using var server = Production(redis, sessionStore, clock);
        using var valid = Client(server);

        using (var healthy = await valid.GetAsync("/api/ready"))
            await AssertStatus(healthy, HttpStatusCode.OK, "ready");

        using (var insecure = Client(server, "http://game.example"))
        using (var denied = await insecure.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using (var unknownHost = Client(server, "https://untrusted.example"))
        using (var denied = await unknownHost.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

        redis.Dispose();
        clock.Advance(TimeSpan.FromSeconds(4));
        using (var unready = await valid.GetAsync("/api/ready"))
            await AssertStatus(unready, HttpStatusCode.ServiceUnavailable, "unavailable");
        using (var alive = await valid.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
    }

    [Fact]
    public async Task DataProtectionFailureNeverLeaksConfigurationOrReportsReady()
    {
        if (RedisAddress is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        using var server = Production(redis, new ToggleSessionStore(),
            new ManualClock(), brokenKeys: true);
        using var client = Client(server);
        using var metrics = new MultiplayerMetricCapture();
        using var response = await client.GetAsync("/api/ready");
        await AssertStatus(response, HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Contains(metrics.ForDurations(MultiplayerMetrics.ReadinessDurationSecondsName),
            x => x.Tags["stage"] == "key-ring" &&
                x.Tags["outcome"] == "unavailable");
    }

    [Fact]
    public async Task ActualRedisSessionCacheAndIndependentLobbyConnectorAreBothReady()
    {
        // This integration is deliberately hosted by the dedicated TLS+ACL
        // CI fixture instead of the plaintext standard Redis test service.
        // The game cache must never bypass TLS to satisfy a test override.
        var redisTls = Environment.GetEnvironmentVariable(
            "SHIROKURO_TEST_REDIS_TLS_ACL");
        if (redisTls is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(redisTls);
        using var server = Production(redis, new ToggleSessionStore(),
            new ManualClock(), realSessions: true,
            realSessionConnection: redisTls);
        // The production Redis cache starts a lazy independent connection.
        // Warm it explicitly so this test verifies healthy steady-state
        // readiness rather than timing the initial connection establishment.
        await server.Services.GetRequiredService<IDistributedCache>()
            .GetAsync("readiness-test-warmup");
        using var client = Client(server);
        using var response = await client.GetAsync("/api/ready");
        await AssertStatus(response, HttpStatusCode.OK, "ready");
    }


    [Fact]
    public async Task CancellingOneHttpWaiterDoesNotCancelTheSharedRedisProbe()
    {
        if (RedisAddress is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var store = new ToggleSessionStore();
        var readStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        store.ReadStarted = readStarted;
        store.ReadBarrier = releaseRead.Task;
        var clock = new ManualClock();
        using var server = Production(redis, store, clock);
        using var client = Client(server);
        using var cancelled = new CancellationTokenSource();

        var firstRequest = client.GetAsync("/api/ready", cancelled.Token);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var survivingRequests = Enumerable.Range(0, 16)
            .Select(_ => client.GetAsync("/api/ready"))
            .ToArray();

        // The original HTTP request disconnects while the shared session
        // read is blocked; later callers still need the successful result.
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await firstRequest);
        releaseRead.TrySetResult(true);

        var responses = await Task.WhenAll(survivingRequests);
        foreach (var response in responses)
        {
            using (response)
                await AssertStatus(response, HttpStatusCode.OK, "ready");
        }
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.Writes);

        clock.Advance(TimeSpan.FromSeconds(4));
        using (var fresh = await client.GetAsync("/api/ready"))
            await AssertStatus(fresh, HttpStatusCode.OK, "ready");
        Assert.Equal(2, store.Reads);
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task TimedOutSharedProbeFailsClosedWithoutQueuingMoreRedisChecks()
    {
        if (RedisAddress is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(RedisAddress);
        var store = new ToggleSessionStore
        {
            ReadBarrier = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task
        };
        var clock = new ManualClock();
        using var server = Production(redis, store, clock);
        using var client = Client(server);
        using var metrics = new MultiplayerMetricCapture();

        var responses = await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => client.GetAsync("/api/ready")));
        foreach (var response in responses)
        {
            using (response)
                await AssertStatus(response,
                    HttpStatusCode.ServiceUnavailable, "unavailable");
        }
        Assert.Equal(1, store.Reads);
        Assert.Equal(1, store.Writes);

        store.ReadBarrier = null;
        clock.Advance(TimeSpan.FromSeconds(4));
        using (var recovered = await client.GetAsync("/api/ready"))
            await AssertStatus(recovered, HttpStatusCode.OK, "ready");
        Assert.Equal(2, store.Reads);
        Assert.Equal(2, store.Writes);

        var durations = metrics.ForDurations(
            MultiplayerMetrics.ReadinessDurationSecondsName);
        Assert.Contains(durations, measurement =>
            measurement.Tags["stage"] == "timeout" &&
            measurement.Tags["outcome"] == "unavailable" &&
            measurement.Seconds >= 1.8);
        Assert.Contains(durations, measurement =>
            measurement.Tags["stage"] == "ok" &&
            measurement.Tags["outcome"] == "ready");
    }
}
