using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class IngressHostPolicyTests
{
    private static IConfiguration Config(string? hosts) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AllowedHosts"] = hosts }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("*")]
    [InlineData("*.example.com")]
    [InlineData("example.com;*")]
    [InlineData("example.com;")]
    [InlineData(";example.com")]
    [InlineData("example.com;example.com")]
    [InlineData("example.com;EXAMPLE.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com?query")]
    [InlineData("example.com#hash")]
    [InlineData("user@example.com")]
    [InlineData("example.com:0")]
    [InlineData("example.com:080")]
    [InlineData("example.com:65536")]
    [InlineData("example..com")]
    [InlineData("example.com.")]
    [InlineData("evil_example.com")]
    [InlineData("evil.example,good.example")]
    [InlineData("https://example.com")]
    public void InvalidNonDevelopmentAllowlistFailsClosed(string? hosts)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IngressHostPolicy.Create(Config(hosts), isDevelopment: false));
    }

    [Fact]
    public void DevelopmentDoesNotRequireHostConfiguration()
    {
        var policy = IngressHostPolicy.Create(Config(null), isDevelopment: true);
        var request = new DefaultHttpContext().Request;
        request.Host = new HostString("localhost", 5186);
        Assert.True(policy.Allows(request));
    }

    [Fact]
    public void CanonicalHostnameAndExplicitPortAreRequired()
    {
        var policy = IngressHostPolicy.Create(
            Config("game.example;api.example:8443;[::1]:5186"), false);
        foreach (var allowed in new[] { "game.example", "GAME.EXAMPLE",
                                         "api.example:8443", "[::1]:5186" })
        {
            var req = new DefaultHttpContext().Request;
            req.Headers.Host = allowed;
            Assert.True(policy.Allows(req));
        }
        foreach (var denied in new[] { "api.example", "api.example:8444",
                                        "evil.example", "game.example.evil.example",
                                        "https://game.example", "game.example/path",
                                        "game.example:5000", "game.example,evil.example",
                                        "game.example.", "attacker@game.example" })
        {
            var req = new DefaultHttpContext().Request;
            req.Headers.Host = denied;
            Assert.False(policy.Allows(req));
        }
        var missing = new DefaultHttpContext().Request;
        Assert.False(policy.Allows(missing));
    }

    private static WebApplicationFactory<Program> Server(
        string environment = "Production",
        string? hosts = "localhost;game.example",
        string? origin = "https://game.example") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = hosts,
                    ["Security:AllowedOrigins:0"] = origin
                }));
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
            client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Request(HttpMethod method, string path,
        string host, string? origin = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Host = host;
        if (origin is not null)
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
            request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        }
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    [Fact]
    public async Task ProductionAllowsOnlyConfiguredHostsEvenForHealthAndPublicReads()
    {
        using var server = Server();
        using var client = Client(server);
        using (var health = Request(HttpMethod.Get, "/api/health", "game.example"))
        using (var allowed = await client.SendAsync(health))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        foreach (var host in new[] { "evil.example", "game.example.evil.example",
                                     "game.example:5555", "evil.example:5186" })
        {
            foreach (var path in new[] { "/api/health", "/api/preview/board",
                                        "/api/sessions/me", "/api/lobbies/secret/roster" })
            {
                using var request = Request(HttpMethod.Get, path, host);
                using var rejected = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                Assert.False(rejected.Headers.Contains("Set-Cookie"));
            }
        }

        using (var negotiate = Request(HttpMethod.Post,
            "/hubs/lobbies/negotiate?negotiateVersion=1", "evil.example"))
        using (var rejected = await client.SendAsync(negotiate))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task ProductionRejectsSpoofedHostBeforeGuestIssueOrLobbyWrites()
    {
        using var server = Server();
        using var client = Client(server);

        using (var bad = Request(HttpMethod.Post, "/api/sessions/guest",
            "evil.example", "https://game.example"))
        using (var forbidden = await client.SendAsync(bad))
        {
            Assert.Equal(HttpStatusCode.BadRequest, forbidden.StatusCode);
            Assert.False(forbidden.Headers.Contains("Set-Cookie"));
        }

        using var signin = Request(HttpMethod.Post, "/api/sessions/guest",
            "game.example", "https://game.example");
        using var signedIn = await client.SendAsync(signin);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var member = Client(server, cookie);

        using (var create = Request(HttpMethod.Post, "/api/lobbies",
            "game.example", "https://game.example",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var created = await member.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var data = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var room = data.RootElement.GetProperty("lobbyId").GetString()!;

            using (var fakeHero = Request(HttpMethod.Patch,
                "/api/lobbies/" + room + "/hero", "evil.example",
                "https://game.example",
                new { heroId = "kira", expectedRevision = 1 }))
            using (var rejected = await member.SendAsync(fakeHero))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            using (var fakeLeave = Request(HttpMethod.Delete,
                "/api/lobbies/" + room + "/membership", "evil.example",
                "https://game.example", new { expectedRevision = 1 }))
            using (var rejected = await member.SendAsync(fakeLeave))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            using (var roster = Request(HttpMethod.Get,
                "/api/lobbies/" + room + "/roster", "game.example"))
            using (var valid = await member.SendAsync(roster))
            {
                Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
                using var dataAfter = JsonDocument.Parse(await valid.Content.ReadAsStringAsync());
                Assert.Equal(1, dataAfter.RootElement.GetProperty("revision").GetInt64());
                Assert.Single(dataAfter.RootElement.GetProperty("participants").EnumerateArray());
            }

            using (var fakeLogout = Request(HttpMethod.Delete,
                "/api/sessions/guest", "evil.example", "https://game.example"))
            using (var blocked = await member.SendAsync(fakeLogout))
                Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
            using (var remains = Request(HttpMethod.Get, "/api/sessions/me",
                "game.example"))
            using (var alive = await member.SendAsync(remains))
                Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

            using (var logout = Request(HttpMethod.Delete,
                "/api/sessions/guest", "game.example", "https://game.example"))
            using (var success = await member.SendAsync(logout))
                Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);
        }
    }

    [Fact]
    public async Task ForwardedHostCannotBypassConfiguredHostEvenWhenProxyIsTrusted()
    {
        using var server = Server();
        using var client = Client(server);
        using var request = Request(HttpMethod.Get, "/api/health", "evil.example");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "game.example");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void MissingOrWildcardHostsCannotStartInNonDevelopment(string env)
    {
        foreach (var hosts in new string?[] { null, "", "*" })
        {
            using var server = Server(env, hosts);
            Assert.ThrowsAny<Exception>(() => server.CreateClient());
        }
    }

    [Fact]
    public async Task DevelopmentLocalViteAndApiHostRemainFunctional()
    {
        using var server = Server("Development", null, null);
        using var client = Client(server);
        using var health = Request(HttpMethod.Get, "/api/health", "127.0.0.1:5186");
        using var response = await client.SendAsync(health);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var signin = Request(HttpMethod.Post, "/api/sessions/guest",
            "127.0.0.1:5186", "http://localhost:5173");
        using var login = await client.SendAsync(signin);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }
}
