using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Exercises the real ASP.NET Core SignalR JSON/WebSocket transport on a
/// TestServer, not a fake HubCallerContext. This intentionally makes no
/// claims about an external TCP load balancer or live Kubernetes pods.
/// </summary>
public sealed class SignalRWebSocketDrainTests
{
    private const string GuestId = "guest-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Room = "room-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const char FrameSeparator = '\u001e';

    private static WebApplicationFactory<Program> Server() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment(Environments.Development));

    private static (string Cookie, ClaimsPrincipal Principal) TicketFor(
        WebApplicationFactory<Program> server, string playerId = GuestId)
    {
        var sessions = server.Services.GetRequiredService<GuestSessionRegistry>();
        var token = sessions.Register(playerId,
            TimeProvider.System.GetUtcNow().AddHours(1));
        var principal = GuestSessions.CreatePrincipal(playerId, token);
        var options = server.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(principal,
            CookieAuthenticationDefaults.AuthenticationScheme);
        return (options.Cookie.Name + "=" + options.TicketDataFormat.Protect(ticket),
            principal);
    }

    private static string CookieFor(WebApplicationFactory<Program> server) =>
        TicketFor(server).Cookie;

    private static WebSocketClient SocketClient(
        WebApplicationFactory<Program> server, string cookie)
    {
        var client = server.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            // Nonbrowser WebSocket clients need no Origin header. Authentication
            // still requires a real server-signed cookie + live guest registry.
            request.Headers["Cookie"] = cookie;
        };
        return client;
    }

    private static async Task SendJsonAsync(
        WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var data = Encoding.UTF8.GetBytes(json + FrameSeparator);
        await socket.SendAsync(new ArraySegment<byte>(data),
            WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonElement> ReadSignalRMessageAsync(
        WebSocket socket, Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var text = new StringBuilder();
        while (true)
        {
            var receive = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer), cancellationToken);
            Assert.Equal(WebSocketMessageType.Text, receive.MessageType);
            text.Append(Encoding.UTF8.GetString(buffer, 0, receive.Count));

            var accumulated = text.ToString();
            var separator = accumulated.IndexOf(FrameSeparator);
            while (separator >= 0)
            {
                var frame = accumulated[..separator];
                accumulated = accumulated[(separator + 1)..];
                if (frame.Length != 0)
                {
                    using var parsed = JsonDocument.Parse(frame);
                    if (predicate(parsed.RootElement))
                        return parsed.RootElement.Clone();
                }
                separator = accumulated.IndexOf(FrameSeparator);
            }

            text.Clear();
            text.Append(accumulated);
            Assert.True(text.Length < 65536, "SignalR response frame is oversized.");
        }
    }

    private static async Task<WebSocket> ConnectAndHandshakeAsync(
        WebApplicationFactory<Program> server, string cookie,
        CancellationToken cancellationToken)
    {
        var socket = await SocketClient(server, cookie).ConnectAsync(
            new Uri("ws://localhost/hubs/lobbies"), cancellationToken);
        try
        {
            await SendJsonAsync(socket, "{\"protocol\":\"json\",\"version\":1}",
                cancellationToken);
            var handshake = await ReadSignalRMessageAsync(socket,
                message => message.ValueKind == JsonValueKind.Object &&
                           !message.TryGetProperty("type", out _),
                cancellationToken);
            Assert.Equal("{}", handshake.GetRawText());
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<JsonElement> InvokeRosterAsync(
        WebSocket socket, string roomId, string invocationId,
        CancellationToken cancellationToken)
    {
        await SendJsonAsync(socket, JsonSerializer.Serialize(new
        {
            type = 1,
            invocationId,
            target = "GetRoster",
            arguments = new[] { roomId }
        }), cancellationToken);
        return await ReadSignalRMessageAsync(socket,
            message => message.TryGetProperty("type", out var type) &&
                       type.GetInt32() == 3 &&
                       message.TryGetProperty("invocationId", out var id) &&
                       id.GetString() == invocationId,
            cancellationToken);
    }

    [Fact]
    public async Task AuthorizedSignalRWebSocketIsClosedAtPreStopWhileHostStaysAlive()
    {
        using var server = Server();
        using var http = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false
        });
        var cookie = CookieFor(server);
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        coordinator.Create(Room, ["white", "black"]);
        coordinator.Join(Room, GuestId, "white", 0);

        var socketClient = SocketClient(server, cookie);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await socketClient.ConnectAsync(
            new Uri("ws://localhost/hubs/lobbies"), timeout.Token);

        // Perform the actual SignalR JSON handshake, then invoke the
        // member-authorized Hub method over the WebSocket to prove that
        // OnConnectedAsync / TryTrack and guest session validation ran.
        await SendJsonAsync(socket, "{\"protocol\":\"json\",\"version\":1}",
            timeout.Token);
        var handshake = await ReadSignalRMessageAsync(socket,
            message => message.ValueKind == JsonValueKind.Object &&
                       !message.TryGetProperty("type", out _),
            timeout.Token);
        Assert.Equal("{}", handshake.GetRawText());

        await SendJsonAsync(socket, JsonSerializer.Serialize(new
        {
            type = 1,
            invocationId = "r1",
            target = "GetRoster",
            arguments = new[] { Room }
        }), timeout.Token);
        var completion = await ReadSignalRMessageAsync(socket,
            message => message.TryGetProperty("type", out var type) &&
                       type.GetInt32() == 3 &&
                       message.TryGetProperty("invocationId", out var id) &&
                       id.GetString() == "r1",
            timeout.Token);
        Assert.False(completion.TryGetProperty("error", out _));
        Assert.Equal(Room, completion.GetProperty("result")
            .GetProperty("lobbyId").GetString());
        Assert.Single(completion.GetProperty("result")
            .GetProperty("participants").EnumerateArray());

        var drain = Assert.IsType<HostLifetimeReadinessDrainSignal>(
            server.Services.GetRequiredService<IReadinessDrainSignal>());
        var lifetime = server.Services.GetRequiredService<IHostApplicationLifetime>();
        Assert.False(drain.IsDraining);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        drain.BeginPreStopDrain();
        Assert.True(drain.IsDraining);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        // Real SignalR HubCallerContext.Abort should cause the *transport*
        // to close, not merely increment a fake AbortCount.
        var closed = false;
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var receive = await socket.ReceiveAsync(
                    new ArraySegment<byte>(new byte[8192]), timeout.Token);
                if (receive.MessageType == WebSocketMessageType.Close)
                {
                    closed = true;
                    break;
                }
            }
        }
        catch (WebSocketException)
        {
            // Abort is allowed to terminate the transport without a
            // graceful WebSocket close frame.
            closed = true;
        }
        catch (System.IO.IOException)
        {
            closed = true;
        }
        Assert.True(closed, "Active SignalR WebSocket was not closed by preStop.");

        drain.BeginPreStopDrain(); // No duplicate lifecycle transition.
        using (var ready = await http.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal("no-store", ready.Headers.CacheControl?.ToString());
        }
        using (var health = await http.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public async Task AuthenticatedNonMemberGetsSameOpaqueErrorAsUnknownRoomOnRealWebSocket()
    {
        using var server = Server();
        const string foreignGuest = "guest-ffffffffffffffffffffffffffffffff";
        const string foreignRoom = "room-ffffffffffffffffffffffffffffffff";
        const string missingRoom = "room-00000000000000000000000000000000";
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        coordinator.Create(Room, ["white", "black"]);
        coordinator.Join(Room, GuestId, "white", 0);
        coordinator.Create(foreignRoom, ["white", "black"]);
        coordinator.Join(foreignRoom, foreignGuest, "black", 0);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await ConnectAndHandshakeAsync(server, CookieFor(server),
            timeout.Token);

        var foreign = await InvokeRosterAsync(socket, foreignRoom, "foreign",
            timeout.Token);
        var missing = await InvokeRosterAsync(socket, missingRoom, "missing",
            timeout.Token);
        Assert.True(foreign.TryGetProperty("error", out var deniedError));
        Assert.True(missing.TryGetProperty("error", out var missingError));
        Assert.Equal(missingError.GetString(), deniedError.GetString());
        Assert.False(foreign.TryGetProperty("result", out _));
        Assert.False(missing.TryGetProperty("result", out _));
        Assert.DoesNotContain(foreignRoom, foreign.GetRawText());
        Assert.DoesNotContain(foreignGuest, foreign.GetRawText());
        Assert.DoesNotContain(missingRoom, missing.GetRawText());

        // A denial does not poison the authenticated connection: only its
        // member-authorized roster can still be returned on this socket.
        var owned = await InvokeRosterAsync(socket, Room, "owned", timeout.Token);
        Assert.False(owned.TryGetProperty("error", out _));
        Assert.Equal(Room, owned.GetProperty("result")
            .GetProperty("lobbyId").GetString());
        var member = Assert.Single(owned.GetProperty("result")
            .GetProperty("participants").EnumerateArray());
        Assert.Equal(GuestId, member.GetProperty("playerId").GetString());
        Assert.DoesNotContain(foreignGuest, owned.GetRawText());
    }

    [Fact]
    public async Task GuestRevocationBlocksNextInvocationOnAlreadyAuthenticatedWebSocket()
    {
        using var server = Server();
        var coordinator = server.Services.GetRequiredService<InMemoryLobbyCoordinator>();
        coordinator.Create(Room, ["white", "black"]);
        coordinator.Join(Room, GuestId, "white", 0);
        var ticket = TicketFor(server);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await ConnectAndHandshakeAsync(server, ticket.Cookie,
            timeout.Token);

        var before = await InvokeRosterAsync(socket, Room, "before-revoke",
            timeout.Token);
        Assert.False(before.TryGetProperty("error", out _));
        Assert.Equal(Room, before.GetProperty("result")
            .GetProperty("lobbyId").GetString());

        // The cookie was valid when the transport connected. A subsequent
        // Hub invocation must recheck the server-side guest registry.
        var sessions = server.Services.GetRequiredService<GuestSessionRegistry>();
        Assert.True(sessions.Revoke(ticket.Principal));
        var after = await InvokeRosterAsync(socket, Room, "after-revoke",
            timeout.Token);
        Assert.True(after.TryGetProperty("error", out var error));
        Assert.False(string.IsNullOrWhiteSpace(error.GetString()));
        Assert.False(after.TryGetProperty("result", out _));
        Assert.DoesNotContain(Room, after.GetRawText());
        Assert.DoesNotContain(GuestId, after.GetRawText());

        // The same revoked ticket is also invalid on the HTTP auth boundary.
        using var http = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sessions/me");
        request.Headers.TryAddWithoutValidation("Cookie", ticket.Cookie);
        using var rejected = await http.SendAsync(request, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidGuestCookieCannotNegotiateSignalR(
        bool invalidCookie)
    {
        using var server = Server();
        using var http = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/hubs/lobbies/negotiate?negotiateVersion=1");
        if (invalidCookie)
        {
            var cookieName = server.Services
                .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme).Cookie.Name;
            request.Headers.TryAddWithoutValidation(
                "Cookie", cookieName + "=invalid-signed-ticket");
        }

        using var rejected = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        Assert.False(rejected.Headers.Contains("Location"));
    }

    [Fact]
    public async Task PreStopRejectsNewSignalRNegotiateWithoutTouchingExistingGuest()
    {
        using var server = Server();
        using var http = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            HandleCookies = false
        });
        var cookie = CookieFor(server);
        using var pre = new HttpRequestMessage(HttpMethod.Post,
            "/hubs/lobbies/negotiate?negotiateVersion=1");
        pre.Headers.TryAddWithoutValidation("Cookie", cookie);
        using (var established = await http.SendAsync(pre))
            Assert.Equal(HttpStatusCode.OK, established.StatusCode);

        var signal = Assert.IsType<HostLifetimeReadinessDrainSignal>(
            server.Services.GetRequiredService<IReadinessDrainSignal>());
        signal.BeginPreStopDrain();

        using var late = new HttpRequestMessage(HttpMethod.Post,
            "/hubs/lobbies/negotiate?negotiateVersion=1");
        late.Headers.TryAddWithoutValidation("Cookie", cookie);
        using var rejected = await http.SendAsync(late);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        Assert.False(rejected.Headers.Contains("Location"));

        using var health = await http.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }
}
