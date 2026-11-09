using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestLobbyHttpTests
{
    private static WebApplicationFactory<Program> Server() => new();

    private static HttpClient Client(WebApplicationFactory<Program> server, string? cookie = null)
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

    private static async Task<(string Id, string Cookie)> SignIn(HttpClient visitor)
    {
        using var request = Write(HttpMethod.Post, "/api/sessions/guest", withBody: false);
        using var response = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        return (data.RootElement.GetProperty("playerId").GetString()!, cookie);
    }

    private static HttpRequestMessage Write(
        HttpMethod method, string url, object? body = null,
        bool withBody = true, string origin = "http://localhost")
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-Shirokuro-Request", "1");
        request.Headers.Add("Origin", origin);
        if (withBody)
            request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<(string Room, long Revision)> Create(HttpClient member)
    {
        using var request = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var response = await member.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (data.RootElement.GetProperty("lobbyId").GetString()!,
            data.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task TwoRealGuestSessionsCanCreateJoinChooseHeroesAndLeave()
    {
        using var server = Server();
        using var ownerVisitor = Client(server);
        using var friendVisitor = Client(server);
        var (ownerId, ownerCookie) = await SignIn(ownerVisitor);
        var (friendId, friendCookie) = await SignIn(friendVisitor);
        Assert.NotEqual(ownerId, friendId);

        using var owner = Client(server, ownerCookie);
        using var friend = Client(server, friendCookie);
        var (roomId, revision) = await Create(owner);
        Assert.Matches("^room-[0-9a-f]{32}$", roomId);
        Assert.Equal(1, revision);

        using (var invisible = await friend.GetAsync("/api/lobbies/" + roomId + "/roster"))
            Assert.Equal(HttpStatusCode.NotFound, invisible.StatusCode);

        using (var join = Write(HttpMethod.Post, "/api/lobbies/" + roomId + "/join",
            new { teamId = "black", expectedRevision = revision }))
        using (var joined = await friend.SendAsync(join))
        {
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
            using var payload = JsonDocument.Parse(await joined.Content.ReadAsStringAsync());
            Assert.Equal(2, payload.RootElement.GetProperty("revision").GetInt64());
        }

        using (var pick = Write(HttpMethod.Patch, "/api/lobbies/" + roomId + "/hero",
            new { heroId = "kira", expectedRevision = 2 }))
        using (var picked = await owner.SendAsync(pick))
            Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

        using (var change = Write(HttpMethod.Patch, "/api/lobbies/" + roomId + "/team",
            new { teamId = "white", expectedRevision = 3 }))
        using (var switched = await friend.SendAsync(change))
            Assert.Equal(HttpStatusCode.OK, switched.StatusCode);

        using (var roster = await owner.GetAsync("/api/lobbies/" + roomId + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            Assert.Equal("no-store", roster.Headers.CacheControl?.ToString());
            var json = await roster.Content.ReadAsStringAsync();
            using var payload = JsonDocument.Parse(json);
            var players = payload.RootElement.GetProperty("participants");
            Assert.Equal(2, players.GetArrayLength());
            Assert.Equal(ownerId, players[0].GetProperty("playerId").GetString());
            Assert.Equal(friendId, players[1].GetProperty("playerId").GetString());
            Assert.Equal("kira", players[0].GetProperty("heroId").GetString());
            Assert.Equal("white", players[1].GetProperty("teamId").GetString());
            Assert.DoesNotContain("plannedDestination", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("position", json, StringComparison.OrdinalIgnoreCase);
        }

        using (var leave = Write(HttpMethod.Delete,
            "/api/lobbies/" + roomId + "/membership", new { expectedRevision = 4 }))
        using (var response = await owner.SendAsync(leave))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var excluded = await owner.GetAsync("/api/lobbies/" + roomId + "/roster"))
            Assert.Equal(HttpStatusCode.NotFound, excluded.StatusCode);
        using (var stillThere = await friend.GetAsync("/api/lobbies/" + roomId + "/roster"))
            Assert.Equal(HttpStatusCode.OK, stillThere.StatusCode);
    }


    [Fact]
    public async Task InvitationAloneAllowsSecureJoinWithoutKnowingCurrentRoomRevision()
    {
        using var server = Server();
        using var creator = Client(server);
        var (_, firstCookie) = await SignIn(creator);
        using var member = Client(server, firstCookie);
        var (room, _) = await Create(member);

        using var secondVisitor = Client(server);
        var (secondId, secondCookie) = await SignIn(secondVisitor);
        using var second = Client(server, secondCookie);
        using var request = Write(HttpMethod.Post, "/api/lobbies/" + room + "/join",
            new { teamId = "black" });
        using var response = await second.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(secondId, json.RootElement.GetProperty("participants")[1]
            .GetProperty("playerId").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task MembershipCannotBeSpoofedByHeadersQueriesOrExtraJsonFields()
    {
        using var server = Server();
        using var visitor = Client(server);
        var (_, ownerCookie) = await SignIn(visitor);
        using var owner = Client(server, ownerCookie);
        var (room, _) = await Create(owner);

        using var strangerVisitor = Client(server);
        var (_, outsiderCookie) = await SignIn(strangerVisitor);
        using var outsider = Client(server, outsiderCookie);

        using (var request = Write(HttpMethod.Patch,
            "/api/lobbies/" + room + "/hero?playerId=owner", new { heroId = "adsiz", expectedRevision = 1 }))
        {
            request.Headers.Add("X-Player-Id", "owner");
            using var response = await outsider.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var request = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white", playerId = "evil" }))
        using (var rejected = await owner.SendAsync(request))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task StaleRevisionsUnknownHeroesOrTeamsDoNotModifyRoster()
    {
        using var server = Server();
        using var visitor = Client(server);
        var (_, cookie) = await SignIn(visitor);
        using var owner = Client(server, cookie);
        var (room, revision) = await Create(owner);

        using (var stale = Write(HttpMethod.Patch, "/api/lobbies/" + room + "/hero",
            new { heroId = "kira", expectedRevision = 0 }))
        using (var conflict = await owner.SendAsync(stale))
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using (var invalidHero = Write(HttpMethod.Patch, "/api/lobbies/" + room + "/hero",
            new { heroId = "not-a-hero", expectedRevision = revision }))
        using (var invalid = await owner.SendAsync(invalidHero))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using (var invalidTeam = Write(HttpMethod.Patch, "/api/lobbies/" + room + "/team",
            new { teamId = "not-a-team", expectedRevision = revision }))
        using (var invalid = await owner.SendAsync(invalidTeam))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var roster = await owner.GetAsync("/api/lobbies/" + room + "/roster");
        using var payload = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
        Assert.Equal(revision, payload.RootElement.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task AnonymousSignedOutOrCrossOriginGuestsCannotMutateRooms()
    {
        using var server = Server();
        using var anon = Client(server);
        using (var req = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var denied = await anon.SendAsync(req))
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var (_, cookie) = await SignIn(anon);
        using var owner = Client(server, cookie);
        using (var req = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" },
            origin: "https://evil.example"))
        using (var denied = await owner.SendAsync(req))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var (room, _) = await Create(owner);
        using (var req = new HttpRequestMessage(HttpMethod.Patch,
            "/api/lobbies/" + room + "/hero"))
        {
            req.Content = JsonContent.Create(new { heroId = "kira", expectedRevision = 1 });
            using var denied = await owner.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var req = Write(HttpMethod.Delete, "/api/sessions/guest", withBody: false))
        using (var response = await owner.SendAsync(req))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // The cookie is cleared in the browser, not forcibly revoked server-side.
        // Request without old cookie must be anonymous; do not assert revocation.
        using var newAnonymous = Client(server);
        using var deniedRoster = await newAnonymous.GetAsync("/api/lobbies/" + room + "/roster");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedRoster.StatusCode);
    }


    [Fact]
    public async Task GuestRoomCreationIsLimitedToEightPerMinutePerIp()
    {
        using var server = Server();
        using var visitor = Client(server);
        var (_, cookie) = await SignIn(visitor);
        using var guest = Client(server, cookie);

        for (var index = 0; index < 8; index++)
        {
            using var request = Write(HttpMethod.Post, "/api/lobbies",
                new { teamIds = new[] { "white", "black" }, teamId = "white" });
            using var response = await guest.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var ninth = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var limited = await guest.SendAsync(ninth);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task OpaqueInviteAndMemberRosterAreNeverPubliclyCacheable()
    {
        using var server = Server();
        using var visitor = Client(server);
        var (_, cookie) = await SignIn(visitor);
        using var owner = Client(server, cookie);

        using var create = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var created = await owner.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        using var payload = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var code = payload.RootElement.GetProperty("lobbyId").GetString();
        using var roster = await owner.GetAsync("/api/lobbies/" + code + "/roster");
        Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
        Assert.Equal("no-store", roster.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task InvalidCreateCannotLeaveOrphanRoomOrExposeExistingRosters()
    {
        using var server = Server();
        using var visitor = Client(server);
        var (_, cookie) = await SignIn(visitor);
        using var owner = Client(server, cookie);
        using (var request = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "unknown" }))
        using (var invalid = await owner.SendAsync(request))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using (var request = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black", "white" }, teamId = "white" }))
        using (var invalid = await owner.SendAsync(request))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var (room, revision) = await Create(owner);
        Assert.Equal(1, revision);
        using var roster = await owner.GetAsync("/api/lobbies/" + room + "/roster");
        Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
    }
}

