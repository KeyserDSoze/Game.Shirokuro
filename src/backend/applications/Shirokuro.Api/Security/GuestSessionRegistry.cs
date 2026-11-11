using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Shirokuro.Api.Security;

/// <summary>
/// A signed cookie contains a random session ID; in Production/Staging
/// Redis is authoritative for validity/revocation across API instances.
/// Local tracking is retained only for pre-match expiry notifications;
/// it is NOT a shared lobby membership or cross-node cleanup service.
/// </summary>
public sealed class GuestSessionRegistry(TimeProvider clock, IDistributedCache? sharedCache = null)
{
    private sealed record Entry(string PlayerId, DateTimeOffset ExpiresUtc);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingExpiredPlayerIds = new(StringComparer.Ordinal);
    private const string KeyPrefix = "guest-session:";
    private const int ValueLength = sizeof(long) + 32; // UTC ticks + SHA-256(player ID)

    /// <summary>Registers a random, server-issued token for local Development.</summary>
    public string Register(string trustedPlayerId, DateTimeOffset expiresUtc)
    {
        if (sharedCache is not null)
            throw new InvalidOperationException("Shared session registration must be async.");
        ValidateRegistration(trustedPlayerId, expiresUtc);
        var token = NewToken();
        lock (_gate)
        {
            PruneExpiredCore();
            _active.Add(token, new Entry(trustedPlayerId, expiresUtc));
        }
        return token;
    }

    public async Task<string> RegisterAsync(
        string trustedPlayerId, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        if (sharedCache is null)
            return Register(trustedPlayerId, expiresUtc);

        ValidateRegistration(trustedPlayerId, expiresUtc);
        var token = NewToken();
        await sharedCache.SetAsync(
            SharedKey(token), Encode(trustedPlayerId, expiresUtc),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiresUtc },
            cancellationToken);

