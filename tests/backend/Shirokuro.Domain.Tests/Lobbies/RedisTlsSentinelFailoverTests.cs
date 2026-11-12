using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Dedicated CI-only Redis TLS master, replica and three-Sentinel quorum.
/// Unlike manual promotion (#52), both original Production hosts survive the
/// automatic Sentinel failover and must rediscover the managed primary via
/// independent lobby and Microsoft IDistributedCache Redis connections.
/// </summary>
public sealed class RedisTlsSentinelFailoverTests
{
    private const string OldMaster = "shirokuro-ci-sentinel-master";
    private const string Replica = "shirokuro-ci-sentinel-replica";
    private const string Sentinel = "shirokuro-ci-sentinel-1";
    private const string DockerCommandFailure = "Sentinel CI Docker command failed.";
    private static string? SentinelConnection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_SENTINEL_TLS");

    private static WebApplicationFactory<Program> Server(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            builder.UseSetting(
                GuestSessionRedisConfiguration.ConnectionStringKey, connection);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [GuestSessionRedisConfiguration.ConnectionStringKey] = connection,
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            // Deliberately NO ConfigureTestServices: both independent managed
            // Sentinel connections, cookie auth, fencing and durable workers
            // use exactly the Production dependency injection setup.
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

    private static async Task<(string Id, string Cookie)> Login(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!,
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
    }

    private static string SessionKey(WebApplicationFactory<Program> server, string cookie)
    {
        var encoded = Uri.UnescapeDataString(
            cookie[(cookie.IndexOf('=') + 1)..]);
        var protector = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
        var ticket = protector.Unprotect(encoded)
            ?? throw new InvalidOperationException("CI ticket could not be decrypted.");
        var token = ticket.Principal.FindFirst(GuestSessions.SessionIdClaim)?.Value
            ?? throw new InvalidOperationException("CI guest token missing.");
        return "shirokuro:guest-session:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    private static async Task<string> DockerAsync(params string[] arguments)
    {
        var psi = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Sentinel CI Docker fixture unavailable.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill();
            throw new TimeoutException("Sentinel CI Docker operation timed out.");
        }
        var value = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(DockerCommandFailure);
        return value.Trim();
    }

    /// <summary>
    /// Redis CLI connections to a replica can briefly fail while Sentinel
    /// promotes it. Retry only the expected sanitized CLI exit failure in a
    /// bounded window; permanent failures must still fail the integration.
    /// Neither the exception nor raw CLI stderr may enter CI output.
    /// </summary>
    private static async Task<bool> AwaitPromotionAsync(
        Func<Task<string>> readElection, Func<Task<string>> readReplicaRole,
        int attempts, TimeSpan delay)
    {
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                var elected = await readElection();
                var role = await readReplicaRole();
                if (elected.Contains("6385", StringComparison.Ordinal) &&
                    role.Contains("role:master", StringComparison.Ordinal))
                    return true;
            }
            catch (InvalidOperationException error) when (
                error.Message == DockerCommandFailure)
            {
                // The Docker container can be alive while redis-cli reports
                // a temporarily failed TLS connection during role promotion.
                // Never turn this into success without independently seeing
                // both the Sentinel election and the promoted replica role.
            }
            if (i + 1 < attempts)
                await Task.Delay(delay);
        }
        return false;
    }

    [Fact]
    public async Task PromotionPollRetriesTransientCliFailuresAndWaitsForBothSignals()
    {
        var sentinels = 0;
        var replicas = 0;
        var promoted = await AwaitPromotionAsync(
            () => { sentinels++; return Task.FromResult("127.0.0.1 6385"); },
            () =>
            {
                replicas++;
                return replicas == 1
                    ? Task.FromException<string>(
                        new InvalidOperationException(DockerCommandFailure))
                    : Task.FromResult(replicas == 2 ? "role:slave" : "role:master");
            }, 3, TimeSpan.Zero);
        Assert.True(promoted);
        Assert.Equal(3, sentinels);
        Assert.Equal(3, replicas);
    }

    [Fact]
    public async Task PromotionPollDoesNotHidePermanentCliOutage()
    {
        var calls = 0;
        var promoted = await AwaitPromotionAsync(
            () =>
            {
                calls++;
                return Task.FromException<string>(
                    new InvalidOperationException(DockerCommandFailure));
            },
            () => Task.FromResult("role:master"),
            4, TimeSpan.Zero);
        Assert.False(promoted);
        Assert.Equal(4, calls);
    }

    private static Task<string> CliAsync(string container, int port,
        params string[] arguments)
    {
        if (container is not (OldMaster or Replica or Sentinel))
            throw new ArgumentOutOfRangeException(nameof(container));
        if (port is not (6384 or 6385 or 26380))
            throw new ArgumentOutOfRangeException(nameof(port));
        var sentinel = container == Sentinel;
        var password = Environment.GetEnvironmentVariable(
            sentinel ? "SHIROKURO_TEST_SENTINEL_CLIENT_CLI_PASSWORD"
                : "SHIROKURO_TEST_SENTINEL_REDIS_CLI_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Authenticated CI CLI configuration missing.");
        // The password is masked in the runner and passed to redis-cli as
        // an environment variable, never appended to the CLI output.
        return DockerAsync([
            "exec", "-e", "REDISCLI_AUTH=" + password, container, "redis-cli", "--tls",
            "--cacert", "/certs/ca.crt",
            "--user", sentinel ? "shirokuro-sentinel-client" : "shirokuro-app",
            "-h", "localhost",
            "-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            .. arguments
        ]);
    }

    private static async Task<bool> ReadyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/ready");
        Assert.True(response.StatusCode is HttpStatusCode.OK
            or HttpStatusCode.ServiceUnavailable);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(response.StatusCode == HttpStatusCode.OK ? "ready" : "unavailable",
            json.RootElement.GetProperty("status").GetString());
        return response.StatusCode == HttpStatusCode.OK;
    }

    [Fact]
    public async Task ThreeTlsSentinelsAutomaticallySwitchBothProductionRedisClientsWithoutRestart()
    {
        var connection = SentinelConnection;
        if (string.IsNullOrWhiteSpace(connection) ||
            Environment.GetEnvironmentVariable("SHIROKURO_TEST_SENTINEL_CHAOS") != "1")
            return;

        var config = ConfigurationOptions.Parse(connection);
        Assert.True(config.Ssl);
        Assert.Equal("shirokuro-main", config.ServiceName);
        Assert.Equal(3, config.EndPoints.Count);
        Assert.Equal("shirokuro-app", config.User);
        Assert.Equal("shirokuro-sentinel-client", config.SentinelUser);
        Assert.False(string.IsNullOrEmpty(config.Password));
        Assert.False(string.IsNullOrEmpty(config.SentinelPassword));
        Assert.NotEqual(config.Password, config.SentinelPassword);

        // A Sentinel discovery client must be isolated from Redis write
        // authority; unauthenticated and invalid AUTH must fail closed.
        // Explicitly connect with the same independent options used by
        // both Production Redis clients.
        var aclProbe = config.Clone();
        // Test the server ACL, not merely the library-side admin command
        // switch. Both real API hosts below still use AllowAdmin=false.
        aclProbe.AllowAdmin = true;
        using (var authenticated = await ConnectionMultiplexer.ConnectAsync(aclProbe))
        {
            var database = authenticated.GetDatabase();
            await Assert.ThrowsAsync<RedisServerException>(() =>
                database.StringSetAsync("outside-shirokuro-namespace", "denied"));
            await Assert.ThrowsAsync<RedisServerException>(() =>
                database.ExecuteAsync("FLUSHDB"));
            await Assert.ThrowsAsync<RedisServerException>(() =>
                database.ExecuteAsync("CONFIG", "GET", "maxmemory"));
        }

        async Task MustRejectAsync(ConfigurationOptions options)
        {
            options.AbortOnConnectFail = true;
            options.ConnectRetry = 0;
            options.ConnectTimeout = 1500;
            options.SyncTimeout = 1500;
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var connectionAttempt =
                    await ConnectionMultiplexer.ConnectAsync(options)
                        .WaitAsync(TimeSpan.FromSeconds(12));
                await connectionAttempt.GetDatabase().PingAsync()
                    .WaitAsync(TimeSpan.FromSeconds(4));
            });
        }

        var badSentinel = config.Clone();
        badSentinel.SentinelPassword = "deliberately-wrong-sentinel-ci-password";
        await MustRejectAsync(badSentinel);

        var badRedis = config.Clone();
        badRedis.Password = "deliberately-wrong-primary-ci-password";
        await MustRejectAsync(badRedis);

        // A distinct unauthenticated transport must not talk to Sentinel,
        // even though the certificate is CA-valid and the port is reachable.
        var unauthenticated = ConfigurationOptions.Parse(
            "localhost:26380,ssl=true,sslHost=localhost," +
            "abortConnect=true,connectRetry=0,connectTimeout=1500");
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var unauthenticatedSentinel =
                await ConnectionMultiplexer.SentinelConnectAsync(unauthenticated)
                    .WaitAsync(TimeSpan.FromSeconds(12));
            await unauthenticatedSentinel.GetServer("localhost", 26380)
                .PingAsync().WaitAsync(TimeSpan.FromSeconds(4));
        });

        // Capture connection telemetry BEFORE starting either API host.
        // No Redis endpoint, password, player id or exception reaches tags;
        // this observes the lobby and guest-cache connections independently.
        using var connectorEvents = new MultiplayerMetricCapture();

        // These two Production TestServers are deliberately never rebuilt
        // between creation and automatic Sentinel failover.
        using var first = Server(connection);
        using var second = Server(connection);
        using var visitorA = Client(first);
        using var visitorB = Client(second);
        Assert.True(await ReadyAsync(visitorA));
        Assert.True(await ReadyAsync(visitorB));

        var (alice, cookieA) = await Login(visitorA);
        var (bob, cookieB) = await Login(visitorB);
        using var memberA = Client(first, cookieA);
        using var memberB = Client(second, cookieB);

        string room;
        long revision;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var response = await memberA.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            room = data.RootElement.GetProperty("lobbyId").GetString()!;
            revision = data.RootElement.GetProperty("revision").GetInt64();
        }
        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                   new { teamId = "black", expectedRevision = revision }))
        using (var response = await memberB.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var roster = await memberB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var data = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            revision = data.RootElement.GetProperty("revision").GetInt64();
            Assert.Equal(2, data.RootElement.GetProperty("participants").GetArrayLength());
        }

        var keyA = SessionKey(first, cookieA);
        var keyB = SessionKey(second, cookieB);
        var roomKey = "shirokuro:lobby:{rooms}:v2:room:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(room)));
        var replicated = false;
        for (var i = 0; i < 65; i++)
        {
            var info = await CliAsync(Replica, 6385, "INFO", "replication");
            var tokenA = await CliAsync(Replica, 6385, "EXISTS", keyA);
            var tokenB = await CliAsync(Replica, 6385, "EXISTS", keyB);
            var roomPresent = await CliAsync(Replica, 6385, "EXISTS", roomKey);
            if (info.Contains("master_link_status:up", StringComparison.Ordinal) &&
                tokenA == "1" && tokenB == "1" && roomPresent == "1")
            {
                replicated = true;
                break;
            }
            await Task.Delay(200);
        }
        Assert.True(replicated, "Sentinel replica did not receive prior writes.");

        // Stop the old primary. The Sentinels independently form quorum 2
        // and promote the replica: this test NEVER sends REPLICAOF NO ONE.
        await DockerAsync("stop", "-t", "1", OldMaster);

        var converged = await AwaitPromotionAsync(
            () => CliAsync(Sentinel, 26380, "SENTINEL",
                "get-master-addr-by-name", "shirokuro-main"),
            () => CliAsync(Replica, 6385, "INFO", "replication"),
            110, TimeSpan.FromMilliseconds(500));
        Assert.True(converged, "Sentinel quorum failed to promote a new primary.");

        // The original API instances must eventually learn the new managed
        // primary on BOTH connectors, not just the lobby multiplexer.
        // During election, authorization/mutations may fail closed. Don't
        // claim a guaranteed zero-time or zero-loss transition.
        var admitted = false;
        for (var i = 0; i < 90; i++)
        {
            try
            {
                if (await ReadyAsync(visitorA) && await ReadyAsync(visitorB))
                {
                    using var a = await memberA.GetAsync("/api/sessions/me");
                    using var b = await memberB.GetAsync("/api/sessions/me");
                    if (a.StatusCode == HttpStatusCode.OK &&
                        b.StatusCode == HttpStatusCode.OK)
                    {
                        admitted = true;
                        break;
                    }
                }
            }
            catch (RedisException)
            {
                // Election and rediscovery are bounded eventual operations.
            }
            await Task.Delay(500);
        }
        Assert.True(admitted,
            "Original Production Redis clients did not rediscover Sentinel primary.");

        using (var restored = await memberB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
            using var data = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
            Assert.Equal(revision, data.RootElement.GetProperty("revision").GetInt64());
            var members = data.RootElement.GetProperty("participants").EnumerateArray()
                .Select(x => x.GetProperty("playerId").GetString()).ToArray();
            Assert.Equal(2, members.Length);
            Assert.Contains(alice, members);
            Assert.Contains(bob, members);
        }
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await memberA.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var denied = await memberA.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            first.Services.GetRequiredService<IAsyncGuestLobby>()
                .JoinAsync(room, alice, "white", null));
        using (var surviving = await memberB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
            using var data = JsonDocument.Parse(await surviving.Content.ReadAsStringAsync());
            Assert.Equal(bob, Assert.Single(data.RootElement
                .GetProperty("participants").EnumerateArray())
                .GetProperty("playerId").GetString());
        }
        var (_, freshCookie) = await Login(visitorA);
        using var freshOnOtherNode = Client(second, freshCookie);
        using var session = await freshOnOtherNode.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);

        // During a real stopped-primary / quorum election at least one
        // physical lifecycle notification must originate independently from
        // EACH Production Redis connector. We deliberately do NOT assert an
        // exact event count, order or one restored notification per endpoint:
        // Sentinel is free to reconnect using a different topology.
        var events = connectorEvents.For(MultiplayerMetrics.RedisConnectionEventsName);
        foreach (var client in new[] { "lobby", "session" })
            Assert.Contains(events, item =>
                item.Tags["client"] == client &&
                item.Tags["event"] is "failed" or "restored" or "configuration-changed");
        Assert.All(events, item =>
        {
            Assert.Equal(1, item.Value);
            Assert.Equal(new[] { "client", "event" },
                item.Tags.Keys.OrderBy(tag => tag, StringComparer.Ordinal));
            Assert.Contains(item.Tags["client"], new[] { "lobby", "session" });
            Assert.Contains(item.Tags["event"],
                new[] { "failed", "restored", "configuration-changed" });
            foreach (var tag in item.Tags.Values)
            {
                Assert.DoesNotContain("password", tag, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("guest-", tag, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("localhost", tag, StringComparison.OrdinalIgnoreCase);
            }
        });
    }
}
