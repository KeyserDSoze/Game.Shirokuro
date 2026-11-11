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
/// CI-only, separate TLS Redis fixture. The default Redis user is disabled
/// and an ephemeral ACL account can access only shirokuro:* keys. Exercise
/// the real independent Production cache and lobby multiplexers without
/// fake services or certificate-validation callbacks.
/// </summary>
[Collection("Production Redis TLS integration")]
public sealed class RedisTlsAclProductionTests
{
    private static string? Connection =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_TLS_ACL");

    private static WebApplicationFactory<Program> Server()
    {
        var connection = Connection
            ?? throw new InvalidOperationException("ACL Redis CI endpoint is required.");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            builder.UseSetting(GuestSessionRedisConfiguration.ConnectionStringKey,
                connection);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [GuestSessionRedisConfiguration.ConnectionStringKey] = connection,
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            // NO ConfigureTestServices. The two Redis connections, sessions,
            // rate limiter, rooms, expiry and DP ring use actual Production DI.
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> host,
        string? cookie = null)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions
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
        object? payload = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (payload is not null)
            request.Content = JsonContent.Create(payload);
        return request;
    }

    private static async Task<(string PlayerId, string Cookie)> Login(
        HttpClient visitor)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    [Fact]
    public async Task TwoProductionNodesUseAuthenticatedTlsRedisForSessionsRoomsAndFence()
    {
        if (Connection is null) return; // Opt-in ephemeral ACL CI fixture.
        var options = GuestSessionRedisConfiguration.ReadTlsOptions(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [GuestSessionRedisConfiguration.ConnectionStringKey] = Connection
                }).Build());
        Assert.True(options.Ssl);
        Assert.Equal("shirokuro-ci", options.User);
        Assert.False(string.IsNullOrWhiteSpace(options.Password));

        using var first = Server();
        using var second = Server();

        // Both independent IDistributedCache clients must be able to AUTH.
        await first.Services.GetRequiredService<IDistributedCache>()
            .GetAsync("shirokuro-acl-warmup");
        await second.Services.GetRequiredService<IDistributedCache>()
            .GetAsync("shirokuro-acl-warmup");

        using var visitorA = Client(first);
        using var visitorB = Client(second);
        using (var a = await visitorA.GetAsync("/api/ready"))
        using (var b = await visitorB.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        }

        var (alice, aliceCookie) = await Login(visitorA);
        var (bob, bobCookie) = await Login(visitorB);
        Assert.NotEqual(alice, bob);
        using var aliceA = Client(first, aliceCookie);
        using var aliceB = Client(second, aliceCookie);
        using var bobA = Client(first, bobCookie);
        using var bobB = Client(second, bobCookie);

        // Shared Production DP and Redis session authority across API nodes.
        using (var crossNode = await aliceB.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, crossNode.StatusCode);

        string roomId;
        long revision;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var response = await aliceA.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            roomId = doc.RootElement.GetProperty("lobbyId").GetString()!;
            revision = doc.RootElement.GetProperty("revision").GetInt64();
        }

        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + roomId +
                   "/join", new { teamId = "black", expectedRevision = revision }))
        using (var joined = await bobB.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        using (var roster = await bobA.GetAsync(
                   "/api/lobbies/" + roomId + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var doc = JsonDocument.Parse(
                await roster.Content.ReadAsStringAsync());
            Assert.Equal(2,
                doc.RootElement.GetProperty("participants").GetArrayLength());
        }

        // Cross-node logout removes authority and installs Redis write fence.
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await aliceB.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var denied = await aliceA.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            first.Services.GetRequiredService<IAsyncGuestLobby>()
                .JoinAsync(roomId, alice, "white", null));

        using var survivor = await bobB.GetAsync(
            "/api/lobbies/" + roomId + "/roster");
        Assert.Equal(HttpStatusCode.OK, survivor.StatusCode);
        using var survivorDoc = JsonDocument.Parse(
            await survivor.Content.ReadAsStringAsync());
        Assert.Equal(bob,
            Assert.Single(survivorDoc.RootElement.GetProperty("participants")
                .EnumerateArray()).GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task AclDeniesOutsideNamespaceAndUnauthenticatedConnections()
    {
        if (Connection is null) return;
        var options = ConfigurationOptions.Parse(Connection);
        Assert.True(options.Ssl);
        Assert.Equal("shirokuro-ci", options.User);
        Assert.NotNull(options.Password);
        using var authorized = await ConnectionMultiplexer.ConnectAsync(options);
        var db = authorized.GetDatabase();

        // Real ACL key patterns, not client-side filtering.
        var permittedKey = "shirokuro:acl-test:bounded-probe";
        Assert.True(await db.StringSetAsync(permittedKey, "1",
            expiry: TimeSpan.FromSeconds(10)));
        Assert.Equal("1", (string?)await db.StringGetAsync(permittedKey));
        await db.KeyDeleteAsync(permittedKey);
        await Assert.ThrowsAsync<RedisServerException>(() =>
            db.StringSetAsync("outside-shirokuro-acl-test", "forbidden"));

        // Redis with default user disabled denies missing or wrong AUTH.
        // A successful Connect followed by PING is NOT enough if the client
        // has silently connected without authenticating.
        async Task MustFailAsync(ConfigurationOptions invalid)
        {
            invalid.AbortOnConnectFail = true;
            invalid.ConnectRetry = 0;
            invalid.ConnectTimeout = 1000;
            invalid.SyncTimeout = 1000;
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var connection = await ConnectionMultiplexer
                    .ConnectAsync(invalid).WaitAsync(TimeSpan.FromSeconds(8));
                await connection.GetDatabase().PingAsync()
                    .WaitAsync(TimeSpan.FromSeconds(3));
            });
        }

        var wrongPassword = ConfigurationOptions.Parse(Connection);
        wrongPassword.Password = "deliberately-invalid-test-password";
        await MustFailAsync(wrongPassword);

        var anonymous = ConfigurationOptions.Parse(Connection);
        anonymous.User = null;
        anonymous.Password = null;
        await MustFailAsync(anonymous);
    }
}
