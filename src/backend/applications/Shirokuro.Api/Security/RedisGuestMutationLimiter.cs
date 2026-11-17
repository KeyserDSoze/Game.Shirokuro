using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using StackExchange.Redis;

namespace Shirokuro.Api.Security;

/// <summary>
/// Server-only endpoint metadata; never derived from the body, cookie or
/// request URL. The technical anti-abuse thresholds are not gameplay rules.
/// </summary>
public enum GuestMutationKind { SessionIssuance, RoomCreation }

public sealed record GuestMutationRateLimit(GuestMutationKind Kind)
{
    public int PermitLimit => Kind switch
    {
        GuestMutationKind.SessionIssuance => 12,
        GuestMutationKind.RoomCreation => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };
}

public readonly record struct GuestMutationDecision(bool Allowed, long RetryAfterMilliseconds);

public interface IGuestMutationLimiter
{
    Task<GuestMutationDecision> AcquireAsync(
        GuestMutationRateLimit policy, IPAddress? trustedRemoteIp,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Redis single-key Lua limiter. Never trusts X-Forwarded-For directly:
/// RemoteIpAddress must already be resolved by trusted-proxy forwarding.
/// This service has no process-local allowance or positive cache.
/// </summary>
public sealed class RedisGuestMutationLimiter(IDatabase database) : IGuestMutationLimiter
{
    public const long WindowMilliseconds = 60_000;
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    // The count and one receipt per successful admission share a Redis
    // hash and a single fixed TTL. A positive Lua reply is only provisional:
    // a read-only Lua verification must certify the full bucket invariant,
    // not just the receipt's presence (#185). Prior proofs also need\n    // canonical names and intact values (#186).
    private const string Script = """
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'none' and kind ~= 'hash' then return {-1, 0, 0} end
        local count = nil
        if kind == 'hash' then
            count = redis.call('HGET', KEYS[1], 'count')
            if not count then return {-1, 0, 0} end
        end
        if not count then
            redis.call('HSET', KEYS[1], 'count', '1', ARGV[3], '1')
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
            return {1, tonumber(ARGV[1]), 1}
        end
        local ttl = redis.call('PTTL', KEYS[1])
        -- Validate persisted shape BEFORE any write. The receipt cardinality
        -- must equal the admissions count. A malformed bucket is not
        -- rewritten, replenished, nor silently converted to a new window.
        if ttl < 1 or ttl > tonumber(ARGV[1]) or
           #count > #ARGV[2] or
           not string.match(count, '^[1-9][0-9]*$') then
            return {-1, 0, 0}
        end
        local current = tonumber(count)
        if not current or current > tonumber(ARGV[2]) or
           redis.call('HLEN', KEYS[1]) ~= current + 1 then
            return {-1, 0, 0}
        end
        -- Only up to 12 receipts plus the count field may be present.
        -- Reject malformed prior receipts before modifying this bucket.
        local entries = redis.call('HGETALL', KEYS[1])
        for i = 1, #entries, 2 do
            local field, value = entries[i], entries[i + 1]
            if field ~= 'count' and
               (#field ~= 38 or
                not string.match(field, '^proof:[0-9a-f]+$') or
                value ~= '1') then
                return {-1, 0, 0}
            end
        end
        if current >= tonumber(ARGV[2]) then
            return {0, ttl, 0}
        end
        redis.call('HINCRBY', KEYS[1], 'count', 1)
        redis.call('HSET', KEYS[1], ARGV[3], '1')
        return {1, ttl, current + 1}
        """;

    // Second, read-only Lua operation on the SAME Redis connector.
    // Check the unique receipt AND counter, receipt cardinality and TTL
    // in one atomic read. Count >= issuedOrdinal accommodates unrelated
    // concurrent admissions between the two scripts; never accept a
    // counter that moved backwards or exceeded the server's policy limit.
    private const string ConfirmPermitScript = """
        if redis.call('TYPE', KEYS[1]).ok ~= 'hash' then return 0 end
        local count = redis.call('HGET', KEYS[1], 'count')
        if not count or #count > #ARGV[3] or
           not string.match(count, '^[1-9][0-9]*$') then return 0 end
        local current = tonumber(count)
        if not current or current < tonumber(ARGV[2]) or
           current > tonumber(ARGV[3]) then return 0 end
        if redis.call('HLEN', KEYS[1]) ~= current + 1 then return 0 end
        -- Recheck every prior receipt atomically with the new one and
        -- counter/TTL, even if other nodes admitted players in between.
        local entries = redis.call('HGETALL', KEYS[1])
        for i = 1, #entries, 2 do
            local field, value = entries[i], entries[i + 1]
            if field ~= 'count' and
               (#field ~= 38 or
                not string.match(field, '^proof:[0-9a-f]+$') or
                value ~= '1') then
                return 0
            end
        end
        local ttl = redis.call('PTTL', KEYS[1])
        if ttl < 1 or ttl > tonumber(ARGV[4]) then return 0 end
        if redis.call('HGET', KEYS[1], ARGV[1]) ~= '1' then return 0 end
        return 1
        """;

    public async Task<GuestMutationDecision> AcquireAsync(
        GuestMutationRateLimit policy, IPAddress? trustedRemoteIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(policy, trustedRemoteIp);
        var receipt = "proof:" + Convert.ToHexStringLower(
            RandomNumberGenerator.GetBytes(16));

        // Both Lua calls share ONE deadline of 2 seconds. A command that
        // completed after client cancellation may still have consumed quota;
        // never retry it or assume the permit was not charged.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var redisResult = await database.ScriptEvaluateAsync(
                Script, [key], [WindowMilliseconds, policy.PermitLimit, receipt])
            .WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        var fields = (RedisResult[])redisResult!;
        if (fields.Length != 3)
            throw new InvalidDataException("Invalid distributed limit result.");
        var status = (long)fields[0];
        var remaining = (long)fields[1];
        var issuedOrdinal = (long)fields[2];
        if (status is not (0 or 1) || remaining < 0 ||
            remaining > WindowMilliseconds ||
            (status == 0 && issuedOrdinal != 0) ||
            (status == 1 && (issuedOrdinal < 1 ||
                             issuedOrdinal > policy.PermitLimit)))
            throw new InvalidDataException("Invalid distributed limit result.");

        if (status == 1)
        {
            var confirmed = (long)await database.ScriptEvaluateAsync(
                    ConfirmPermitScript, [key],
                    [receipt, issuedOrdinal, policy.PermitLimit,
                     WindowMilliseconds])
                .WaitAsync(timeout.Token);
            if (confirmed != 1)
                throw new InvalidDataException("Distributed permit not confirmed.");
        }
        timeout.Token.ThrowIfCancellationRequested();
        return new GuestMutationDecision(status == 1, remaining);
    }

    public static string Key(GuestMutationRateLimit policy, IPAddress? remoteIp)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _ = policy.PermitLimit; // Fail closed on unexpected enum variants.
        var canonicalIp = remoteIp?.IsIPv4MappedToIPv6 == true
            ? remoteIp.MapToIPv4() : remoteIp;
        var address = canonicalIp?.GetAddressBytes() ?? [0xff];
        // A native IPv6 device can rotate the lower 64 interface-ID bits
        // without changing its delegated network. Charge the stable /64
        // instead; keep native/mapped IPv4 and missing-IP keys unchanged.
        // Only the SHA-256 digest, never the raw prefix, enters Redis.
        if (canonicalIp?.AddressFamily == AddressFamily.InterNetworkV6)
            address = address[..8];
        var digest = Convert.ToHexStringLower(SHA256.HashData(address));
        var scope = policy.Kind == GuestMutationKind.SessionIssuance
            ? "sessions" : "rooms";
        return "shirokuro:ratelimit:v2:" + scope + ":" + digest;
    }
}
