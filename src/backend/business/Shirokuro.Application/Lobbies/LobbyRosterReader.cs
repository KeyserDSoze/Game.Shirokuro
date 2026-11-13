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
        // Atomic member-scoped read and inactivity refresh.
        // Unknown and non-member rooms stay indistinguishable on the wire.
        return Project(coordinator.GetForMember(lobbyId, trustedPlayerId));
    }

    public LobbyRosterView PeekForMember(string lobbyId, string trustedPlayerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        return Project(coordinator.PeekForMember(lobbyId, trustedPlayerId));
    }

    private static LobbyRosterView Project(Shirokuro.Domain.Lobbies.LobbyState state) =>
        new(
            state.Definition.LobbyId,
            state.Revision,
            Array.AsReadOnly(state.Definition.TeamIds.ToArray()),
            Array.AsReadOnly(state.Participants.Select(participant =>
                new LobbyParticipantView(
                    participant.PlayerId,
                    participant.TeamId,
                    participant.HeroId)).ToArray()));
}


