using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Shirokuro.Api.Security;
using Shirokuro.Application.Lobbies;

namespace Shirokuro.Api.Hubs;

/// <summary>
/// Authenticated, read-only SignalR transport. There are deliberately no
/// subscribe/group/broadcast methods: membership can change, so future push
/// notifications must reauthorize every receiver before sending.
/// </summary>
[Authorize]
public sealed class LobbyRosterHub(LobbyRosterReader reader) : Hub
{
    public LobbyRosterView GetRoster(string lobbyId)
    {
        var trustedPlayerId = AuthenticatedPlayerIdentity.FromPrincipal(Context.User);
        if (trustedPlayerId is null)
        {
            throw new HubException("Lobby unavailable.");
        }

        try
        {
            return reader.ForMember(lobbyId, trustedPlayerId);
        }
        catch (Exception error) when (
            error is KeyNotFoundException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            // Indistinguishable responses to hidden/absent lobbies and non-members.
            throw new HubException("Lobby unavailable.");
        }
    }
}

