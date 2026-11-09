namespace Shirokuro.Domain.Lobbies;

/// <summary>
/// Immutable, pre-match roster. No readiness, death, treasure or actual match
/// mechanics are implied. All actors are supplied by a trusted server caller.
/// </summary>
public sealed class LobbyState
{
    public LobbyDefinition Definition { get; }
    public long Revision { get; }
    public IReadOnlyList<LobbyParticipant> Participants { get; }

    private LobbyState(
        LobbyDefinition definition,
        long revision,
        IReadOnlyList<LobbyParticipant> participants)
    {
        Definition = definition;
        Revision = revision;
        Participants = Array.AsReadOnly(participants.ToArray());
    }

    public static LobbyState Create(LobbyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new LobbyState(definition, 0, Array.Empty<LobbyParticipant>());
    }

    public LobbyState Join(string trustedPlayerId, string teamId, long expectedRevision)
    {
        CheckRevision(expectedRevision);
        LobbyDefinition.RequireId(trustedPlayerId, nameof(trustedPlayerId));
        EnsureTeam(teamId);

        var existing = Participants.SingleOrDefault(x => x.PlayerId == trustedPlayerId);
        if (existing is not null)
        {
            if (existing.TeamId == teamId) return this; // Idempotent reconnect.
            throw new InvalidOperationException("Participant already joined a different team; use ChangeTeam.");
        }

        return Next([.. Participants, new LobbyParticipant(trustedPlayerId, teamId, null)]);
    }

    public LobbyState ChangeTeam(string trustedPlayerId, string teamId, long expectedRevision)
    {
        CheckRevision(expectedRevision);
        EnsureTeam(teamId);
        var participant = FindParticipant(trustedPlayerId);
        if (participant.TeamId == teamId) return this;

        return Next(Participants.Select(p =>
            p.PlayerId == trustedPlayerId ? p with { TeamId = teamId } : p));
    }

    public LobbyState ChooseHero(string trustedPlayerId, string heroId, long expectedRevision)
    {
        CheckRevision(expectedRevision);
        if (!Definition.AllowsHero(heroId))
        {
            throw new ArgumentException("Unknown hero for this lobby.", nameof(heroId));
        }

        var participant = FindParticipant(trustedPlayerId);
        if (participant.HeroId == heroId) return this;

        return Next(Participants.Select(p =>
            p.PlayerId == trustedPlayerId ? p with { HeroId = heroId } : p));
    }

    public LobbyState Leave(string trustedPlayerId, long expectedRevision)
    {
        CheckRevision(expectedRevision);
        FindParticipant(trustedPlayerId);
        return Next(Participants.Where(p => p.PlayerId != trustedPlayerId));
    }

    private LobbyParticipant FindParticipant(string playerId)
    {
        LobbyDefinition.RequireId(playerId, nameof(playerId));
        return Participants.SingleOrDefault(p => p.PlayerId == playerId)
            ?? throw new InvalidOperationException("Player is not a lobby participant.");
    }

    private void EnsureTeam(string teamId)
    {
        if (!Definition.AllowsTeam(teamId))
        {
            throw new ArgumentException("Unknown team for this lobby.", nameof(teamId));
        }
    }

    private void CheckRevision(long expectedRevision)
    {
        if (expectedRevision != Revision)
        {
            throw new LobbyRevisionConflictException(expectedRevision, Revision);
        }
    }

    private LobbyState Next(IEnumerable<LobbyParticipant> participants)
        => new(Definition, checked(Revision + 1), participants.ToArray());
}

