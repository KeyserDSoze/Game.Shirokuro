using System.IO;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Cross-origin or missing-intent POSTs must fail before they can deplete a
/// trusted client's shared Redis admission budget or local dev allowance.
/// </summary>
public sealed class GuestAdmissionOriginOrderingTests
{
    private sealed class CountingLimiter : IGuestMutationLimiter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool Unavailable { get; set; }

        public Task<GuestMutationDecision> AcquireAsync(
            GuestMutationRateLimit policy, IPAddress? trustedRemoteIp,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            if (Unavailable)
                throw new IOException("Private Redis connection details");
            return Task.FromResult(new GuestMutationDecision(true, 60_000));
        }
    }

    private static WebApplicationFactory<Program> Production(CountingLimiter limiter) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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

    private static HttpClient Client(
        WebApplicationFactory<Program> server, bool development = false) =>
        server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(development
                ? "http://localhost" : "https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static HttpRequestMessage Post(
        string path, string? origin, bool withIntent = true,
        string? cookie = null, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (origin is not null)
            request.Headers.TryAddWithoutValidation("Origin", origin);
        if (withIntent)
            request.Headers.Add("X-Shirokuro-Request", "1");
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static void AssertSanitizedForbidden(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task ProductionInvalidGuestIntentsNeverCallUnavailableRedisLimiter()
    {
        var limiter = new CountingLimiter { Unavailable = true };
        using var server = Production(limiter);
        using var client = Client(server);

        foreach (var (origin, intent) in new (string?, bool)[]
        {
            (null, true),
            ("https://evil.example", true),
            ("http://game.example", true),
            ("https://game.example/", true),
            ("https://game.example", false)
        })
        {
            using var request = Post("/api/sessions/guest", origin, intent);
            using var response = await client.SendAsync(request);
            AssertSanitizedForbidden(response);
            Assert.Equal(0, limiter.Calls);
        }

        // A valid request still reaches the unavailable distributed budget,
        // fails closed and never issues a cookie.
        using var authorized = Post("/api/sessions/guest",
            "https://game.example");
        using var unavailable = await client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.False(unavailable.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, limiter.Calls);
    }

    [Fact]
    public async Task ProductionInvalidRoomIntentsDoNotSpendSharedRoomPermits()
    {
        var limiter = new CountingLimiter();
        using var server = Production(limiter);
        using var client = Client(server);
        using var login = Post("/api/sessions/guest", "https://game.example");
        using var issued = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var cookie = Assert.Single(issued.Headers.GetValues("Set-Cookie"))
            .Split(';')[0];
        Assert.Equal(1, limiter.Calls);

        var payload = new { teamIds = new[] { "white", "black" },
            teamId = "white" };
        foreach (var (origin, intent) in new (string?, bool)[]
        {
            ("https://evil.example", true),
            ("https://game.example", false),
            (null, true)
        })
        {
            using var request = Post("/api/lobbies", origin, intent,
                cookie: cookie, body: payload);
            using var response = await client.SendAsync(request);
            AssertSanitizedForbidden(response);
            Assert.Equal(1, limiter.Calls);
        }

        limiter.Unavailable = true;
        using (var valid = Post("/api/lobbies", "https://game.example",
            cookie: cookie, body: payload))
        using (var unavailable = await client.SendAsync(valid))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.False(unavailable.Headers.Contains("Set-Cookie"));
            Assert.Equal(2, limiter.Calls);
        }

        limiter.Unavailable = false;
        using var permitted = Post("/api/lobbies", "https://game.example",
            cookie: cookie, body: payload);
        using var created = await client.SendAsync(permitted);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(3, limiter.Calls);
    }

    [Fact]
    public async Task DevelopmentCrossOriginFloodDoesNotExhaustLocalGuestBudget()
    {
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = Client(server, development: true);

        // Without the early guard, the fixed-window limiter rejects with
        // 429 after 12 attempts, and a later legitimate guest is throttled.
        for (var i = 0; i < 18; i++)
        {
            using var bad = Post("/api/sessions/guest",
                "http://attacker.example");
            using var response = await client.SendAsync(bad);
            AssertSanitizedForbidden(response);
        }

        using var valid = Post("/api/sessions/guest", "http://localhost");
        using var issued = await client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.True(issued.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task ReadOnlyRoutesRemainUnaffectedByAdmissionGuard()
    {
        var limiter = new CountingLimiter { Unavailable = true };
        using var server = Production(limiter);
        using var client = Client(server);
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(0, limiter.Calls);
    }
}
