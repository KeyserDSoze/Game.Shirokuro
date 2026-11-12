using System.Security.Claims;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Readiness;
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
// Metrics export is OFF unless the operator explicitly enables a trusted
// HTTPS OTLP collector. Never add an unauthenticated HTTP /metrics endpoint.
MultiplayerMetricsExport.Configure(
    builder.Services, builder.Configuration, builder.Environment);
// Production-only opt-in read-only INFO replication sampler. Neither a
// Redis collector outage nor INFO permissions may gate lobby/guest routes.
RedisReplicationMonitoring.Configure(
    builder.Services, builder.Configuration, builder.Environment);
// An operator-managed cutoff lives OUTSIDE Redis and therefore survives
// rollback of an older RDB. Validate it at startup rather than accepting a
// misformatted setting or treating restored Redis tokens as authority.
var guestRecoveryCutoffUtc = GuestSessionRecoveryCutoff.Read(
    builder.Configuration, builder.Environment);

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
    builder.Services.AddSingleton<IDeploymentReadiness, DevelopmentReadiness>();
}
else
{
    // All production routes, websocket roster reads and guest cleanup use
    // Redis as a single authority. Never fall back to local room state.
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
        RedisConnectionMetrics.Observe(
            ConnectionMultiplexer.Connect(
                GuestSessionRedisConfiguration.ReadTlsOptions(
                    builder.Configuration)),
            RedisConnector.Lobby));
    builder.Services.AddSingleton(provider => new RedisLobbySnapshotStore(
        provider.GetRequiredService<IConnectionMultiplexer>().GetDatabase(),
        provider.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IAsyncGuestLobby, RedisGuestLobbyRuntime>();
    builder.Services.AddSingleton<IGuestRevocationFence>(
        provider => provider.GetRequiredService<RedisLobbySnapshotStore>());
    builder.Services.AddSingleton<IReadinessKeyVerifier, ReadinessKeyVerifier>();
    builder.Services.AddSingleton<IDeploymentReadiness, RedisDeploymentReadiness>();
    builder.Services.AddSingleton<IGuestMutationLimiter>(sp =>
        new RedisGuestMutationLimiter(
            sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase()));
    builder.Services.AddSingleton<IGuestExpiryIndex>(sp =>
        new RedisGuestExpiryIndex(
            sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase(),
            sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            recoveryCutoffUtc: guestRecoveryCutoffUtc));
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
            // Public health/readiness do not read the guest principal. Let
            // the normal authentication middleware run for every route, but
            // avoid Redis ticket validation here: a browser may attach an
            // unrelated guest cookie while polling liveness/readiness.
            // This marker is attached by the server to exactly two endpoints;
            // NEVER apply it to a route that reads HttpContext.User.
            if (context.HttpContext.GetEndpoint()?.Metadata
                    .GetMetadata<PublicProbeEndpoint>() is not null)
                return;

            var principal = context.Principal;
            var playerId = AuthenticatedPlayerIdentity.FromPrincipal(principal);
            var claimsGuestKind = principal?.FindAll(GuestSessions.SessionKindClaim).Any() == true;
            var looksLikeGuestId = playerId?.StartsWith("guest-", StringComparison.Ordinal) == true;
            if (claimsGuestKind || looksLikeGuestId)
            {
                // IssuedUtc belongs to the signed Data Protection ticket,
                // never to a client header or restored Redis. An older
                // ticket cannot authenticate after an operator rotates this
                // out-of-band cutoff during a stale snapshot recovery.
                if (!GuestSessionRecoveryCutoff.Accepts(
                        context.Properties.IssuedUtc, guestRecoveryCutoffUtc))
                {
                    context.RejectPrincipal();
                    return;
                }

                var registry = context.HttpContext.RequestServices
                    .GetRequiredService<GuestSessionRegistry>();
                var authority = await registry.CheckAuthorityAsync(
                    principal, context.HttpContext.RequestAborted);
                if (authority != GuestSessionAuthorityStatus.Active)
                {
                    // The ticket was not authenticated in either case.
                    // Mark only this server-owned request, never a cookie or
                    // a client-supplied header, for an accurate 503 challenge.
                    if (authority == GuestSessionAuthorityStatus.Unavailable)
                        context.HttpContext.Items[
                            GuestSessionRegistry.UnavailableRequestMarker] = true;
                    context.RejectPrincipal();
                }
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            // An unavailable Redis authority is neither a valid login nor a
            // proven revoked ticket. 503 preserves the browser's signed
            // cookie while denying every protected route and negotiation.
            if (context.HttpContext.Items.ContainsKey(
                    GuestSessionRegistry.UnavailableRequestMarker))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
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
    options.OnRejected = (rejection, _) =>
    {
        // Only two known technical policies carry metadata. Never tag
        // untrusted URL paths, remote IPs, principals or request headers.
        var rule = rejection.HttpContext.GetEndpoint()?.Metadata
            .GetMetadata<GuestMutationRateLimit>();
        if (rule is not null)
            MultiplayerMetrics.RecordMutation(rule.Kind, MutationOutcome.Limited);
        rejection.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return ValueTask.CompletedTask;
    };
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

// Endpoint routing selects trusted metadata before these middleware calls.
// Invalid Origin/intent must be rejected BEFORE cookie authentication reads
// the independent Redis guest session authority and BEFORE any shared or
// Development per-IP mutation budget is spent. Every mutation carries the
// marker; read-only API, health/readiness and SignalR remain unaffected.
// Handler-level checks are intentionally retained as defense in depth.
app.Use(async (context, next) =>
{
    if (context.GetEndpoint()?.Metadata.GetMetadata<WebMutationGuardRequired>()
            is not null &&
        !WebMutationGuard.Allows(context, app.Environment))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});

// Keep authentication on the root pipeline so ASP.NET Core does NOT insert
// an additional automatic authentication middleware before the CSRF guard.
// For the public probes, OnValidatePrincipal skips only the Redis lookup,
// not cookie middleware or protected-route authorization.
app.UseAuthentication();
app.UseAuthorization();

// The two explicit POST endpoints alone carry admission-budget metadata.
// Authorization still precedes the per-IP Redis limiter for authenticated
// room creation. Failed Origin checks cannot spend Redis budget or auth reads.
if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        var rule = context.GetEndpoint()?.Metadata
            .GetMetadata<GuestMutationRateLimit>();
        if (rule is not null)
        {
            context.Response.Headers.CacheControl = "no-store";
            GuestMutationDecision decision;
            try
            {
                decision = await context.RequestServices
                    .GetRequiredService<IGuestMutationLimiter>()
                    .AcquireAsync(rule, context.Connection.RemoteIpAddress,
                        context.RequestAborted);
            }
            catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
            {
                // Fail closed on Redis/connection/protocol outages, without
                // printing remote IP, Redis credentials or secret identities.
                MultiplayerMetrics.RecordMutation(
                    rule.Kind, MutationOutcome.Unavailable);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (!decision.Allowed)
            {
                MultiplayerMetrics.RecordMutation(rule.Kind, MutationOutcome.Limited);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter =
                    Math.Max(1, (long)Math.Ceiling(
                        decision.RetryAfterMilliseconds / 1000d)).ToString(
                            System.Globalization.CultureInfo.InvariantCulture);
                return;
            }
            MultiplayerMetrics.RecordMutation(rule.Kind, MutationOutcome.Allowed);
        }
        await next(context);
    });
}
app.UseRateLimiter();
if (app.Environment.IsDevelopment())
{
    // Runs only after the local rate limiter has granted a permit.
    // Rejections are counted by OnRejected above, never twice.
    app.Use(async (context, next) =>
    {
        var rule = context.GetEndpoint()?.Metadata
            .GetMetadata<GuestMutationRateLimit>();
        if (rule is not null)
            MultiplayerMetrics.RecordMutation(rule.Kind, MutationOutcome.Allowed);
        await next(context);
    });
}

// Liveness is intentionally independent of Redis, the key ring and guests.
// Never allow a browser/proxy/monitor to reuse an old positive probe after
// a process has gone away. Readiness uses the same uncached HTTP contract.
app.MapGet("/api/health", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { status = "ok", application = "Shirokuro" });
}).WithMetadata(new PublicProbeEndpoint());

