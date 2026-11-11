using System.Net;
using System.IO;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class DistributedGuestRateLimitTests
{
    private static string? Address => Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private static async Task<(ConnectionMultiplexer A, ConnectionMultiplexer B)> Connect()
    {
        if (string.IsNullOrWhiteSpace(Address))
            throw new InvalidOperationException("Live Redis CI environment required.");
        return (await ConnectionMultiplexer.ConnectAsync(Address),
            await ConnectionMultiplexer.ConnectAsync(Address));
    }

    [Fact]
    public void KeysHashCanonicalTrustedAddressAndSeparatePolicies()
    {
        var session = new GuestMutationRateLimit(GuestMutationKind.SessionIssuance);
        var room = new GuestMutationRateLimit(GuestMutationKind.RoomCreation);
        var ipv4 = IPAddress.Parse("192.0.2.41");
        var mapped = IPAddress.Parse("::ffff:192.0.2.41");
        var ipv6 = IPAddress.Parse("2001:db8::42");
        Assert.Equal(RedisGuestMutationLimiter.Key(session, ipv4),
            RedisGuestMutationLimiter.Key(session, mapped));
        Assert.NotEqual(RedisGuestMutationLimiter.Key(session, ipv4),
            RedisGuestMutationLimiter.Key(session, ipv6));
        Assert.NotEqual(RedisGuestMutationLimiter.Key(session, ipv4),
            RedisGuestMutationLimiter.Key(room, ipv4));
        Assert.DoesNotContain("192.0.2.41", RedisGuestMutationLimiter.Key(session, ipv4));
        Assert.DoesNotContain("2001:db8", RedisGuestMutationLimiter.Key(room, ipv6));
        Assert.NotEqual(RedisGuestMutationLimiter.Key(session, null),
            RedisGuestMutationLimiter.Key(session, ipv4));
    }

    [Fact]
    public async Task RedisAtomicCountersAreSharedAcrossClientsAndDoNotSlide()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var policy = new GuestMutationRateLimit(GuestMutationKind.SessionIssuance);
            var one = new RedisGuestMutationLimiter(a.GetDatabase());
            var two = new RedisGuestMutationLimiter(b.GetDatabase());
            var ip = IPAddress.Parse("2001:db8::" + Guid.NewGuid().ToString("N")[..4]);
            var tasks = Enumerable.Range(0, 48).Select(i =>
                (i % 2 == 0 ? one : two).AcquireAsync(policy, ip));
            var replies = await Task.WhenAll(tasks);
            Assert.Equal(12, replies.Count(x => x.Allowed));
            Assert.Equal(36, replies.Count(x => !x.Allowed));
            Assert.All(replies, x => Assert.InRange(x.RetryAfterMilliseconds, 1, 60000));
            var blocked = await two.AcquireAsync(policy, ip);
            Assert.False(blocked.Allowed);
            Assert.True(blocked.RetryAfterMilliseconds <= replies[0].RetryAfterMilliseconds);

            var independent = await one.AcquireAsync(
                new GuestMutationRateLimit(GuestMutationKind.RoomCreation), ip);
            Assert.True(independent.Allowed);
            var other = await two.AcquireAsync(policy, IPAddress.Parse("198.51.100.35"));
            Assert.True(other.Allowed);
        }
    }

    [Fact]
    public async Task RedisCountersExpireAndReopenWithoutExplicitWorker()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var limiter = new RedisGuestMutationLimiter(a.GetDatabase());
            var policy = new GuestMutationRateLimit(GuestMutationKind.RoomCreation);
            var ip = IPAddress.Parse("2001:db8::" + Guid.NewGuid().ToString("N")[..4]);
            // The application window is exactly 60s and is never manually
            // renewed by GET/INCR. Override the TTL on a test-only key to
            // speed up a real Redis expiry integration scenario.
            var original = await limiter.AcquireAsync(policy, ip);
            Assert.True(original.Allowed);
            var key = RedisGuestMutationLimiter.Key(policy, ip);
            var database = b.GetDatabase();
            Assert.True(await database.KeyExpireAsync(key, TimeSpan.FromMilliseconds(300)));
            await Task.Delay(650);
            Assert.False(await database.KeyExistsAsync(key));
            Assert.True((await limiter.AcquireAsync(policy, ip)).Allowed);
        }
    }

    private static WebApplicationFactory<Program> Server(
        IDistributedCache sessions, IConnectionMultiplexer redis,
        bool trustProxy = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, sessions);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example",
                    ["Security:KnownProxies:0"] = trustProxy ? "192.0.2.10" : null
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConnectionMultiplexer>();
                services.AddSingleton(redis);
                services.RemoveAll<IGuestMutationLimiter>();
                services.AddSingleton<IGuestMutationLimiter>(
                    new RedisGuestMutationLimiter(redis.GetDatabase()));
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> server) =>
        server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static HttpRequestMessage Post(string path, object? payload = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path);
        message.Headers.Add("Origin", "https://game.example");
        message.Headers.Add("X-Shirokuro-Request", "1");
        if (payload is not null)
            message.Content = JsonContent.Create(payload);
        return message;
    }

    private sealed class ToggleFailureLimiter : IGuestMutationLimiter
    {
        public bool Unavailable { get; set; }
        public Task<GuestMutationDecision> AcquireAsync(
            GuestMutationRateLimit policy, IPAddress? remote,
            CancellationToken cancellationToken = default)
        {
            if (Unavailable)
                throw new IOException("Fake outage, no secrets.");
            return Task.FromResult(new GuestMutationDecision(true, 60_000));
        }
    }

    [Fact]
    public async Task StoreOutageNeverIssuesGuestCookieOrCreatesRoom()
    {
        var limiter = new ToggleFailureLimiter { Unavailable = true };
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                TestDataProtectionConfiguration.Apply(builder);
                TestGuestSessionRedisConfiguration.Apply(builder);
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AllowedHosts"] = "game.example",
                        ["Security:AllowedOrigins:0"] = "https://game.example"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGuestMutationLimiter>();
                    services.AddSingleton<IGuestMutationLimiter>(limiter);
                });
            });
        using var client = Client(server);
        using (var request = Post("/api/sessions/guest"))
        using (var error = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
            Assert.False(error.Headers.Contains("Set-Cookie"));
            Assert.Equal("no-store", error.Headers.CacheControl?.ToString());
        }

        limiter.Unavailable = false;
        string cookie;
        using (var request = Post("/api/sessions/guest"))
        using (var response = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        }
        limiter.Unavailable = true;
        using (var request = Post("/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        {
            request.Headers.Add("Cookie", cookie);
            using var error = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
            Assert.Equal("no-store", error.Headers.CacheControl?.ToString());
        }

        // Other endpoints are not accidentally rate-limited.
        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/sessions/me");
        me.Headers.Add("Cookie", cookie);
        using var stillValid = await client.SendAsync(me);
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    [Fact]
    public async Task TrustedForwardedIpAndUntrustedSpoofingUseCorrectSharedPartitions()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var services = new ServiceCollection();
            services.AddDistributedMemoryCache();
            using var sessionProvider = services.BuildServiceProvider();
            var cache = sessionProvider.GetRequiredService<IDistributedCache>();
            using var serverA = Server(cache, a, trustProxy: true);
            using var serverB = Server(cache, b, trustProxy: true);

            static Task<HttpContext> Issue(WebApplicationFactory<Program> server,
                string peer, string fakeClient)
            {
                return server.Server.SendAsync(context =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                    context.Request.Method = "POST";
                    context.Request.Path = "/api/sessions/guest";
                    context.Request.Scheme = "https";
                    context.Request.Host = new HostString("game.example");
                    context.Request.Headers["Origin"] = "https://game.example";
                    context.Request.Headers["X-Shirokuro-Request"] = "1";
                    context.Request.Headers["X-Forwarded-For"] = fakeClient;
                    context.Request.Headers["X-Forwarded-Proto"] = "https";
                });
            }

            // Trusted proxy: the real forwarded address is shared across
            // nodes and a second client gets a separate 12-request budget.
            for (var i = 0; i < 12; i++)
            {
                var response = await Issue(i % 2 == 0 ? serverA : serverB,
                    "192.0.2.10", "198.51.100.121");
                Assert.Equal(200, response.Response.StatusCode);
                Assert.Equal(IPAddress.Parse("198.51.100.121"),
                    response.Connection.RemoteIpAddress);
            }
            Assert.Equal(429, (await Issue(serverB, "192.0.2.10",
                "198.51.100.121")).Response.StatusCode);
            Assert.Equal(200, (await Issue(serverA, "192.0.2.10",
                "198.51.100.122")).Response.StatusCode);

            // Untrusted TCP peers cannot change their partition by
            // supplying arbitrary XFF headers.
            for (var i = 0; i < 12; i++)
            {
                var response = await Issue(i % 2 == 0 ? serverA : serverB,
                    "192.0.2.11", "203.0.113." + (i + 1));
                Assert.Equal(200, response.Response.StatusCode);
                Assert.Equal(IPAddress.Parse("192.0.2.11"),
                    response.Connection.RemoteIpAddress);
            }
            Assert.Equal(429, (await Issue(serverA, "192.0.2.11",
                "198.51.100.200")).Response.StatusCode);
        }
    }

    [Fact]
    public async Task IndependentProductionApisShareBothIpBudgets()
    {
        if (Address is null) return;
        var (a, b) = await Connect();
        using (a)
        using (b)
        {
            var services = new ServiceCollection();
            services.AddDistributedMemoryCache();
            using var sessionProvider = services.BuildServiceProvider();
            var sessionCache = sessionProvider.GetRequiredService<IDistributedCache>();
            using var serverA = Server(sessionCache, a);
            using var serverB = Server(sessionCache, b);
            using var aClient = Client(serverA);
            using var bClient = Client(serverB);

            async Task<(HttpStatusCode Status, string? Cookie, string Body)> Issue(int i)
            {
                using var request = Post("/api/sessions/guest");
                using var response = await (i % 2 == 0 ? aClient : bClient).SendAsync(request);
                return (response.StatusCode,
                    response.Headers.TryGetValues("Set-Cookie", out var cookies)
                        ? Assert.Single(cookies).Split(';')[0] : null,
                    await response.Content.ReadAsStringAsync());
            }
            var issued = await Task.WhenAll(Enumerable.Range(0, 48).Select(Issue));
            Assert.Equal(12, issued.Count(x => x.Status == HttpStatusCode.OK));
            Assert.Equal(36, issued.Count(x => x.Status == HttpStatusCode.TooManyRequests));
            Assert.All(issued.Where(x => x.Status == HttpStatusCode.TooManyRequests),
                x => Assert.Null(x.Cookie));

            var activeCookies = issued.Where(x => x.Cookie is not null)
                .Select(x => x.Cookie!).ToArray();
            Assert.Equal(12, activeCookies.Length);
            async Task<HttpStatusCode> MakeRoom(int i)
            {
                using var request = Post("/api/lobbies",
                    new { teamIds = new[] { "white", "black" }, teamId = "white" });
                request.Headers.Add("Cookie", activeCookies[i % activeCookies.Length]);
                using var response = await (i % 2 == 0 ? aClient : bClient).SendAsync(request);
                return response.StatusCode;
            }
            var created = await Task.WhenAll(Enumerable.Range(0, 48).Select(MakeRoom));
            Assert.Equal(8, created.Count(x => x == HttpStatusCode.Created));
            Assert.Equal(40, created.Count(x => x == HttpStatusCode.TooManyRequests));
        }
    }
}
