using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Dedicated ephemeral Redis TLS + ACL job. Production API instances always
/// use startup-bound credentials; only a CI operator connection can alter
/// users. Existing guest cookies and lobby keys must survive blue/green
/// replacement, and a disabled ACL user is NOT considered fully evicted until
/// old authenticated Redis connections are explicitly terminated.
/// </summary>
[Collection("Production Redis TLS integration")]
public sealed class RedisTlsCredentialRotationTests
{
    private static string? Blue =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_ROTATION_BLUE");
    private static string? Green =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_ROTATION_GREEN");
    private static string? Admin =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS_ROTATION_ADMIN");

    private static WebApplicationFactory<Program> Server(string connection) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
            // No test service overrides: real guest-cache, lobby and expiry
            // Redis TLS connectors and shared encrypted DP key ring.
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
        object? data = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (data is not null)
            request.Content = JsonContent.Create(data);
        return request;
    }

    private static async Task<(string Id, string Cookie)> Login(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    private static async Task AssertRejectedAuthenticationAsync(string connection)
    {
        var invalid = ConfigurationOptions.Parse(connection);
        Assert.True(invalid.Ssl);
        invalid.AbortOnConnectFail = true;
        invalid.ConnectRetry = 0;
        invalid.ConnectTimeout = 1000;
        invalid.SyncTimeout = 1000;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var rejected = await ConnectionMultiplexer
                .ConnectAsync(invalid).WaitAsync(TimeSpan.FromSeconds(8));
            await rejected.GetDatabase().PingAsync()
                .WaitAsync(TimeSpan.FromSeconds(3));
        });
    }

    [Fact]
    public async Task BlueGreenAclCutoverPreservesSharedStateAndEvictsOldConnections()
    {
        if (Blue is null || Green is null || Admin is null)
            return; // Only the dedicated isolated ephemeral CI fixture.

        var blueOptions = ConfigurationOptions.Parse(Blue);
        var greenOptions = ConfigurationOptions.Parse(Green);
        var adminOptions = ConfigurationOptions.Parse(Admin);
        Assert.True(blueOptions.Ssl && greenOptions.Ssl && adminOptions.Ssl);
        Assert.Equal("shirokuro-blue", blueOptions.User);
        Assert.Equal("shirokuro-green", greenOptions.User);
        Assert.Equal("shirokuro-operator", adminOptions.User);
        Assert.False(string.IsNullOrWhiteSpace(greenOptions.Password));
        Assert.NotEqual(blueOptions.Password, greenOptions.Password);
        Assert.NotEqual(adminOptions.Password, greenOptions.Password);
        Assert.True(adminOptions.AllowAdmin);
        Assert.False(blueOptions.AllowAdmin || greenOptions.AllowAdmin);

        // New identity does not exist yet; TLS does not bypass Redis AUTH.
        await AssertRejectedAuthenticationAsync(Green);
        using var operatorMux = await ConnectionMultiplexer.ConnectAsync(adminOptions);
        var operatorDb = operatorMux.GetDatabase();

        using var blueA = Server(Blue);
        using var blueB = Server(Blue);
        using var visitorBlueA = Client(blueA);
        using var visitorBlueB = Client(blueB);
        using (var readyA = await visitorBlueA.GetAsync("/api/ready"))
        using (var readyB = await visitorBlueB.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, readyA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, readyB.StatusCode);
        }

        var (alice, aliceCookie) = await Login(visitorBlueA);
        var (bob, bobCookie) = await Login(visitorBlueB);
        using var aliceBlue = Client(blueA, aliceCookie);
        using var bobBlue = Client(blueB, bobCookie);
        string room;
        long revision;
        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
                   new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var result = await aliceBlue.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, result.StatusCode);
            using var json = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
            room = json.RootElement.GetProperty("lobbyId").GetString()!;
            revision = json.RootElement.GetProperty("revision").GetInt64();
        }
        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + room + "/join",
                   new { teamId = "black", expectedRevision = revision }))
        using (var result = await bobBlue.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using (var roster = await bobBlue.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var json = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            revision = json.RootElement.GetProperty("revision").GetInt64();
            Assert.Equal(2, json.RootElement.GetProperty("participants").GetArrayLength());
        }

        // Operator stages green identity with the same scoped app privileges.
        // Never use application credentials to run ACL/CLIENT commands.
        await operatorDb.ExecuteAsync("ACL",
            "SETUSER", "shirokuro-green", "reset", "on",
            ">" + greenOptions.Password, "~shirokuro:*",
            "+@read", "+@write", "+@connection", "+@scripting",
            "+@pubsub", "+@transaction", "-@dangerous", "-@admin",
            "+info", "+command", "+client", "+time");

        // Both new hosts are really configured with the NEW credentials.
        using var greenA = Server(Green);
        using var greenB = Server(Green);
        using var visitorGreenA = Client(greenA);
        using var visitorGreenB = Client(greenB);
        using (var readyA = await visitorGreenA.GetAsync("/api/ready"))
        using (var readyB = await visitorGreenB.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, readyA.StatusCode);
            Assert.Equal(HttpStatusCode.OK, readyB.StatusCode);
        }

        using var aliceGreen = Client(greenA, aliceCookie);
        using var aliceOtherGreen = Client(greenB, aliceCookie);
        using var bobGreen = Client(greenB, bobCookie);
        using (var session = await aliceOtherGreen.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using (var roster = await bobGreen.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var json = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            Assert.Equal(revision, json.RootElement.GetProperty("revision").GetInt64());
            var ids = json.RootElement.GetProperty("participants").EnumerateArray()
                .Select(p => p.GetProperty("playerId").GetString()).ToArray();
            Assert.Equal(2, ids.Length);
            Assert.Contains(alice, ids);
            Assert.Contains(bob, ids);
        }

        // Redis documents this trap: SETUSER off prevents new AUTH, but
        // connections authenticated before the change REMAIN authorized.
        using var blueProbe = await ConnectionMultiplexer.ConnectAsync(blueOptions);
        var blueDb = blueProbe.GetDatabase();
        await operatorDb.ExecuteAsync("ACL", "SETUSER", "shirokuro-blue", "off");
        await AssertRejectedAuthenticationAsync(Blue);
        Assert.True(await blueDb.StringSetAsync(
            "shirokuro:rotation:already-authenticated", "still-connected",
            TimeSpan.FromSeconds(10)));

        // Revoke *existing* authenticated connections as a separate step.
        // The old blue clients must lose both their session and lobby paths.
        var killed = await operatorDb.ExecuteAsync("CLIENT", "KILL", "USER",
            "shirokuro-blue");
        Assert.True((long)killed >= 1);

        await Task.Delay(RedisDeploymentReadiness.CachedFor +
            TimeSpan.FromMilliseconds(350)); // Bypass the 3s positive probe cache.
        using (var oldReadyA = await visitorBlueA.GetAsync("/api/ready"))
        using (var oldReadyB = await visitorBlueB.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, oldReadyA.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, oldReadyB.StatusCode);
            Assert.Equal("no-store", oldReadyA.Headers.CacheControl?.ToString());
        }
        using (var health = await visitorBlueA.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var oldCookie = await aliceBlue.GetAsync("/api/sessions/me"))
        {
            // Disabled/evicted ACL credentials make Redis unverifiable,
            // NOT proof that the formerly issued guest ticket was revoked.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, oldCookie.StatusCode);
            Assert.Equal("no-store", oldCookie.Headers.CacheControl?.ToString());
            Assert.False(oldCookie.Headers.Contains("Set-Cookie"));
        }

        // Green remains fully active across the blue eviction, and logout
        // fences even requests that validated an old guest cookie earlier.
        using (var ready = await visitorGreenB.GetAsync("/api/ready"))
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using (var session = await aliceGreen.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var result = await aliceOtherGreen.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
        using (var rejected = await aliceGreen.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            greenA.Services.GetRequiredService<IAsyncGuestLobby>()
                .JoinAsync(room, alice, "white", null));

        using (var survivor = await bobGreen.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, survivor.StatusCode);
            using var json = JsonDocument.Parse(await survivor.Content.ReadAsStringAsync());
            Assert.Equal(bob, Assert.Single(json.RootElement
                .GetProperty("participants").EnumerateArray())
                .GetProperty("playerId").GetString());
        }

        var (_, freshCookie) = await Login(visitorGreenA);
        using var freshOnOtherNode = Client(greenB, freshCookie);
        using (var fresh = await freshOnOtherNode.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        // Green has no ability to write outside its namespace or administer
        // Redis, even though the CI operator can rotate the ACL.
        var aclProbe = greenOptions.Clone();
        aclProbe.AllowAdmin = true;
        using var scoped = await ConnectionMultiplexer.ConnectAsync(aclProbe);
        await Assert.ThrowsAsync<RedisServerException>(() =>
            scoped.GetDatabase().StringSetAsync("outside-shirokuro-rotation", "bad"));
        await Assert.ThrowsAsync<RedisServerException>(() =>
            scoped.GetDatabase().ExecuteAsync("FLUSHDB"));
    }
}
