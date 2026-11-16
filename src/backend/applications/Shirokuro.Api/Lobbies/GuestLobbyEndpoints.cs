using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Shirokuro.Api.Security;
using Shirokuro.Api.Readiness;
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
        // Every route in this group mutates cookie-backed lobby state.
        // The marker runs Origin/intent validation before cookie auth and
        // before the POST create quota, without URL-based security routing.
        var route = app.MapGroup("/api/lobbies")
            .RequireAuthorization()
            .WithMetadata(
                new WebMutationGuardRequired(),
                new GuestLobbyMutationBodyLimitRequired());

        var createRoom = route.MapPost("", async (CreateRoomRequest request, HttpContext context,
            IHostEnvironment environment, IAsyncGuestLobby lobby, IReadinessDrainSignal drain) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            // DTO binding runs AFTER middleware; a slow request body can
            // cross ApplicationStopping while both earlier fences were green.
            // Do not begin a new room CAS after that boundary.
            if (drain.IsDraining)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await Respond(() => AuthorizedMutationRosterAsync(
                context,
                () => lobby.CreateAsync(actor!, request.TeamIds,
                    request.TeamId, context.RequestAborted),
                created: true), context.RequestAborted);
        });
        createRoom.WithMetadata(
            new GuestMutationRateLimit(GuestMutationKind.RoomCreation));
        if (app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
            createRoom.RequireRateLimiting("guest-room-creation");

        route.MapPost("/{roomId}/join", async (string roomId, JoinRoomRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby,
            IReadinessDrainSignal drain) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            // A late JSON body must not admit a participant after host stop.
            // Already-started Redis CAS calls remain governed by their own
            // authoritative result; do not claim that they can be cancelled.
            if (drain.IsDraining)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return await Respond(() => AuthorizedMutationRosterAsync(
                context,
                () => lobby.JoinAsync(roomId, actor!, request.TeamId,
                    request.ExpectedRevision, context.RequestAborted)),
                context.RequestAborted);
        }).WithMetadata(new CanonicalLobbyIdRequired("roomId"),
            new Shirokuro.Api.Readiness.LobbyJoinDrainRequired());

        route.MapPatch("/{roomId}/hero", async (string roomId, ChooseHeroRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(() => AuthorizedMutationRosterAsync(
                context,
                () => lobby.ChooseHeroAsync(roomId, actor!, request.HeroId,
                    request.ExpectedRevision, context.RequestAborted)),
                context.RequestAborted);
        }).WithMetadata(new CanonicalLobbyIdRequired("roomId"));

        route.MapPatch("/{roomId}/team", async (string roomId, ChangeTeamRequest request,
            HttpContext context, IHostEnvironment environment, IAsyncGuestLobby lobby) =>
        {
            if (!TryGuest(context, environment, out var actor))
                return Results.Forbid();
            return await Respond(() => AuthorizedMutationRosterAsync(
                context,
                () => lobby.ChangeTeamAsync(roomId, actor!, request.TeamId,
                    request.ExpectedRevision, context.RequestAborted)),
                context.RequestAborted);
        }).WithMetadata(new CanonicalLobbyIdRequired("roomId"));

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
            }, context.RequestAborted);
        }).WithMetadata(new CanonicalLobbyIdRequired("roomId"));
    }

    /// <summary>
    /// A roster-returning mutation may have committed its Redis CAS while an
    /// independent logout revoked this guest's cookie. Recheck the
    /// authoritative session AFTER the command completes, BEFORE emitting
    /// the body (or a Location header containing a newly issued room ID).
    /// This does not replay or roll back an already successful mutation.
    /// Only successful commands pay the second guest-authority read.
    /// </summary>
    private static async Task<IResult> AuthorizedMutationRosterAsync(
        HttpContext context, Func<Task<LobbyRosterView>> execute, bool created = false)
    {
        var roster = await execute();
        var status = await context.RequestServices
            .GetRequiredService<GuestSessionRegistry>()
            .CheckAuthorityAsync(context.User, context.RequestAborted);
        if (status != GuestSessionAuthorityStatus.Active)
            return Results.StatusCode(
                status == GuestSessionAuthorityStatus.Inactive
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status503ServiceUnavailable);

        return created
            ? Results.Created("/api/lobbies/" + roster.LobbyId + "/roster", roster)
            : Results.Ok(roster);
    }

    private static bool TryGuest(
        HttpContext context, IHostEnvironment environment, out string? playerId)
    {
        context.Response.Headers.CacheControl = "no-store";
        playerId = AuthenticatedPlayerIdentity.FromPrincipal(context.User);
        return WebMutationGuard.Allows(context, environment) &&
               playerId is not null && GuestSessions.IsGuest(context.User);
    }

    private static async Task<IResult> Respond(
        Func<Task<IResult>> execute, CancellationToken cancellationToken)
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
        catch (LobbySnapshotUnavailableException)
        {
            // An authoritative room record is undecodable: no mutation may
            // be attempted or acknowledged without a verified snapshot.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (LobbyJoinContentionException)
        {
            // The unversioned invitation join exhausted all bounded Redis
            // CAS attempts. It is unavailable, not a user revision conflict.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Never confirm a Redis mutation whose result is unknown.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (StackExchange.Redis.RedisException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}