// Liveness above is intentionally independent of Redis; the readiness route
// is for load balancers and checks BOTH Redis connections plus Data Protection.
// It returns a minimal, uncached response without exposing failed dependencies.
app.MapGet("/api/ready", async (HttpContext context, IDeploymentReadiness readiness) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var ready = await readiness.IsReadyAsync(context.RequestAborted);
    return Results.Json(new { status = ready ? "ready" : "unavailable" },
        statusCode: ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
}).WithMetadata(new PublicProbeEndpoint());

// Ephemeral guests: identity comes solely from cryptographically random server bytes.
var guestSessionIssuance = app.MapPost("/api/sessions/guest",
    async (HttpContext context, IHostEnvironment environment) =>
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
    string sessionId;
    try
    {
        sessionId = await registry.RegisterAsync(
            playerId, expires, context.RequestAborted);
    }
    catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
    {
        // Shared authority unavailable: never issue a guest ticket.
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    var principal = GuestSessions.CreatePrincipal(playerId, sessionId);
    try
    {
        // Persist encrypted maintenance work BEFORE returning any guest cookie.
        // If Redis rejects this write, revoke the newly registered ticket.
        if (!environment.IsDevelopment())
            await context.RequestServices.GetRequiredService<IGuestExpiryIndex>()
                .ScheduleAsync(playerId, expires, context.RequestAborted,
                    trustedSessionToken: sessionId);
        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = false,
                ExpiresUtc = expires,
                AllowRefresh = false
            });
    }
    catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
    {
        try
        {
            await registry.RevokeAsync(principal, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Even if rollback cannot contact Redis, no ticket is returned.
        }
        context.Response.Headers.Remove("Set-Cookie");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    return Results.Ok(new { playerId, kind = "guest" });
});
// Metadata has only trusted enum values for both deployment modes.
guestSessionIssuance.WithMetadata(
    new WebMutationGuardRequired(),
    new GuestMutationRateLimit(GuestMutationKind.SessionIssuance));
