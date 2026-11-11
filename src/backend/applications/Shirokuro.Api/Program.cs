using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Application.Preview;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
// Ingress forwarding stays disabled unless exact trusted proxy IPs are configured.
TrustedProxyForwarding.Configure(builder.Services, builder.Configuration);
builder.Services.AddIngressHostPolicy();
PersistentDataProtection.Configure(
    builder.Services, builder.Configuration, builder.Environment);
GuestSessionRedisConfiguration.Configure(
    builder.Services, builder.Configuration, builder.Environment);
// A bounded HSTS policy for HTTPS-only non-Development ingress. Do not
// include arbitrary subdomains or request preload before edge TLS is verified.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(30);
    options.IncludeSubDomains = false;
    options.Preload = false;
});
builder.Services.AddSingleton(TimeProvider.System);
if (builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<LobbyExpiryWorker>();
builder.Services.AddSingleton<GuestSessionRegistry>();
builder.Services.AddHostedService<GuestSessionExpiryWorker>();
builder.Services.AddSingleton<BoardPreviewService>();
builder.Services.AddSingleton<InMemoryLobbyCoordinator>();
builder.Services.AddSingleton<LobbyRosterReader>();
builder.Services.AddSingleton<GuestLobbyCommands>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IAsyncGuestLobby, LocalGuestLobbyRuntime>();
}
else
{
    // All production routes, websocket roster reads and guest cleanup use
    // Redis as a single authority. Never fall back to local room state.
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        ConnectionMultiplexer.Connect(
            ConfigurationOptions.Parse(builder.Configuration[
                GuestSessionRedisConfiguration.ConnectionStringKey]!)));
    builder.Services.AddSingleton(provider => new RedisLobbySnapshotStore(
        provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase(),
        provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IAsyncGuestLobby, RedisGuestLobbyRuntime>();
}

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
        options.Events.OnValidatePrincipal = async context =>
        {
            var principal = context.Principal;
            var playerId = AuthenticatedPlayerIdentity.FromPrincipal(principal);
            var claimsGuestKind = principal?.FindAll(GuestSessions.SessionKindClaim).Any() == true;
            var looksLikeGuestId = playerId?.StartsWith("guest-", StringComparison.Ordinal) == true;
            if (claimsGuestKind || looksLikeGuestId)
            {
                var registry = context.HttpContext.RequestServices
                    .GetRequiredService<GuestSessionRegistry>();
                if (!await registry.IsActiveAsync(
                        principal, context.HttpContext.RequestAborted))
                    context.RejectPrincipal();
            }
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

// Host validation precedes CORS, authentication, SignalR and all endpoints.
// This explicitly rejects unlisted/malformed hosts even for read-only routes.
app.Use(async (context, next) =>
{
    var policy = context.RequestServices
        .GetRequiredService<IOptions<IngressHostPolicyOptions>>().Value.Policy;
    if (!policy.Allows(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }
    await next(context);
});

// Fail closed on plaintext HTTP outside Development. Trust proxy-reported
// scheme only after ForwardedHeadersMiddleware has authenticated the TCP
// peer via Security:KnownProxies (#29). No automatic 30x redirects for APIs,
// authenticated cookies or SignalR negotiation.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    });
    app.UseHsts();
}

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
    var sessionId = await registry.RegisterAsync(
        playerId, expires, context.RequestAborted);
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
        await registry.RevokeAsync(principal, context.RequestAborted);
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
    if (!await registry.RevokeAsync(context.User, context.RequestAborted))
        return Results.Forbid();

    // The guest can no longer authenticate. Remove their stale pre-match
    // memberships, preserving the other players' roster and room TTL.
    var playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User)!;
    await context.RequestServices.GetRequiredService<IAsyncGuestLobby>()
        .RemovePlayerFromAllRoomsAsync(playerId, context.RequestAborted);

    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();


// Read-only authenticated roster. Never accept a playerId from the request.
// A missing lobby and a non-member both return 404 to avoid room enumeration.
app.MapGet("/api/lobbies/{lobbyId}/roster",
    async (string lobbyId, ClaimsPrincipal user, HttpContext context,
        IAsyncGuestLobby lobby) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var trustedPlayerId = AuthenticatedPlayerIdentity.FromPrincipal(user);
        if (trustedPlayerId is null)
        {
            return Results.Forbid();
        }

        try
        {
            return Results.Ok(await lobby.ForMemberAsync(
                lobbyId, trustedPlayerId, context.RequestAborted));
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





