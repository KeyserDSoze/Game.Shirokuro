using System.Security.Cryptography;
using System.Text;
using StackExchange.Redis;

namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Atomic single-room Redis storage foundation (#34).
/// NOT wired into the HTTP/SignalR coordinator: cross-room quotas, member
/// authorization and cleanup require their own distributed transactions.
/// </summary>
public sealed class RedisLobbySnapshotStore(
    IDatabase database, TimeProvider clock, TimeSpan? inactivityTimeout = null)
{
    private const string KeyPrefix = "shirokuro:lobby:v1:";
    private readonly TimeSpan _timeout = inactivityTimeout ??
        TimeSpan.FromMinutes(30);

    // Compare the complete stored snapshot, not just its revision. This
    // detects concurrent authorized heartbeat/TTL refreshes whose revision
    // is intentionally unchanged, and protects against stale deletes.
    private const string CompareExchangeScript = """
        local current = redis.call('GET', KEYS[1])
        if not current or current ~= ARGV[1] then return 0 end
        redis.call('PSETEX', KEYS[1], ARGV[3], ARGV[2])
        return 1
        """;

    private const string DeleteIfCurrentScript = """
        local current = redis.call('GET', KEYS[1])
        if not current or current ~= ARGV[1] then return 0 end
        return redis.call('DEL', KEYS[1])
        """;

    public sealed record Stored(LobbySnapshot Snapshot, byte[] RawBytes);

    public async Task<bool> CreateAsync(LobbySnapshot snapshot)
    {
        var ttl = Remaining(snapshot);
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(snapshot),
                "Cannot create expired room.");
        return await database.StringSetAsync(
            RoomKey(snapshot.State.Definition.LobbyId),
            RedisLobbySnapshotCodec.Encode(snapshot),
            expiry: ttl, when: When.NotExists);
    }

    /// <summary>
    /// Pure read, never refreshing Redis TTL or exposing nonmember state
    /// through an HTTP endpoint. Caller must authorize before projection.
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
            : null; // Redis's TTL is a second, independent expiry guard.
    }

    /// <summary>
    /// For an already-authorized domain mutation, save iff nothing has
    /// changed since ReadAsync, including original activity timestamp.
    /// No retry here: the caller must re-read and reauthorize on conflict.
    /// </summary>
    public async Task<bool> CompareExchangeAsync(Stored expected, LobbySnapshot updated)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(updated);
        if (!string.Equals(expected.Snapshot.State.Definition.LobbyId,
                updated.State.Definition.LobbyId, StringComparison.Ordinal) ||
            updated.State.Revision < expected.Snapshot.State.Revision ||
            updated.State.Revision > expected.Snapshot.State.Revision + 1 ||
            updated.LastActivityUtc < expected.Snapshot.LastActivityUtc)
            throw new ArgumentException("Invalid atomic lobby update.");

        var ttl = Remaining(updated);
        if (ttl <= TimeSpan.Zero)
            return false;
        var result = await database.ScriptEvaluateAsync(
            CompareExchangeScript, [RoomKey(updated.State.Definition.LobbyId)],
            [expected.RawBytes, RedisLobbySnapshotCodec.Encode(updated),
             (long)Math.Ceiling(ttl.TotalMilliseconds)]);
        return (long)result == 1;
    }

    public async Task<bool> DeleteIfCurrentAsync(Stored expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var result = await database.ScriptEvaluateAsync(
            DeleteIfCurrentScript,
            [RoomKey(expected.Snapshot.State.Definition.LobbyId)],
            [expected.RawBytes]);
        return (long)result == 1;
    }

    private TimeSpan Remaining(LobbySnapshot snapshot)
    {
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout));
        return snapshot.LastActivityUtc + _timeout - clock.GetUtcNow();
    }

    private static RedisKey RoomKey(string lobbyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lobbyId);
        // Room invites stay opaque: do not reveal bearer codes in Redis keys.
        return KeyPrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(lobbyId)));
    }
}
