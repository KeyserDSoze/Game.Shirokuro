using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Shirokuro.Application.Lobbies;
using StackExchange.Redis;

namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Redis foundation for atomic, per-room snapshots (#34) and shared creator
/// and global capacity indexes (#35). Deliberately not wired to HTTP yet:
/// live membership, quotas, expiry/cleanup must migrate as one unit.
/// </summary>
public sealed class RedisLobbySnapshotStore(
    IDatabase database, TimeProvider clock, TimeSpan? inactivityTimeout = null,
    string? namespaceName = null) : IGuestRevocationFence
{
    private readonly TimeSpan _timeout = inactivityTimeout ??
        TimeSpan.FromMinutes(30);
    // All room, global and owner keys use the SAME Redis Cluster hash tag,
    // making their multi-key Lua scripts legal in a cluster.
    private readonly string _prefix = Prefix(namespaceName);

    // Compare the complete stored snapshot, including its activity timestamp:
    // unchanged-revision authorized heartbeats must still detect conflicts.
    private const string CompareExchangeScript = """
        -- KEYS[4] shares the exact cluster hash tag with the room and quota.
        -- Fencing and CAS occur within one atomic Lua operation.
        if ARGV[6] == '1' and redis.call('EXISTS', KEYS[4]) == 1 then
            return -1
        end
        local current = redis.call('GET', KEYS[1])
        if not current or current ~= ARGV[1] then return 0 end

        if ARGV[5] == '1' then
            if not redis.call('ZSCORE', KEYS[2], KEYS[1]) or
               not redis.call('ZSCORE', KEYS[3], KEYS[1]) then
                return 0
            end
        end

        redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[2])
        if ARGV[5] == '1' then
            local deadline = redis.call('PEXPIRETIME', KEYS[1])
            redis.call('ZADD', KEYS[2], deadline, KEYS[1])
            redis.call('ZADD', KEYS[3], deadline, KEYS[1])
            redis.call('PEXPIRE', KEYS[2], ARGV[4])
            redis.call('PEXPIRE', KEYS[3], ARGV[4])
        end
        return 1
        """;

    private const string DeleteIfCurrentScript = """
        if ARGV[3] == '1' and redis.call('EXISTS', KEYS[4]) == 1 then
            return -1
        end
        local current = redis.call('GET', KEYS[1])
        if not current or current ~= ARGV[1] then return 0 end
        if ARGV[2] == '1' then
            if not redis.call('ZSCORE', KEYS[2], KEYS[1]) or
               not redis.call('ZSCORE', KEYS[3], KEYS[1]) then
                return 0
            end
        end
        redis.call('DEL', KEYS[1])
        if ARGV[2] == '1' then
            redis.call('ZREM', KEYS[2], KEYS[1])
            redis.call('ZREM', KEYS[3], KEYS[1])
        end
        return 1
        """;

    // All quota checks and the room insertion share the same Redis Lua
    // execution, even when N independent API processes create concurrently.
    // Scores use Redis's own clock and PEXPIRETIME, not per-node clocks.
    private const string BoundedCreateScript = """
        if redis.call('EXISTS', KEYS[4]) == 1 then return -3 end
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end

        local nowparts = redis.call('TIME')
        local now = tonumber(nowparts[1]) * 1000 +
                    math.floor(tonumber(nowparts[2]) / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now)

        if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[3]) then
            return -1
        end
        if redis.call('ZCARD', KEYS[3]) >= tonumber(ARGV[4]) then
            return -2
        end

        redis.call('PSETEX', KEYS[1], ARGV[2], ARGV[1])
        local deadline = redis.call('PEXPIRETIME', KEYS[1])
        redis.call('ZADD', KEYS[2], deadline, KEYS[1])
        redis.call('ZADD', KEYS[3], deadline, KEYS[1])
        redis.call('PEXPIRE', KEYS[2], ARGV[5])
        redis.call('PEXPIRE', KEYS[3], ARGV[5])
        return 1
        """;

    public sealed record Stored(LobbySnapshot Snapshot, byte[] RawBytes);

    // Guest cookie TTL is 8h. Keep a hashed actor tombstone no longer than
    // that duration after revocation; a new guest has a different random ID.
    public static readonly TimeSpan RevocationFenceTtl = TimeSpan.FromHours(8);

    /// <summary>
    /// After revocation (or natural session expiry), mark this server-issued
    /// actor as permanently ineligible for lobby mutations while a stale
    /// request may still be in flight. A failed Redis write must propagate:
    /// availability is not permission. The hash slot is shared with all room
    /// create/CAS/delete scripts, including Redis Cluster deployments.
    /// </summary>
    public async Task FenceGuestAsync(
        string trustedGuestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidServerGuestId(trustedGuestId))
            throw new ArgumentException("Invalid trusted guest identity.",
                nameof(trustedGuestId));

        // No raw identity/session token in either key or value.
        await database.StringSetAsync(RevokedGuestKey(trustedGuestId),
            "1", expiry: RevocationFenceTtl, when: When.NotExists);
    }

    /// <summary>Unindexed low-level create retained for #34 compatibility.</summary>
    public async Task<bool> CreateAsync(LobbySnapshot snapshot)
    {
        if (snapshot.QuotaTracked)
            throw new ArgumentException(
                "Quota-tracked snapshots require bounded creation.", nameof(snapshot));
        var ttl = DeadlineMilliseconds(snapshot);
        return await database.StringSetAsync(
            RoomKey(snapshot.State.Definition.LobbyId),
            RedisLobbySnapshotCodec.Encode(snapshot),
            expiry: TimeSpan.FromMilliseconds(ttl), when: When.NotExists);
    }

    /// <summary>
    /// Admit a server-authenticated creator atomically under BOTH global and
    /// creator limits. Never called with an actor supplied by the browser.
    /// </summary>
    public async Task<bool> CreateBoundedAsync(
        LobbySnapshot snapshot, int maxRooms, int maxRoomsPerCreator)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.CreatorId);
        if (snapshot.QuotaTracked)
            throw new ArgumentException("Already quota-tracked.", nameof(snapshot));
        if (maxRooms < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRooms));
        if (maxRoomsPerCreator < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRoomsPerCreator));

        var bounded = snapshot with { QuotaTracked = true };
        var ttl = DeadlineMilliseconds(bounded);
        var result = (long)await database.ScriptEvaluateAsync(
            BoundedCreateScript,
            [RoomKey(bounded.State.Definition.LobbyId), GlobalIndexKey,
             CreatorIndexKey(bounded.CreatorId!), RevokedGuestKey(bounded.CreatorId!)],
            [RedisLobbySnapshotCodec.Encode(bounded), ttl,
             maxRooms, maxRoomsPerCreator, IndexTtlMilliseconds()]);
        if (result == -3)
            throw new UnauthorizedAccessException("Lobby unavailable.");
        if (result is -1 or -2)
            throw new LobbyCapacityException();
        if (result is not (0 or 1))
            throw new InvalidOperationException("Unexpected Redis lobby result.");
        return result == 1;
    }

    /// <summary>
    /// Internal read only: never refreshes Redis TTL, and is not an HTTP
    /// roster response. API layers must authorize membership first.
    /// </summary>
    public async Task<Stored?> ReadAsync(string lobbyId)
    {
        var raw = await database.StringGetAsync(RoomKey(lobbyId));
        if (raw.IsNull)
            return null;
        var bytes = (byte[])raw!;
        var snapshot = RedisLobbySnapshotCodec.Decode(bytes, lobbyId);
        return Remaining(snapshot) > TimeSpan.Zero
            ? new Stored(snapshot, bytes)
            : null;
    }

    /// <summary>
    /// Save only if the complete previously-read value matches. If bounded,
    /// update the room plus both quota-index deadlines in the same script.
    /// </summary>
    public async Task<bool> CompareExchangeAsync(
        Stored expected, LobbySnapshot updated, string? trustedActor = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        ValidateExpected(expected);

        var oldState = expected.Snapshot.State;
        var newState = updated.State;
        if (!string.Equals(oldState.Definition.LobbyId,
                newState.Definition.LobbyId, StringComparison.Ordinal) ||
            !oldState.Definition.TeamIds.SequenceEqual(newState.Definition.TeamIds) ||
            !oldState.Definition.HeroIds.SequenceEqual(newState.Definition.HeroIds) ||
            !string.Equals(expected.Snapshot.CreatorId, updated.CreatorId,
                StringComparison.Ordinal) ||
            expected.Snapshot.QuotaTracked != updated.QuotaTracked ||
            newState.Revision < oldState.Revision ||
            newState.Revision - oldState.Revision > 1 ||
            (newState.Revision == oldState.Revision &&
                !oldState.Participants.SequenceEqual(newState.Participants)) ||
            updated.LastActivityUtc < expected.Snapshot.LastActivityUtc)
            throw new ArgumentException("Invalid atomic lobby update.");

        // A stale read cannot revive an expired room with a later timestamp.
        if (Remaining(expected.Snapshot) <= TimeSpan.Zero)
            return false;

        var ttl = Remaining(updated);
        if (ttl <= TimeSpan.Zero)
            return false;
        var bytes = RedisLobbySnapshotCodec.Encode(updated);
        var result = (long)await database.ScriptEvaluateAsync(
            CompareExchangeScript,
            [RoomKey(newState.Definition.LobbyId), GlobalIndexKey,
             CreatorIndexKey(updated.CreatorId),
             RevokedGuestKey(trustedActor ?? "administrative")],
            [expected.RawBytes, bytes, DeadlineMilliseconds(updated),
             IndexTtlMilliseconds(), updated.QuotaTracked ? 1 : 0,
             trustedActor is null ? 0 : 1]);
        if (result == -1)
            throw new UnauthorizedAccessException("Lobby unavailable.");
        if (result is not (0 or 1))
            throw new InvalidDataException("Invalid Redis lobby result.");
        return result == 1;
    }

    public async Task<bool> DeleteIfCurrentAsync(
        Stored expected, string? trustedActor = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateExpected(expected);
        var result = (long)await database.ScriptEvaluateAsync(
            DeleteIfCurrentScript,
            [RoomKey(expected.Snapshot.State.Definition.LobbyId), GlobalIndexKey,
             CreatorIndexKey(expected.Snapshot.CreatorId),
             RevokedGuestKey(trustedActor ?? "administrative")],
            [expected.RawBytes, expected.Snapshot.QuotaTracked ? 1 : 0,
             trustedActor is null ? 0 : 1]);
        if (result == -1)
            throw new UnauthorizedAccessException("Lobby unavailable.");
        if (result is not (0 or 1))
            throw new InvalidDataException("Invalid Redis lobby result.");
        return result == 1;
    }

    private static void ValidateExpected(Stored expected)
    {
        if (!expected.RawBytes.AsSpan().SequenceEqual(
                RedisLobbySnapshotCodec.Encode(expected.Snapshot)))
            throw new InvalidDataException("Untrusted persisted lobby version.");
    }

    /// <summary>
    /// Server-only bounded enumeration for cross-instance session revocation.
    /// Hashed keys are fetched internally; no invitation or roster is
    /// returned without independent authenticated membership authorization.
    /// </summary>
    public async Task<IReadOnlyList<Stored>> GetIndexedRoomsAsync()
    {
        var members = await database.SortedSetRangeByRankAsync(GlobalIndexKey);
        var result = new List<Stored>(members.Length);
        foreach (var member in members)
        {
            var key = (RedisKey)member.ToString();
            var raw = await database.StringGetAsync(key);
            if (raw.IsNull)
                continue; // Redis expiry can race this maintenance scan.
            var bytes = (byte[])raw!;
            using var json = JsonDocument.Parse(bytes);
            var id = json.RootElement.GetProperty("LobbyId").GetString()
                ?? throw new InvalidDataException("Missing persisted lobby ID.");
            if (!string.Equals(key.ToString(), RoomKey(id).ToString(),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Persisted lobby key mismatch.");
            var snapshot = RedisLobbySnapshotCodec.Decode(bytes, id);
            if (!snapshot.QuotaTracked)
                throw new InvalidDataException("Missing lobby capacity tracking.");
            if (Remaining(snapshot) > TimeSpan.Zero)
                result.Add(new Stored(snapshot, bytes));
        }
        return result;
    }

    private TimeSpan Remaining(LobbySnapshot snapshot)
    {
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        return snapshot.LastActivityUtc + _timeout - clock.GetUtcNow();
    }

    private long DeadlineMilliseconds(LobbySnapshot snapshot)
    {
        var remaining = Remaining(snapshot);
        if (remaining <= TimeSpan.Zero ||
            remaining > _timeout)
            throw new ArgumentOutOfRangeException(nameof(snapshot),
                "Invalid or expired lobby activity time.");
        return Math.Max(1, (long)Math.Ceiling(remaining.TotalMilliseconds));
    }

    private long IndexTtlMilliseconds() =>
        checked(2 * (long)Math.Ceiling(_timeout.TotalMilliseconds) + 10000);

    private RedisKey RoomKey(string id) =>
        _prefix + "room:" + Hash(id);
    private RedisKey GlobalIndexKey => _prefix + "capacity:all";
    private RedisKey CreatorIndexKey(string? creatorId) =>
        _prefix + "capacity:creator:" + Hash(creatorId ?? "untracked");
    private RedisKey RevokedGuestKey(string trustedGuestId) =>
        _prefix + "revoked:" + Hash(trustedGuestId);

    private static bool ValidServerGuestId(string? value)
    {
        if (value is not { Length: 38 } ||
            !value.StartsWith("guest-", StringComparison.Ordinal))
            return false;
        return value.AsSpan(6).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    private static string Prefix(string? name)
    {
        name ??= "rooms";
        if (name.Length is < 1 or > 48 ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid Redis lobby key namespace.",
                nameof(name));
        return "shirokuro:lobby:{" + name + "}:v2:";
    }

    private static string Hash(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    }
}
