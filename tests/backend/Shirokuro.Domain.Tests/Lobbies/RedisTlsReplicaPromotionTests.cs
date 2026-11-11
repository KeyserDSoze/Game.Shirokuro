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
/// Independent, TLS-only Redis master + replica on ephemeral CI ports.
/// The replica is promoted explicitly; new Production hosts must be given its
/// endpoint. This does NOT exercise Sentinel, Cluster or automatic failover.
/// </summary>
public sealed class RedisTlsReplicaPromotionTests
{
    private const string MasterContainer = "shirokuro-ci-redis-ha-master";
    private const string ReplicaContainer = "shirokuro-ci-redis-ha-replica";

    private static string? MasterConnection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_HA_MASTER");
    private static string? ReplicaConnection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_HA_REPLICA");

    private static WebApplicationFactory<Program> Server(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
            // No ConfigureTestServices: both independent Redis clients and
            // the full Production cookie, lobby, expiry/fence DI are real.
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

    private static HttpRequestMessage Mutation(HttpMethod method,
        string path, object? body = null)
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
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!,
            Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0]);
    }

    private static string SessionCacheKey(WebApplicationFactory<Program> server,
        string cookie)
    {
        // Decoded ONLY inside this CI test process; neither the token nor its
        // digest is printed, uploaded or accepted from an untrusted request.
        var protectedCookie = Uri.UnescapeDataString(
            cookie[(cookie.IndexOf('=') + 1)..]);
        var format = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
        var ticket = format.Unprotect(protectedCookie)
            ?? throw new InvalidOperationException("Cannot read test guest ticket.");
        var token = ticket.Principal.FindFirst(GuestSessions.SessionIdClaim)?.Value
            ?? throw new InvalidOperationException("Missing authenticated session token.");
        return "shirokuro:guest-session:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes(token)));
    }

    // Fixed commands and container names, no shell interpolation or credentials
    // from HTTP requests. This works ONLY against opted-in CI containers.
    private static async Task<string> DockerAsync(params string[] args)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Isolated Redis HA fixture unavailable.");
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
            throw new TimeoutException("Isolated Redis HA operation timed out.");
        }
        var result = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Isolated Redis HA operation failed.");
        return result.Trim();
    }

    private static Task<string> RedisCliAsync(string container, params string[] command)
    {
        if (container is not (MasterContainer or ReplicaContainer))
            throw new ArgumentOutOfRangeException(nameof(container));
        return DockerAsync(
            ["exec", container, "redis-cli", "--tls", "--cacert", "/certs/ca.crt",
             "-h", "localhost", "-p", "6380", .. command]);
    }

    private static async Task EnsureReadyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/ready");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task RealTlsReplicaPromotionRecoversPreviouslyReplicatedTicketsAndRoom()
    {
        if (MasterConnection is null || ReplicaConnection is null ||
            Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_HA_CHAOS") != "1")
            return;
        Assert.True(ConfigurationOptions.Parse(MasterConnection).Ssl);
        Assert.True(ConfigurationOptions.Parse(ReplicaConnection).Ssl);

        using var first = Server(MasterConnection);
        using var second = Server(MasterConnection);
        using var visitorA = Client(first);
        using var visitorB = Client(second);
        await EnsureReadyAsync(visitorA);
        await EnsureReadyAsync(visitorB);

        var (alice, aliceCookie) = await Login(visitorA);
        var (bob, bobCookie) = await Login(visitorB);
        using var aliceOnA = Client(first, aliceCookie);
        using var aliceOnB = Client(second, aliceCookie);
        using var bobOnB = Client(second, bobCookie);

        string room;
        long revision;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var created = await aliceOnA.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            room = json.RootElement.GetProperty("lobbyId").GetString()!;
            revision = json.RootElement.GetProperty("revision").GetInt64();
        }
        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                   new { teamId = "black", expectedRevision = revision }))
        using (var joined = await bobOnB.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        using (var before = await bobOnB.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            using var json = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
            revision = json.RootElement.GetProperty("revision").GetInt64();
            Assert.Equal(2, json.RootElement.GetProperty("participants").GetArrayLength());
        }

        // Assert that the replica really received BOTH cache tokens, and the
        // TLS replication link was live. Polling boundedly avoids an
        // unwarranted RPO=0 assumption about asynchronous replication.
        var aliceKey = SessionCacheKey(first, aliceCookie);
        var bobKey = SessionCacheKey(second, bobCookie);
        var roomKey = "shirokuro:lobby:{rooms}:v2:room:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(room)));
        var replicated = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var info = await RedisCliAsync(ReplicaContainer, "INFO", "replication");
            Assert.Contains("role:slave", info, StringComparison.Ordinal);
            var a = await RedisCliAsync(ReplicaContainer, "EXISTS", aliceKey);
            var b = await RedisCliAsync(ReplicaContainer, "EXISTS", bobKey);
            var savedRoom = await RedisCliAsync(ReplicaContainer, "EXISTS", roomKey);
            if (info.Contains("master_link_status:up", StringComparison.Ordinal) &&
                a == "1" && b == "1" && savedRoom == "1")
            {
                replicated = true;
                break;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        Assert.True(replicated, "TLS replica never caught up with guest tokens.");

        // A replica can PING and serve existing GETs, but must NOT pass
        // readiness: both independent Production Redis connectors need a
        // short-lived WRITE, not just liveness/read access. This host is
        // already running BEFORE promotion and keeps its 6383 endpoint.
        using var replicaObserver = Server(ReplicaConnection);
        using var observerClient = Client(replicaObserver);
        using (var replicaLiveness = await observerClient.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, replicaLiveness.StatusCode);
        using (var replicaUnready = await observerClient.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, replicaUnready.StatusCode);
            Assert.Equal("no-store",
                replicaUnready.Headers.CacheControl?.ToString());
            using var deniedPayload = JsonDocument.Parse(
                await replicaUnready.Content.ReadAsStringAsync());
            Assert.Equal("unavailable",
                deniedPayload.RootElement.GetProperty("status").GetString());
            Assert.Single(deniedPayload.RootElement.EnumerateObject());
        }

        // Explicitly stop the old writable master BEFORE promoting its
        // replica. This does not model two simultaneously writable primaries.
        await DockerAsync("stop", "-t", "1", MasterContainer);
        await Task.Delay(RedisDeploymentReadiness.CachedFor +
                         TimeSpan.FromMilliseconds(300));
        using (var health = await visitorA.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var notReadyA = await visitorA.GetAsync("/api/ready"))
        using (var notReadyB = await visitorB.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReadyA.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReadyB.StatusCode);
        }
        using (var deniedA = await aliceOnB.GetAsync("/api/sessions/me"))
        using (var deniedB = await bobOnB.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, deniedA.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, deniedB.StatusCode);
        }

        Assert.Equal("OK", await RedisCliAsync(ReplicaContainer,
            "REPLICAOF", "NO", "ONE"));
        var promoted = await RedisCliAsync(ReplicaContainer, "INFO", "replication");
        Assert.Contains("role:master", promoted, StringComparison.Ordinal);
        Assert.Equal("PONG", await RedisCliAsync(ReplicaContainer, "PING"));

        // An API already targeting this endpoint becomes ready only AFTER
        // writable promotion and expiry of its three-second cached 503.
        await Task.Delay(RedisDeploymentReadiness.CachedFor +
            TimeSpan.FromMilliseconds(300));
        await EnsureReadyAsync(observerClient);

        // Manual operator redirection to the promoted server. The product
        // does not claim existing multiplexer instances discover this node:
        // replacement Production hosts are explicitly configured with 6383.
        using var recoveredA = Server(ReplicaConnection);
        using var recoveredB = Server(ReplicaConnection);
        using var newVisitor = Client(recoveredA);
        await EnsureReadyAsync(newVisitor);
        using var aliceOnRecoveredB = Client(recoveredB, aliceCookie);
        using var bobOnRecoveredA = Client(recoveredA, bobCookie);
        using (var meAlice = await aliceOnRecoveredB.GetAsync("/api/sessions/me"))
        using (var meBob = await bobOnRecoveredA.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.OK, meAlice.StatusCode);
            Assert.Equal(HttpStatusCode.OK, meBob.StatusCode);
        }
        using (var after = await bobOnRecoveredA.GetAsync(
                   "/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
            using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
            Assert.Equal(revision, json.RootElement.GetProperty("revision").GetInt64());
            var participants = json.RootElement.GetProperty("participants")
                .EnumerateArray().Select(x => x.GetProperty("playerId").GetString()).ToArray();
            Assert.Equal(2, participants.Length);
            Assert.Contains(alice, participants);
            Assert.Contains(bob, participants);
        }

        // Confirm the newly promoted master still supports authenticated
        // writes, server-side revocation and the cross-node Redis Lua fence.
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await aliceOnRecoveredB.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var oldTicket = Client(recoveredA, aliceCookie))
        using (var denied = await oldTicket.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            recoveredA.Services.GetRequiredService<IAsyncGuestLobby>()
                .JoinAsync(room, alice, "white", null));
        using (var survivor = await bobOnRecoveredA.GetAsync(
                   "/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, survivor.StatusCode);
            using var json = JsonDocument.Parse(await survivor.Content.ReadAsStringAsync());
            Assert.Equal(bob, Assert.Single(json.RootElement
                .GetProperty("participants").EnumerateArray())
                .GetProperty("playerId").GetString());
        }
        using (var issued = Mutation(HttpMethod.Post, "/api/sessions/guest"))
        using (var result = await newVisitor.SendAsync(issued))
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }
}
