using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Runs in CI against a TLS-only Redis server with a short-lived certificate
/// issued by an ephemeral CA trusted by the runner. This test intentionally
/// uses the unmodified Production DI for both Redis connections, the durable
/// expiry index, quotas/fences, and encrypted persistent Data Protection.
/// Neither the server certificate nor trust validation is bypassed.
/// </summary>
[Collection("Production Redis TLS integration")]
public sealed class RedisTlsProductionTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_TLS");

    private static WebApplicationFactory<Program> Server()
    {
        var connection = Connection
            ?? throw new InvalidOperationException("TLS Redis CI endpoint is required.");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            builder.UseSetting(GuestSessionRedisConfiguration.ConnectionStringKey, connection);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [GuestSessionRedisConfiguration.ConnectionStringKey] = connection,
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            // Deliberately NO ConfigureTestServices: real TLS session cache,
            // real TLS multiplexer, Redis lobby, rate limiter and expiry worker.
        });
    }

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

    private static HttpRequestMessage Mutation(HttpMethod method, string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string PlayerId, string Cookie)> Login(HttpClient client)
    {
        using var req = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var result = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var cookie = Assert.Single(result.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    [Fact]
    public async Task ReadOnlyProductionReplicationMonitorUsesAuthenticatedTlsWithoutACollector()
    {
        if (Connection is null) return; // Dedicated TLS CI fixture.
        using var capture = new MultiplayerMetricCapture();
        using var server = Server();
        using var client = Client(server);
        using (var ready = await client.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var redis = server.Services.GetRequiredService<IConnectionMultiplexer>();
        var servers = redis.GetEndPoints(configuredOnly: false)
            .Select(endpoint => redis.GetServer(endpoint)).ToArray();
        Assert.Contains(servers, endpoint => endpoint.IsConnected);
        var connected = servers.First(endpoint => endpoint.IsConnected);
        try
        {
            // Directly assert the server-level INFO command; if a client
            // blocks it, print ONLY a bounded exception category in CI.
            var raw = await connected.InfoRawAsync("replication");
            Assert.Equal(RedisReplicationRole.Primary,
                RedisReplicationInfo.Parse(raw).Role);
        }
        catch (Exception ex)
        {
            Assert.Fail("Safe INFO command failure type: " + ex.GetType().Name);
        }
        var sampler = new RedisReplicationSampler(redis);
        await sampler.SampleOnceAsync();

        // Print ONLY the finite enum labels (never Redis connection
        // details or exception messages) when a real TLS integration fails.
        var observed = capture.For(MultiplayerMetrics.RedisReplicationObservationsName);
        Assert.True(observed.Any(sample =>
                sample.Tags["role"] == "master" &&
                sample.Tags["outcome"] == "ok" &&
                sample.Tags["link"] == "na"),
            "Safe INFO probe status: " + string.Join("; ",
                observed.Select(sample =>
                    sample.Tags["role"] + "/" +
                    sample.Tags["link"] + "/" +
                    sample.Tags["outcome"])));
        Assert.Contains(
            capture.ForDurations(MultiplayerMetrics.RedisConnectedReplicasName),
            sample => sample.Seconds == 0 && sample.Tags.Count == 0);
        Assert.Empty(capture.ForDurations(
            MultiplayerMetrics.RedisReplicaAckLagSecondsName));
        using var healthy = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
    }

    [Fact]
    public async Task TwoRealProductionNodesShareTlsSessionsReadinessRoomsAndLogoutFence()
    {
        if (Connection is null) return; // Dedicated TLS job always sets this.
        Assert.True(ConfigurationOptions.Parse(Connection).Ssl);

        using var first = Server();
        using var second = Server();
        // Exercise both independently configured, real Redis TLS session
        // connectors without relying on startup race in the readiness probe.
        await first.Services.GetRequiredService<IDistributedCache>()
            .GetAsync("shirokuro-tls-ci-warmup");
        await second.Services.GetRequiredService<IDistributedCache>()
            .GetAsync("shirokuro-tls-ci-warmup");

        using var visitorA = Client(first);
        using var visitorB = Client(second);
        using (var readinessA = await visitorA.GetAsync("/api/ready"))
        using (var readinessB = await visitorB.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, readinessA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, readinessB.StatusCode);
            Assert.Contains("\"ready\"", await readinessA.Content.ReadAsStringAsync());
            Assert.Contains("\"ready\"", await readinessB.Content.ReadAsStringAsync());
        }

        var (alice, aliceCookie) = await Login(visitorA);
        var (bob, bobCookie) = await Login(visitorB);
        Assert.NotEqual(alice, bob);

        using var aliceA = Client(first, aliceCookie);
        using var aliceB = Client(second, aliceCookie);
        using var bobA = Client(first, bobCookie);
        using var bobB = Client(second, bobCookie);

        // A ticket issued on A is valid on B: both the encrypted Data
        // Protection ring and the Redis session store must be shared.
        using (var crossNodeMe = await aliceB.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, crossNodeMe.StatusCode);

        string room;
        long revision;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var result = await aliceA.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, result.StatusCode);
            using var doc = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            room = doc.RootElement.GetProperty("lobbyId").GetString()!;
            revision = doc.RootElement.GetProperty("revision").GetInt64();
        }

        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                   new { teamId = "black", expectedRevision = revision }))
        using (var result = await bobB.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);

        using (var roster = await bobA.GetAsync("/api/lobbies/" + room + "/roster"))
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);

        // Logout on B revokes the Redis cache token, persists an encrypted
        // already-due cleanup item, installs the Redis Lua mutation fence,
        // and deletes Alice while preserving Bob's membership.
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var result = await aliceB.SendAsync(logout))
        {
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
            Assert.Contains("expires=", Assert.Single(result.Headers.GetValues("Set-Cookie")),
                StringComparison.OrdinalIgnoreCase);
        }

        using (var replayA = await aliceA.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, replayA.StatusCode);
        using (var replayB = await aliceB.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, replayB.StatusCode);

        // Simulate a request on A whose cookie was checked BEFORE logout.
        // The Redis Lua fence still blocks a late write from that actor.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            first.Services.GetRequiredService<IAsyncGuestLobby>()
                .JoinAsync(room, alice, "white", null));

        using (var surviving = await bobB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
            using var doc = JsonDocument.Parse(await surviving.Content.ReadAsStringAsync());
            var participants = doc.RootElement.GetProperty("participants").EnumerateArray()
                .ToArray();
            Assert.Single(participants);
            Assert.Equal(bob, participants[0].GetProperty("playerId").GetString());
        }
    }

    // Only the dedicated, opt-in CI job may control this fixture. The
    // executable, action and container are hard-coded; no shell, request
    // value, guest identity, Redis credential or user input is interpolated.
    private static async Task ControlTlsRedisAsync(string action)
    {
        if (action is not ("pause" or "unpause"))
            throw new ArgumentOutOfRangeException(nameof(action));
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add(action);
        start.ArgumentList.Add("shirokuro-ci-redis-tls");
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Unable to control isolated test Redis.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill();
            throw new TimeoutException("Isolated Redis control timed out.");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Isolated Redis control command failed.");
    }

    private static async Task AssertEventuallyReadyAsync(HttpClient client)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(35))
        {
            using var response = await client.GetAsync("/api/ready");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                return;
            }
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        Assert.Fail("Production Redis node did not recover readiness within the test deadline.");
    }

    [Fact]
    public async Task TransientTlsRedisOutageFailsClosedThenExistingNodesAndCookiesRecover()
    {
        if (Connection is null ||
            Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_CHAOS") != "1")
            return; // This can pause only the explicitly opted-in ephemeral CI fixture.

        Assert.True(ConfigurationOptions.Parse(Connection).Ssl);
        using var first = Server();
        using var second = Server();
        using var visitorA = Client(first);
        using var visitorB = Client(second);

        // Verify both Redis client types and Data Protection work before we
        // deliberately interrupt the still-running Production TestServers.
        await AssertEventuallyReadyAsync(visitorA);
        await AssertEventuallyReadyAsync(visitorB);

        var (aliceId, aliceCookie) = await Login(visitorA);
        var (bobId, bobCookie) = await Login(visitorB);
        using var aliceA = Client(first, aliceCookie);
        using var aliceB = Client(second, aliceCookie);
        using var bobA = Client(first, bobCookie);
        using var bobB = Client(second, bobCookie);

        string room;
        long revision;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var response = await aliceA.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            room = doc.RootElement.GetProperty("lobbyId").GetString()!;
            revision = doc.RootElement.GetProperty("revision").GetInt64();
        }

        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                   new { teamId = "black", expectedRevision = revision }))
        using (var joined = await bobB.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        using (var before = await bobA.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var doc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
            revision = doc.RootElement.GetProperty("revision").GetInt64();
            Assert.Equal(2, doc.RootElement.GetProperty("participants").GetArrayLength());
        }

        // docker pause freezes the *same* TLS Redis process with all existing
        // keys intact. This tests outage/reconnection, NOT replica promotion.
        var paused = false;
        try
        {
            await ControlTlsRedisAsync("pause");
            paused = true;

            // Readiness caches previous results for 3s. Do not assert stale
            // cached success represents a live Redis connection.
            await Task.Delay(RedisDeploymentReadiness.CachedFor +
                TimeSpan.FromMilliseconds(300));
            using (var health = await visitorA.GetAsync("/api/health"))
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            using (var unreadyA = await visitorA.GetAsync("/api/ready"))
            using (var unreadyB = await visitorB.GetAsync("/api/ready"))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unreadyA.StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, unreadyB.StatusCode);
                Assert.Equal("no-store", unreadyA.Headers.CacheControl?.ToString());
                Assert.Equal("{\"status\":\"unavailable\"}",
                    await unreadyA.Content.ReadAsStringAsync());
            }

            // Already-issued cookies must NOT authenticate from a positive
            // per-node cache. Do not treat a temporary Redis outage as a
            // revocation or permanently clear the browser's original cookie.
            using (var deniedA = await aliceA.GetAsync("/api/sessions/me"))
            using (var deniedB = await bobB.GetAsync("/api/sessions/me"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, deniedA.StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, deniedB.StatusCode);
                Assert.DoesNotContain(aliceId, await deniedA.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            // Redis rate limiting also fails closed rather than falling
            // back to local issuance or accepting a new guest.
            using var issue = Mutation(HttpMethod.Post, "/api/sessions/guest");
            using var refused = await visitorA.SendAsync(issue);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("no-store", refused.Headers.CacheControl?.ToString());
            Assert.DoesNotContain("guest-", await refused.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            if (paused)
                await ControlTlsRedisAsync("unpause");
        }

        // Same two Production hosts, same original signed cookies and same
        // Redis room data: no restart, rediscovery or reauthentication.
        await AssertEventuallyReadyAsync(visitorA);
        await AssertEventuallyReadyAsync(visitorB);
        using (var validA = await aliceB.GetAsync("/api/sessions/me"))
        using (var validB = await bobA.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.OK, validA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, validB.StatusCode);
        }

        using (var after = await bobB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            using var doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            Assert.Equal(revision, doc.RootElement.GetProperty("revision").GetInt64());
            var participants = doc.RootElement.GetProperty("participants")
                .EnumerateArray().Select(x =>
                    x.GetProperty("playerId").GetString()).ToArray();
            Assert.Equal(2, participants.Length);
            Assert.Contains(aliceId, participants);
            Assert.Contains(bobId, participants);
        }

        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await aliceB.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var revoked = await aliceA.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);

        using var surviving = await bobB.GetAsync("/api/lobbies/" + room + "/roster");
        Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
        using var roster = JsonDocument.Parse(await surviving.Content.ReadAsStringAsync());
        Assert.Equal(bobId, Assert.Single(roster.RootElement.GetProperty("participants")
            .EnumerateArray()).GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task WrongTlsServerNameFailsCertificateValidationWithoutTrustBypass()
    {
        if (Connection is null) return;
        var options = ConfigurationOptions.Parse(Connection);
        Assert.True(options.Ssl);
        // The ephemeral certificate also covers the isolated Docker master
        // DNS name for encrypted replication. This deliberately unrelated
        // hostname must still be rejected by standard TLS verification.
        options.SslHost = "not-the-redis-server.invalid";
        options.ConnectRetry = 0;
        options.ConnectTimeout = 1200;
        options.SyncTimeout = 1200;
        options.AbortOnConnectFail = true;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await ConnectionMultiplexer.ConnectAsync(options)
                .WaitAsync(TimeSpan.FromSeconds(8));
            await rejected.GetDatabase().PingAsync()
                .WaitAsync(TimeSpan.FromSeconds(4));
        });
    }

    [Fact]
    public async Task TlsOnlyPortRejectsUnencryptedRedisProtocol()
    {
        if (Connection is null) return;
        var plaintext = ConfigurationOptions.Parse("localhost:6380");
        plaintext.Ssl = false;
        plaintext.ConnectRetry = 0;
        plaintext.ConnectTimeout = 1200;
        plaintext.SyncTimeout = 1200;
        plaintext.AbortOnConnectFail = true;

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await ConnectionMultiplexer.ConnectAsync(plaintext)
                .WaitAsync(TimeSpan.FromSeconds(8));
            await rejected.GetDatabase().PingAsync()
                .WaitAsync(TimeSpan.FromSeconds(4));
        });
    }
}
