using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class DistributedGuestSessionTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private static (IDistributedCache Cache, ServiceProvider Provider) Cache()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IDistributedCache>(), provider);
    }

    [Fact]
    public async Task SharedStoreValidatesAndRevokesAcrossIndependentRegistries()
    {
        var (cache, provider) = Cache();
        using (provider)
        {
            var clock = new ManualClock();
            var a = new GuestSessionRegistry(clock, cache);
            var b = new GuestSessionRegistry(clock, cache);
            var player = GuestSessions.NewPlayerId();
            var token = await a.RegisterAsync(player, clock.GetUtcNow().AddHours(8));
            var principal = GuestSessions.CreatePrincipal(player, token);

            Assert.False(a.IsActive(principal)); // local authority must never override Redis
            Assert.True(await b.IsActiveAsync(principal));
            Assert.False(await b.IsActiveAsync(GuestSessions.CreatePrincipal(
                GuestSessions.NewPlayerId(), token)));
            Assert.False(await b.IsActiveAsync(GuestSessions.CreatePrincipal(
                player, new string('f', 32))));
            Assert.True(await b.RevokeAsync(principal));
            Assert.False(await a.IsActiveAsync(principal));
            Assert.False(await b.RevokeAsync(principal));
            Assert.False(await a.IsActiveAsync(principal));
        }
    }

    [Fact]
    public async Task AbsoluteExpiryIsEnforcedWithoutSlidingAcrossTwoProcesses()
    {
        var (cache, provider) = Cache();
        using (provider)
        {
            var clock = new ManualClock();
            var a = new GuestSessionRegistry(clock, cache);
            var b = new GuestSessionRegistry(clock, cache);
            var id = GuestSessions.NewPlayerId();
            var token = await a.RegisterAsync(id, clock.GetUtcNow().AddMinutes(5));
            var principal = GuestSessions.CreatePrincipal(id, token);

            clock.Advance(TimeSpan.FromMinutes(4));
            Assert.True(await b.IsActiveAsync(principal));
            Assert.True(await a.IsActiveAsync(principal));
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(await b.IsActiveAsync(principal));
            Assert.False(await a.RevokeAsync(principal));
            Assert.Equal([id], a.DrainExpiredPlayerIds());
            Assert.Empty(b.DrainExpiredPlayerIds()); // B did not issue this ticket
        }
    }

    [Fact]
    public async Task UnsharedStoresDenyEvenWithIdenticalSignedGuestClaims()
    {
        var (firstCache, firstProvider) = Cache();
        var (otherCache, otherProvider) = Cache();
        using (firstProvider)
        using (otherProvider)
        {
            var clock = new ManualClock();
            var id = GuestSessions.NewPlayerId();
            var a = new GuestSessionRegistry(clock, firstCache);
            var b = new GuestSessionRegistry(clock, otherCache);
            var token = await a.RegisterAsync(id, clock.GetUtcNow().AddHours(1));
            var principal = GuestSessions.CreatePrincipal(id, token);
            Assert.True(await a.IsActiveAsync(principal));
            Assert.False(await b.IsActiveAsync(principal));
        }
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void RedisConfigurationIsRequiredOutsideDevelopment(string environment)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRedisConfiguration.Configure(
                services, config, new FakeEnvironment(environment)));
    }

    [Theory]
    [InlineData("localhost:6379")]
    [InlineData("localhost:6379,ssl=false")]
    [InlineData("not-an-option=oops")]
    public void UnencryptedOrMalformedRedisConfigFailsClosed(string connection)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [GuestSessionRedisConfiguration.ConnectionStringKey] = connection
            }).Build();
        Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRedisConfiguration.Configure(
                services, config, new FakeEnvironment("Production")));
    }

    [Fact]
    public void DevelopmentNeedsNoRedis()
    {
        var services = new ServiceCollection();
        GuestSessionRedisConfiguration.Configure(
            services, new ConfigurationBuilder().Build(),
            new FakeEnvironment("Development"));
        Assert.DoesNotContain(services,
            item => item.ServiceType == typeof(IDistributedCache));
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Shirokuro";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private static WebApplicationFactory<Program> Server(IDistributedCache shared) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, shared);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
        });

    private static HttpClient Client(WebApplicationFactory<Program> server, string? cookie = null)
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

    private static HttpRequestMessage Mutation(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        request.Headers.TryAddWithoutValidation("X-Shirokuro-Request", "1");
        return request;
    }

    [Fact]
    public async Task TwoIndependentApisAcceptAndRevokeTheSameGuestCookie()
    {
        var (cache, provider) = Cache();
        using (provider)
        using (var aServer = Server(cache))
        using (var bServer = Server(cache))
        {
            using var visitor = Client(aServer);
            using var login = Mutation(HttpMethod.Post, "/api/sessions/guest");
            using var response = await visitor.SendAsync(login);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"))
                .Split(';')[0];
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var id = json.RootElement.GetProperty("playerId").GetString()!;

            using var bClient = Client(bServer, cookie);
            using var meOnB = await bClient.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.OK, meOnB.StatusCode);
            using var bData = JsonDocument.Parse(await meOnB.Content.ReadAsStringAsync());
            Assert.Equal(id, bData.RootElement.GetProperty("playerId").GetString());

            using var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest");
            using var loggedOut = await bClient.SendAsync(logout);
            Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

            using var aClient = Client(aServer, cookie);
            using var revokedOnA = await aClient.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, revokedOnA.StatusCode);
            using var revokedOnB = await bClient.GetAsync("/api/sessions/me");
            Assert.Equal(HttpStatusCode.Unauthorized, revokedOnB.StatusCode);
        }
    }
}
