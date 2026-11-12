using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class LobbyRosterTransportTests
{
    private const string RoomA = "room-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RoomB = "room-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RoomMissing = "room-00000000000000000000000000000000";
    private const string RoomSecret = "room-cccccccccccccccccccccccccccccccc";
    private const string RoomShared = "room-dddddddddddddddddddddddddddddddd";

    private static WebApplicationFactory<Program> CreateServer(
        IAsyncGuestLobby? overrideLobby = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ =>
                {
                    var coordinator = new InMemoryLobbyCoordinator();
                    coordinator.Create(RoomA, ["white", "black"]);
                    coordinator.Join(RoomA, "alice", "white", 0);
                    coordinator.Join(RoomA, "bob", "black", 1);
                    coordinator.ChooseHero(RoomA, "alice", "kira", 2);
                    coordinator.Create(RoomB, ["white", "black"]);
                    coordinator.Join(RoomB, "cara", "white", 0);
                    return coordinator;
                });
                if (overrideLobby is not null)
                {
                    services.RemoveAll<IAsyncGuestLobby>();
                    services.AddSingleton(overrideLobby);
                }
            }));

    private static HttpClient ClientFor(
        WebApplicationFactory<Program> factory,
        params Claim[] claims)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
        var signed = options.TicketDataFormat.Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + signed);
        return client;
    }

    private sealed class ContendedLobby : IAsyncGuestLobby
    {
        public int Reads { get; private set; }

        public Task<LobbyRosterView> ForMemberAsync(string roomId, string trustedPlayerId,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            // Simulate the Redis runtime's bounded heartbeat CAS retry
            // exhaustion AFTER auth/member routing, without real contention
            // races or an arbitrary InvalidOperationException catch-all.
            return Task.FromException<LobbyRosterView>(
                new LobbyReadContentionException());
        }

        public Task<LobbyRosterView> CreateAsync(string trustedPlayerId,
            IReadOnlyList<string>? teamIds, string? initialTeam,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> JoinAsync(string roomId, string trustedPlayerId,
            string? teamId, long? expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChooseHeroAsync(string roomId, string trustedPlayerId,
            string? heroId, long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<LobbyRosterView> ChangeTeamAsync(string roomId, string trustedPlayerId,
            string? teamId, long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task LeaveAsync(string roomId, string trustedPlayerId,
            long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<int> RemovePlayerFromAllRoomsAsync(string trustedPlayerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task ContendedAuthenticatedHttpRosterReturnsUncachedSanitized503()
    {
        var contended = new ContendedLobby();
        using var server = CreateServer(contended);
        using var alice = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "alice"));

        using var response = await alice.GetAsync($"/api/lobbies/{RoomA}/roster");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("contention", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, contended.Reads);
    }

    [Fact]
    public async Task ContendedSignalRRosterKeepsExistingOpaqueUnavailableError()
    {
        var contended = new ContendedLobby();
        var hub = new LobbyRosterHub(
            new LobbyRosterReader(new InMemoryLobbyCoordinator()),
            lobby: contended)
        {
            Context = new FakeHubContext(Principal("alice"))
        };
        var error = await Assert.ThrowsAsync<HubException>(
            () => hub.GetRoster(RoomA));
        Assert.Equal("Lobby unavailable.", error.Message);
        Assert.Equal(1, contended.Reads);
    }

    [Fact]
    public async Task AnonymousOrSpoofedHttpIdentityCannotReadRoster()
    {
        using var server = CreateServer();
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var anonymous = await client.GetAsync($"/api/lobbies/{RoomA}/roster");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add("X-Player-Id", "alice");
        client.DefaultRequestHeaders.Add("X-User-Id", "alice");
        var spoofed = await client.GetAsync($"/api/lobbies/{RoomA}/roster?playerId=alice");
        Assert.Equal(HttpStatusCode.Unauthorized, spoofed.StatusCode);
    }

    [Fact]
    public async Task SignedInMemberCanReadOnlyExplicitRosterData()
    {
        using var server = CreateServer();
        using var alice = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "alice"));
        var response = await alice.GetAsync($"/api/lobbies/{RoomA}/roster");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(RoomA, root.GetProperty("lobbyId").GetString());
        Assert.Equal(3, root.GetProperty("revision").GetInt64());
        Assert.Equal(2, root.GetProperty("participants").GetArrayLength());
        Assert.Equal("alice",
            root.GetProperty("participants")[0].GetProperty("playerId").GetString());
        Assert.Equal("kira",
            root.GetProperty("participants")[0].GetProperty("heroId").GetString());
        Assert.DoesNotContain("plannedDestination", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateRoute", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("matchState", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtherMemberIdInQueryCannotOverrideSignedIdentity()
    {
        using var server = CreateServer();
        using var bob = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "bob"));
        var response = await bob.GetAsync($"/api/lobbies/{RoomB}/roster?playerId=cara");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownLobbyAndUnauthorizedMembershipHaveSameNotFoundResponse()
    {
        using var server = CreateServer();
        using var bob = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "bob"));
        var absent = await bob.GetAsync($"/api/lobbies/{RoomMissing}/roster");
        var denied = await bob.GetAsync($"/api/lobbies/{RoomB}/roster");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal(absent.StatusCode, denied.StatusCode);
        Assert.Equal(await absent.Content.ReadAsStringAsync(),
                     await denied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SignedInButAmbiguousOrMissingIdentityFailsWithForbidden()
    {
        using var server = CreateServer();
        using var noId = ClientFor(server, new Claim(ClaimTypes.Name, "alice"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noId.GetAsync($"/api/lobbies/{RoomA}/roster")).StatusCode);

        using var duplicateId = ClientFor(server,
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim(ClaimTypes.NameIdentifier, "bob"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await duplicateId.GetAsync($"/api/lobbies/{RoomA}/roster")).StatusCode);
    }

    [Fact]
    public async Task ForgedCookieCannotImpersonateAMember()
    {
        using var server = CreateServer();
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("Cookie", "Shirokuro.Session=alice");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync($"/api/lobbies/{RoomA}/roster")).StatusCode);
    }

    [Fact]
    public async Task BrowserSignalRRequiresTrustedOriginButAllowsViteAndNonBrowserClients()
    {
        using var server = CreateServer();
        using var member = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "alice"));
        const string negotiate = "/hubs/lobbies/negotiate?negotiateVersion=1";

        // A browser's cross-origin Vite development client is explicitly
        // supported. No X-Shirokuro-Request mutation header is required.
        foreach (var allowed in new[] { "http://localhost", "http://localhost:5173",
                                        "http://127.0.0.1:5173" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, negotiate);
            request.Headers.TryAddWithoutValidation("Origin", allowed);
            using var response = await member.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Existing non-browser SignalR clients may omit Origin entirely.
        using (var noOrigin = await member.PostAsync(negotiate, null))
            Assert.Equal(HttpStatusCode.OK, noOrigin.StatusCode);

        foreach (var hostile in new[] { "https://evil.example", "null",
                                        "http://localhost:5173/path",
                                        "http://attacker.localhost:5173",
                                        "http://localhost:5173, http://evil.example" })
        {
            foreach (var (method, path) in new[]
                     {
                         (HttpMethod.Post, negotiate),
                         (HttpMethod.Get, "/hubs/lobbies")
                     })
            {
                using var request = new HttpRequestMessage(method, path);
                request.Headers.TryAddWithoutValidation("Origin", hostile);
                using var denied = await member.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
                Assert.False(denied.Headers.Contains("Set-Cookie"));
            }
        }

        // The browser hub fence does not spill onto ordinary member-scoped
        // HTTP reads or the unauthenticated liveness endpoint.
        using var me = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var health = await member.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task SignalRNegotiateRequiresAuthenticationEvenWithSpoofedHeaders()
    {
        using var server = CreateServer();
        using var anonymous = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        anonymous.DefaultRequestHeaders.Add("X-Player-Id", "alice");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/hubs/lobbies/negotiate?negotiateVersion=1", null)).StatusCode);

        using var alice = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "alice"));
        var negotiate = await alice.PostAsync("/hubs/lobbies/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var json = await negotiate.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicHealthAndBoardPreviewEndpointsRemainUnauthenticated()
    {
        using var server = CreateServer();
        using var anonymous = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/preview/board")).StatusCode);
    }

    [Fact]
    public void HubHasNoCallerControlledPlayerIdOrStateMutations()
    {
        var methods = typeof(LobbyRosterHub).GetMethods(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.DeclaredOnly);
        var method = Assert.Single(methods);
        Assert.Equal("GetRoster", method.Name);
        var parameter = Assert.Single(method.GetParameters());
        Assert.Equal("lobbyId", parameter.Name);
        Assert.Equal(typeof(string), parameter.ParameterType);
        Assert.NotNull(Attribute.GetCustomAttribute(
            typeof(LobbyRosterHub), typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute)));
    }

    [Fact]
    public async Task SignalRMethodReauthorizesMemberAndRejectsSpoofedContextUserIdentifier()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, "alice", "white", 0);
        coordinator.Join(RoomA, "bob", "black", 1);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator));

        // The caller's authenticated principal, not a transport-supplied or
        // caller-controlled identifier, is the authoritative identity.
        hub.Context = new FakeHubContext(Principal("alice"), "spoofed-bob");
        var roster = await hub.GetRoster(RoomA);
        Assert.Equal(RoomA, roster.LobbyId);
        Assert.Equal(2, roster.Participants.Count);

        hub.Context = new FakeHubContext(Principal("outsider"), "alice");
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomA))).Message);

        hub.Context = new FakeHubContext(Principal("alice"));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomMissing))).Message);

        hub.Context = new FakeHubContext(new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomA))).Message);

        hub.Context = new FakeHubContext(Principal("bob"));
        Assert.Equal(RoomA, (await hub.GetRoster(RoomA)).LobbyId);
    }


    [Fact]
    public async Task HubRejectsMalformedIdsWithUniformNotFoundError()
    {
        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomA, ["white", "black"]);
        coordinator.Join(RoomA, "alice", "white", 0);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator))
        {
            Context = new FakeHubContext(Principal("alice"))
        };
        Assert.Equal(RoomA, (await hub.GetRoster(RoomA)).LobbyId);

        foreach (var bad in new[]
                 {
                     "", "room-1", "room-" + new string('A', 32),
                     "room-" + new string('a', 31),
                     "room-" + new string('a', 33),
                     "room-" + new string('a', 31) + "/",
                     " room-" + new string('a', 32),
                     "room-" + new string('a', 31) + "é"
                 })
        {
            var error = await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(bad));
            Assert.Equal("Lobby unavailable.", error.Message);
        }
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(
                () => hub.GetRoster(RoomMissing))).Message);
    }

    [Fact]
    public async Task ExistingSignalRConnectionCannotReadAfterGuestLogoutRevocation()
    {
        var playerId = "guest-" + new string('a', 32);
        var clock = TimeProvider.System;
        var registry = new GuestSessionRegistry(clock);
        var token = registry.Register(playerId, clock.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(playerId, token);

        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomSecret, ["white", "black"]);
        coordinator.Join(RoomSecret, playerId, "white", 0);

        // The same Context.User survives as a cached principal across a
        // long-lived socket; the registry must be consulted per invocation.
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator), registry)
        {
            Context = new FakeHubContext(principal)
        };
        Assert.Equal(RoomSecret, (await hub.GetRoster(RoomSecret)).LobbyId);
        Assert.True(registry.Revoke(principal));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomSecret))).Message);

        // A valid ticket with a session ID no longer registered also fails.
        var newHub = new LobbyRosterHub(new LobbyRosterReader(coordinator), registry)
        {
            Context = new FakeHubContext(principal)
        };
        await Assert.ThrowsAsync<HubException>(() => newHub.GetRoster(RoomSecret));
    }

    [Fact]
    public async Task ExistingHubRejectsSessionRevokedByAnotherRegistry()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var shared = provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
        var clock = TimeProvider.System;
        var onA = new GuestSessionRegistry(clock, shared);
        var onB = new GuestSessionRegistry(clock, shared);
        var id = GuestSessions.NewPlayerId();
        var token = await onA.RegisterAsync(id, clock.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(id, token);

        var coordinator = new InMemoryLobbyCoordinator();
        coordinator.Create(RoomShared, ["white", "black"]);
        coordinator.Join(RoomShared, id, "white", 0);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator), onA)
        {
            Context = new FakeHubContext(principal)
        };

        Assert.Equal(RoomShared, (await hub.GetRoster(RoomShared)).LobbyId);
        Assert.True(await onB.RevokeAsync(principal));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster(RoomShared))).Message);
    }

    [Fact]
    public void SessionCookieSettingsHaveStrictSecurityDefaults()
    {
        using var server = CreateServer();
        // Trigger host initialization before obtaining its configured options.
        using var client = server.CreateClient();
        var cookie = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal("Shirokuro.Session", cookie.Cookie.Name);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Strict, cookie.Cookie.SameSite);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
    }

    private static ClaimsPrincipal Principal(string playerId) =>
        new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, playerId)
        ], CookieAuthenticationDefaults.AuthenticationScheme));

    private sealed class FakeHubContext(
        ClaimsPrincipal user,
        string? spoofedUserIdentifier = null) : HubCallerContext
    {
        public override string ConnectionId => "test-connection";
        public override string? UserIdentifier => spoofedUserIdentifier;
        public override ClaimsPrincipal User => user;
        public override IDictionary<object, object?> Items { get; } =
            new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
