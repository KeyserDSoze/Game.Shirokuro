using System.Security.Claims;
using System.Security.Cryptography;

namespace Shirokuro.Api.Security;

/// <summary>
/// Volatile, per-process, deny-by-default guest session revocation store.
/// Cookies are still signed and encrypted by ASP.NET Core Data Protection.
/// This must become a shared persistent store before multi-instance deployment.
/// </summary>
public sealed class GuestSessionRegistry(TimeProvider clock)
{
    private sealed record Entry(string PlayerId, DateTimeOffset ExpiresUtc);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);

    // Both the periodic worker and opportunistic registration pruning may
    // discover expiry. Retain only the trusted player ID until the worker can
    // clean stale room membership; never expose IDs through HTTP or logs.
    private readonly HashSet<string> _pendingExpiredPlayerIds = new(StringComparer.Ordinal);

    /// <summary>Registers only cryptographically random, server-issued tokens.</summary>
    public string Register(string trustedPlayerId, DateTimeOffset expiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        if (expiresUtc <= clock.GetUtcNow())
            throw new ArgumentOutOfRangeException(nameof(expiresUtc));

        var sessionId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            PruneExpiredCore();
            _active.Add(sessionId, new Entry(trustedPlayerId, expiresUtc));
        }
        return sessionId;
    }

    public bool IsActive(ClaimsPrincipal? principal)
    {
        if (!GuestSessions.IsGuest(principal))
            return false;

        var authenticated = principal!.Identities.Single(identity => identity.IsAuthenticated);
        var tokens = authenticated.FindAll(GuestSessions.SessionIdClaim).ToArray();
        if (tokens.Length != 1)
            return false;

        var trustedPlayerId = AuthenticatedPlayerIdentity.FromPrincipal(principal);
        if (trustedPlayerId is null)
            return false;

        lock (_gate)
        {
            return _active.TryGetValue(tokens[0].Value, out var entry) &&
                entry.ExpiresUtc > clock.GetUtcNow() &&
                string.Equals(entry.PlayerId, trustedPlayerId, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Called before SignOutAsync; a copied cookie immediately loses server
    /// access even if its protected ticket has not expired.
    /// </summary>
    public bool Revoke(ClaimsPrincipal? principal)
    {
        if (!GuestSessions.IsGuest(principal))
            return false;

        var authenticated = principal!.Identities.Single(identity => identity.IsAuthenticated);
        var tokens = authenticated.FindAll(GuestSessions.SessionIdClaim).ToArray();
        if (tokens.Length != 1)
            return false;

        var playerId = AuthenticatedPlayerIdentity.FromPrincipal(principal);
        lock (_gate)
        {
            if (!_active.TryGetValue(tokens[0].Value, out var entry) ||
                !string.Equals(entry.PlayerId, playerId, StringComparison.Ordinal))
                return false;

            return _active.Remove(tokens[0].Value);
        }
    }

    /// <summary>Server-only diagnostic count, never a public API value.</summary>
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
    /// Server-only, one-shot notification of identities whose sessions expired.
    /// Pruning during Register or PruneExpired also queues those identities.
    /// If another live session for the same trusted player exists, keep their
    /// membership: session expiry never revokes a still-active identity.
    /// </summary>
    public IReadOnlyList<string> DrainExpiredPlayerIds()
    {
        lock (_gate)
        {
            PruneExpiredCore();
            var activeIds = _active.Values
                .Select(entry => entry.PlayerId)
                .ToHashSet(StringComparer.Ordinal);
            var expiredPlayers = _pendingExpiredPlayerIds
                .Where(playerId => !activeIds.Contains(playerId))
                .ToArray();
            _pendingExpiredPlayerIds.Clear();
            return expiredPlayers;
        }
    }

    private int PruneExpiredCore()
    {
        var now = clock.GetUtcNow();
        var expired = _active
            .Where(pair => pair.Value.ExpiresUtc <= now)
            .ToArray();
        foreach (var (sessionId, entry) in expired)
        {
            _active.Remove(sessionId);
            _pendingExpiredPlayerIds.Add(entry.PlayerId);
        }
        return expired.Length;
    }
}

