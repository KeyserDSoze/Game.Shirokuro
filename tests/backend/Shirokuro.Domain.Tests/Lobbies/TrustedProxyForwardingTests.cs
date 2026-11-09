using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class TrustedProxyForwardingTests
{
    // TestServer doesn't provide a real TCP peer. This filter inserts a
    // deterministic transport-level address before the application pipeline.
    private sealed class RemotePeerFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(nextMiddleware => context =>
                {
                    context.Connection.RemoteIpAddress = address;
                    return nextMiddleware(context);
                });
                next(app);
            };
    }

    private static IConfiguration Config(params string[] entries) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            entries.Select((value, index) =>
                new KeyValuePair<string, string?>("Security:KnownProxies:" + index, value)))
            .Build();

    [Fact]
    public void WithoutConfiguredProxiesMiddlewareStaysOffIncludingLoopback()
    {
        var services = new ServiceCollection();
        Assert.False(TrustedProxyForwarding.Configure(services, Config()));
    }

    [Fact]
    public void ExplicitProxiesOnlyAllowSingleHopForAndProtoNeverHost()
    {
        var services = new ServiceCollection();
        Assert.True(TrustedProxyForwarding.Configure(
            services, Config("192.0.2.10", "2001:db8::10")));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            options.ForwardedHeaders);
        Assert.Equal(1, options.ForwardLimit);
        Assert.True(options.RequireHeaderSymmetry);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(2, options.KnownProxies.Count);
        Assert.Contains(IPAddress.Parse("192.0.2.10"), options.KnownProxies);
        Assert.Contains(IPAddress.Parse("2001:db8::10"), options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Loopback, options.KnownProxies);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("192.0.2.0/24")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("255.255.255.255")]
    [InlineData("invalid")]
    [InlineData("192.0.2.10 ")]
    [InlineData("192.0.2.010")]
    public void InvalidOrNonCanonicalTrustedProxiesFailAtStartup(string entry)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            TrustedProxyForwarding.Configure(services, Config(entry)));
    }

    [Fact]
    public void DuplicateTrustedProxyAndExcessiveEntryCountFailAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TrustedProxyForwarding.Configure(new ServiceCollection(),
                Config("192.0.2.10", "192.0.2.10")));
        var tooMany = Enumerable.Range(1, 33)
            .Select(index => "192.0.2." + index).ToArray();
        Assert.Throws<InvalidOperationException>(() =>
            TrustedProxyForwarding.Configure(new ServiceCollection(), Config(tooMany)));
    }

    private static WebApplicationFactory<Program> Server(string peerAddress,
        params string[] knownProxies) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(knownProxies.Select((value, index) =>
                    new KeyValuePair<string, string?>(
                        "Security:KnownProxies:" + index, value))));
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new RemotePeerFilter(IPAddress.Parse(peerAddress))));
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

    private static HttpRequestMessage Write(HttpMethod method, string path,
        string forwardedFor, string forwardedProto = "http",
        object? body = null, bool spoofHost = false)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", forwardedProto);
        if (spoofHost)
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "evil.example");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string Cookie, HttpStatusCode Status)> Issue(
        HttpClient client, string clientIp, string proto = "http", bool spoofHost = false)
    {
        using var request = Write(HttpMethod.Post, "/api/sessions/guest",
            clientIp, proto, spoofHost: spoofHost);
        using var response = await client.SendAsync(request);
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? Assert.Single(values).Split(';')[0] : "";
        return (cookie, response.StatusCode);
    }

    [Fact]
    public async Task TrustedProxyUsesClientAddressForGuestIssuanceThrottle()
    {
        using var server = Server("192.0.2.10", "192.0.2.10");
        using var client = Client(server);
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await Issue(client, "198.51.100.20", spoofHost: true)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Issue(client, "198.51.100.20")).Status);
        // Same physical proxy, different client IP: independent throttle.
        Assert.Equal(HttpStatusCode.OK,
            (await Issue(client, "198.51.100.21")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoTrustOrUnknownPeerCannotBypassLimitBySpoofingClientIp(
        bool explicitlyConfigureAnotherProxy)
    {
        using var server = explicitlyConfigureAnotherProxy
            ? Server("192.0.2.11", "192.0.2.10")
            : Server("127.0.0.1");
        using var client = Client(server);
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await Issue(client, "198.51.100." + (i + 1))).Status);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Issue(client, "203.0.113.1")).Status);
    }

    [Fact]
    public async Task OnlyRightmostHopIsConsumedFromAForwardedChain()
    {
        using var server = Server("192.0.2.10", "192.0.2.10");
        using var client = Client(server);
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await Issue(client, "198.51.100.1, 203.0.113.10",
                    "http, http")).Status);

        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Issue(client, "198.51.100.2, 203.0.113.10",
                "http, http")).Status);
        Assert.Equal(HttpStatusCode.OK,
            (await Issue(client, "198.51.100.1, 203.0.113.11",
                "http, http")).Status);
    }

    [Fact]
    public async Task MismatchedForwardedHeadersCannotChangeTheClientPartition()
    {
        using var server = Server("192.0.2.10", "192.0.2.10");
        using var client = Client(server);
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.OK,
                (await Issue(client, "203.0.113." + (i + 1) + ", 192.0.2.200",
                    "http")).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await Issue(client, "198.51.100.100, 192.0.2.200",
                "http")).Status);
    }

    [Fact]
    public async Task TrustedProxyAlsoKeepsRoomCreationQuotaPerClient()
    {
        using var server = Server("192.0.2.10", "192.0.2.10");
        using var visitor = Client(server);
        var first = await Issue(visitor, "203.0.113.10");
        var second = await Issue(visitor, "203.0.113.10");
        var third = await Issue(visitor, "203.0.113.11");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal(HttpStatusCode.OK, third.Status);

        using var playerOne = Client(server, first.Cookie);
        using var playerTwo = Client(server, second.Cookie);
        using var playerThree = Client(server, third.Cookie);

        async Task<HttpStatusCode> Create(HttpClient player, string ip)
        {
            using var request = Write(HttpMethod.Post, "/api/lobbies", ip, body:
                new { teamIds = new[] { "white", "black" }, teamId = "white" });
            using var response = await player.SendAsync(request);
            return response.StatusCode;
        }

        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.Created,
                await Create(i < 4 ? playerOne : playerTwo, "203.0.113.10"));
        Assert.Equal(HttpStatusCode.TooManyRequests,
            await Create(playerThree, "203.0.113.10"));
        Assert.Equal(HttpStatusCode.Created,
            await Create(playerThree, "203.0.113.11"));
    }
}
