using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
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

    [Fact]
    public async Task WrongTlsServerNameFailsCertificateValidationWithoutTrustBypass()
    {
        if (Connection is null) return;
        var options = ConfigurationOptions.Parse(Connection);
        Assert.True(options.Ssl);
        // Same reachable TLS port, but the SAN of the issued server certificate
        // is only localhost and 127.0.0.1. Hostname verification must reject.
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
