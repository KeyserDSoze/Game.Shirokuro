using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Api.Lobbies;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateRoomRequest(string[]? TeamIds, string? TeamId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JoinRoomRequest(string? TeamId, long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeTeamRequest(string? TeamId, long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChooseHeroRequest(string? HeroId, long ExpectedRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LeaveRoomRequest(long ExpectedRevision);

public static class GuestLobbyEndpoints
{
    public static void MapGuestLobbyEndpoints(this IEndpointRouteBuilder app)
    {
        var route = app.MapGroup("/api/lobbies").RequireAuthorization();

        route.MapPost("", (CreateRoomRequest request, HttpContext context,
            IHostEnvironment environment, GuestLobbyCommands commands) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return Respond(() =>
            {
                var roster = commands.Create(actor!, request.TeamIds, request.TeamId);
                return Results.Created("/api/lobbies/" + roster.LobbyId + "/roster", roster);
            });
        }).RequireRateLimiting("guest-room-creation");

        route.MapPost("/{roomId}/join", (string roomId, JoinRoomRequest request,
            HttpContext context, IHostEnvironment environment, GuestLobbyCommands commands) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return Respond(() => Results.Ok(
                commands.Join(roomId, actor!, request.TeamId, request.ExpectedRevision)));
        });

        route.MapPatch("/{roomId}/hero", (string roomId, ChooseHeroRequest request,
            HttpContext context, IHostEnvironment environment, GuestLobbyCommands commands) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return Respond(() => Results.Ok(
                commands.ChooseHero(roomId, actor!, request.HeroId, request.ExpectedRevision)));
        });

        route.MapPatch("/{roomId}/team", (string roomId, ChangeTeamRequest request,
            HttpContext context, IHostEnvironment environment, GuestLobbyCommands commands) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return Respond(() => Results.Ok(
                commands.ChangeTeam(roomId, actor!, request.TeamId, request.ExpectedRevision)));
        });

        route.MapDelete("/{roomId}/membership", (string roomId, LeaveRoomRequest request,
            HttpContext context, IHostEnvironment environment, GuestLobbyCommands commands) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return Respond(() =>
            {
                commands.Leave(roomId, actor!, request.ExpectedRevision);
                return Results.NoContent();
            });
        });
    }

    private static bool TryGuest(
        HttpContext context, IHostEnvironment environment, out string? playerId)
    {
        playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User);
        return WebMutationGuard.Allows(context, environment) &&
               playerId is not null && GuestSessions.IsGuest(context.User);
    }

    private static IResult Respond(Func<IResult> execute)
    {
        try
        {
            return execute();
        }
        catch (LobbyRevisionConflictException)
        {
            return Results.Conflict(new { code = "revision_conflict" });
        }
        catch (LobbyCapacityException)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        catch (Exception error) when (error is KeyNotFoundException or UnauthorizedAccessException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { code = "invalid_lobby_request" });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { code = "lobby_state_conflict" });
        }
    }
}

