using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shirokuro.Api.Security;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Lobbies;
using Shirokuro.Application.Lobbies;

namespace Shirokuro.Api.Hubs;

/// <summary>
/// Authenticated, read-only SignalR transport. There are deliberately no
/// subscribe/group/broadcast methods: membership can change, so future push
/// notifications must reauthorize every receiver before sending.
/// </summary>
[Authorize]
public sealed class LobbyRosterHub(LobbyRosterReader reader,
    GuestSessionRegistry? sessions = null, IAsyncGuestLobby? lobby = null,
    IReadinessDrainSignal? drain = null,
    LobbyConnectionDrainRegistry? connections = null) : Hub
{
    // SignalR invokes these lifecycle callbacks, not as callable Hub methods.
    public override async Task OnConnectedAsync()
    {
        if (drain?.IsDraining == true || connections?.IsDraining == true)
        {
            // The admission fence can reject this socket before it is
            // passed to the registry. No registry abort has occurred yet.
            Context.Abort();
            return;
        }
        if (connections is not null && !connections.TryTrack(Context))
        {
            // TryTrack always attempts Abort() when it rejects a connection.
            // A late preStop racing with the post-insert check must not
            // trigger a second Abort from the Hub lifecycle callback.
            return;
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        connections?.Untrack(Context);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<LobbyRosterView> GetRoster(string lobbyId)
    {
        // Already-open sockets must not outlive a node drain with access to
        // a member-only roster. Fail with the same opaque error as a missing
        // room, before asking Redis to revalidate any guest session.
        if (drain?.IsDraining == true || connections?.IsDraining == true)
            throw new HubException("Lobby unavailable.");
        // A cached SignalR principal may outlive revocation. Malformed room
        // input, however, cannot be a real server-issued lobby: reject it
        // before any guest Redis authority lookup or room storage access.
        if (!CanonicalLobbyId.IsValid(lobbyId))
            throw new HubException("Lobby unavailable.");

        var trustedPlayerId = AuthenticatedPlayerIdentity.FromPrincipal(Context.User);
        if (trustedPlayerId is null)
        {
            throw new HubException("Lobby unavailable.");
        }

        // SignalR connections may outlive cookie logout. Never trust only the
        // principal cached when the socket connected: check active guest
        // registration again on every Hub method invocation.
        var isGuestLike = trustedPlayerId.StartsWith("guest-", StringComparison.Ordinal) ||
            Context.User?.FindAll(GuestSessions.SessionKindClaim).Any() == true;
        if (isGuestLike && (sessions is null ||
            !await sessions.IsActiveAsync(Context.User, Context.ConnectionAborted)))
        {
            throw new HubException("Lobby unavailable.");
        }

        try
        {
            var roster = lobby is null
                ? reader.ForMember(lobbyId, trustedPlayerId)
                : await lobby.ForMemberAsync(
                    lobbyId, trustedPlayerId, Context.ConnectionAborted);
            // The first guest-authority check happened BEFORE the awaited
            // roster read. A concurrent logout (potentially on another node)
            // may have revoked the signed session while Redis was resolving
            // that read. Recheck the authoritative registry before publishing
            // even a previously member-authorized snapshot. An unavailable
            // registry must also fail closed. This is a second read, not an
            // atomic transaction with concurrent revocation.
            if (isGuestLike)
            {
                var authority = await sessions!.CheckAuthorityAsync(
                    Context.User, Context.ConnectionAborted);
                MultiplayerMetrics.RecordRosterPostReadAuthority(
                    RosterPostReadSurface.SignalR, authority);
                if (authority != GuestSessionAuthorityStatus.Active)
                    throw new HubException("Lobby unavailable.");
            }

            // Shutdown might begin during either awaited Redis operation.
            // Even a successful in-flight result is no longer deliverable.
            if (drain?.IsDraining == true || connections?.IsDraining == true)
                throw new HubException("Lobby unavailable.");
            return roster;
        }
        catch (Exception error) when (
            error is KeyNotFoundException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            // Indistinguishable responses to hidden/absent lobbies and non-members.
            throw new HubException("Lobby unavailable.");
        }
        catch (LobbyMutationUnavailableException)
        {
            throw new HubException("Lobby unavailable.");
        }
        catch (LobbySnapshotUnavailableException)
        {
            // Corrupted/incompatible authoritative Redis room data must
            // look exactly like any other unavailable lobby to the socket.
            throw new HubException("Lobby unavailable.");
        }
        catch (LobbyReadContentionException)
        {
            // The caller must not receive an unverifiable stale roster or
            // implementation details when concurrent Redis CAS retries
            // exceed the bounded budget.
            throw new HubException("Lobby unavailable.");
        }
        catch (OperationCanceledException) when (!Context.ConnectionAborted.IsCancellationRequested)
        {
            throw new HubException("Lobby unavailable.");
        }
        catch (StackExchange.Redis.RedisException)
        {
            throw new HubException("Lobby unavailable.");
        }
    }
}

