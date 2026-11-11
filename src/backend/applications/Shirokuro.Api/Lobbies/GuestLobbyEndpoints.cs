using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Api.Lobbies;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateRoomRequest(string[]? TeamIds, string? TeamId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JoinRoomRequest(string? TeamId, long? ExpectedRevision);
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

        route.MapPost("", async (CreateRoomRequest request, HttpContext context,
            IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(async () =>
            {
                var roster = await lobby.CreateAsync(actor!, request.TeamIds,
                    request.TeamId, context.RequestAborted);
                return Results.Created("/api/lobbies/" + roster.LobbyId + "/roster", roster);
            });
        }).RequireRateLimiting("guest-room-creation");

        route.MapPost("/{roomId}/join", async (string roomId, JoinRoomRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(async () => Results.Ok(await lobby.JoinAsync(
                roomId, actor!, request.TeamId, request.ExpectedRevision, context.RequestAborted)));
        });

        route.MapPatch("/{roomId}/hero", async (string roomId, ChooseHeroRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(async () => Results.Ok(await lobby.ChooseHeroAsync(
                roomId, actor!, request.HeroId, request.ExpectedRevision, context.RequestAborted)));
        });

        route.MapPatch("/{roomId}/team", async (string roomId, ChangeTeamRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(async () => Results.Ok(await lobby.ChangeTeamAsync(
                roomId, actor!, request.TeamId, request.ExpectedRevision, context.RequestAborted)));
        });

        route.MapDelete("/{roomId}/membership", async (string roomId, [FromBody] LeaveRoomRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(async () =>
            {
                await lobby.LeaveAsync(roomId, actor!, request.ExpectedRevision,
                    context.RequestAborted);
                return Results.NoContent();
            });
        });
    }

    private static bool TryGuest(
        HttpContext context, IHostEnvironment environment, out string? playerId)
    {
        context.Response.Headers.CacheControl = "no-store";
        playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User);
        return WebMutationGuard.Allows(context, environment) &&
               playerId is not null && GuestSessions.IsGuest(context.User);
    }

    private static async Task<IResult> Respond(Func<Task<IResult>> execute)
    {
        try
        {
            return await execute();
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

