using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class HttpsIngressTests
{
    private static WebApplicationFactory<Program> Server(
        string environment = "Production", string? trustedProxy = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (environment != "Development")
                TestDataProtectionConfiguration.Apply(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example;localhost",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                };
                if (trustedProxy is not null)
                    settings["Security:KnownProxies:0"] = trustedProxy;
                configuration.AddInMemoryCollection(settings);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string scheme = "https", string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(scheme + "://game.example"),
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
        request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task NonDevelopmentRejectsEveryPlaintextRouteWithoutRedirect(string environment)
    {
        using var server = Server(environment);
        using var http = Client(server, "http");
        foreach (var path in new[]
        {
            "/api/health", "/api/preview/board", "/api/sessions/me",
            "/api/lobbies/unlisted/roster"
        })
        {
            using var response = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.False(response.Headers.Location is not null);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        }

        using (var request = Mutation(HttpMethod.Post, "/api/sessions/guest"))
        using (var rejected = await http.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
        }

        using (var negotiate = await http.PostAsync(
            "/hubs/lobbies/negotiate?negotiateVersion=1", null))
            Assert.Equal(HttpStatusCode.Forbidden, negotiate.StatusCode);
    }

    [Fact]
    public async Task HttpsLoginAndLobbyStillWorkAndPlaintextCannotMutateSession()
    {
        using var server = Server();
        using var secure = Client(server);
        using var login = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var signedIn = await secure.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains("max-age=2592000",
            signedIn.Headers.GetValues("Strict-Transport-Security").Single(),
            StringComparison.OrdinalIgnoreCase);

        var cookie = Assert.Single(signedIn.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        using var member = Client(server, cookie: cookie);
        using var insecureMember = Client(server, "http", cookie);

        using (var create = Mutation(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var created = await member.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var room = payload.RootElement.GetProperty("lobbyId").GetString()!;

            using (var badHero = Mutation(HttpMethod.Patch,
                "/api/lobbies/" + room + "/hero",
                new { heroId = "kira", expectedRevision = 1 }))
            using (var rejected = await insecureMember.SendAsync(badHero))
                Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            using (var badLogout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
            using (var rejected = await insecureMember.SendAsync(badLogout))
                Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);

            using (var roster = await member.GetAsync(
                "/api/lobbies/" + room + "/roster"))
            {
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var stillPresent = JsonDocument.Parse(
                    await roster.Content.ReadAsStringAsync());
                Assert.Equal(1, stillPresent.RootElement.GetProperty("revision").GetInt64());
                Assert.Single(stillPresent.RootElement.GetProperty("participants").EnumerateArray());
            }

            using (var stillSignedIn = await member.GetAsync("/api/sessions/me"))
                Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);

            using (var secureLogout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
            using (var success = await member.SendAsync(secureLogout))
                Assert.Equal(HttpStatusCode.NoContent, success.StatusCode);

            using var afterLogout = await member.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
        }
    }

    private static Task<HttpContext> SendAsPeer(
        WebApplicationFactory<Program> server,
        string peer, string forwardedProtocol,
        string host = "game.example")
    {
        return server.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString(host);
            context.Request.Path = "/api/health";
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.55";
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProtocol;
            context.Request.Headers["X-Forwarded-Host"] = "evil.example";
        });
    }

    [Fact]
    public async Task TrustedSingleHopHttpsForwardingIsAcceptedWithoutTrustingForwardedHost()
    {
        using var server = Server(trustedProxy: "192.0.2.10");
        var context = await SendAsPeer(server, "192.0.2.10", "https");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Request.IsHttps);
        Assert.Equal("game.example", context.Request.Host.Host);
        Assert.Equal(IPAddress.Parse("203.0.113.55"),
            context.Connection.RemoteIpAddress);
        Assert.Contains("max-age=2592000",
            context.Response.Headers["Strict-Transport-Security"].ToString());
    }

    [Theory]
    [InlineData(null, "127.0.0.1")]
    [InlineData("192.0.2.10", "192.0.2.11")]
    public async Task UntrustedForwardedHttpsCannotBypassPlaintextRejection(
        string? configuredProxy, string peer)
    {
        using var server = Server(trustedProxy: configuredProxy);
        var context = await SendAsPeer(server, peer, "https");

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(context.Request.IsHttps);
        Assert.False(context.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }

    [Fact]
    public async Task TrustedProxyForwardingHttpIsNotAccepted()
    {
        using var server = Server(trustedProxy: "192.0.2.10");
        var context = await SendAsPeer(server, "192.0.2.10", "http");
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task DevelopmentContinuesServingPlainHttpWithoutHsts()
    {
        using var server = Server("Development");
        using var client = Client(server, "http");
        using (var health = await client.GetAsync("/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.False(health.Headers.Contains("Strict-Transport-Security"));
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/guest");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
        request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
