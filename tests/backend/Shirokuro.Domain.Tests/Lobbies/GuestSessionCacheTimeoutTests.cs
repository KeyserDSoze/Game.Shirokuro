using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Deliberately ignores cancellation inside IDistributedCache to exercise
/// the registry's own deadline and HTTP fail-closed contract.
/// </summary>
public sealed class GuestSessionCacheTimeoutTests
{
    private sealed class StalledCache : IDistributedCache
    {
        private readonly ConcurrentDictionary<string, byte[]> _data = new();
        private int _gets;
        private int _sets;
        private int _removes;
        public int GetCalls => Volatile.Read(ref _gets);
        public int SetCalls => Volatile.Read(ref _sets);
        public int RemoveCalls => Volatile.Read(ref _removes);
        public int TotalCalls => GetCalls + SetCalls + RemoveCalls;
        public bool BlockGet { get; set; }
        public bool FailGet { get; set; }
        public bool BlockSet { get; set; }
        public bool BlockRemove { get; set; }

        public TaskCompletionSource<byte[]?> PendingGet { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PendingSet { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PendingRemove { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[]? Get(string key) =>
            _data.TryGetValue(key, out var bytes) ? bytes : null;

        public Task<byte[]?> GetAsync(string key,
            CancellationToken token = default)
        {
            Interlocked.Increment(ref _gets);
            if (FailGet)
                return Task.FromException<byte[]?>(
                    new IOException("sensitive private Redis connection detail"));
            return BlockGet ? PendingGet.Task : Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
            => _data[key] = value;

        public Task SetAsync(string key, byte[] value,
            DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Interlocked.Increment(ref _sets);
            if (BlockSet) return PendingSet.Task;
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) =>
            Task.CompletedTask;
        public void Remove(string key) => _data.TryRemove(key, out _);
        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Interlocked.Increment(ref _removes);
            if (BlockRemove) return PendingRemove.Task;
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(8);

    private static async Task AssertCanceledSoon(Task pending)
    {
        var finished = await Task.WhenAny(pending, Task.Delay(TestDeadline));
        Assert.Same(pending, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static async Task<(GuestSessionRegistry Registry,
        System.Security.Claims.ClaimsPrincipal Principal)> Registered(StalledCache cache)
    {
        var registry = new GuestSessionRegistry(TimeProvider.System, cache);
        var id = GuestSessions.NewPlayerId();
        var ticket = await registry.RegisterAsync(id, DateTimeOffset.UtcNow.AddHours(1));
        return (registry, GuestSessions.CreatePrincipal(id, ticket));
    }

    [Theory]
    [InlineData("register")]
    [InlineData("validate")]
    [InlineData("revoke-read")]
    [InlineData("revoke-delete")]
    [InlineData("recovery-delete")]
    public async Task PreCancelledCacheOperationDoesNotSendNetworkCommand(
        string operation)
    {
        var cache = new StalledCache();
        var (registry, principal) = await Registered(cache);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var prior = cache.TotalCalls;
        var id = GuestSessions.NewPlayerId();
        var task = operation switch
        {
            "register" => (Task)registry.RegisterAsync(id,
                DateTimeOffset.UtcNow.AddHours(1), cancelled.Token),
            "validate" => registry.IsActiveAsync(principal, cancelled.Token),
            "revoke-read" or "revoke-delete" =>
                registry.RevokeAsync(principal, cancelled.Token),
            "recovery-delete" => registry.RemoveRestoredSessionAsync(
                new string('a', 64), cancelled.Token),
            _ => throw new InvalidOperationException()
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(prior, cache.TotalCalls);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("validate")]
    [InlineData("revoke-read")]
    [InlineData("revoke-delete")]
    [InlineData("recovery-delete")]
    public async Task AbortedRequestStopsWaitWithoutConfirmingCacheOperation(
        string operation)
    {
        var cache = new StalledCache();
        var (registry, principal) = await Registered(cache);
        using var abort = new CancellationTokenSource();
        var id = GuestSessions.NewPlayerId();
        switch (operation)
        {
            case "register": cache.BlockSet = true; break;
            case "validate":
            case "revoke-read": cache.BlockGet = true; break;
            case "revoke-delete":
            case "recovery-delete": cache.BlockRemove = true; break;
        }

        var before = cache.TotalCalls;
        Task pending = operation switch
        {
            "register" => registry.RegisterAsync(id,
                DateTimeOffset.UtcNow.AddHours(1), abort.Token),
            "validate" => registry.IsActiveAsync(principal, abort.Token),
            "revoke-read" or "revoke-delete" =>
                registry.RevokeAsync(principal, abort.Token),
            "recovery-delete" => registry.RemoveRestoredSessionAsync(
                new string('b', 64), abort.Token),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(before + (operation == "revoke-delete" ? 2 : 1),
            cache.TotalCalls);
        abort.Cancel();
        await AssertCanceledSoon(pending);
        Assert.Equal(operation == "register" ? 2 : 1, cache.SetCalls);
        Assert.Equal(operation is "revoke-delete" or "recovery-delete" ? 1 : 0,
            cache.RemoveCalls); // no second dispatch after client cancellation
    }

    [Theory]
    [InlineData("register")]
    [InlineData("validate")]
    [InlineData("revoke-read")]
    [InlineData("revoke-delete")]
    [InlineData("recovery-delete")]
    public async Task CacheTimeoutNeverGrantsSessionOrConfirmsRevocation(
        string operation)
    {
        var cache = new StalledCache();
        var (registry, principal) = await Registered(cache);
        switch (operation)
        {
            case "register": cache.BlockSet = true; break;
            case "validate":
            case "revoke-read": cache.BlockGet = true; break;
            default: cache.BlockRemove = true; break;
        }
        var before = cache.TotalCalls;
        Task pending = operation switch
        {
            "register" => registry.RegisterAsync(GuestSessions.NewPlayerId(),
                DateTimeOffset.UtcNow.AddHours(1)),
            "validate" => registry.IsActiveAsync(principal),
            "revoke-read" or "revoke-delete" => registry.RevokeAsync(principal),
            "recovery-delete" => registry.RemoveRestoredSessionAsync(
                new string('c', 64)),
            _ => throw new InvalidOperationException()
        };
        Assert.Equal(before + (operation == "revoke-delete" ? 2 : 1),
            cache.TotalCalls);
        if (operation == "validate")
        {
            var task = (Task<bool>)pending;
            Assert.False(await task.WaitAsync(TestDeadline)); // explicit deny
        }
        else await AssertCanceledSoon(pending);

        // The previously registered guest remains tracked; a timed-out new
        // registration must never create a second local tracking entry.
        Assert.Equal(1, registry.StoredSessionCount);
        Assert.False(cache.PendingSet.Task.IsCompleted);
        Assert.False(cache.PendingRemove.Task.IsCompleted);
        Assert.False(cache.PendingGet.Task.IsCompleted);
    }

    private static WebApplicationFactory<Program> Production(StalledCache cache) =>
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
        });

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string? cookie = null)
    {
        var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static HttpRequestMessage Mutate(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        return request;
    }

    private static async Task<string> Issue(HttpClient client)
    {
        using var request = Mutate(HttpMethod.Post, "/api/sessions/guest");
        using var issued = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        return Assert.Single(issued.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    [Fact]
    public async Task ProductionDelayedSessionWriteReturns503WithoutGuestCookie()
    {
        var cache = new StalledCache { BlockSet = true };
        using var server = Production(cache);
        using var client = Client(server);
        using var request = Mutate(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request).WaitAsync(TestDeadline);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, server.Services
            .GetRequiredService<GuestSessionRegistry>().StoredSessionCount);
        Assert.Equal(1, cache.SetCalls);
    }

    [Fact]
    public async Task ProductionDelayedAuthorityReadReturns503AndRecoversWithSameCookie()
    {
        var cache = new StalledCache();
        using var server = Production(cache);
        using var visitor = Client(server);
        var cookie = await Issue(visitor);
        using var member = Client(server, cookie);

        cache.BlockGet = true;
        using (var denied = await member.GetAsync("/api/sessions/me")
            .WaitAsync(TestDeadline))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("Redis",
                await denied.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
        cache.BlockGet = false;
        using var accepted = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task ProductionDelayedRevocationReturnsSanitized503WithoutFalseSignOut()
    {
        var cache = new StalledCache();
        using var server = Production(cache);
        using var visitor = Client(server);
        var cookie = await Issue(visitor);
        using var member = Client(server, cookie);
        cache.BlockRemove = true;

        using (var request = Mutate(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await member.SendAsync(request).WaitAsync(TestDeadline))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-", body, StringComparison.OrdinalIgnoreCase);
        }
        cache.BlockRemove = false;
        using (var stillActive = await member.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
        using (var retry = Mutate(HttpMethod.Delete, "/api/sessions/guest"))
        using (var succeeded = await member.SendAsync(retry))
            Assert.Equal(HttpStatusCode.NoContent, succeeded.StatusCode);
        using var revoked = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task AuthorityStatusDistinguishesAbsentTicketFromCacheFailure()
    {
        var cache = new StalledCache();
        var (registry, principal) = await Registered(cache);
        Assert.Equal(GuestSessionAuthorityStatus.Active,
            await registry.CheckAuthorityAsync(principal));
        Assert.Equal(GuestSessionAuthorityStatus.Inactive,
            await registry.CheckAuthorityAsync(null));

        cache.FailGet = true;
        Assert.Equal(GuestSessionAuthorityStatus.Unavailable,
            await registry.CheckAuthorityAsync(principal));
        Assert.False(await registry.IsActiveAsync(principal));
        cache.FailGet = false;

        Assert.True(await registry.RevokeAsync(principal));
        Assert.Equal(GuestSessionAuthorityStatus.Inactive,
            await registry.CheckAuthorityAsync(principal));
    }

    [Fact]
    public async Task ProductionAuthorityOutageNeverAuthorizesHttpOrSignalR()
    {
        var cache = new StalledCache();
        using var server = Production(cache);
        using var visitor = Client(server);
        var cookie = await Issue(visitor);
        using var member = Client(server, cookie);

        // A previously issued cookie remains stored, but no protected data
        // or negotiation is reachable without Redis confirming its ticket.
        cache.FailGet = true;
        var calls = cache.GetCalls;
        foreach (var path in new[]
        {
            "/api/sessions/me",
            "/api/lobbies/room-00000000000000000000000000000000/roster"
        })
        {
            using var response = await member.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.DoesNotContain("sensitive private Redis",
                await response.Content.ReadAsStringAsync());
        }

        using (var negotiate = await member.PostAsync(
            "/hubs/lobbies/negotiate?negotiateVersion=1", content: null))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, negotiate.StatusCode);
            Assert.Equal("no-store", negotiate.Headers.CacheControl?.ToString());
            Assert.False(negotiate.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(calls + 3, cache.GetCalls);

        // A truly absent cookie is still 401 even if Redis is offline;
        // it does not manufacture an availability check or leak its state.
        using (var anonymous = Client(server))
        using (var denied = await anonymous.GetAsync("/api/sessions/me"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        Assert.Equal(calls + 3, cache.GetCalls);
        using (var health = await visitor.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        cache.FailGet = false;
        using var recovered = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ProductionConfirmedRevocationIsStill401NotTemporary503()
    {
        var cache = new StalledCache();
        using var server = Production(cache);
        using var visitor = Client(server);
        var cookie = await Issue(visitor);
        using var member = Client(server, cookie);
        using (var logout = Mutate(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await member.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var denied = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.False(denied.Headers.Contains("Set-Cookie"));
    }
}
