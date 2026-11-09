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

