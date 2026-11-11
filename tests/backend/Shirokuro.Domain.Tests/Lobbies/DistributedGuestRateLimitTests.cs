using System.Net;
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
        IDistributedCache sessions, IConnectionMultiplexer redis) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, sessions);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
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