        // Do not issue local session state until the shared store accepted
        // the ticket. The worker still knows which identities originated here.
        lock (_gate)
        {
            PruneExpiredCore();
            _active.Add(token, new Entry(trustedPlayerId, expiresUtc));
        }
        return token;
    }

    public bool IsActive(ClaimsPrincipal? principal)
    {
        if (sharedCache is not null)
            return false; // Never mistake the local tracking set for shared authority.
        if (!TryClaims(principal, out var token, out var playerId))
            return false;
        lock (_gate)
            return _active.TryGetValue(token, out var entry) &&
                   entry.ExpiresUtc > clock.GetUtcNow() &&
                   string.Equals(entry.PlayerId, playerId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Authenticated HTTP requests and every SignalR hub invocation must
    /// consult the shared authority; no positive per-node cache is permitted.
    /// A shared-store outage denies access instead of trusting local memory.
    /// </summary>
    public async Task<bool> IsActiveAsync(
        ClaimsPrincipal? principal, CancellationToken cancellationToken = default)
    {
        if (sharedCache is null)
            return IsActive(principal);
        if (!TryClaims(principal, out var token, out var playerId))
            return false;
        try
        {
            var value = await sharedCache.GetAsync(SharedKey(token), cancellationToken);
            return Matches(value, playerId, clock.GetUtcNow());
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Availability is not permission: a Redis outage is fail-closed.
            return false;
        }
    }

    /// <summary>Development-only revocation. Shared revocation is async.</summary>
    public bool Revoke(ClaimsPrincipal? principal)
    {
        if (sharedCache is not null)
            return false;
        if (!TryClaims(principal, out var token, out var playerId))
            return false;
        lock (_gate)
        {
            if (!_active.TryGetValue(token, out var entry) ||
                !string.Equals(entry.PlayerId, playerId, StringComparison.Ordinal))
                return false;
            return _active.Remove(token);
        }
    }

    public async Task<bool> RevokeAsync(
        ClaimsPrincipal? principal, CancellationToken cancellationToken = default)
    {
        if (sharedCache is null)
            return Revoke(principal);
        if (!TryClaims(principal, out var token, out var playerId))
            return false;

        var key = SharedKey(token);
        var value = await sharedCache.GetAsync(key, cancellationToken);
        if (!Matches(value, playerId, clock.GetUtcNow()))
            return false;

        // Tokens are never reused: removing their unique key invalidates any
        // later validation in every node. Concurrent already-in-flight checks
        // may finish before this deletion, as with local revocation.
        await sharedCache.RemoveAsync(key, cancellationToken);
        lock (_gate)
            _active.Remove(token);
        return true;
    }

    /// <summary>
    /// Retry a trusted expiry cleanup after a temporary Redis outage.
    /// No request or untrusted actor can enqueue maintenance identities.
    /// </summary>
    public void RequeueExpiredPlayerIds(IEnumerable<string> trustedPlayerIds)
    {
        lock (_gate)
            foreach (var playerId in trustedPlayerIds)
                _pendingExpiredPlayerIds.Add(playerId);
    }

    public int StoredSessionCount
    {
        get
        {
            lock (_gate)
                return _active.Count;
        }
    }

    public int PruneExpired()
    {
        lock (_gate)
            return PruneExpiredCore();
    }

    /// <summary>
    /// Session expiry cleanup for rooms originated in this process only.
    /// Remote-node logout does not yet broadcast local roster cleanup (#21).
    /// </summary>
    public IReadOnlyList<string> DrainExpiredPlayerIds()
    {
        lock (_gate)
        {
            PruneExpiredCore();
            var activeIds = _active.Values
                .Select(entry => entry.PlayerId)
                .ToHashSet(StringComparer.Ordinal);
            var expired = _pendingExpiredPlayerIds
                .Where(playerId => !activeIds.Contains(playerId))
                .ToArray();
            _pendingExpiredPlayerIds.Clear();
            return expired;
        }
    }

    private int PruneExpiredCore()
    {
        var now = clock.GetUtcNow();
        var expired = _active
            .Where(pair => pair.Value.ExpiresUtc <= now)
            .ToArray();
        foreach (var (token, entry) in expired)
        {
            _active.Remove(token);
            _pendingExpiredPlayerIds.Add(entry.PlayerId);
        }
        return expired.Length;
    }

    private static bool TryClaims(ClaimsPrincipal? principal,
        out string token, out string playerId)
    {
        token = "";
        playerId = "";
        if (!GuestSessions.IsGuest(principal))
            return false;

        var identity = principal!.Identities.Single(identity => identity.IsAuthenticated);
        var tokens = identity.FindAll(GuestSessions.SessionIdClaim).ToArray();
        if (tokens.Length != 1 || tokens[0].Value.Length != 32 ||
            tokens[0].Value.Any(c => !char.IsAsciiHexDigitLower(c)))
            return false;

        var id = AuthenticatedPlayerIdentity.FromPrincipal(principal);
        if (id is null)
            return false;
        token = tokens[0].Value;
        playerId = id;
        return true;
    }

    private void ValidateRegistration(string playerId, DateTimeOffset expiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerId);
        if (expiresUtc <= clock.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresUtc));
    }

    private static string NewToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string SharedKey(string token) =>
        KeyPrefix + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    private static byte[] Encode(string playerId, DateTimeOffset expiresUtc)
    {
        var value = new byte[ValueLength];
        BinaryPrimitives.WriteInt64BigEndian(value.AsSpan(0, 8), expiresUtc.UtcTicks);
        SHA256.HashData(Encoding.UTF8.GetBytes(playerId))
            .CopyTo(value, sizeof(long));
        return value;
    }

    private static bool Matches(byte[]? value, string playerId, DateTimeOffset now)
    {
        if (value is null || value.Length != ValueLength)
            return false;
        var ticks = BinaryPrimitives.ReadInt64BigEndian(value.AsSpan(0, 8));
        if (ticks <= now.UtcTicks)
            return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(playerId));
        return CryptographicOperations.FixedTimeEquals(
            value.AsSpan(sizeof(long), hash.Length), hash);
    }
}
