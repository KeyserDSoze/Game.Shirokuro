using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>Captures .NET meter measurements without opening any HTTP endpoint.</summary>
internal sealed class MultiplayerMetricCapture : IDisposable
{
    public sealed record Observation(
        string Name, long Value, IReadOnlyDictionary<string, string> Tags);

    private readonly ConcurrentQueue<Observation> _observations = new();
    private readonly MeterListener _listener = new();

    public MultiplayerMetricCapture()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MultiplayerMetrics.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var pairs = tags.ToArray().ToDictionary(
                tag => tag.Key, tag => tag.Value?.ToString() ?? "",
                StringComparer.Ordinal);
            _observations.Enqueue(new Observation(instrument.Name, value, pairs));
        });
        _listener.Start();
    }

    public Observation[] For(string name) =>
        _observations.Where(o => o.Name == name).ToArray();

    public void Dispose() => _listener.Dispose();
}

public sealed class MultiplayerMetricsTests
{
    private static string? Address =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private sealed class MutableKeys : IReadinessKeyVerifier
    {
        public bool Broken { get; set; }
        public int Calls { get; private set; }
        public void Verify()
        {
            Calls++;
            if (Broken)
                throw new IOException("password=not-for-telemetry guest-private");
        }
    }

    private sealed class ExpiryIndex : IGuestExpiryIndex
    {
        public bool Broken { get; set; }
        public int Acknowledgments { get; private set; }
        private bool _pending = true;

        public Task ScheduleAsync(string player, DateTimeOffset expires,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
            CancellationToken cancellationToken = default)
        {
            if (Broken)
                throw new IOException("guest-secret Redis error invitation=private");
            return Task.FromResult<IReadOnlyList<DueGuestExpiry>>(_pending
                ? [new DueGuestExpiry("encrypted-entry", "guest-11111111111111111111111111111111")]
                : []);
        }

        public Task AcknowledgeAsync(string entry, CancellationToken cancellationToken = default)
        {
            Assert.Equal("encrypted-entry", entry);
            Acknowledgments++;
            _pending = false;
            return Task.CompletedTask;
        }
    }

    private sealed class Limiter : IGuestMutationLimiter
    {
        public MutationOutcome Outcome { get; set; }

        public Task<GuestMutationDecision> AcquireAsync(
            GuestMutationRateLimit policy, System.Net.IPAddress? ip,
            CancellationToken cancellationToken = default) => Outcome switch
            {
                MutationOutcome.Allowed => Task.FromResult(
                    new GuestMutationDecision(true, 60_000)),
                MutationOutcome.Limited => Task.FromResult(
                    new GuestMutationDecision(false, 10_000)),
                _ => throw new IOException("Redis session token private")
            };
    }

