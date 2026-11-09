using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Application.Preview;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<BoardPreviewService>();
builder.Services.AddSingleton<InMemoryLobbyCoordinator>();
builder.Services.AddSingleton<LobbyRosterReader>();

// Deliberately fail closed: no login/session issuance endpoint exists yet.
// A future identity provider may issue signed cookies, never user-supplied IDs.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Shirokuro.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
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


if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
        options.AddPolicy("local-dev", policy =>
            policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod()));
}

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseCors("local-dev");
}

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", application = "Shirokuro" }));

// Read-only authenticated roster. Never accept a playerId from the request.
// A missing lobby and a non-member both return 404 to avoid room enumeration.
app.MapGet("/api/lobbies/{lobbyId}/roster",
    (string lobbyId, ClaimsPrincipal user, LobbyRosterReader reader) =>
    {
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

