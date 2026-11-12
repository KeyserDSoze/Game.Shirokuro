using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Security;
using Shirokuro.Api.Readiness;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// A browser's invalid mutation intent must be rejected by endpoint metadata
/// before guest cookie validation can read Redis, even with a signed cookie.
/// </summary>
public sealed class PreAuthenticationMutationGuardTests
{
    private sealed class CountingCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _data = new();
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);
        public bool Unavailable { get; set; }

        public byte[]? Get(string key) =>
            _data.TryGetValue(key, out var value) ? value : null;

        public Task<byte[]?> GetAsync(string key,
            CancellationToken token = default)
        {
            Interlocked.Increment(ref _reads);
            if (Unavailable)
                throw new IOException("Private Redis endpoint and password");
            return Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _data[key] = value;

        public Task SetAsync(string key, byte[] value,
            DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;

        public void Remove(string key) => _data.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingLimiter : IGuestMutationLimiter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<GuestMutationDecision> AcquireAsync(
            GuestMutationRateLimit rule, IPAddress? clientIp,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new GuestMutationDecision(true, 60_000));
        }
    }

    private static readonly (HttpMethod Method, string Path)[] MutationRoutes =
    [
        (HttpMethod.Post, "/api/sessions/guest"),
        (HttpMethod.Delete, "/api/sessions/guest"),
        (HttpMethod.Post, "/api/lobbies"),
        (HttpMethod.Post, "/api/lobbies/room-00000000000000000000000000000000/join"),
        (HttpMethod.Patch, "/api/lobbies/room-00000000000000000000000000000000/hero"),
        (HttpMethod.Patch, "/api/lobbies/room-00000000000000000000000000000000/team"),
        (HttpMethod.Delete, "/api/lobbies/room-00000000000000000000000000000000/membership")
    ];

    private sealed class ReadyProbe : IDeploymentReadiness
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }

    private static WebApplicationFactory<Program> Production(
        CountingCache cache, CountingLimiter limiter, bool stubReadiness = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, cache);
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
                if (stubReadiness)
                {
                    services.RemoveAll<IDeploymentReadiness>();
                    services.AddSingleton<IDeploymentReadiness, ReadyProbe>();
                }
            });
        });

    private static HttpClient Client(
        WebApplicationFactory<Program> server, bool development = false,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(development
                ? "http://localhost" : "https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Mutation(
        HttpMethod method, string path, string? origin, bool withIntent = true)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        if (withIntent)
            request.Headers.Add(WebMutationGuard.RequestHeaderName,
                WebMutationGuard.RequestHeaderValue);
        return request;
    }

    private static async Task<string> Issue(
        HttpClient client, string origin)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest", origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    private static void AssertEarlyForbidden(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task ProductionRejectsEveryBadMutationBeforeRedisCookieReadOrQuota()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A valid cookie would otherwise trigger a Redis lookup and 503
        // when Redis is unavailable. These rejections must stay 403 before
        // Redis is contacted, and must not consume shared mutation budget.
        cache.Unavailable = true;
        foreach (var (method, path) in MutationRoutes)
        {
            foreach (var (origin, intent) in new (string?, bool)[]
            {
                (null, true),
                ("https://evil.example", true),
                ("https://game.example", false)
            })
            {
                using var request = Mutation(method, path, origin, intent);
                using var denied = await guest.SendAsync(request);
                AssertEarlyForbidden(denied);
                Assert.Equal(0, cache.Reads);
                Assert.Equal(1, limiter.Calls);
            }
        }

        using (var request = Mutation(
            HttpMethod.Patch,
            "/api/lobbies/room-00000000000000000000000000000000/hero",
            "https://game.example"))
        using (var unavailable = await guest.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("Private Redis",
                await unavailable.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // Read-only endpoints never acquire Origin requirements; protected
        // reads still require Redis, but liveness stays independent.
        using (var read = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.Equal(2, cache.Reads);
        // A genuinely anonymous liveness check never needs guest Redis.
        // Requests carrying a cookie can still attempt authentication even
        // on public routes; that separate behavior is outside this guard.
        using (var health = await anonymous.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(2, cache.Reads);

        cache.Unavailable = false;
        using var recovered = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(3, cache.Reads);
    }

    [Fact]
    public async Task DevelopmentRejectsAllBadCookieMutationsButAcceptsValidReads()
    {
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var anonymous = Client(server, development: true);
        var cookie = await Issue(anonymous, "http://localhost");
        using var guest = Client(server, development: true, cookie: cookie);

        foreach (var (method, path) in MutationRoutes)
        {
            using var invalid = Mutation(method, path, "http://attacker.example");
            using var denied = await guest.SendAsync(invalid);
            AssertEarlyForbidden(denied);
        }
        using (var absentIntent = Mutation(
            HttpMethod.Delete, "/api/sessions/guest", "http://localhost",
            withIntent: false))
        using (var denied = await guest.SendAsync(absentIntent))
            AssertEarlyForbidden(denied);

        using (var me = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using (var badRoom = Mutation(
            HttpMethod.Patch, "/api/lobbies/not-a-room/hero",
            "http://localhost"))
        using (var response = await guest.SendAsync(badRoom))
        {
            // Valid Origin passes the new middleware and reaches normal
            // handler input validation/membership checks.
            Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CookieAuthAnonymousPrivateReadsAreNeverCacheable(
        bool development)
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = development
            ? new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder => builder.UseEnvironment("Development"))
            : Production(cache, limiter);
        using var anonymous = Client(server, development: development);

        // These 401s are generated by RequireAuthorization BEFORE the
        // endpoint sets Cache-Control. Neither response may be cached or
        // set a guest cookie.
        foreach (var path in new[]
                 {
                     "/api/sessions/me",
                     "/api/lobbies/room-00000000000000000000000000000000/roster"
                 })
        {
            using var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var raw = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("guest-", raw, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, limiter.Calls);
    }

    [Fact]
    public async Task CookieAuthForbidIsNeverCacheableEvenWithoutEndpointHandler()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var bootstrap = Client(server);
        // Starting TestServer resolves the actual configured cookie auth
        // handler. ForbidAsync deliberately skips any API handler, proving
        // that the event itself installs no-store.
        using (var health = await bootstrap.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var context = new DefaultHttpContext
        {
            RequestServices = server.Services
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("game.example");

        await context.ForbidAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Equal(0, cache.Reads);
        Assert.Equal(0, limiter.Calls);
    }

    [Fact]
    public async Task ProductionHostileHubOriginsAreRejectedBeforeGuestRedisAuthority()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        Assert.Equal(1, limiter.Calls);
        Assert.Equal(0, cache.Reads);

        cache.Unavailable = true;
        foreach (var (method, path) in new[]
                 {
                     (HttpMethod.Post, "/hubs/lobbies/negotiate?negotiateVersion=1"),
                     (HttpMethod.Get, "/hubs/lobbies")
                 })
        {
            foreach (var origin in new[]
                     {
                         "https://evil.example", "null", "https://game.example/path",
                         "https://game.example, https://evil.example"
                     })
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation("Origin", origin);
                using var denied = await guest.SendAsync(request);
                AssertEarlyForbidden(denied);
                Assert.Equal(0, cache.Reads); // Hostile Origin never reaches Redis.
                Assert.Equal(1, limiter.Calls);
            }
        }

        // A legitimate HTTPS Origin passes this fence, but Redis failure
        // still fails closed. No cookie, sensitive error or roster leaks.
        using (var trusted = new HttpRequestMessage(HttpMethod.Post,
                   "/hubs/lobbies/negotiate?negotiateVersion=1"))
        {
            trusted.Headers.TryAddWithoutValidation("Origin", "https://game.example");
            using var unavailable = await guest.SendAsync(trusted);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("no-store", unavailable.Headers.CacheControl?.ToString());
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("Private Redis",
                await unavailable.Content.ReadAsStringAsync());
        }
        Assert.Equal(1, cache.Reads);
        Assert.Equal(1, limiter.Calls);

        // A valid signed guest still negotiates once the authority recovers.
        cache.Unavailable = false;
        using var recoveredRequest = new HttpRequestMessage(HttpMethod.Post,
            "/hubs/lobbies/negotiate?negotiateVersion=1");
        recoveredRequest.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        using var recovered = await guest.SendAsync(recoveredRequest);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(2, cache.Reads);
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task PublicProbesWithGuestCookieNeverAskUnavailableRedisForAuthentication()
    {
        var cache = new CountingCache();
        var limiter = new CountingLimiter();
        using var server = Production(cache, limiter, stubReadiness: true);
        using var anonymous = Client(server);
        var cookie = await Issue(anonymous, "https://game.example");
        using var guest = Client(server, cookie: cookie);
        cache.Unavailable = true;

        // The signed cookie is attached, but neither public probe needs
        // to identify its visitor. Stub readiness isolates cookie auth.
        foreach (var route in new[] { "/api/health", "/api/ready" })
        {
            using var probe = await guest.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
            Assert.Equal("no-store", probe.Headers.CacheControl?.ToString());
            Assert.False(probe.Headers.Contains("Set-Cookie"));
            using var body = JsonDocument.Parse(
                await probe.Content.ReadAsStringAsync());
            if (route == "/api/health")
            {
                Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
                Assert.Equal("Shirokuro",
                    body.RootElement.GetProperty("application").GetString());
                Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            }
            else
            {
                Assert.Equal("ready", body.RootElement.GetProperty("status").GetString());
                Assert.Single(body.RootElement.EnumerateObject());
            }
        }
        Assert.Equal(0, cache.Reads);
        Assert.Equal(1, limiter.Calls); // Only initial issuance.

        // The protected API still revalidates this exact guest cookie
        // and fails closed if authoritative Redis is down.
        using var restricted = await guest.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, restricted.StatusCode);
        Assert.Equal("no-store", restricted.Headers.CacheControl?.ToString());
        Assert.False(restricted.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, cache.Reads);
    }
}