    private static WebApplicationFactory<Program> Production(Limiter limiter) =>
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
                services.RemoveAll<IGuestMutationLimiter>();
                services.AddSingleton<IGuestMutationLimiter>(limiter);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> server, bool production) =>
        server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(production ? "https://game.example" : "http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static HttpRequestMessage Post(string path, bool production,
        object? body = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Origin", production ? "https://game.example" : "http://localhost");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public void MetricContractUsesOnlyClosedFiniteLabelsAndRejectsUnknownEnums()
    {
        using var capture = new MultiplayerMetricCapture();
        MultiplayerMetrics.RecordMutation(GuestMutationKind.SessionIssuance, MutationOutcome.Allowed);
        MultiplayerMetrics.RecordMutation(GuestMutationKind.SessionIssuance, MutationOutcome.Limited);
        MultiplayerMetrics.RecordMutation(GuestMutationKind.RoomCreation, MutationOutcome.Unavailable);
        MultiplayerMetrics.RecordReadinessProbe(true);
        MultiplayerMetrics.RecordReadinessProbe(false);
        MultiplayerMetrics.RecordExpirySweep(sharedRedis: true, success: false, membershipsRemoved: 0);
        MultiplayerMetrics.RecordExpirySweep(sharedRedis: true, success: true, membershipsRemoved: 3);
        MultiplayerMetrics.RecordExpirySweep(sharedRedis: false, success: true, membershipsRemoved: 1);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiplayerMetrics.RecordMutation((GuestMutationKind)999, MutationOutcome.Allowed));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiplayerMetrics.RecordMutation(GuestMutationKind.RoomCreation, (MutationOutcome)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiplayerMetrics.RecordExpirySweep(false, true, -1));

        Assert.Contains(capture.For(MultiplayerMetrics.MutationDecisionsName),
            m => m.Tags["policy"] == "session" && m.Tags["outcome"] == "limited");
        Assert.Contains(capture.For(MultiplayerMetrics.MutationDecisionsName),
            m => m.Tags["policy"] == "lobby" && m.Tags["outcome"] == "unavailable");
        Assert.Contains(capture.For(MultiplayerMetrics.ReadinessProbesName),
            m => m.Tags["outcome"] == "unavailable");
        Assert.Contains(capture.For(MultiplayerMetrics.ExpirySweepsName),
            m => m.Tags["mode"] == "redis" && m.Tags["outcome"] == "failure");
        Assert.Contains(capture.For(MultiplayerMetrics.ExpiryMembershipsRemovedName),
            m => m.Tags["mode"] == "redis" && m.Value == 3);

        // This guard applies to measurements from other concurrently running
        // test requests too: all values must remain bounded literals.
        foreach (var metric in new[]
        {
            MultiplayerMetrics.MutationDecisionsName,
            MultiplayerMetrics.ReadinessProbesName,
            MultiplayerMetrics.ExpirySweepsName,
            MultiplayerMetrics.ExpiryMembershipsRemovedName
        })
        foreach (var measurement in capture.For(metric))
        {
            Assert.True(measurement.Value > 0);
            var expectedKeys = metric switch
            {
                MultiplayerMetrics.MutationDecisionsName => new[] { "outcome", "policy" },
                MultiplayerMetrics.ReadinessProbesName => new[] { "outcome" },
                MultiplayerMetrics.ExpirySweepsName => new[] { "mode", "outcome" },
                _ => new[] { "mode" }
            };
            Assert.Equal(expectedKeys,
                measurement.Tags.Keys.OrderBy(x => x, StringComparer.Ordinal));
            foreach (var (key, value) in measurement.Tags)
            {
                var valid = key switch
                {
                    "policy" => value is "session" or "lobby",
                    "outcome" => value is "allowed" or "limited" or
                        "unavailable" or "ready" or "success" or "failure",
                    "mode" => value is "local" or "redis",
                    _ => false
                };
                Assert.True(valid, "Unbounded or sensitive metric label!");
                Assert.DoesNotContain("guest-", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("private", value, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", value, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task ProductionHttpReportsAllowedLimitedAndUnavailableByTrustedEndpointOnly()
    {
        var limiter = new Limiter { Outcome = MutationOutcome.Limited };
        using var server = Production(limiter);
        using var client = Client(server, production: true);
        using var capture = new MultiplayerMetricCapture();

        using (var request = Post("/api/sessions/guest", true))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        limiter.Outcome = MutationOutcome.Unavailable;
        using (var request = Post("/api/sessions/guest", true))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        limiter.Outcome = MutationOutcome.Allowed;
        string cookie;
        using (var request = Post("/api/sessions/guest", true))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        }
        using (var request = Post("/api/lobbies", true,
            new { teamIds = new[] { "white", "black" }, teamId = "white" }, cookie))
        using (var response = await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // An unrelated endpoint must not be tagged as rate limited.
        using (var health = await client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var events = capture.For(MultiplayerMetrics.MutationDecisionsName);
        Assert.Contains(events, x => x.Tags["policy"] == "session" &&
            x.Tags["outcome"] == "limited");
        Assert.Contains(events, x => x.Tags["policy"] == "session" &&
            x.Tags["outcome"] == "unavailable");
        Assert.Contains(events, x => x.Tags["policy"] == "session" &&
            x.Tags["outcome"] == "allowed");
        Assert.Contains(events, x => x.Tags["policy"] == "lobby" &&
            x.Tags["outcome"] == "allowed");
    }

    [Fact]
    public async Task DevelopmentUsesSameFinitePolicyLabelsWithLocalLimiter()
    {
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(server, production: false);
        using var capture = new MultiplayerMetricCapture();

        for (var i = 0; i < 13; i++)
        {
            using var request = Post("/api/sessions/guest", false);
            using var response = await client.SendAsync(request);
            Assert.Equal(i < 12 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests,
                response.StatusCode);
        }

        Assert.Contains(capture.For(MultiplayerMetrics.MutationDecisionsName),
            x => x.Tags["policy"] == "session" && x.Tags["outcome"] == "allowed");
        Assert.Contains(capture.For(MultiplayerMetrics.MutationDecisionsName),
            x => x.Tags["policy"] == "session" && x.Tags["outcome"] == "limited");
    }

    [Fact]
    public async Task ReadinessOnlyMeasuresRealProbesNotCachedHitsAndDetectsRecovery()
    {
        if (Address is null) return;
        using var redis = await ConnectionMultiplexer.ConnectAsync(Address);
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var clock = new ManualClock();
        var keys = new MutableKeys();
        var probe = new RedisDeploymentReadiness(redis,
            provider.GetRequiredService<IDistributedCache>(), keys, clock);
        using var capture = new MultiplayerMetricCapture();

        Assert.True(await probe.IsReadyAsync());
        Assert.True(await probe.IsReadyAsync());
        Assert.Equal(1, keys.Calls);

        keys.Broken = true;
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(await probe.IsReadyAsync());
        Assert.False(await probe.IsReadyAsync());
        Assert.Equal(2, keys.Calls);

        keys.Broken = false;
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(await probe.IsReadyAsync());
        Assert.Equal(3, keys.Calls);

        var events = capture.For(MultiplayerMetrics.ReadinessProbesName);
        Assert.True(events.Count(x => x.Tags["outcome"] == "ready") >= 2);
        Assert.Contains(events, x => x.Tags["outcome"] == "unavailable");
        Assert.All(events, x => Assert.Single(x.Tags));
    }

    [Fact]
    public async Task ExpiryMetricsDistinguishFailedRetryAndSuccessfulCleanupWithoutIdentityTags()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var coordinator = new InMemoryLobbyCoordinator(clock);
        var reader = new LobbyRosterReader(coordinator);
        var lobby = new LocalGuestLobbyRuntime(
            new GuestLobbyCommands(coordinator, reader), reader, coordinator);
        var player = "guest-11111111111111111111111111111111";
        coordinator.CreateWithOwner("room-private", ["white", "black"], player, "white", 10);
        var index = new ExpiryIndex { Broken = true };
        using var worker = new GuestSessionExpiryWorker(
            registry, coordinator, clock, lobby, index);
        using var capture = new MultiplayerMetricCapture();

        await Assert.ThrowsAsync<IOException>(() => worker.SweepOnceAsync());
        Assert.Equal(0, index.Acknowledgments);
        Assert.Equal(1, coordinator.StoredRoomCount);

        index.Broken = false;
        Assert.Equal(1, await worker.SweepOnceAsync());
        Assert.Equal(1, index.Acknowledgments);
        Assert.Equal(0, coordinator.StoredRoomCount);
        Assert.Equal(0, await worker.SweepOnceAsync());

        Assert.Contains(capture.For(MultiplayerMetrics.ExpirySweepsName),
            x => x.Tags["mode"] == "redis" && x.Tags["outcome"] == "failure");
        Assert.Contains(capture.For(MultiplayerMetrics.ExpirySweepsName),
            x => x.Tags["mode"] == "redis" && x.Tags["outcome"] == "success");
        Assert.Contains(capture.For(MultiplayerMetrics.ExpiryMembershipsRemovedName),
            x => x.Tags["mode"] == "redis" && x.Value == 1);
        Assert.All(capture.For(MultiplayerMetrics.ExpiryMembershipsRemovedName),
            x => Assert.Single(x.Tags));
    }
}
