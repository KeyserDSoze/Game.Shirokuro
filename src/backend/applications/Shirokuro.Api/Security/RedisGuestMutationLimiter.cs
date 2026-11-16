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

    // An admission carries a random receipt in the SAME atomic Redis hash
    // as its fixed-window count. Unlike a last-writer marker, unique receipt
    // fields survive other nodes' concurrent increments until TTL.
    // V2 avoids interpreting pre-existing v1 string counters as hashes.
    private const string Script = """
        local kind = redis.call('TYPE', KEYS[1]).ok
        if kind ~= 'none' and kind ~= 'hash' then return {-1, 0} end
        local count = nil
        if kind == 'hash' then
            count = redis.call('HGET', KEYS[1], 'count')
            if not count then return {-1, 0} end
        end
        if not count then
            redis.call('HSET', KEYS[1], 'count', '1', ARGV[3], '1')
            redis.call('PEXPIRE', KEYS[1], ARGV[1])
            return {1, tonumber(ARGV[1])}
        end
        local ttl = redis.call('PTTL', KEYS[1])
        -- Corrupt persisted counters/TTLs must never grant permits.
        -- Do not reset, renew or increment a suspicious quota bucket.
        if ttl < 1 or ttl > tonumber(ARGV[1]) or
           #count > #ARGV[2] or
           not string.match(count, '^[1-9][0-9]*$') then
            return {-1, 0}
        end
        local current = tonumber(count)
        if not current or current > tonumber(ARGV[2]) then
            return {-1, 0}
        end
        if current >= tonumber(ARGV[2]) then
            return {0, ttl}
        end
        redis.call('HINCRBY', KEYS[1], 'count', 1)
        redis.call('HSET', KEYS[1], ARGV[3], '1')
        return {1, ttl}
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

        // A Lua 1 is only an acknowledgement; a lost mutation must not
        // admit extra guests/rooms. Read back the unique receipt on the same
        // Redis connection before returning a permit. A single 2s deadline
        // includes BOTH operations, without retrying a possibly applied Lua.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var redisResult = await database.ScriptEvaluateAsync(
                Script, [key], [WindowMilliseconds, policy.PermitLimit, receipt])
            .WaitAsync(timeout.Token);
        timeout.Token.ThrowIfCancellationRequested();
        var fields = (RedisResult[])redisResult!;
        if (fields.Length != 2)
            throw new InvalidDataException("Invalid distributed limit result.");
        var status = (long)fields[0];
        var remaining = (long)fields[1];
        if (status is not (0 or 1) || remaining < 0 || remaining > WindowMilliseconds)
            throw new InvalidDataException("Invalid distributed limit result.");

        if (status == 1)
        {
            var confirmed = await database.HashGetAsync(key, receipt)
                .WaitAsync(timeout.Token);
            if (!string.Equals((string?)confirmed, "1", StringComparison.Ordinal))
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
