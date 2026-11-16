using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
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

        // The standalone fixture has no ACL; use a disposable test-only
        // observation mux, NEVER enable AllowAdmin on the game client.
        var observerOptions = ConfigurationOptions.Parse(Connection);
        Assert.False(observerOptions.AllowAdmin);
        observerOptions.AllowAdmin = true;
        using var redis = await ConnectionMultiplexer.ConnectAsync(observerOptions);
        var servers = redis.GetEndPoints(configuredOnly: false)
            .Select(endpoint => redis.GetServer(endpoint)).ToArray();
        Assert.Contains(servers, endpoint => endpoint.IsConnected);
        var connected = servers.First(endpoint => endpoint.IsConnected);
        try
        {
            // Directly assert the server-level INFO command; if a client
            // blocks it, print ONLY a bounded exception category in CI.
            var raw = await connected.ExecuteAsync("INFO", "replication");
            Assert.Equal(RedisReplicationRole.Primary,
                RedisReplicationInfo.Parse(raw.ToString()).Role);
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


    private const char SignalRRecordSeparator = '\u001e';

    private static async Task SendSignalRJsonAsync(
        WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json + SignalRRecordSeparator);
        await socket.SendAsync(new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveSignalRJsonAsync(
        WebSocket socket, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[8192];
        var remaining = new StringBuilder();
        while (true)
        {
            var received = await socket.ReceiveAsync(
                new ArraySegment<byte>(bytes), cancellationToken);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            remaining.Append(Encoding.UTF8.GetString(bytes, 0, received.Count));
            var fragment = remaining.ToString();
            var separator = fragment.IndexOf(SignalRRecordSeparator);
            while (separator >= 0)
            {
                var frame = fragment[..separator];
                fragment = fragment[(separator + 1)..];
                if (frame.Length > 0)
                {
                    using var parsed = JsonDocument.Parse(frame);
                    if (predicate(parsed.RootElement))
                        return parsed.RootElement.Clone();
                }
                separator = fragment.IndexOf(SignalRRecordSeparator);
            }
            remaining.Clear();
            remaining.Append(fragment);
            Assert.True(remaining.Length < 65536, "Oversized SignalR JSON frame.");
        }
    }

    private static async Task<WebSocket> ConnectTlsProductionHubAsync(
        WebApplicationFactory<Program> server, string cookie,
        CancellationToken cancellationToken)
    {
        var webSockets = server.Server.CreateWebSocketClient();
        webSockets.ConfigureRequest = request =>
        {
            request.Headers["Cookie"] = cookie;
            // Browser-equivalent, verified same-origin WSS upgrade: no
            // X-Forwarded-Proto, spoofed user ID or debug auth override.
            request.Headers["Origin"] = "https://game.example";
        };
        var socket = await webSockets.ConnectAsync(
            new Uri("wss://game.example/hubs/lobbies"), cancellationToken);
        try
        {
            await SendSignalRJsonAsync(socket,
                "{\"protocol\":\"json\",\"version\":1}", cancellationToken);
            var handshake = await ReceiveSignalRJsonAsync(socket,
                message => !message.TryGetProperty("type", out _),
                cancellationToken);
            Assert.Equal("{}", handshake.GetRawText());
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonElement> InvokeTlsRosterAsync(
        WebSocket socket, string room, string invocation,
        CancellationToken cancellationToken)
    {
        await SendSignalRJsonAsync(socket, JsonSerializer.Serialize(new
        {
            type = 1, invocationId = invocation,
            target = "GetRoster", arguments = new[] { room }
        }), cancellationToken);
        return await ReceiveSignalRJsonAsync(socket,
            message => message.TryGetProperty("type", out var type) &&
                type.GetInt32() == 3 &&
                message.TryGetProperty("invocationId", out var id) &&
                id.GetString() == invocation,
            cancellationToken);
    }

    private static void AssertOpaqueRosterDenial(
        JsonElement completion, string room, string alice, string bob)
    {
        Assert.True(completion.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
        Assert.False(completion.TryGetProperty("result", out _));
        var json = completion.GetRawText();
        Assert.DoesNotContain(room, json, StringComparison.Ordinal);
        Assert.DoesNotContain(alice, json, StringComparison.Ordinal);
        Assert.DoesNotContain(bob, json, StringComparison.Ordinal);
        Assert.DoesNotContain("participants", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrossNodeProductionTlsRealSignalRWebSocketsHonorJoinAndRemoteLogout()
    {
        // This case executes under the actual TLS Redis CI filter. Standard
        // no-Redis xUnit runs intentionally skip external service setup.
        if (Connection is null) return;
        Assert.True(ConfigurationOptions.Parse(Connection).Ssl);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var first = Server();
        using var second = Server();
        using var visitorA = Client(first);
        using var visitorB = Client(second);
        var (alice, aliceCookie) = await Login(visitorA);
        var (bob, bobCookie) = await Login(visitorB);
        Assert.NotEqual(alice, bob);

        using var aliceA = Client(first, aliceCookie);
        using var aliceB = Client(second, aliceCookie);
        using var bobB = Client(second, bobCookie);
        using var bobA = Client(first, bobCookie);

        string room;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var created = await aliceA.SendAsync(create, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var json = JsonDocument.Parse(
                await created.Content.ReadAsStringAsync(timeout.Token));
            room = json.RootElement.GetProperty("lobbyId").GetString()!;
        }

        // Three actual authenticated SignalR JSON/WebSocket transports:
        // Alice's ticket issued by A works on both A and B (shared
        // persistent DP ring + session authority); Bob connects on B.
        using var socketAliceA = await ConnectTlsProductionHubAsync(
            first, aliceCookie, timeout.Token);
        using var socketAliceB = await ConnectTlsProductionHubAsync(
            second, aliceCookie, timeout.Token);
        using var socketBobB = await ConnectTlsProductionHubAsync(
            second, bobCookie, timeout.Token);

        var outsider = await InvokeTlsRosterAsync(socketBobB, room,
            "before-join", timeout.Token);
        // Missing membership and missing room must be indistinguishable on
        // this same authenticated WebSocket, before any join occurs.
        var missing = await InvokeTlsRosterAsync(socketBobB,
            "room-00000000000000000000000000000000",
            "missing-room", timeout.Token);
        AssertOpaqueRosterDenial(outsider, room, alice, bob);
        AssertOpaqueRosterDenial(missing, room, alice, bob);
        Assert.Equal(missing.GetProperty("error").GetString(),
            outsider.GetProperty("error").GetString());

        var aliceBefore = await InvokeTlsRosterAsync(socketAliceA,
            room, "alice-before", timeout.Token);
        Assert.False(aliceBefore.TryGetProperty("error", out _));
        Assert.Equal(alice, Assert.Single(aliceBefore.GetProperty("result")
            .GetProperty("participants").EnumerateArray())
            .GetProperty("playerId").GetString());

        // Bob's previously denied socket becomes authorized after a real
        // cross-node Redis Lua admission (not a cached positive/negative
        // authorization decision in a HubCallerContext mock).
        using (var join = Mutation(HttpMethod.Post,
            "/api/lobbies/" + room + "/join",
            new { teamId = "black", expectedRevision = 1L }))
        using (var joined = await bobB.SendAsync(join, timeout.Token))
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        foreach (var (socket, invocation) in new[]
        {
            (socketAliceA, "alice-on-A"),
            (socketAliceB, "alice-on-B"),
            (socketBobB, "bob-on-B")
        })
        {
            var completed = await InvokeTlsRosterAsync(socket, room, invocation,
                timeout.Token);
            Assert.False(completed.TryGetProperty("error", out _));
            var roster = completed.GetProperty("result");
            Assert.Equal(room, roster.GetProperty("lobbyId").GetString());
            Assert.Equal(2, roster.GetProperty("revision").GetInt64());
            var members = roster.GetProperty("participants").EnumerateArray()
                .Select(member => member.GetProperty("playerId").GetString()).ToArray();
            Assert.Equal(2, members.Length);
            Assert.Contains(alice, members);
            Assert.Contains(bob, members);
        }

        // Logout on node B revokes Alice's Redis TLS ticket and fences stale
        // writes. Both already-open sockets must re-authorize, including the
        // socket served by independent node A.
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var revoked = await aliceB.SendAsync(logout, timeout.Token))
        {
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
            Assert.True(revoked.Headers.CacheControl?.NoStore == true);
        }

        AssertOpaqueRosterDenial(
            await InvokeTlsRosterAsync(socketAliceA, room,
                "alice-after-on-A", timeout.Token), room, alice, bob);
        AssertOpaqueRosterDenial(
            await InvokeTlsRosterAsync(socketAliceB, room,
                "alice-after-on-B", timeout.Token), room, alice, bob);

        using (var meA = await aliceA.GetAsync("/api/sessions/me", timeout.Token))
        using (var meB = await aliceB.GetAsync("/api/sessions/me", timeout.Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, meA.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, meB.StatusCode);
            Assert.False(meA.Headers.Contains("Set-Cookie"));
            Assert.False(meB.Headers.Contains("Set-Cookie"));
        }

        var survivor = await InvokeTlsRosterAsync(socketBobB,
            room, "bob-survives", timeout.Token);
        Assert.False(survivor.TryGetProperty("error", out _));
        var surviving = survivor.GetProperty("result");
        Assert.Equal(3, surviving.GetProperty("revision").GetInt64());
        Assert.Equal(bob, Assert.Single(surviving.GetProperty("participants")
            .EnumerateArray()).GetProperty("playerId").GetString());
        Assert.DoesNotContain(alice, survivor.GetRawText(), StringComparison.Ordinal);
        using (var validA = await bobA.GetAsync("/api/sessions/me", timeout.Token))
        using (var validB = await bobB.GetAsync("/api/sessions/me", timeout.Token))
        {
            Assert.Equal(HttpStatusCode.OK, validA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, validB.StatusCode);
        }

        // An old cookie cannot negotiate a *new* SignalR connection on
        // either node after revocation; no location or replacement cookie.
        foreach (var client in new[] { aliceA, aliceB })
        {
            using var rejected = await client.PostAsync(
                "/hubs/lobbies/negotiate?negotiateVersion=1",
                content: null, timeout.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.True(rejected.Headers.CacheControl?.NoStore == true);
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.False(rejected.Headers.Contains("Location"));
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
                // The Redis authority is unavailable, not a proven
                // revoked ticket. Preserve the same signed cookie for retry
                // while denying all access during the outage.
                Assert.Equal(HttpStatusCode.ServiceUnavailable, deniedA.StatusCode);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, deniedB.StatusCode);
                Assert.Equal("no-store", deniedA.Headers.CacheControl?.ToString());
                Assert.False(deniedA.Headers.Contains("Set-Cookie"));
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
