using System.Buffers.Binary;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Distributed;

namespace Shirokuro.Api.Security;

// Never confuse an unavailable shared authority with a proven invalid
// ticket. Neither status grants access; only Active can authenticate.
public enum GuestSessionAuthorityStatus
{
    Active,
    Inactive,
    Unavailable
}

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
    // Bounded client wait; does not cancel a Redis command already delivered.
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    // Per-request HttpContext.Items sentinel. Never expose availability
    // details to the browser; the cookie challenge uses this only for 503.
    public static readonly object UnavailableRequestMarker = new();

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
        cancellationToken.ThrowIfCancellationRequested();
        if (sharedCache is null)
            return Register(trustedPlayerId, expiresUtc);

        ValidateRegistration(trustedPlayerId, expiresUtc);
        var token = NewToken();
        // An uncertain Redis write never issues a guest cookie or local
        // tracking entry. A delayed server-side SET can leave only an orphan
        // ticket with an absolute TTL, not an authorized browser session.
        await AwaitCacheAsync(sharedCache.SetAsync(
            SharedKey(token), Encode(trustedPlayerId, expiresUtc),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiresUtc },
            cancellationToken), cancellationToken);

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
        ClaimsPrincipal? principal, CancellationToken cancellationToken = default) =>
        await CheckAuthorityAsync(principal, cancellationToken) ==
            GuestSessionAuthorityStatus.Active;

    /// <summary>
    /// Cookie validation needs to distinguish a missing/revoked ticket from
    /// Redis being temporarily unable to verify it. Both reject access;
    /// only a definitively inactive ticket should trigger a 401 and client
    /// identity reset. The older boolean API stays fail-closed for SignalR.
    /// </summary>
    public async Task<GuestSessionAuthorityStatus> CheckAuthorityAsync(
        ClaimsPrincipal? principal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sharedCache is null)
            return IsActive(principal) ? GuestSessionAuthorityStatus.Active
                : GuestSessionAuthorityStatus.Inactive;
        if (!TryClaims(principal, out var token, out var playerId))
            return GuestSessionAuthorityStatus.Inactive;
        try
        {
            var value = await AwaitCacheAsync(
                sharedCache.GetAsync(SharedKey(token), cancellationToken),
                cancellationToken);
            return Matches(value, playerId, clock.GetUtcNow())
                ? GuestSessionAuthorityStatus.Active
                : GuestSessionAuthorityStatus.Inactive;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A Redis failure or command deadline is NOT proof of revocation.
            // Still deny access; never fall back to local session tracking.
            return GuestSessionAuthorityStatus.Unavailable;
        }
    }

    /// <summary>
    /// Delete precisely the session whose SHA-256 token digest was sealed by
    /// the server inside an authenticated expiry queue entry. Never accepts
    /// a raw browser token, never scans Redis and never deletes by player ID.
    /// This is called by recovery maintenance only after lobby fencing/CAS.
    /// </summary>
    public async Task RemoveRestoredSessionAsync(
        string trustedTokenDigest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sharedCache is null)
            throw new InvalidOperationException(
                "Distributed recovery requires the shared guest store.");
        if (trustedTokenDigest.Length != 64 ||
            trustedTokenDigest.Any(c =>
                c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid trusted session digest.",
                nameof(trustedTokenDigest));
        await AwaitCacheAsync(
            sharedCache.RemoveAsync(KeyPrefix + trustedTokenDigest,
                cancellationToken), cancellationToken);
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
        cancellationToken.ThrowIfCancellationRequested();
        if (sharedCache is null)
            return Revoke(principal);
        if (!TryClaims(principal, out var token, out var playerId))
            return false;

        var key = SharedKey(token);
        var value = await AwaitCacheAsync(
            sharedCache.GetAsync(key, cancellationToken), cancellationToken);
        if (!Matches(value, playerId, clock.GetUtcNow()))
            return false;

        // Tokens are never reused: removing their unique key invalidates any
        // later validation in every node. Concurrent already-in-flight checks
        // may finish before this deletion, as with local revocation.
        // A timeout cannot confirm revocation. If Redis has already deleted
        // the key, a subsequent request will fail auth; never report success
        // based on a timed-out RemoveAsync.
        await AwaitCacheAsync(sharedCache.RemoveAsync(key, cancellationToken),
            cancellationToken);
        lock (_gate)
            _active.Remove(token);
        return true;
    }

    private static async Task AwaitCacheAsync(
        Task operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        await operation.WaitAsync(timeout.Token);
    }

    private static async Task<T> AwaitCacheAsync<T>(
        Task<T> operation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        return await operation.WaitAsync(timeout.Token);
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
