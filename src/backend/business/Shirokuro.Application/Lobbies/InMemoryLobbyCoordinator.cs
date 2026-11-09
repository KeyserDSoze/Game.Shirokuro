using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Application.Lobbies;

/// <summary>
/// Server-owned, process-local storage for future authenticated lobby flows.
/// This is NOT a durable, distributed or authenticated multiplayer endpoint.
/// Actor IDs must be derived from an authenticated server-side principal.
/// </summary>
public sealed class InMemoryLobbyCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LobbyState> _lobbies = new(StringComparer.Ordinal);

    public LobbyState Create(string lobbyId, IEnumerable<string> configuredTeams)
    {
        // The original hero roster is embedded from the single source of truth.
        var definition = new LobbyDefinition(lobbyId, configuredTeams, OriginalHeroRoster.Ids);
        lock (_gate)
        {
            if (_lobbies.ContainsKey(definition.LobbyId))
            {
                throw new InvalidOperationException("Lobby ID already exists.");
            }

            var created = LobbyState.Create(definition);
            _lobbies.Add(definition.LobbyId, created);
            return created;
        }
    }


    /// <summary>
    /// Create the room and admit its owner under one lock, or publish neither.
    /// Capacity is process-local; do not mistake this for distributed storage.
    /// </summary>
    public LobbyState CreateWithOwner(
        string lobbyId, IEnumerable<string> configuredTeams, string trustedOwnerId,
        string initialTeamId, int maxRooms)
    {
        var definition = new LobbyDefinition(lobbyId, configuredTeams, OriginalHeroRoster.Ids);
        lock (_gate)
        {
            if (_lobbies.Count >= maxRooms)
                throw new LobbyCapacityException();

            if (_lobbies.ContainsKey(definition.LobbyId))
                throw new InvalidOperationException("Lobby ID already exists.");

            var joined = LobbyState.Create(definition).Join(trustedOwnerId, initialTeamId, 0);
            _lobbies.Add(definition.LobbyId, joined);
            return joined;
        }
    }

    public LobbyState JoinBounded(
        string lobbyId, string trustedPlayerId, string teamId,
        long? expectedRevision, int maxParticipants)
    {
        lock (_gate)
        {
            var state = Find(lobbyId);
            if (expectedRevision.HasValue && state.Revision != expectedRevision.Value)
                throw new LobbyRevisionConflictException(expectedRevision, state.Revision);

            if (state.Participants.Count >= maxParticipants &&
                !state.Participants.Any(p => p.PlayerId == trustedPlayerId))
                throw new LobbyCapacityException();

            var next = state.Join(trustedPlayerId, teamId, state.Revision);
            _lobbies[lobbyId] = next;
            return next;
        }
    }

    /// <summary>Public HTTP leave prunes empty ephemeral rooms to free capacity.</summary>
    public LobbyState LeaveAndPrune(
        string lobbyId, string trustedPlayerId, long expectedRevision)
    {
        lock (_gate)
        {
            var previous = Find(lobbyId);
            var next = previous.Leave(trustedPlayerId, expectedRevision);
            if (next.Participants.Count == 0)
                _lobbies.Remove(lobbyId);
            else
                _lobbies[lobbyId] = next;
            return next;
        }
    }

    public LobbyState Get(string lobbyId)
    {
        lock (_gate)
        {
            return Find(lobbyId);
        }
    }

    public LobbyState Join(string lobbyId, string trustedPlayerId, string teamId, long expectedRevision)
        => Mutate(lobbyId, state => state.Join(trustedPlayerId, teamId, expectedRevision));

    public LobbyState ChangeTeam(string lobbyId, string trustedPlayerId, string teamId, long expectedRevision)
        => Mutate(lobbyId, state => state.ChangeTeam(trustedPlayerId, teamId, expectedRevision));

    public LobbyState ChooseHero(string lobbyId, string trustedPlayerId, string heroId, long expectedRevision)
        => Mutate(lobbyId, state => state.ChooseHero(trustedPlayerId, heroId, expectedRevision));

    public LobbyState Leave(string lobbyId, string trustedPlayerId, long expectedRevision)
        => Mutate(lobbyId, state => state.Leave(trustedPlayerId, expectedRevision));

    private LobbyState Mutate(string lobbyId, Func<LobbyState, LobbyState> update)
    {
        lock (_gate)
        {
            var previous = Find(lobbyId);
            var next = update(previous);
            _lobbies[lobbyId] = next;
            return next;
        }
    }

    private LobbyState Find(string lobbyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lobbyId);
        return _lobbies.TryGetValue(lobbyId, out var state)
            ? state : throw new KeyNotFoundException("Unknown lobby.");
    }
}


