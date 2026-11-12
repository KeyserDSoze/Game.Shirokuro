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

    private const string Script = """
        local count = redis.call('GET', KEYS[1])
        if not count then
            redis.call('PSETEX', KEYS[1], ARGV[1], 1)
            return {1, tonumber(ARGV[1])}
        end
        local ttl = redis.call('PTTL', KEYS[1])
        if tonumber(count) >= tonumber(ARGV[2]) then
            return {0, ttl}
        end
        redis.call('INCR', KEYS[1])
        return {1, ttl}
        """;

    public async Task<GuestMutationDecision> AcquireAsync(
        GuestMutationRateLimit policy, IPAddress? trustedRemoteIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(policy, trustedRemoteIp);
        // A single Lua script serializes every participating API/node's
        // check and increment. The bounded wait honours HTTP disconnects.
        // Abandoning a client wait does NOT necessarily cancel a Lua script
        // already accepted by Redis; do not retry, or assume quota was unused.
        // No local permits/fallback are ever granted on an unknown result.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        var redisResult = await database.ScriptEvaluateAsync(
                Script, [key], [WindowMilliseconds, policy.PermitLimit])
            .WaitAsync(timeout.Token);
        var fields = (RedisResult[])redisResult!;
        if (fields.Length != 2)
            throw new InvalidDataException("Invalid distributed limit result.");
        var status = (long)fields[0];
        var remaining = (long)fields[1];
        if (status is not (0 or 1) || remaining < 0 || remaining > WindowMilliseconds)
            throw new InvalidDataException("Invalid distributed limit result.");
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
        return "shirokuro:ratelimit:v1:" + scope + ":" + digest;
    }
}
