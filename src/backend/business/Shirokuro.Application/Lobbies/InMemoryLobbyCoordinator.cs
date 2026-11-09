using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Application.Lobbies;

/// <summary>
/// Process-local, revision-safe room coordinator. All writes and inactivity
/// bookkeeping are protected by one lock. Never pass client-selected actors.
/// </summary>
public sealed class InMemoryLobbyCoordinator
{
    public static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromMinutes(30);

    private sealed record RoomEntry(LobbyState State, DateTimeOffset LastActivityUtc);

    private readonly object _gate = new();
    private readonly Dictionary<string, RoomEntry> _lobbies = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly TimeSpan _inactivityTimeout;

    public InMemoryLobbyCoordinator(
        TimeProvider? clock = null, TimeSpan? inactivityTimeout = null)
    {
        _clock = clock ?? TimeProvider.System;
        _inactivityTimeout = inactivityTimeout ?? DefaultInactivityTimeout;
        if (_inactivityTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
    }

    public LobbyState Create(string lobbyId, IEnumerable<string> configuredTeams)
    {
        var definition = new LobbyDefinition(lobbyId, configuredTeams, OriginalHeroRoster.Ids);
        lock (_gate)
        {
            PruneExpiredCore();
            if (_lobbies.ContainsKey(definition.LobbyId))
                throw new InvalidOperationException("Lobby ID already exists.");

            var created = LobbyState.Create(definition);
            Store(created);
            return created;
        }
    }

    /// <summary>
    /// Create and admit the owner atomically. Expired rooms never occupy a
    /// capacity slot, even if the periodic background sweep has not run.
    /// </summary>
    public LobbyState CreateWithOwner(
        string lobbyId, IEnumerable<string> configuredTeams, string trustedOwnerId,
        string initialTeamId, int maxRooms)
    {
        var definition = new LobbyDefinition(lobbyId, configuredTeams, OriginalHeroRoster.Ids);
        if (maxRooms < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRooms));

        lock (_gate)
        {
            PruneExpiredCore();
            if (_lobbies.Count >= maxRooms)
                throw new LobbyCapacityException();
            if (_lobbies.ContainsKey(definition.LobbyId))
                throw new InvalidOperationException("Lobby ID already exists.");

            var joined = LobbyState.Create(definition).Join(trustedOwnerId, initialTeamId, 0);
            Store(joined);
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
                throw new LobbyRevisionConflictException(expectedRevision.Value, state.Revision);

            if (state.Participants.Count >= maxParticipants &&
                !state.Participants.Any(p => p.PlayerId == trustedPlayerId))
                throw new LobbyCapacityException();

            var next = state.Join(trustedPlayerId, teamId, state.Revision);
            Store(next);
            return next;
        }
    }

    /// <summary>Delete the room immediately if its last member leaves.</summary>
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
                Store(next);
            return next;
        }
    }

    /// <summary>
    /// A pure lookup does NOT extend TTL; server logic and failed/anonymous
    /// reads must never keep rooms alive by accidentally calling Get.
    /// </summary>
    public LobbyState Get(string lobbyId)
    {
        lock (_gate)
            return Find(lobbyId);
    }

    /// <summary>
    /// Membership check and activity refresh share the same lock; outsiders
    /// cannot use roster requests to prevent the lobby from expiring.
    /// </summary>
    public LobbyState GetForMember(string lobbyId, string trustedPlayerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        lock (_gate)
        {
            var state = Find(lobbyId);
            if (!state.Participants.Any(p =>
                string.Equals(p.PlayerId, trustedPlayerId, StringComparison.Ordinal)))
                throw new UnauthorizedAccessException("Player is not a lobby participant.");

            Store(state); // Same immutable revision; only activity timestamp changes.
            return state;
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

    /// <summary>
    /// Called by the background worker, and also during room creation for
    /// immediate capacity reclamation. Never returns expired room identifiers.
    /// </summary>
    public int PruneExpired()
    {
        lock (_gate)
            return PruneExpiredCore();
    }

    private LobbyState Mutate(string lobbyId, Func<LobbyState, LobbyState> update)
    {
        lock (_gate)
        {
            var previous = Find(lobbyId);
            var next = update(previous);
            Store(next);
            return next;
        }
    }

    private LobbyState Find(string lobbyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lobbyId);
        if (!_lobbies.TryGetValue(lobbyId, out var entry))
            throw new KeyNotFoundException("Unknown lobby.");

        if (IsExpired(entry))
        {
            _lobbies.Remove(lobbyId);
            throw new KeyNotFoundException("Unknown lobby.");
        }

        return entry.State;
    }

    private void Store(LobbyState state)
        => _lobbies[state.Definition.LobbyId] = new RoomEntry(state, _clock.GetUtcNow());

    private bool IsExpired(RoomEntry entry)
        => _clock.GetUtcNow() - entry.LastActivityUtc >= _inactivityTimeout;

    private int PruneExpiredCore()
    {
        var expired = _lobbies
            .Where(pair => IsExpired(pair.Value))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var id in expired)
            _lobbies.Remove(id);
        return expired.Length;
    }
}

