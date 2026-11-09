namespace Shirokuro.Application.Lobbies;

/// <summary>
/// Produces a copy of public pre-match selection data only for an existing
/// member. The caller must supply the player ID from trusted authentication.
/// </summary>
public sealed class LobbyRosterReader(InMemoryLobbyCoordinator coordinator)
{
    public LobbyRosterView ForMember(string lobbyId, string trustedPlayerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        var state = coordinator.Get(lobbyId);

        if (!state.Participants.Any(participant =>
            StringComparer.Ordinal.Equals(participant.PlayerId, trustedPlayerId)))
        {
            throw new UnauthorizedAccessException("Player is not a member of this lobby.");
        }

        return new LobbyRosterView(
            state.Definition.LobbyId,
            state.Revision,
            Array.AsReadOnly(state.Definition.TeamIds.ToArray()),
            Array.AsReadOnly(state.Participants.Select(participant =>
                new LobbyParticipantView(
                    participant.PlayerId,
                    participant.TeamId,
                    participant.HeroId)).ToArray()));
    }
}

