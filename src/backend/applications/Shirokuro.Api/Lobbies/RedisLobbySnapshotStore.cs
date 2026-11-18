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

    // Bound concurrent administrative GETs even when the index contains 256
    // rooms. Each request still carries the Lua byte bound and 2s deadline.
    private const int IndexedReadWindowSize = 8;

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
            -- The existing quota indices MUST describe the actual room TTL,
            -- not merely contain a membership. Do not silently repair or
            -- erase corrupt scores while authenticating a guest mutation.
            local deadline = redis.call('PEXPIRETIME', KEYS[1])
            local global = redis.call('ZSCORE', KEYS[2], KEYS[1])
            local creator = redis.call('ZSCORE', KEYS[3], KEYS[1])
            if deadline <= 0 or not global or not creator or
               tonumber(global) ~= deadline or
               tonumber(creator) ~= deadline then
                return -2
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
            -- Never turn a room with inconsistent quota metadata into an
            -- acknowledged DELETE. A missing/early/late index score is
            -- an unavailable authoritative store, not a CAS conflict.
            local deadline = redis.call('PEXPIRETIME', KEYS[1])
            local global = redis.call('ZSCORE', KEYS[2], KEYS[1])
            local creator = redis.call('ZSCORE', KEYS[3], KEYS[1])
            if deadline <= 0 or not global or not creator or
               tonumber(global) ~= deadline or
               tonumber(creator) ~= deadline then
                return -2
            end
        end
        redis.call('DEL', KEYS[1])
        if ARGV[2] == '1' then
            redis.call('ZREM', KEYS[2], KEYS[1])
            redis.call('ZREM', KEYS[3], KEYS[1])
        end
        return 1
        """;

    // A positive DELETE Lua result is not enough to acknowledge a final
    // membership removal or durable cleanup. Confirm the room AND both quota
    // index memberships are absent, on this same Redis connector/slot.
    // Read-only, returns only a numeric flag; never leaks private roster.
    private const string ConfirmLobbyDeleteScript = """
        if redis.call('GET', KEYS[1]) then return 0 end
        if ARGV[1] == '1' then
            if redis.call('ZSCORE', KEYS[2], KEYS[1]) or
               redis.call('ZSCORE', KEYS[3], KEYS[1]) then
                return 0
            end
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

        -- A stale/early ZSET score is NOT proof that its room has expired.
        -- Before touching EITHER index, verify every due entry in both
        -- bounded windows points to a genuinely absent Redis room. A live
        -- room (including one whose TTL was lost) must retain its quota.
        -- Maximum scan: 257 keys/index; never load private roster bytes.
        local roomPrefix = string.sub(KEYS[1], 1, #KEYS[1] - 64)
        local function dueEntriesAreOrphans(index, requestedLimit)
            local limit = math.min(requestedLimit, 256)
            local due = redis.call('ZRANGEBYSCORE', index, '-inf', now,
                                   'LIMIT', 0, limit + 1)
            if #due > limit then return false end
            for _, member in ipairs(due) do
                if #member ~= #KEYS[1] or
                   string.sub(member, 1, #roomPrefix) ~= roomPrefix or
                   not string.match(string.sub(member, #roomPrefix + 1),
                                    '^[0-9a-f]+$') or
                   redis.call('PEXPIRETIME', member) ~= -2 then
                    return false
                end
            end
            return true
        end
        if not dueEntriesAreOrphans(KEYS[2], tonumber(ARGV[3])) or
           not dueEntriesAreOrphans(KEYS[3], tonumber(ARGV[4])) then
            return -4
        end
        -- Both indices have passed validation. Redis Lua is atomic, so no
        -- other writer can add a new due entry between check and prune.
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

    // Only acknowledge a newly created bounded room after the same Redis
    // connection confirms the exact snapshot and BOTH quota memberships.
    // The Lua reply never returns roster bytes, keys or player identities.
    // Read-only: a verification attempt does not refresh activity/TTL.
    private const string ConfirmBoundedCreateScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw or #raw ~= #ARGV[1] or raw ~= ARGV[1] then return 0 end
        local deadline = redis.call('PEXPIRETIME', KEYS[1])
        if deadline <= 0 then return 0 end
        local global = redis.call('ZSCORE', KEYS[2], KEYS[1])
        local creator = redis.call('ZSCORE', KEYS[3], KEYS[1])
        if not global or not creator then return 0 end
        if tonumber(global) ~= deadline or
           tonumber(creator) ~= deadline then return 0 end
        return 1
        """;

    // Read-only exact post-CAS check. Never return private roster bytes,
    // modify the TTL or claim a room whose quota indices were lost.
    private const string ConfirmLobbyCasScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw or raw ~= ARGV[1] then return 0 end
        local deadline = redis.call('PEXPIRETIME', KEYS[1])
        if deadline <= 0 then return 0 end
        if ARGV[2] == '1' then
            local global = redis.call('ZSCORE', KEYS[2], KEYS[1])
            local creator = redis.call('ZSCORE', KEYS[3], KEYS[1])
            if not global or not creator or
               tonumber(global) ~= deadline or
               tonumber(creator) ~= deadline then return 0 end
        end
        return 1
        """;

    // The administrative index is persisted data. A single corrupt ZSET
    // member can be enormous even when the scan is limited to 257 entries.
    // Strip oversized values inside Redis before returning any bytes.
    // Lua is read-only; no index entry is repaired or acknowledged here.
    private const string BoundedIndexScanScript = """
        local members = redis.call('ZRANGE', KEYS[1], 0, tonumber(ARGV[1]))
        for i,member in ipairs(members) do
            if #member > tonumber(ARGV[2]) then
                members[i] = ''
            end
        end
        return members
        """;

    // All room snapshots are untrusted persisted bytes. Bound the Redis reply
    // BEFORE sending it across the network, including administrative scans.
    // The empty sentinel is deliberately invalid for the strict codec;
    // nil is reserved for a genuinely missing key. No writes or TTL changes.
    private const string BoundedSnapshotReadScript = """
        local raw = redis.call('GET', KEYS[1])
        if not raw then return nil end
        if #raw > tonumber(ARGV[1]) then return '' end
        return raw
        """;

    // Read-only, atomic fenced GET for the periodic member roster poll.
    // Unlike a heartbeat CAS this cannot refresh the snapshot or its TTL.
    // A revoked actor sees the same absent result as a missing lobby.
    private const string ReadFencedWithoutHeartbeatScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then return nil end
        local raw = redis.call('GET', KEYS[1])
        if not raw then return nil end
        if #raw > tonumber(ARGV[1]) then return '' end
        return raw
        """;

    public sealed record Stored(LobbySnapshot Snapshot, byte[] RawBytes);

    // Guest cookie TTL is 8h. Keep a hashed actor tombstone no longer than
    // that duration after revocation; a new guest has a different random ID.
    public static readonly TimeSpan RevocationFenceTtl = TimeSpan.FromHours(8);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    // Abandon only the .NET wait, not an already-dispatched Redis command.
    // An uncertain Lua mutation must never be retried or treated as success.
    private static async Task<T> AwaitRedisAsync<T>(
        Func<Task<T>> command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        return await command().WaitAsync(timeout.Token);
    }

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

        // SET NX=false means that a prior fence may already exist; SET
        // NX=true alone does not prove persistence. Confirm the exact
        // immutable marker through the same authoritative Redis connector
        // before any caller can acknowledge a protected logout or ack expiry.
        // Keep the old TTL: never renew an already installed fence.
        // SET and read-back share ONE bounded deadline. Cancellation before
        // the read must not dispatch a late GET on a dropped request.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var key = RevokedGuestKey(trustedGuestId);
        await database.StringSetAsync(
                key, "1", expiry: RevocationFenceTtl, when: When.NotExists)
            .WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        var confirmed = await database.StringGetAsync(key)
            .WaitAsync(timeout.Token);
        if (!string.Equals((string?)confirmed, "1", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Guest revocation fence write was not confirmed.");
        timeout.Token.ThrowIfCancellationRequested();
    }

    /// <summary>Unindexed low-level create retained for #34 compatibility.</summary>
    public async Task<bool> CreateAsync(
        LobbySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (snapshot.QuotaTracked)
            throw new ArgumentException(
                "Quota-tracked snapshots require bounded creation.", nameof(snapshot));
        var ttl = DeadlineMilliseconds(snapshot);
        return await AwaitRedisAsync(() => database.StringSetAsync(
            RoomKey(snapshot.State.Definition.LobbyId),
            RedisLobbySnapshotCodec.Encode(snapshot),
            expiry: TimeSpan.FromMilliseconds(ttl), when: When.NotExists),
            cancellationToken);
    }

    /// <summary>
    /// Admit a server-authenticated creator atomically under BOTH global and
    /// creator limits. Never called with an actor supplied by the browser.
    /// </summary>
    public async Task<bool> CreateBoundedAsync(
        LobbySnapshot snapshot, int maxRooms, int maxRoomsPerCreator,
        CancellationToken cancellationToken = default)
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
        var roomKey = RoomKey(bounded.State.Definition.LobbyId);
        var globalKey = GlobalIndexKey;
        var creatorKey = CreatorIndexKey(bounded.CreatorId!);
        var bytes = RedisLobbySnapshotCodec.Encode(bounded);

        // A successful Lua reply alone cannot certify that a failover or a
        // silently ignored adapter persisted all three quota-bearing keys.
        // If this was a NEW room, read back the exact bytes, both ZSET
        // memberships AND matching Redis absolute expiry in a separate,
        // read-only Lua script. The whole create+check has a single deadline.
        // An uncertain result must never be retried with a new room ID.
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var result = (long)await database.ScriptEvaluateAsync(
                BoundedCreateScript,
                [roomKey, globalKey, creatorKey, RevokedGuestKey(bounded.CreatorId!)],
                [bytes, ttl, maxRooms, maxRoomsPerCreator, IndexTtlMilliseconds()])
            .WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        if (result == -3)
            throw new UnauthorizedAccessException("Lobby unavailable.");
        if (result == -4)
            throw new LobbyCreationUnavailableException();
        if (result is -1 or -2)
            throw new LobbyCapacityException();
        if (result is not (0 or 1))
            throw new InvalidOperationException("Unexpected Redis lobby result.");
        if (result == 0)
            return false; // Collision: the new room was not created.

        var confirmed = (long)await database.ScriptEvaluateAsync(
                ConfirmBoundedCreateScript,
                [roomKey, globalKey, creatorKey], [bytes])
            .WaitAsync(timeout.Token);
        if (confirmed != 1)
            throw new LobbyCreationUnavailableException();
        timeout.Token.ThrowIfCancellationRequested();
        return true;
    }

    /// <summary>
    /// Internal read only: never refreshes Redis TTL, and is not an HTTP
    /// roster response. API layers must authorize membership first.
    /// </summary>
    public async Task<Stored?> ReadAsync(
        string lobbyId, CancellationToken cancellationToken = default)
    {
        var raw = await ReadBoundedSnapshotBytesAsync(
            RoomKey(lobbyId), cancellationToken);
        return raw.IsNull ? null : DecodeStored((byte[])raw!, lobbyId);
    }

    private Task<RedisResult> ReadBoundedSnapshotBytesAsync(
        RedisKey key, CancellationToken cancellationToken) =>
        AwaitRedisAsync(() => database.ScriptEvaluateAsync(
            BoundedSnapshotReadScript, [key], [RedisLobbySnapshotCodec.MaxBytes]),
            cancellationToken);

    /// <summary>
    /// The browser's 15-second pre-match poll is not user activity.
    /// Use a single Redis Lua read for the room and guest revocation fence,
    /// avoiding even an unverified stale room read after known revocation.
    /// Does not PEXPIRE or touch quota/room indices.
    /// </summary>
    public async Task<Stored?> ReadForGuestWithoutHeartbeatAsync(
        string lobbyId, string trustedGuestId,
        CancellationToken cancellationToken = default)
    {
        if (!ValidServerGuestId(trustedGuestId))
            throw new ArgumentException("Invalid trusted guest identity.",
                nameof(trustedGuestId));
        var response = await AwaitRedisAsync(
            () => database.ScriptEvaluateAsync(
                ReadFencedWithoutHeartbeatScript,
                [RoomKey(lobbyId), RevokedGuestKey(trustedGuestId)],
                [RedisLobbySnapshotCodec.MaxBytes]),
            cancellationToken);
        return response.IsNull ? null : DecodeStored((byte[])response!, lobbyId);
    }

    private Stored? DecodeStored(byte[] bytes, string lobbyId)
    {
        LobbySnapshot snapshot;
        try
        {
            snapshot = RedisLobbySnapshotCodec.Decode(bytes, lobbyId);
        }
        catch (InvalidDataException)
        {
            // Never return a damaged or incompatible private roster.
            throw new LobbySnapshotUnavailableException();
        }
        return VerifiedStoredRemaining(snapshot) > TimeSpan.Zero
            ? new Stored(snapshot, bytes)
            : null;
    }

    /// <summary>
    /// Save only if the complete previously-read value matches. If bounded,
    /// update the room plus both quota-index deadlines in the same script.
    /// </summary>
    public async Task<bool> CompareExchangeAsync(
        Stored expected, LobbySnapshot updated, string? trustedActor = null,
        CancellationToken cancellationToken = default)
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
        var roomKey = RoomKey(newState.Definition.LobbyId);
        var globalKey = GlobalIndexKey;
        var creatorKey = CreatorIndexKey(updated.CreatorId);
        // Mutation and read-only verification share the SAME bounded wait;
        // a falsely acknowledged CAS cannot publish an invented roster.
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var result = (long)await database.ScriptEvaluateAsync(
                CompareExchangeScript,
                [roomKey, globalKey, creatorKey,
                 RevokedGuestKey(trustedActor ?? "administrative")],
                [expected.RawBytes, bytes, DeadlineMilliseconds(updated),
                 IndexTtlMilliseconds(), updated.QuotaTracked ? 1 : 0,
                 trustedActor is null ? 0 : 1])
            .WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        if (result == -1)
            throw new UnauthorizedAccessException("Lobby unavailable.");
        if (result == -2)
            throw new LobbyMutationUnavailableException();
        if (result is not (0 or 1))
            throw new InvalidDataException("Invalid Redis lobby result.");
        if (result == 0)
            return false;
        var confirmed = (long)await database.ScriptEvaluateAsync(
                ConfirmLobbyCasScript,
                [roomKey, globalKey, creatorKey],
                [bytes, updated.QuotaTracked ? 1 : 0])
            .WaitAsync(timeout.Token);
        if (confirmed != 1)
            throw new LobbyMutationUnavailableException();
        timeout.Token.ThrowIfCancellationRequested();
        return true;
    }

    public async Task<bool> DeleteIfCurrentAsync(
        Stored expected, string? trustedActor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateExpected(expected);
        var roomKey = RoomKey(expected.Snapshot.State.Definition.LobbyId);
        var globalKey = GlobalIndexKey;
        var creatorKey = CreatorIndexKey(expected.Snapshot.CreatorId);

        // The Lua may have completed on Redis even if its response was
        // lost. Never retry an uncertain mutation; after a positive result,
        // verify room/index ABSENCE before declaring a completed logout or
        // allowing the recovery worker to acknowledge its expiry work.
        // The delete and read-only check share the same 2s deadline.
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var result = (long)await database.ScriptEvaluateAsync(
                DeleteIfCurrentScript,
                [roomKey, globalKey, creatorKey,
                 RevokedGuestKey(trustedActor ?? "administrative")],
                [expected.RawBytes, expected.Snapshot.QuotaTracked ? 1 : 0,
                 trustedActor is null ? 0 : 1])
            .WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        if (result == -1)
            throw new UnauthorizedAccessException("Lobby unavailable.");
        if (result == -2)
            throw new LobbyMutationUnavailableException();
        if (result is not (0 or 1))
            throw new InvalidDataException("Invalid Redis lobby result.");
        if (result == 0)
            return false; // Actual CAS/version conflict: no delete to verify.

        var confirmed = (long)await database.ScriptEvaluateAsync(
                ConfirmLobbyDeleteScript,
                [roomKey, globalKey, creatorKey],
                [expected.Snapshot.QuotaTracked ? 1 : 0])
            .WaitAsync(timeout.Token);
        if (confirmed != 1)
            throw new LobbyMutationUnavailableException();
        timeout.Token.ThrowIfCancellationRequested();
        return true;
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
    public async Task<IReadOnlyList<Stored>> GetIndexedRoomsAsync(
        CancellationToken cancellationToken = default)
    {
        // Global creation is capped at MaxRooms. Read at most one extra
        // indexed member to detect oversized/legacy indices. ZRANGE stop
        // rank is inclusive (0..256). Lua replaces oversized member bytes
        // with an invalid opaque marker before any network transfer.
        var members = (RedisResult[]?)await AwaitRedisAsync(
            () => database.ScriptEvaluateAsync(
                BoundedIndexScanScript, [GlobalIndexKey],
                [GuestLobbyCommands.MaxRooms, _prefix.Length + "room:".Length + 64]),
            cancellationToken) ?? throw new LobbySnapshotUnavailableException();
        if (members.Length > GuestLobbyCommands.MaxRooms)
            throw new LobbySnapshotUnavailableException();

        // Validate the ENTIRE index before any GET, so a corrupted member
        // cannot cause even a partial room read or successful cleanup/ack.
        var keys = new List<string>(members.Length);
        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = member?.ToString() ?? string.Empty;
            if (!IsIndexedRoomKey(key))
                throw new LobbySnapshotUnavailableException();
            keys.Add(key);
        }

        var result = new List<Stored>(keys.Count);
        // Do not issue 256 sequential Redis roundtrips during a guest expiry
        // sweep. A fixed window bounds both concurrent commands and buffered
        // snapshot bytes (at most 8 * 32 KiB), while preserving index order.
        // Crucially this method returns NOTHING until every window succeeds:
        // later corrupt records cannot authorize a partial guest cleanup.
        for (var start = 0; start < keys.Count; start += IndexedReadWindowSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(IndexedReadWindowSize, keys.Count - start);
            var pending = new Task<Stored?>[count];
            for (var i = 0; i < count; i++)
                pending[i] = ReadIndexedRoomAsync(keys[start + i], cancellationToken);

            foreach (var room in await Task.WhenAll(pending))
            {
                if (room is not null)
                    result.Add(room);
            }
        }
        return result;
    }

    private async Task<Stored?> ReadIndexedRoomAsync(
        string key, CancellationToken cancellationToken)
    {
        var raw = await ReadBoundedSnapshotBytesAsync(
            (RedisKey)key, cancellationToken);
        if (raw.IsNull)
            return null; // Redis expiry can race this maintenance scan.

        var bytes = (byte[])raw!;
        if (bytes.Length is 0 or > RedisLobbySnapshotCodec.MaxBytes)
            throw new LobbySnapshotUnavailableException();

        LobbySnapshot snapshot;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("LobbyId", out var lobbyId) ||
                lobbyId.ValueKind != JsonValueKind.String)
                throw new LobbySnapshotUnavailableException();

            var id = lobbyId.GetString();
            if (string.IsNullOrWhiteSpace(id) ||
                !string.Equals(key, RoomKey(id).ToString(),
                    StringComparison.Ordinal))
                throw new LobbySnapshotUnavailableException();

            snapshot = RedisLobbySnapshotCodec.Decode(bytes, id);
            if (!snapshot.QuotaTracked)
                throw new LobbySnapshotUnavailableException();
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            // Indexed bytes are untrusted just like member roster reads.
            // Do not expose keys, room IDs, JSON fragments or exceptions.
            // The caller must retry cleanup; never silently skip/ack.
            throw new LobbySnapshotUnavailableException();
        }

        return VerifiedStoredRemaining(snapshot) > TimeSpan.Zero
            ? new Stored(snapshot, bytes)
            : null;
    }

    private bool IsIndexedRoomKey(string key)
    {
        var roomPrefix = _prefix + "room:";
        return key.Length == roomPrefix.Length + 64 &&
               key.StartsWith(roomPrefix, StringComparison.Ordinal) &&
               key.AsSpan(roomPrefix.Length).IndexOfAnyExcept(
                   "0123456789abcdef") < 0;
    }

    // V2 JSON can be structurally valid while claiming a future activity
    // time. Never make its roster available or start administrative cleanup.
    private TimeSpan VerifiedStoredRemaining(LobbySnapshot snapshot)
    {
        var remaining = Remaining(snapshot);
        if (remaining > _timeout)
            throw new LobbySnapshotUnavailableException();
        return remaining;
    }

    private TimeSpan Remaining(LobbySnapshot snapshot)
    {
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        // Subtract instants before adding the timeout. DateTimeOffset.MaxValue
        // must not overflow the deadline calculation on corrupt Redis input.
        return snapshot.LastActivityUtc - clock.GetUtcNow() + _timeout;
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
