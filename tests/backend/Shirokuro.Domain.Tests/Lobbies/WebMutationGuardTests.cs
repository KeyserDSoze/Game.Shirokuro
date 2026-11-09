using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class WebMutationGuardTests
{
    private static WebApplicationFactory<Program> Server(
        string environment = "Production", params string[] allowedOrigins) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(
                    new[] { new KeyValuePair<string, string?>(
                        "AllowedHosts", "localhost;game.example") }.Concat(allowedOrigins
                        .Select((origin, index) => new KeyValuePair<string, string?>(
                            "Security:AllowedOrigins:" + index, origin))));
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        if (cookie is not null)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Mutation(HttpMethod method, string path,
        string origin, object? body = null, string? host = null,
        bool includeIntent = true)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        if (includeIntent)
            request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        if (host is not null)
            request.Headers.Host = host;
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string PlayerId, string Cookie)> SignIn(
        HttpClient client, string origin)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest", origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task WithoutExplicitOriginsNonDevelopmentWriteRoutesFailClosed(string env)
    {
        using var server = Server(env);
        using var visitor = Client(server);
        Assert.Equal(env, server.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        using (var health = await visitor.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using (var post = Mutation(HttpMethod.Post, "/api/sessions/guest", "http://localhost"))
        using (var denied = await visitor.SendAsync(post))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }

        // Matching a forged Host is not proof of a trusted Origin.
        using (var forged = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://evil.example", host: "evil.example"))
        using (var denied = await visitor.SendAsync(forged))
        {
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task ProductionAllowsConfiguredHttpsOriginAcrossGuestAndLobbyMutations()
    {
        using var server = Server("Production", "https://game.example");
        using var visitor = Client(server);
        var (aliceId, cookie) = await SignIn(visitor, "https://game.example");
        using var member = Client(server, cookie);

        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
            "https://game.example",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var created = await member.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var data = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var room = data.RootElement.GetProperty("lobbyId").GetString()!;
            Assert.Equal(1, data.RootElement.GetProperty("revision").GetInt64());

            using (var forged = Mutation(HttpMethod.Patch,
                "/api/lobbies/" + room + "/hero", "https://evil.example",
                new { heroId = "kira", expectedRevision = 1 }, host: "evil.example"))
            using (var blocked = await member.SendAsync(forged))
                Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);

            using (var roster = await member.GetAsync("/api/lobbies/" + room + "/roster"))
            {
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var doc = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
                Assert.Equal(1, doc.RootElement.GetProperty("revision").GetInt64());
                Assert.Equal(aliceId, doc.RootElement.GetProperty("participants")[0]
                    .GetProperty("playerId").GetString());
            }

            using (var hero = Mutation(HttpMethod.Patch,
                "/api/lobbies/" + room + "/hero", "https://game.example",
                new { heroId = "kira", expectedRevision = 1 }))
            using (var picked = await member.SendAsync(hero))
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

            // Even a valid guest cookie must not be revoked by an untrusted Origin.
            using (var logout = Mutation(HttpMethod.Delete,
                "/api/sessions/guest", "https://evil.example"))
            using (var blocked = await member.SendAsync(logout))
                Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

            using (var stillSignedIn = await member.GetAsync("/api/sessions/me"))
                Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);

            using (var logout = Mutation(HttpMethod.Delete,
                "/api/sessions/guest", "https://game.example"))
            using (var signedOut = await member.SendAsync(logout))
                Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

            using var denied = await member.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
    }

    [Theory]
    [InlineData("http://game.example")]
    [InlineData("https://game.example.evil.example")]
    [InlineData("https://evil.example")]
    [InlineData("https://game.example/any")]
    [InlineData("https://game.example/")]
    [InlineData("https://game.example?next=x")]
    [InlineData("https://game.example#fragment")]
    [InlineData("https://attacker@game.example")]
    [InlineData("https://game.example,https://evil.example")]
    [InlineData("https://game.example:444")]
    [InlineData("null")]
    [InlineData("*")]
    [InlineData("https://game.example\\@evil.example")]
    public async Task ProductionRejectsMalformedAndUnlistedOrigins(string origin)
    {
        using var server = Server("Production", "https://game.example");
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest", origin);
        using var response = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task MisconfiguredOriginEntriesAreNotTrustedAndMultipleOriginsAreDenied()
    {
        using var server = Server("Production",
            "*", "http://insecure.example", "https://game.example/path");
        using var visitor = Client(server);
        foreach (var origin in new[] { "https://evil.example", "http://insecure.example",
                                       "https://game.example" })
        {
            using var request = Mutation(HttpMethod.Post, "/api/sessions/guest", origin);
            using var response = await visitor.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using var duplicate = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example");
        duplicate.Headers.Remove("Origin");
        duplicate.Headers.TryAddWithoutValidation("Origin",
            new[] { "https://game.example", "https://evil.example" });
        using var denied = await visitor.SendAsync(duplicate);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task ProductionDoesNotTrustForwardedHeadersForOriginAuthorization()
    {
        using var server = Server("Production", "https://game.example");
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://evil.example", host: "evil.example");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "game.example");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        using var response = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProductionConfiguredOriginStillRequiresIntentHeader()
    {
        using var server = Server("Production", "https://game.example");
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "https://game.example", includeIntent: false);
        using var response = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentRetainsLoopbackAndViteButRejectsForgedHost()
    {
        using var server = Server("Development");
        using var visitor = Client(server);
        Assert.Equal("Development",
            server.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);

        // Direct local API call and the two known Vite proxy origins.
        await SignIn(visitor, "http://localhost");
        await SignIn(visitor, "http://localhost:5173");
        await SignIn(visitor, "http://127.0.0.1:5173");

        using (var forgery = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "http://evil.example", host: "evil.example"))
        using (var denied = await visitor.SendAsync(forgery))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using (var typo = Mutation(HttpMethod.Post, "/api/sessions/guest",
            "http://localhost:5173.evil.example"))
        using (var denied = await visitor.SendAsync(typo))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var unrelatedLoopback = Mutation(HttpMethod.Post,
            "/api/sessions/guest", "http://127.0.0.1:5555");
        using var blocked = await visitor.SendAsync(unrelatedLoopback);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }
}
