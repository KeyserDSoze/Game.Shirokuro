using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestSessionExpirationCleanupTests
{
    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _milliseconds, (long)duration.TotalMilliseconds);
    }

    [Fact]
    public void ExpiryWorkerRemovesOnlyExpiredGuestAndKeepsSurvivingRoomTtl()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var coordinator = new InMemoryLobbyCoordinator(clock);
        var worker = new GuestSessionExpiryWorker(registry, coordinator, clock);
        registry.Register("alice", clock.GetUtcNow().AddMinutes(6));
        registry.Register("bob", clock.GetUtcNow().AddHours(1));

        coordinator.CreateWithOwner("shared", ["white", "black"], "alice", "white", 10);
        coordinator.JoinBounded("shared", "bob", "black", 1, 16);
        coordinator.CreateWithOwner("solo", ["white", "black"], "alice", "white", 10);
        coordinator.CreateWithOwner("bob-room", ["white", "black"], "bob", "black", 10);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(2, worker.SweepOnce());
        Assert.Equal(0, worker.SweepOnce());
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("solo"));

        var shared = coordinator.Get("shared");
        Assert.Equal(3, shared.Revision);
        Assert.Equal("bob", Assert.Single(shared.Participants).PlayerId);
        Assert.Equal(1, coordinator.Get("bob-room").Revision);
        Assert.Equal(2, coordinator.StoredRoomCount);

        // The worker's administrative mutation must preserve the original
        // LastActivityUtc, so the room expires 30m after creation, not cleanup.
        clock.Advance(TimeSpan.FromMinutes(24));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("shared"));
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("bob-room"));
        Assert.Equal(1, registry.StoredSessionCount); // Bob's ticket remains registered.
    }

    [Fact]
    public void SweepAfterOpportunisticExpiryStillRemovesMembershipOnce()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var coordinator = new InMemoryLobbyCoordinator(clock);
        var worker = new GuestSessionExpiryWorker(registry, coordinator, clock);
        coordinator.CreateWithOwner("room", ["white", "black"], "alice", "white", 10);
        registry.Register("alice", clock.GetUtcNow().AddMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(5));
        registry.Register("bob", clock.GetUtcNow().AddHours(1)); // Prunes alice.
        Assert.Equal(0, registry.PruneExpired());
        Assert.Equal(1, worker.SweepOnce());
        Assert.Throws<KeyNotFoundException>(() => coordinator.Get("room"));
    }

    [Fact]
    public void ConcurrentWorkerSweepsAreIdempotentAndLockSafe()
    {
        var clock = new ManualClock();
        var registry = new GuestSessionRegistry(clock);
        var coordinator = new InMemoryLobbyCoordinator(clock);
        var worker = new GuestSessionExpiryWorker(registry, coordinator, clock);
        registry.Register("alice", clock.GetUtcNow().AddMinutes(1));
        registry.Register("bob", clock.GetUtcNow().AddMinutes(30));
        coordinator.CreateWithOwner("shared", ["white", "black"], "alice", "white", 10);
        coordinator.JoinBounded("shared", "bob", "black", 1, 16);
        clock.Advance(TimeSpan.FromMinutes(1));

        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(worker.SweepOnce));
        var removed = Task.WhenAll(tasks).GetAwaiter().GetResult().Sum();
        Assert.Equal(1, removed);
        Assert.Equal(3, coordinator.Get("shared").Revision);
        Assert.Equal("bob", Assert.Single(coordinator.Get("shared").Participants).PlayerId);
    }

    [Fact]
    public async Task TestServerExpiryDeniesOldCookieAndKeepsOtherGuestsRoster()
    {
        var clock = new ManualClock();
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<TimeProvider>(clock)));
        using var aliceVisitor = CreateClient(server);
        var (aliceId, aliceCookie) = await SignIn(aliceVisitor);

        // A second guest signs in later, so at the first guest's absolute 8h
        // expiry its cookie remains authorized and its roster is still visible.
        clock.Advance(TimeSpan.FromMinutes(10));
        using var bobVisitor = CreateClient(server);
        var (bobId, bobCookie) = await SignIn(bobVisitor);
        using var alice = CreateClient(server, aliceCookie);
        using var bob = CreateClient(server, bobCookie);

        var shared = await CreateRoom(alice);
        var solo = await CreateRoom(alice);
        using (var join = Mutation(HttpMethod.Post, "/api/lobbies/" + shared + "/join",
            new { teamId = "black" }))
        using (var joined = await bob.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        var worker = Assert.Single(server.Services.GetServices<IHostedService>()
            .OfType<GuestSessionExpiryWorker>());
        // Both rooms remain active over eight hours; the other member keeps
        // the shared room alive, while the owner keeps the solo room alive.
        for (var iteration = 0; iteration < 19; iteration++)
        {
            clock.Advance(TimeSpan.FromMinutes(24));
            coordinator.GetForMember(shared, bobId);
            coordinator.GetForMember(solo, aliceId);
        }
        clock.Advance(TimeSpan.FromMinutes(14)); // Exactly 8h after alice sign-in.
        Assert.Equal(2, worker.SweepOnce());
        Assert.Equal(0, worker.SweepOnce());

        using (var expired = await alice.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        using (var roster = await bob.GetAsync("/api/lobbies/" + shared + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            var json = await roster.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(json);
            Assert.Equal(3, payload.RootElement.GetProperty("revision").GetInt64());
            var onlyMember = Assert.Single(payload.RootElement.GetProperty("participants").EnumerateArray());
            Assert.Equal(bobId, onlyMember.GetProperty("playerId").GetString());
            Assert.DoesNotContain(aliceId, json, StringComparison.Ordinal);
        }

        using (var joinSolo = Mutation(HttpMethod.Post, "/api/lobbies/" + solo + "/join",
            new { teamId = "black" }))
        using (var absent = await bob.SendAsync(joinSolo))
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        using var stillValid = await bob.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> server,
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

    private static HttpRequestMessage Mutation(HttpMethod method, string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "http://localhost");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string PlayerId, string Cookie)> SignIn(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("playerId").GetString()!;
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        return (id, cookie);
    }

    private static async Task<string> CreateRoom(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("lobbyId").GetString()!;
    }
}
