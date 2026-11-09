using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestSessionTests
{
    private static WebApplicationFactory<Program> CreateServer() => new();

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

    private static HttpRequestMessage Mutation(
        HttpMethod method, string path, string origin = "http://localhost")
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(
            WebMutationGuard.RequestHeaderName, WebMutationGuard.RequestHeaderValue);
        request.Headers.TryAddWithoutValidation("Origin", origin);
        return request;
    }

    private static async Task<(string PlayerId, string Cookie)> Issue(HttpClient client)
    {
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var playerId = doc.RootElement.GetProperty("playerId").GetString();
        Assert.NotNull(playerId);
        Assert.Equal("guest", doc.RootElement.GetProperty("kind").GetString());

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        return (playerId, setCookie.Split(';')[0]);
    }

    private static string SignedCookie(WebApplicationFactory<Program> server, params Claim[] claims)
    {
        using var _ = server.CreateClient();
        var options = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme));
        var ticket = new AuthenticationTicket(
            principal, CookieAuthenticationDefaults.AuthenticationScheme);
        return options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket);
    }

    [Fact]
    public void GuestIdsAreCryptographicallyGeneratedAndNotCallerControlled()
    {
        var ids = Enumerable.Range(0, 128).Select(_ => GuestSessions.NewPlayerId()).ToArray();
        Assert.Equal(128, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^guest-[0-9a-f]{32}$", id));
    }

    [Fact]
    public async Task GuestCookieAuthenticatesAgainstTheActualSessionMeEndpoint()
    {
        using var server = CreateServer();
        using var anonymous = Client(server);
        var (id, cookie) = await Issue(anonymous);
        Assert.StartsWith("Shirokuro.Session=", cookie);
        using var member = Client(server, cookie);
        using var response = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("playerId").GetString());
        Assert.Equal("guest", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task RepeatingGuestLoginKeepsOriginalIdentityWithoutNewCookie()
    {
        using var server = CreateServer();
        using var first = Client(server);
        var (id, cookie) = await Issue(first);
        using var member = Client(server, cookie);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var response = await member.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(id, doc.RootElement.GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task TwoVisitorsAreDistinctAndSpoofedPlayerIdIsIgnored()
    {
        using var server = CreateServer();
        using var visitor = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest?playerId=alice");
        request.Headers.Add("X-Player-Id", "alice");
        using var response = await visitor.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var firstId = doc.RootElement.GetProperty("playerId").GetString();
        Assert.NotEqual("alice", firstId);

        using var anotherVisitor = Client(server);
        var (secondId, _) = await Issue(anotherVisitor);
        Assert.NotEqual(firstId, secondId);
    }

    [Theory]
    [InlineData("https://evil.example", true, true)]
    [InlineData("http://localhost:5173.evil.example", true, true)]
    [InlineData("null", true, true)]
    [InlineData("http://localhost", false, true)]
    [InlineData("http://localhost", true, false)]
    public async Task CrossSiteOrMissingCsrfMetadataCannotIssueSession(
        string origin, bool withIntent, bool withOrigin)
    {
        using var server = CreateServer();
        using var client = Client(server);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/guest");
        if (withIntent)
            request.Headers.TryAddWithoutValidation(
                WebMutationGuard.RequestHeaderName, WebMutationGuard.RequestHeaderValue);
        if (withOrigin)
            request.Headers.TryAddWithoutValidation("Origin", origin);

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
    }

    [Fact]
    public async Task ForgedCookieCannotAuthenticateOrNegotiateSignalR()
    {
        using var server = CreateServer();
        using var client = Client(server, "Shirokuro.Session=guest-deadbeef");
        using var response = await client.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var negotiate = await client.PostAsync(
            "/hubs/lobbies/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }

    [Fact]
    public async Task AmbiguousAndNonGuestSessionsCannotBeReplaced()
    {
        using var server = CreateServer();
        using var ambiguous = Client(server, SignedCookie(server,
            new Claim(ClaimTypes.NameIdentifier, "alice"),
            new Claim(ClaimTypes.NameIdentifier, "bob")));
        using (var req = Mutation(HttpMethod.Post, "/api/sessions/guest"))
        using (var response = await ambiguous.SendAsync(req))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var nonGuest = Client(server, SignedCookie(server,
            new Claim(ClaimTypes.NameIdentifier, "alice")));
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var result = await nonGuest.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
    }

    [Fact]
    public async Task LogoutClearsGuestCookieAndRequiresValidIntent()
    {
        using var server = CreateServer();
        using var first = Client(server);
        var (_, cookie) = await Issue(first);
        using var guest = Client(server, cookie);

        using var rejected = await guest.DeleteAsync("/api/sessions/guest");
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var spoofed = Mutation(HttpMethod.Delete, "/api/sessions/guest", "https://evil.example");
        using var blocked = await guest.SendAsync(spoofed);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        using var request = Mutation(HttpMethod.Delete, "/api/sessions/guest");
        using var response = await guest.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("expires=", Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LogoutIsUnauthorizedWithoutSignedSession()
    {
        using var server = CreateServer();
        using var client = Client(server);
        using var request = Mutation(HttpMethod.Delete, "/api/sessions/guest");
        using var result = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task BodyCannotSupplyGuestIdentity()
    {
        using var server = CreateServer();
        using var client = Client(server);
        using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
        request.Content = new StringContent("{\"playerId\":\"alice\"}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }


    [Fact]
    public async Task GuestSessionCannotReadALobbyBeforeTrustedServerAddsMembership()
    {
        using var server = CreateServer();
        using var anonymous = Client(server);
        var (guestId, cookie) = await Issue(anonymous);
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        coordinator.Create("invited-room", ["white", "black"]);

        using var guest = Client(server, cookie);
        using (var before = await guest.GetAsync("/api/lobbies/invited-room/roster"))
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        // The server, not the client's supplied playerId, authorizes admission.
        coordinator.Join("invited-room", guestId, "white", 0);
        using var after = await guest.GetAsync(
            "/api/lobbies/invited-room/roster?playerId=another-user");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var body = await after.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(guestId, doc.RootElement.GetProperty("participants")[0]
            .GetProperty("playerId").GetString());
        Assert.DoesNotContain("plannedDestination", body, StringComparison.OrdinalIgnoreCase);

        using var negotiate = await guest.PostAsync(
            "/hubs/lobbies/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }


    [Fact]
    public async Task StolenGuestCookieIsRejectedImmediatelyAfterOriginalLogout()
    {
        using var server = CreateServer();
        using var visitor = Client(server);
        var (id, stolenCookie) = await Issue(visitor);

        using var owner = Client(server, stolenCookie);
        using var attackerWithCopiedTicket = Client(server, stolenCookie);

        using (var before = await attackerWithCopiedTicket.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var request = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await owner.SendAsync(request))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using (var after = await attackerWithCopiedTicket.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        using (var hub = await attackerWithCopiedTicket.PostAsync(
            "/hubs/lobbies/negotiate?negotiateVersion=1", null))
            Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);

        // A new login cannot silently reuse the revoked guest ticket.
        using var freshRequest = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var freshResponse = await attackerWithCopiedTicket.SendAsync(freshRequest);
        Assert.Equal(HttpStatusCode.OK, freshResponse.StatusCode);
        using var data = JsonDocument.Parse(await freshResponse.Content.ReadAsStringAsync());
        Assert.NotEqual(id, data.RootElement.GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task RevokingOneGuestCannotInvalidateAnotherVisitorsSession()
    {
        using var server = CreateServer();
        using var first = Client(server);
        using var second = Client(server);
        var (_, firstCookie) = await Issue(first);
        var (secondId, secondCookie) = await Issue(second);
        using var firstMember = Client(server, firstCookie);
        using var secondMember = Client(server, secondCookie);

        using (var logout = Mutation(HttpMethod.Delete, "/api/sessions/guest"))
        using (var response = await firstMember.SendAsync(logout))
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var stillValid = await secondMember.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
        using var doc = JsonDocument.Parse(await stillValid.Content.ReadAsStringAsync());
        Assert.Equal(secondId, doc.RootElement.GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task UnregisteredOrLegacySignedGuestTicketsFailClosed()
    {
        using var server = CreateServer();
        var legacy = SignedCookie(server,
            new Claim(ClaimTypes.NameIdentifier, "guest-" + new string('a', 32)),
            new Claim(GuestSessions.SessionKindClaim, GuestSessions.GuestKind));
        using var legacyClient = Client(server, legacy);
        using var deniedLegacy = await legacyClient.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLegacy.StatusCode);

        var phantomToken = SignedCookie(server,
            new Claim(ClaimTypes.NameIdentifier, "guest-" + new string('a', 32)),
            new Claim(GuestSessions.SessionKindClaim, GuestSessions.GuestKind),
            new Claim(GuestSessions.SessionIdClaim, new string('b', 32)));
        using var phantomClient = Client(server, phantomToken);
        using var deniedPhantom = await phantomClient.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.Unauthorized, deniedPhantom.StatusCode);
    }

    [Fact]
    public async Task InvalidLogoutCsrfCannotRevokeAnExistingGuestTicket()
    {
        using var server = CreateServer();
        using var visitor = Client(server);
        var (_, cookie) = await Issue(visitor);
        using var member = Client(server, cookie);

        using (var bad = Mutation(HttpMethod.Delete, "/api/sessions/guest", "https://evil.example"))
        using (var result = await member.SendAsync(bad))
            Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);

        using var stillValid = await member.GetAsync("/api/sessions/me");
        Assert.Equal(HttpStatusCode.OK, stillValid.StatusCode);
    }

    [Fact]
    public async Task ThirteenthGuestIssuanceInOneMinuteIsRateLimited()
    {
        using var server = CreateServer();
        using var client = Client(server);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var request = Mutation(HttpMethod.Post, "/api/sessions/guest");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var request13 = Mutation(HttpMethod.Post, "/api/sessions/guest");
        using var limited = await client.SendAsync(request13);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
