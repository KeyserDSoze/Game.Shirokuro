using System.Net;
using System.Security.Claims;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.SignalR;using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shirokuro.Application.Lobbies;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Security;using Shirokuro.Api.Lobbies;
using Shirokuro.Domain.Lobbies;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Real Redis service, independent API TestServers, shared guest cache and
/// encrypted Data Protection key ring. Production DI must never serve a room
/// from the process-local coordinator.
/// </summary>
public sealed class RedisLobbyHttpIntegrationTests
{
    private sealed class FakeHubContext(ClaimsPrincipal user) : HubCallerContext
    {
        public override string ConnectionId => "test-crossnode";
        public override string? UserIdentifier => "spoofed-identity";
        public override ClaimsPrincipal User => user;
        public override IDictionary<object, object?> Items { get; } =
            new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("SHIROKURO_TEST_REDIS");

    private static async Task<(ConnectionMultiplexer A, ConnectionMultiplexer B)> Redis()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
            throw new InvalidOperationException("SHIROKURO_TEST_REDIS is required in CI.");
        return (await ConnectionMultiplexer.ConnectAsync(ConnectionString),
                await ConnectionMultiplexer.ConnectAsync(ConnectionString));
    }

    private static (ServiceProvider Provider, IDistributedCache Cache) SharedSessions()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        var provider = services.BuildServiceProvider();
        return (provider, provider.GetRequiredService<IDistributedCache>());
    }

    private static WebApplicationFactory<Program> Server(IDistributedCache shared,
        IConnectionMultiplexer redis, string ns) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder, shared);
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
                services.RemoveAll<RedisLobbySnapshotStore>();
                services.AddSingleton(provider => new RedisLobbySnapshotStore(
                    redis.GetDatabase(), provider.GetRequiredService<TimeProvider>(),
                    namespaceName: ns));
                services.RemoveAll<IAsyncGuestLobby>();
                services.AddSingleton<IAsyncGuestLobby, RedisGuestLobbyRuntime>();
            });
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

    private static HttpRequestMessage Write(HttpMethod method, string path, object? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        if (json is not null)
            request.Content = JsonContent.Create(json);
        return request;
    }

    private static async Task<(string Id, string Cookie)> Login(HttpClient client)
    {
        using var request = Write(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        return (json.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    private static async Task<(string Id, long Revision)> Create(HttpClient client)
    {
        using var req = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("lobbyId").GetString()!,
            json.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task ProductionCrossNodeRoomIsSharedAuthorizedAndLogoutCleansEveryRoster()
    {
        if (ConnectionString is null) return;
        var (redisA, redisB) = await Redis();
        using (redisA)
        using (redisB)
        {
            var (sessionProvider, cache) = SharedSessions();
            using (sessionProvider)
            {
                var ns = "api-" + Guid.NewGuid().ToString("N");
                using var serverA = Server(cache, redisA, ns);
                using var serverB = Server(cache, redisB, ns);
                using var visitorA = Client(serverA);
                using var visitorB = Client(serverB);
                var (aliceId, aliceCookie) = await Login(visitorA);
                var (bobId, bobCookie) = await Login(visitorB);
                using var aliceA = Client(serverA, aliceCookie);
                using var aliceB = Client(serverB, aliceCookie);
                using var bobB = Client(serverB, bobCookie);
                var (sharedRoom, revision) = await Create(aliceA);
                var (soloRoom, _) = await Create(aliceA);

                // A newly created room is readable on the other process,
                // without ever consulting either node's local coordinator.
                using (var crossRead = await aliceB.GetAsync(
                    "/api/lobbies/" + sharedRoom + "/roster"))
                {
                    Assert.Equal(HttpStatusCode.OK, crossRead.StatusCode);
                    using var data = JsonDocument.Parse(await crossRead.Content.ReadAsStringAsync());
                    Assert.Equal(revision, data.RootElement.GetProperty("revision").GetInt64());
                }

                using (var outsiderRead = await bobB.GetAsync(
                    "/api/lobbies/" + sharedRoom + "/roster"))
                    Assert.Equal(HttpStatusCode.NotFound, outsiderRead.StatusCode);

                using (var outsiderHero = Write(HttpMethod.Patch,
                    "/api/lobbies/" + sharedRoom + "/hero",
                    new { heroId = "kira", expectedRevision = 1 }))
                using (var denied = await bobB.SendAsync(outsiderHero))
                    Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

                using (var join = Write(HttpMethod.Post,
                    "/api/lobbies/" + sharedRoom + "/join",
                    new { teamId = "black", expectedRevision = revision }))
                using (var joined = await bobB.SendAsync(join))
                {
                    Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
                    using var j = JsonDocument.Parse(await joined.Content.ReadAsStringAsync());
                    Assert.Equal(2, j.RootElement.GetProperty("revision").GetInt64());
                }
                using (var pick = Write(HttpMethod.Patch,
                    "/api/lobbies/" + sharedRoom + "/hero",
                    new { heroId = "kira", expectedRevision = 2 }))
                using (var selected = await bobB.SendAsync(pick))
                    Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

                using (var team = Write(HttpMethod.Patch,
                    "/api/lobbies/" + sharedRoom + "/team",
                    new { teamId = "black", expectedRevision = 3 }))
                using (var changed = await aliceA.SendAsync(team))
                    Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

                using (var stale = Write(HttpMethod.Patch,
                    "/api/lobbies/" + sharedRoom + "/hero",
                    new { heroId = "krugg", expectedRevision = 2 }))
                using (var conflict = await bobB.SendAsync(stale))
                    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

                // Simulate an already-open SignalR method on B, using the
                // signed principal cached before logout. Hub DI must use
                // the same Redis coordinator as the HTTP roster endpoints.
                var ticketFormat = serverA.Services
                    .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                    .Get(CookieAuthenticationDefaults.AuthenticationScheme).TicketDataFormat;
                var principal = ticketFormat.Unprotect(aliceCookie.Split('=', 2)[1])!.Principal;
                var hubOnB = new LobbyRosterHub(
                    serverB.Services.GetRequiredService<LobbyRosterReader>(),
                    serverB.Services.GetRequiredService<GuestSessionRegistry>(),
                    serverB.Services.GetRequiredService<IAsyncGuestLobby>())
                {
                    Context = new FakeHubContext(principal)
                };
                Assert.Equal(sharedRoom, (await hubOnB.GetRoster(sharedRoom)).LobbyId);

                // Logout on B revokes A's session in shared cache, fences
                // future lobby CAS scripts and then removes Alice's rooms
                // without renewing survivor TTLs.
                using (var logout = Write(HttpMethod.Delete, "/api/sessions/guest"))
                using (var signedOut = await aliceB.SendAsync(logout))
                    Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

                // Even an already-authenticated node cannot recreate Alice's
                // membership after the cross-node revocation fence.
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    serverA.Services.GetRequiredService<IAsyncGuestLobby>()
                        .JoinAsync(sharedRoom, aliceId, "white", null));
                Assert.Equal("Lobby unavailable.",
                    (await Assert.ThrowsAsync<HubException>(
                        () => hubOnB.GetRoster(sharedRoom))).Message);

                using (var stolen = await aliceA.GetAsync(
                    "/api/lobbies/" + sharedRoom + "/roster"))
                    Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
                using (var surviving = await bobB.GetAsync(
                    "/api/lobbies/" + sharedRoom + "/roster"))
                {
                    Assert.Equal(HttpStatusCode.OK, surviving.StatusCode);
                    using var j = JsonDocument.Parse(await surviving.Content.ReadAsStringAsync());
                    Assert.Equal(5, j.RootElement.GetProperty("revision").GetInt64());
                    var participants = j.RootElement.GetProperty("participants").EnumerateArray()
                        .ToArray();
                    Assert.Single(participants);
                    Assert.Equal(bobId, participants[0].GetProperty("playerId").GetString());
                    Assert.DoesNotContain(aliceId, await surviving.Content.ReadAsStringAsync());
                }

                using (var oldInvite = Write(HttpMethod.Post,
                    "/api/lobbies/" + soloRoom + "/join",
                    new { teamId = "black" }))
                using (var missing = await bobB.SendAsync(oldInvite))
                    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
        }
    }

    [Fact]
    public async Task ParallelJoinRevisionIsAtomicAndThirdInstanceRecoversRoster()
    {
        if (ConnectionString is null) return;
        var (redisA, redisB) = await Redis();
        using (redisA)
        using (redisB)
        {
            var (sessionProvider, cache) = SharedSessions();
            using (sessionProvider)
            {
                var ns = "parallel-" + Guid.NewGuid().ToString("N");
                using var serverA = Server(cache, redisA, ns);
                using var serverB = Server(cache, redisB, ns);
                using var visitorA = Client(serverA);
                using var visitorB = Client(serverB);
                var (_, ownerCookie) = await Login(visitorA);
                var (_, bobCookie) = await Login(visitorA);
                var (_, caraCookie) = await Login(visitorB);
                using var owner = Client(serverA, ownerCookie);
                using var bob = Client(serverB, bobCookie);
                using var cara = Client(serverA, caraCookie);
                var (room, _) = await Create(owner);

                async Task<HttpStatusCode> Join(HttpClient client, string team)
                {
                    using var request = Write(HttpMethod.Post,
                        "/api/lobbies/" + room + "/join",
                        new { teamId = team, expectedRevision = 1L });
                    using var response = await client.SendAsync(request);
                    return response.StatusCode;
                }
                var codes = await Task.WhenAll(Join(bob, "black"), Join(cara, "white"));
                Assert.Single(codes, c => c == HttpStatusCode.OK);
                Assert.Single(codes, c => c == HttpStatusCode.Conflict);

                using var fresh = Server(cache, redisB, ns); // New host, no local room state.
                using var recovered = Client(fresh, ownerCookie);
                using var roster = await recovered.GetAsync("/api/lobbies/" + room + "/roster");
                Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
                using var doc = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
                Assert.Equal(2, doc.RootElement.GetProperty("revision").GetInt64());
                Assert.Equal(2, doc.RootElement.GetProperty("participants").GetArrayLength());
            }
        }
    }

    [Fact]
    public async Task SessionExpiryWorkerOnAnotherNodeRemovesMemberWithoutExtendingTtl()
    {
        if (ConnectionString is null) return;
        var (redisA, redisB) = await Redis();
        using (redisA)
        using (redisB)
        {
            var clock = new ManualClock();
            var ns = "expired-" + Guid.NewGuid().ToString("N");
            var storeA = new RedisLobbySnapshotStore(redisA.GetDatabase(), clock,
                namespaceName: ns);
            var storeB = new RedisLobbySnapshotStore(redisB.GetDatabase(), clock,
                namespaceName: ns);
            var onA = new RedisGuestLobbyRuntime(storeA, clock);
            var onB = new RedisGuestLobbyRuntime(storeB, clock);
            var registry = new GuestSessionRegistry(clock);
            var aliceId = GuestSessions.NewPlayerId();
            var bobId = GuestSessions.NewPlayerId();
            registry.Register(aliceId, clock.GetUtcNow().AddHours(8));

            // The room has recent activity shortly before the guest's
            // absolute 8h session expiry, so it must survive cleanup.
            clock.Advance(TimeSpan.FromMinutes(479));
            var room = await onA.CreateAsync(aliceId,
                ["white", "black"], "white");
            var joined = await onB.JoinAsync(
                room.LobbyId, bobId, "black", room.Revision);
            var before = (await storeA.ReadAsync(room.LobbyId))!;
            Assert.Equal(2, joined.Revision);

            clock.Advance(TimeSpan.FromMinutes(2));
            var worker = new GuestSessionExpiryWorker(registry,
                new InMemoryLobbyCoordinator(), clock, onB);
            Assert.Equal(1, await worker.SweepOnceAsync());

            var after = (await storeA.ReadAsync(room.LobbyId))!;
            Assert.Equal(before.Snapshot.LastActivityUtc,
                after.Snapshot.LastActivityUtc);
            Assert.Equal(aliceId, after.Snapshot.CreatorId);
            Assert.Equal(3, after.Snapshot.State.Revision);
            Assert.Equal(bobId, Assert.Single(after.Snapshot.State.Participants).PlayerId);
            Assert.Equal(0, await worker.SweepOnceAsync());
        }
    }

    [Fact]
    public async Task ProductionNeverPublishesAnUnindexedLegacyRoom()
    {
        if (ConnectionString is null) return;
        var (redisA, redisB) = await Redis();
        using (redisA)
        using (redisB)
        {
            var (sessionProvider, cache) = SharedSessions();
            using (sessionProvider)
            {
                var ns = "legacy-" + Guid.NewGuid().ToString("N");
                using var serverA = Server(cache, redisA, ns);
                using var serverB = Server(cache, redisB, ns);
                using var visitor = Client(serverA);
                var (actor, cookie) = await Login(visitor);
                var roomId = "room-" + Guid.NewGuid().ToString("N");
                var definition = new LobbyDefinition(roomId, ["white", "black"],
                    OriginalHeroRoster.Ids);
                var state = LobbyState.Create(definition).Join(actor, "white", 0);
                var lowLevel = new RedisLobbySnapshotStore(redisA.GetDatabase(),
                    TimeProvider.System, namespaceName: ns);
                Assert.True(await lowLevel.CreateAsync(
                    new LobbySnapshot(state, DateTimeOffset.UtcNow, actor)));

                using var authorized = Client(serverB, cookie);
                using var response = await authorized.GetAsync(
                    "/api/lobbies/" + roomId + "/roster");
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                using var selection = Write(HttpMethod.Patch,
                    "/api/lobbies/" + roomId + "/hero",
                    new { heroId = "kira", expectedRevision = 1 });
                using var blocked = await authorized.SendAsync(selection);
                Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
            }
        }
    }

    [Fact]
    public async Task PerCreatorQuotaPersistsAcrossNodesAndLastLeaveReleasesIt()
    {
        if (ConnectionString is null) return;
        var (redisA, redisB) = await Redis();
        using (redisA)
        using (redisB)
        {
            var (sessionProvider, cache) = SharedSessions();
            using (sessionProvider)
            {
                var ns = "limits-" + Guid.NewGuid().ToString("N");
                using var serverA = Server(cache, redisA, ns);
                using var serverB = Server(cache, redisB, ns);
                using var visitor = Client(serverA);
                var (_, cookie) = await Login(visitor);
                using var aliceA = Client(serverA, cookie);
                using var aliceB = Client(serverB, cookie);
                var rooms = new List<string>();
                for (var i = 0; i < 4; i++)
                    rooms.Add((await Create(i % 2 == 0 ? aliceA : aliceB)).Id);

                using (var fifth = Write(HttpMethod.Post, "/api/lobbies",
                    new { teamIds = new[] { "white", "black" }, teamId = "white" }))
                using (var limited = await aliceB.SendAsync(fifth))
                    Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);

                using (var leave = Write(HttpMethod.Delete,
                    "/api/lobbies/" + rooms[0] + "/membership",
                    new { expectedRevision = 1 }))
                using (var left = await aliceB.SendAsync(leave))
                    Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);

                var (replacement, _) = await Create(aliceA);
                Assert.DoesNotContain(replacement, rooms);
            }
        }
    }
}
