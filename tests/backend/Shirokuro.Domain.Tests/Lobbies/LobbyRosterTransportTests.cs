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
using Microsoft.Extensions.Options;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class LobbyRosterTransportTests
{
    private static WebApplicationFactory<Program> CreateServer() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(_ =>
                {
                    var coordinator = new InMemoryLobbyCoordinator();
                    coordinator.Create("room-1", ["white", "black"]);
                    coordinator.Join("room-1", "alice", "white", 0);
                    coordinator.Join("room-1", "bob", "black", 1);
                    coordinator.ChooseHero("room-1", "alice", "kira", 2);
                    coordinator.Create("room-2", ["white", "black"]);
                    coordinator.Join("room-2", "cara", "white", 0);
                    return coordinator;
                });
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

    [Fact]
    public async Task AnonymousOrSpoofedHttpIdentityCannotReadRoster()
    {
        using var server = CreateServer();
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var anonymous = await client.GetAsync("/api/lobbies/room-1/roster");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        client.DefaultRequestHeaders.Add("X-Player-Id", "alice");
        client.DefaultRequestHeaders.Add("X-User-Id", "alice");
        var spoofed = await client.GetAsync("/api/lobbies/room-1/roster?playerId=alice");
        Assert.Equal(HttpStatusCode.Unauthorized, spoofed.StatusCode);
    }

    [Fact]
    public async Task SignedInMemberCanReadOnlyExplicitRosterData()
    {
        using var server = CreateServer();
        using var alice = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "alice"));
        var response = await alice.GetAsync("/api/lobbies/room-1/roster");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("room-1", root.GetProperty("lobbyId").GetString());
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
        var response = await bob.GetAsync("/api/lobbies/room-2/roster?playerId=cara");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UnknownLobbyAndUnauthorizedMembershipHaveSameNotFoundResponse()
    {
        using var server = CreateServer();
        using var bob = ClientFor(server, new Claim(ClaimTypes.NameIdentifier, "bob"));
        var absent = await bob.GetAsync("/api/lobbies/missing/roster");
        var denied = await bob.GetAsync("/api/lobbies/room-2/roster");
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
            (await noId.GetAsync("/api/lobbies/room-1/roster")).StatusCode);

        using var duplicateId = ClientFor(server,
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim(ClaimTypes.NameIdentifier, "bob"));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await duplicateId.GetAsync("/api/lobbies/room-1/roster")).StatusCode);
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
            (await client.GetAsync("/api/lobbies/room-1/roster")).StatusCode);
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
        coordinator.Create("room", ["white", "black"]);
        coordinator.Join("room", "alice", "white", 0);
        coordinator.Join("room", "bob", "black", 1);
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator));

        // The caller's authenticated principal, not a transport-supplied or
        // caller-controlled identifier, is the authoritative identity.
        hub.Context = new FakeHubContext(Principal("alice"), "spoofed-bob");
        var roster = await hub.GetRoster("room");
        Assert.Equal("room", roster.LobbyId);
        Assert.Equal(2, roster.Participants.Count);

        hub.Context = new FakeHubContext(Principal("outsider"), "alice");
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster("room"))).Message);

        hub.Context = new FakeHubContext(Principal("alice"));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster("unknown"))).Message);

        hub.Context = new FakeHubContext(new ClaimsPrincipal(new ClaimsIdentity()));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster("room"))).Message);

        hub.Context = new FakeHubContext(Principal("bob"));
        Assert.Equal("room", (await hub.GetRoster("room")).LobbyId);
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
        coordinator.Create("secret", ["white", "black"]);
        coordinator.Join("secret", playerId, "white", 0);

        // The same Context.User survives as a cached principal across a
        // long-lived socket; the registry must be consulted per invocation.
        var hub = new LobbyRosterHub(new LobbyRosterReader(coordinator), registry)
        {
            Context = new FakeHubContext(principal)
        };
        Assert.Equal("secret", (await hub.GetRoster("secret")).LobbyId);
        Assert.True(registry.Revoke(principal));
        Assert.Equal("Lobby unavailable.",
            (await Assert.ThrowsAsync<HubException>(() => hub.GetRoster("secret"))).Message);

        // A valid ticket with a session ID no longer registered also fails.
        var newHub = new LobbyRosterHub(new LobbyRosterReader(coordinator), registry)
        {
            Context = new FakeHubContext(principal)
        };
        await Assert.ThrowsAsync<HubException>(() => newHub.GetRoster("secret"));
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
