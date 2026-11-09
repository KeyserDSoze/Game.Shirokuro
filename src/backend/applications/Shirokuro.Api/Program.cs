using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Application.Preview;

var builder = WebApplication.CreateBuilder(args);
// Ingress forwarding stays disabled unless exact trusted proxy IPs are configured.
TrustedProxyForwarding.Configure(builder.Services, builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<LobbyExpiryWorker>();
builder.Services.AddSingleton<GuestSessionRegistry>();
builder.Services.AddHostedService<GuestSessionExpiryWorker>();
builder.Services.AddSingleton<BoardPreviewService>();
builder.Services.AddSingleton<InMemoryLobbyCoordinator>();
builder.Services.AddSingleton<LobbyRosterReader>();
builder.Services.AddSingleton<GuestLobbyCommands>();

// The cookie may contain a server-generated, non-verified guest ID.
// No trusted account login or client-selected player ID is supported.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Shirokuro.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        // Treat guest-like or legacy guest tickets as invalid unless their
        // signed session token remains present in the server's active registry.
        options.Events.OnValidatePrincipal = context =>
        {
            var principal = context.Principal;
            var playerId = AuthenticatedPlayerIdentity.FromPrincipal(principal);
            var claimsGuestKind = principal?.FindAll(GuestSessions.SessionKindClaim).Any() == true;
            var looksLikeGuestId = playerId?.StartsWith("guest-", StringComparison.Ordinal) == true;
            if (claimsGuestKind || looksLikeGuestId)
            {
                var registry = context.HttpContext.RequestServices
                    .GetRequiredService<GuestSessionRegistry>();
                if (!registry.IsActive(principal))
                    context.RejectPrincipal();
            }
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSignalR();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("guest-room-creation", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("guest-session-issuance", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});



if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
        options.AddPolicy("local-dev", policy =>
            policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials()));
}

var app = builder.Build();
// Must precede CORS, authentication and the per-IP rate limiter.
// Without trusted proxies, ForwardedHeaders.None consumes nothing.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseCors("local-dev");
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", application = "Shirokuro" }));

// Ephemeral guests: identity comes solely from cryptographically random server bytes.
app.MapPost("/api/sessions/guest", async (HttpContext context, IHostEnvironment environment) =>
{
    if (!WebMutationGuard.Allows(context, environment))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.ContentLength is > 0 ||
        context.Request.Headers.ContainsKey("Transfer-Encoding"))
        return Results.BadRequest(new { code = "session_request_must_be_empty" });

    var existingId = AuthenticatedPlayerIdentity.FromPrincipal(context.User);
    if (context.User.Identities.Any(identity => identity.IsAuthenticated))
    {
        if (existingId is null)
            return Results.Forbid();
        if (!GuestSessions.IsGuest(context.User))
            return Results.Conflict(new { code = "existing_session_is_not_guest" });

        // No new cookie and no implicit extension of the existing expiry.
        return Results.Ok(new { playerId = existingId, kind = "guest" });
    }

    var playerId = GuestSessions.NewPlayerId();
    var expires = context.RequestServices.GetRequiredService<TimeProvider>()
        .GetUtcNow().AddHours(8);
    var registry = context.RequestServices.GetRequiredService<GuestSessionRegistry>();
    var sessionId = registry.Register(playerId, expires);
    var principal = GuestSessions.CreatePrincipal(playerId, sessionId);
    try
    {
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = expires,
                AllowRefresh = false
            });
    }
    catch
    {
        registry.Revoke(principal);
        throw;
    }
    return Results.Ok(new { playerId, kind = "guest" });
}).RequireRateLimiting("guest-session-issuance");

app.MapGet("/api/sessions/me", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User);
    return playerId is null
        ? Results.Forbid()
        : Results.Ok(new { playerId, kind = GuestSessions.IsGuest(context.User) ? "guest" : "other" });
}).RequireAuthorization();

app.MapDelete("/api/sessions/guest", async (HttpContext context, IHostEnvironment environment) =>
{
    if (!WebMutationGuard.Allows(context, environment) ||
        !GuestSessions.IsGuest(context.User))
        return Results.Forbid();

    context.Response.Headers.CacheControl = "no-store";
    // Revoke on the server BEFORE the cookie is cleared client-side.
    var registry = context.RequestServices.GetRequiredService<GuestSessionRegistry>();
    if (!registry.Revoke(context.User))
        return Results.Forbid();

    // The guest can no longer authenticate. Remove their stale pre-match
    // memberships, preserving the other players' roster and room TTL.
    var playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User)!;
    context.RequestServices.GetRequiredService<InMemoryLobbyCoordinator>()
        .RemovePlayerFromAllRooms(playerId);

    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();


// Read-only authenticated roster. Never accept a playerId from the request.
// A missing lobby and a non-member both return 404 to avoid room enumeration.
app.MapGet("/api/lobbies/{lobbyId}/roster",
    (string lobbyId, ClaimsPrincipal user, HttpContext context, LobbyRosterReader reader) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var trustedPlayerId = AuthenticatedPlayerIdentity.FromPrincipal(user);
        if (trustedPlayerId is null)
        {
            return Results.Forbid();
        }

        try
        {
            return Results.Ok(reader.ForMember(lobbyId, trustedPlayerId));
        }
        catch (Exception error) when (
            error is KeyNotFoundException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return Results.NotFound();
        }
    }).RequireAuthorization();

// Server-owned guest room mutations, with CSRF and revision controls.
app.MapGuestLobbyEndpoints();

// The connection is authenticated; each Hub method also checks membership.
app.MapHub<LobbyRosterHub>("/hubs/lobbies").RequireAuthorization();

app.MapGet("/api/preview/board", (int? radius, BoardPreviewService board) =>
{
    var requestedRadius = radius ?? 2;
    if (requestedRadius is < 1 or > 6)
    {
        return Results.BadRequest(new { code = "invalid_preview_radius" });
    }

    return Results.Ok(new
    {
        radius = requestedRadius,
        cells = board.Create(requestedRadius).Select(c => new { q = c.Q, r = c.R, s = c.S })
    });
});

app.Run();

public partial class Program
{
}





