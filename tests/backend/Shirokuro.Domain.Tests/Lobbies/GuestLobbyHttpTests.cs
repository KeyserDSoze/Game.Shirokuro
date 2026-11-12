using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestLobbyHttpTests
{
    private static WebApplicationFactory<Program> Server() => new();

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _timestamp));
        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, (long)duration.TotalMilliseconds);
    }

    private static WebApplicationFactory<Program> ServerWithClock(ManualClock clock) =>
        Server().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<TimeProvider>(clock)));


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
    public async Task ExpiredRoomIsNotVisibleAndInviteCannotReviveIt()
    {
        var clock = new ManualClock();
        using var server = ServerWithClock(clock);
        using var guestVisitor = Client(server);
        var (_, cookie) = await SignIn(guestVisitor);
        using var owner = Client(server, cookie);
        var (room, _) = await Create(owner);

        clock.Advance(TimeSpan.FromMinutes(30));
        using (var expired = await owner.GetAsync("/api/lobbies/" + room + "/roster"))
            Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);

        using var secondVisitor = Client(server);
        var (_, anotherCookie) = await SignIn(secondVisitor);
        using var second = Client(server, anotherCookie);
        using var join = Write(HttpMethod.Post, "/api/lobbies/" + room + "/join",
            new { teamId = "black" });
        using var result = await second.SendAsync(join);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task MemberReadExtendsRoomExpiryButIntruderReadCannot()
    {
        var clock = new ManualClock();
        using var server = ServerWithClock(clock);
        using var guestVisitor = Client(server);
        var (_, cookie) = await SignIn(guestVisitor);
        using var member = Client(server, cookie);
        var (room, _) = await Create(member);

        using var strangerVisitor = Client(server);
        var (_, outsiderCookie) = await SignIn(strangerVisitor);
        using var outsider = Client(server, outsiderCookie);
        clock.Advance(TimeSpan.FromMinutes(20));
        using (var denied = await outsider.GetAsync("/api/lobbies/" + room + "/roster"))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(9));
        using (var refreshed = await member.GetAsync("/api/lobbies/" + room + "/roster"))
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        clock.Advance(TimeSpan.FromMinutes(29));
        using (var alive = await member.GetAsync("/api/lobbies/" + room + "/roster"))
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);

        // A later outsider read must not change the TTL.
        clock.Advance(TimeSpan.FromMinutes(29));
        using (var denied = await outsider.GetAsync("/api/lobbies/" + room + "/roster"))
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var last = await member.GetAsync("/api/lobbies/" + room + "/roster");
        Assert.Equal(HttpStatusCode.NotFound, last.StatusCode);
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

        // The server revokes the old cookie as well as clearing it client-side.
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

        // The per-creator room quota is four; exercise the independent
        // per-IP limiter with distinct authenticated creators instead.
        using var secondVisitor = Client(server);
        var (_, secondCookie) = await SignIn(secondVisitor);
        using var secondGuest = Client(server, secondCookie);

        for (var index = 0; index < 8; index++)
        {
            using var request = Write(HttpMethod.Post, "/api/lobbies",
                new { teamIds = new[] { "white", "black" }, teamId = "white" });
            using var response = await (index < 4 ? guest : secondGuest).SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var thirdVisitor = Client(server);
        var (_, thirdCookie) = await SignIn(thirdVisitor);
        using var thirdGuest = Client(server, thirdCookie);
        using var ninth = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" });
        using var limited = await thirdGuest.SendAsync(ninth);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        // This 429 is generated by ASP.NET rate-limiter middleware before
        // the endpoint can install its own private response cache policy.
        Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
        Assert.False(limited.Headers.Contains("Set-Cookie"));
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
        Assert.InRange(limited.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [Fact]
    public async Task GuestSessionIssuanceLimitIsNeverCachedOrReissuesCookies()
    {
        using var server = Server();
        using var visitor = Client(server);

        // The technical fixed-window budget is per IP and only permits
        // 12 anonymous guest issuances per minute. No cookies are reused.
        for (var index = 0; index < 12; index++)
        {
            using var request = Write(HttpMethod.Post, "/api/sessions/guest",
                withBody: false);
            using var response = await visitor.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains("Set-Cookie"));
        }

        using var deniedRequest = Write(HttpMethod.Post, "/api/sessions/guest",
            withBody: false);
        using var denied = await visitor.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        Assert.NotNull(denied.Headers.RetryAfter?.Delta);
        Assert.InRange(denied.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        Assert.False(denied.Headers.Contains("Set-Cookie"));

        // A technical guest issuance limit never blocks the independent
        // public process liveness endpoint.
        using var health = await visitor.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("no-store", health.Headers.CacheControl?.ToString());
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

    [Fact]
    public async Task RejectedCrossOriginLogoutKeepsSessionAndAllRoomMembershipsIntact()
    {
        using var server = Server();
        using var visitor = Client(server);
        var (_, cookie) = await SignIn(visitor);
        using var guest = Client(server, cookie);
        var (room, revision) = await Create(guest);

        using (var forgedLogout = Write(HttpMethod.Delete, "/api/sessions/guest",
            withBody: false, origin: "https://evil.example"))
        using (var forbidden = await guest.SendAsync(forgedLogout))
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        // An invalid Origin must neither revoke the signed ticket nor
        // remove memberships or increase the room's revision.
        using (var stillSignedIn = await guest.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);
        using (var roster = await guest.GetAsync("/api/lobbies/" + room + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
            using var payload = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            Assert.Equal(revision, payload.RootElement.GetProperty("revision").GetInt64());
            Assert.Single(payload.RootElement.GetProperty("participants").EnumerateArray());
        }

        using (var realLogout = Write(HttpMethod.Delete, "/api/sessions/guest", withBody: false))
        using (var signedOut = await guest.SendAsync(realLogout))
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        using var denied = await guest.GetAsync("/api/lobbies/" + room + "/roster");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task GuestCreateRoomQuotaReturns429WithoutAffectingOtherGuests()
    {
        using var server = Server();
        using var firstVisitor = Client(server);
        var (_, firstCookie) = await SignIn(firstVisitor);
        using var creator = Client(server, firstCookie);

        var rooms = new List<string>();
        for (var i = 0; i < 4; i++)
            rooms.Add((await Create(creator)).Room);

        using (var request = Write(HttpMethod.Post, "/api/lobbies",
            new { teamIds = new[] { "white", "black" }, teamId = "white" }))
        using (var rejected = await creator.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        }
        foreach (var room in rooms)
        {
            using var roster = await creator.GetAsync("/api/lobbies/" + room + "/roster");
            Assert.Equal(HttpStatusCode.OK, roster.StatusCode);
        }

        using var anotherVisitor = Client(server);
        var (_, anotherCookie) = await SignIn(anotherVisitor);
        using var independent = Client(server, anotherCookie);
        var newRoom = await Create(independent);
        Assert.DoesNotContain(newRoom.Room, rooms);
    }

    [Fact]
    public async Task LogoutRevokesCookieAndAtomicallyClearsOnlyGuestMemberships()
    {
        using var server = Server();
        using var firstVisitor = Client(server);
        using var secondVisitor = Client(server);
        var (aliceId, aliceCookie) = await SignIn(firstVisitor);
        var (bobId, bobCookie) = await SignIn(secondVisitor);
        using var alice = Client(server, aliceCookie);
        using var bob = Client(server, bobCookie);

        var (sharedRoom, revision) = await Create(alice);
        var (soloRoom, _) = await Create(alice);
        using (var join = Write(HttpMethod.Post, "/api/lobbies/" + sharedRoom + "/join",
            new { teamId = "black", expectedRevision = revision }))
        using (var joined = await bob.SendAsync(join))
            Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        using (var logout = Write(HttpMethod.Delete, "/api/sessions/guest", withBody: false))
        using (var signedOut = await alice.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        using (var stolen = await alice.GetAsync("/api/lobbies/" + sharedRoom + "/roster"))
            Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);

        using (var remaining = await bob.GetAsync("/api/lobbies/" + sharedRoom + "/roster"))
        {
            Assert.Equal(HttpStatusCode.OK, remaining.StatusCode);
            var json = await remaining.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            Assert.Equal(3, document.RootElement.GetProperty("revision").GetInt64());
            var member = Assert.Single(document.RootElement.GetProperty("participants").EnumerateArray());
            Assert.Equal(bobId, member.GetProperty("playerId").GetString());
            Assert.DoesNotContain(aliceId, json, StringComparison.Ordinal);
        }

        // An old invite to a room without remaining members is gone.
        using (var joinSolo = Write(HttpMethod.Post, "/api/lobbies/" + soloRoom + "/join",
            new { teamId = "black" }))
        using (var result = await bob.SendAsync(joinSolo))
            Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);

        // The other guest is still authorized to use the surviving room.
        using (var pick = Write(HttpMethod.Patch, "/api/lobbies/" + sharedRoom + "/hero",
            new { heroId = "kira", expectedRevision = 3 }))
        using (var response = await bob.SendAsync(pick))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