if (app.Environment.IsDevelopment())
    guestSessionIssuance.RequireRateLimiting("guest-session-issuance");

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
    bool revoked;
    try
    {
        revoked = await registry.RevokeAsync(context.User, context.RequestAborted);
    }
    catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
    {
        // Unconfirmed Redis revocation is never a success or a raw 500.
        // Don't sign out and don't claim the ticket was removed across nodes.
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    if (!revoked)
        return Results.Forbid();

    // The shared ticket is NOW invalid across nodes. Install the mutation
    // fence after revocation but before cleanup: an already-authenticated
    // in-flight request cannot commit after the Redis fence is installed.
    // Failures cannot restore the revoked cookie.
    var playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User)!;
    var lobby = context.RequestServices.GetRequiredService<IAsyncGuestLobby>();
    var fenceInstalled = environment.IsDevelopment();
    if (!environment.IsDevelopment())
    {
        try
        {
            await context.RequestServices.GetRequiredService<IGuestRevocationFence>()
                .FenceGuestAsync(playerId, context.RequestAborted);
            fenceInstalled = true;
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // Continue with immediate durable cleanup and direct removal.
            // A 503 below must disclose no Redis or identity details.
        }
    }
    IGuestExpiryIndex? expiryIndex = null;
    string? queuedMember = null;
    if (!environment.IsDevelopment())
    {
        expiryIndex = context.RequestServices.GetRequiredService<IGuestExpiryIndex>();
        try
        {
            queuedMember = await expiryIndex.ScheduleRevokedAsync(
                playerId, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // The natural 8h expiry work item remains queued. Still attempt
            // synchronous cleanup below; never claim immediate recovery if
            // both operations fail.
        }
    }

    var cleanupCompleted = false;
    try
    {
        await lobby.RemovePlayerFromAllRoomsAsync(playerId, context.RequestAborted);
        cleanupCompleted = true;
    }
    catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
    {
        // The already-due encrypted queue item is retried from any node on
        // the next maintenance pass. Do not expose Redis or identity details.
    }

    // After revocation, even an unsuccessful cleanup cannot re-enable the
    // ticket. Clear the browser cookie rather than leaving an invalid cookie
    // that cannot be used to retry logout.
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

    if (!cleanupCompleted || !fenceInstalled)
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

    // Best-effort ack prevents a needless later scan when direct cleanup
    // succeeded. If ack fails, the worker's replay remains idempotent.
    if (queuedMember is not null)
    {
        try
        {
            await expiryIndex!.AcknowledgeAsync(queuedMember, context.RequestAborted);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            // The queue safely retains a retryable, already-due item.
        }
    }

    return Results.NoContent();
}).RequireAuthorization().WithMetadata(new WebMutationGuardRequired());


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
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (RedisException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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





