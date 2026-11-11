using System.Security.Cryptography;
using Shirokuro.Application.Lobbies;
using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Production/staging authoritative lobby coordinator. All operations
/// re-read from Redis and CAS under domain invariants. No positive per-node
/// cache, no client actor, no raw roster to HTTP. Redis outage denies access.
/// </summary>
public sealed class RedisGuestLobbyRuntime(
    RedisLobbySnapshotStore store, TimeProvider clock) : IAsyncGuestLobby
{
    private const int RetryLimit = 32;

    public async Task<LobbyRosterView> CreateAsync(
        string trustedPlayerId, IReadOnlyList<string>? teamIds, string? initialTeam,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        GuestLobbyCommands.ValidateTeams(teamIds);
        if (initialTeam is null || !teamIds!.Contains(initialTeam, StringComparer.Ordinal))
            throw new ArgumentException("Initial team must be configured.");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = "room-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var definition = new LobbyDefinition(id, teamIds!, OriginalHeroRoster.Ids);
            var state = LobbyState.Create(definition).Join(
                trustedPlayerId, initialTeam, 0);
            var snapshot = new LobbySnapshot(state, clock.GetUtcNow(), trustedPlayerId);
            if (await store.CreateBoundedAsync(snapshot, GuestLobbyCommands.MaxRooms,
                    GuestLobbyCommands.MaxRoomsPerCreator))
                return Project(state);
        }
        throw new InvalidOperationException("Unable to allocate a lobby.");
    }

    public async Task<LobbyRosterView> JoinAsync(
        string roomId, string trustedPlayerId, string? teamId, long? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        if (teamId is null || teamId.Length is < 1 or > 24)
            throw new ArgumentException("Invalid team.");

        for (var attempt = 0; attempt < RetryLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = await Find(roomId);
            var before = found.Snapshot.State;
            if (expectedRevision.HasValue && before.Revision != expectedRevision.Value)
                throw new LobbyRevisionConflictException(expectedRevision.Value, before.Revision);
            if (before.Participants.Count >= GuestLobbyCommands.MaxParticipants &&
                !IsMember(before, trustedPlayerId))
                throw new LobbyCapacityException();
            var next = before.Join(trustedPlayerId, teamId, before.Revision);
            if (await store.CompareExchangeAsync(found,
                found.Snapshot with { State = next, LastActivityUtc = NextActivity(found) }))
                return Project(next);
            if (expectedRevision.HasValue)
                throw new LobbyRevisionConflictException(expectedRevision.Value,
                    (await store.ReadAsync(roomId))?.Snapshot.State.Revision ?? before.Revision);
        }
        throw new InvalidOperationException("Concurrent lobby updates exceeded retry budget.");
    }

    public Task<LobbyRosterView> ChooseHeroAsync(
        string roomId, string trustedPlayerId, string? heroId, long expectedRevision,
        CancellationToken cancellationToken = default) =>
        MutateForMember(roomId, trustedPlayerId, expectedRevision,
            state => state.ChooseHero(trustedPlayerId, heroId!, expectedRevision),
            cancellationToken);

    public Task<LobbyRosterView> ChangeTeamAsync(
        string roomId, string trustedPlayerId, string? teamId, long expectedRevision,
        CancellationToken cancellationToken = default) =>
        MutateForMember(roomId, trustedPlayerId, expectedRevision,
            state => state.ChangeTeam(trustedPlayerId, teamId!, expectedRevision),
            cancellationToken);

    public async Task LeaveAsync(
        string roomId, string trustedPlayerId, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = await Find(roomId);
        EnsureMember(found.Snapshot.State, trustedPlayerId);
        var next = found.Snapshot.State.Leave(trustedPlayerId, expectedRevision);
        var success = next.Participants.Count == 0
            ? await store.DeleteIfCurrentAsync(found)
            : await store.CompareExchangeAsync(found,
                found.Snapshot with { State = next, LastActivityUtc = NextActivity(found) });
        if (!success)
            throw new LobbyRevisionConflictException(expectedRevision,
                (await store.ReadAsync(roomId))?.Snapshot.State.Revision ?? expectedRevision);
    }

    public async Task<LobbyRosterView> ForMemberAsync(
        string roomId, string trustedPlayerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        for (var attempt = 0; attempt < RetryLimit; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = await Find(roomId);
            EnsureMember(found.Snapshot.State, trustedPlayerId);
            if (await store.CompareExchangeAsync(found,
                found.Snapshot with { LastActivityUtc = NextActivity(found) }))
                return Project(found.Snapshot.State);
        }
        throw new InvalidOperationException("Concurrent lobby reads exceeded retry budget.");
    }

    /// <summary>
    /// Administrative removal after a trusted session revocation/expiry.
    /// Scan the bounded server-only global index, never client supplied IDs.
    /// Deletion and roster revision increments CAS against concurrent writes.
    /// Inactivity timestamps are intentionally unchanged.
    /// </summary>
    public async Task<int> RemovePlayerFromAllRoomsAsync(
        string trustedPlayerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        var removed = 0;
        var rooms = await store.GetIndexedRoomsAsync();
        foreach (var item in rooms)
        {
            var roomId = item.Snapshot.State.Definition.LobbyId;
            for (var attempt = 0; attempt < RetryLimit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await store.ReadAsync(roomId);
                if (current is null || !IsMember(current.Snapshot.State, trustedPlayerId))
                    break;

                var next = current.Snapshot.State.Leave(
                    trustedPlayerId, current.Snapshot.State.Revision);
                var success = next.Participants.Count == 0
                    ? await store.DeleteIfCurrentAsync(current)
                    : await store.CompareExchangeAsync(current,
                        current.Snapshot with { State = next });
                if (success)
                {
                    removed++;
                    break;
                }
                if (attempt == RetryLimit - 1)
                    throw new InvalidOperationException("Concurrent administrative cleanup failed.");
            }
        }
        return removed;
    }

    private async Task<LobbyRosterView> MutateForMember(
        string roomId, string actor, long revision,
        Func<LobbyState, LobbyState> mutation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var found = await Find(roomId);
        EnsureMember(found.Snapshot.State, actor);
        var next = mutation(found.Snapshot.State);
        if (!await store.CompareExchangeAsync(found,
            found.Snapshot with { State = next, LastActivityUtc = NextActivity(found) }))
            throw new LobbyRevisionConflictException(revision,
                (await store.ReadAsync(roomId))?.Snapshot.State.Revision ?? revision);
        return Project(next);
    }

    private async Task<RedisLobbySnapshotStore.Stored> Find(string roomId) =>
        await store.ReadAsync(roomId) ??
        throw new KeyNotFoundException("Lobby unavailable.");

    private DateTimeOffset NextActivity(RedisLobbySnapshotStore.Stored previous)
    {
        var now = clock.GetUtcNow();
        return now > previous.Snapshot.LastActivityUtc
            ? now : previous.Snapshot.LastActivityUtc;
    }

    private static bool IsMember(LobbyState state, string actor) =>
        state.Participants.Any(p =>
            string.Equals(p.PlayerId, actor, StringComparison.Ordinal));

    private static void EnsureMember(LobbyState state, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!IsMember(state, actor))
            throw new UnauthorizedAccessException("Lobby unavailable.");
    }

    private static LobbyRosterView Project(LobbyState state) => new(
        state.Definition.LobbyId, state.Revision,
        Array.AsReadOnly(state.Definition.TeamIds.ToArray()),
        Array.AsReadOnly(state.Participants.Select(p =>
            new LobbyParticipantView(p.PlayerId, p.TeamId, p.HeroId)).ToArray()));
}
