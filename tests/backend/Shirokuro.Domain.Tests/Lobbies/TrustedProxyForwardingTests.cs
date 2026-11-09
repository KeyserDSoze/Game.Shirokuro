using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class TrustedProxyForwardingTests
{
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

    private static WebApplicationFactory<Program> Server(params string[] knownProxies) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(knownProxies.Select((value, index) =>
                    new KeyValuePair<string, string?>(
                        "Security:KnownProxies:" + index, value))));
        });

    /// <summary>
    /// The TestServer transport-level peer address must be assigned before
    /// app middleware executes. Unlike a StartupFilter, SendAsync injects it
    /// into the request's connection feature, exactly as a real TCP peer.
    /// </summary>
    private static Task<HttpContext> Send(
        WebApplicationFactory<Program> server,
        string peer,
        HttpMethod method,
        string path,
        string forwardedFor,
        string forwardedProto = "http",
        string? cookie = null,
        object? body = null,
        bool spoofHost = false)
    {
        var bytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body);
        return server.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            context.Request.Method = method.Method;
            context.Request.Path = path;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");
            context.Request.Headers["Origin"] = "http://localhost";
            context.Request.Headers["X-Shirokuro-Request"] = "1";
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            if (spoofHost)
                context.Request.Headers["X-Forwarded-Host"] = "evil.example";
            if (cookie is not null)
                context.Request.Headers["Cookie"] = cookie;
            if (bytes is not null)
            {
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);
            }
        });
    }

    private static async Task<(string Cookie, HttpContext Context)> Issue(
        WebApplicationFactory<Program> server,
        string peer,
        string clientIp,
        string proto = "http",
        bool spoofHost = false)
    {
        var context = await Send(server, peer, HttpMethod.Post,
            "/api/sessions/guest", clientIp, proto, spoofHost: spoofHost);
        var cookie = context.Response.Headers.TryGetValue("Set-Cookie", out var values)
            ? values.ToString().Split(';')[0] : "";
        return (cookie, context);
    }

    [Fact]
    public async Task TrustedProxyUsesClientAddressForGuestIssuanceThrottle()
    {
        using var server = Server("192.0.2.10");
        for (var i = 0; i < 12; i++)
        {
            var issued = await Issue(server, "192.0.2.10", "198.51.100.20",
                spoofHost: true);
            Assert.Equal(200, issued.Context.Response.StatusCode);
            Assert.Equal(IPAddress.Parse("198.51.100.20"),
                issued.Context.Connection.RemoteIpAddress);
            Assert.Equal("localhost", issued.Context.Request.Host.Host);
        }
        Assert.Equal(429, (await Issue(server, "192.0.2.10",
            "198.51.100.20")).Context.Response.StatusCode);
        Assert.Equal(200, (await Issue(server, "192.0.2.10",
            "198.51.100.21")).Context.Response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoTrustOrUnknownPeerCannotBypassLimitBySpoofingClientIp(
        bool explicitlyConfigureAnotherProxy)
    {
        using var server = explicitlyConfigureAnotherProxy
            ? Server("192.0.2.10") : Server();
        var peer = explicitlyConfigureAnotherProxy ? "192.0.2.11" : "127.0.0.1";
        for (var i = 0; i < 12; i++)
        {
            var issued = await Issue(server, peer, "198.51.100." + (i + 1));
            Assert.Equal(200, issued.Context.Response.StatusCode);
            Assert.Equal(IPAddress.Parse(peer),
                issued.Context.Connection.RemoteIpAddress);
        }
        Assert.Equal(429, (await Issue(server, peer,
            "203.0.113.1")).Context.Response.StatusCode);
    }

    [Fact]
    public async Task OnlyRightmostHopIsConsumedFromAForwardedChain()
    {
        using var server = Server("192.0.2.10");
        for (var i = 0; i < 12; i++)
        {
            var result = await Issue(server, "192.0.2.10",
                "198.51.100.1, 203.0.113.10", "http, http");
            Assert.Equal(200, result.Context.Response.StatusCode);
            Assert.Equal(IPAddress.Parse("203.0.113.10"),
                result.Context.Connection.RemoteIpAddress);
        }
        Assert.Equal(429, (await Issue(server, "192.0.2.10",
            "198.51.100.2, 203.0.113.10", "http, http")).Context.Response.StatusCode);
        Assert.Equal(200, (await Issue(server, "192.0.2.10",
            "198.51.100.1, 203.0.113.11", "http, http")).Context.Response.StatusCode);
    }

    [Fact]
    public async Task MismatchedForwardedHeadersCannotChangeTheClientPartition()
    {
        using var server = Server("192.0.2.10");
        for (var i = 0; i < 12; i++)
        {
            var result = await Issue(server, "192.0.2.10",
                "203.0.113." + (i + 1) + ", 192.0.2.200", "http");
            Assert.Equal(200, result.Context.Response.StatusCode);
            Assert.Equal(IPAddress.Parse("192.0.2.10"),
                result.Context.Connection.RemoteIpAddress);
        }
        Assert.Equal(429, (await Issue(server, "192.0.2.10",
            "198.51.100.100, 192.0.2.200", "http")).Context.Response.StatusCode);
    }

    [Fact]
    public async Task TrustedProxyAlsoKeepsRoomCreationQuotaPerClient()
    {
        using var server = Server("192.0.2.10");
        var one = await Issue(server, "192.0.2.10", "203.0.113.10");
        var two = await Issue(server, "192.0.2.10", "203.0.113.10");
        var three = await Issue(server, "192.0.2.10", "203.0.113.11");
        Assert.Equal(200, one.Context.Response.StatusCode);
        Assert.Equal(200, two.Context.Response.StatusCode);
        Assert.Equal(200, three.Context.Response.StatusCode);
        Assert.NotEmpty(one.Cookie);
        Assert.NotEmpty(two.Cookie);
        Assert.NotEmpty(three.Cookie);

        async Task<int> Create(string cookie, string clientIp)
        {
            var response = await Send(server, "192.0.2.10", HttpMethod.Post,
                "/api/lobbies", clientIp, cookie: cookie,
                body: new { teamIds = new[] { "white", "black" }, teamId = "white" });
            return response.Response.StatusCode;
        }

        for (var i = 0; i < 8; i++)
            Assert.Equal(201,
                await Create(i < 4 ? one.Cookie : two.Cookie, "203.0.113.10"));
        Assert.Equal(429, await Create(three.Cookie, "203.0.113.10"));
        Assert.Equal(201, await Create(three.Cookie, "203.0.113.11"));
    }
}
