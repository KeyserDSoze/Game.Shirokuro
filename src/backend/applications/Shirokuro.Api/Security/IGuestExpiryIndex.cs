namespace Shirokuro.Api.Security;

/// <summary>
/// Durable server-only guest expiry maintenance. Implementations must never
/// expose these identities through HTTP, logs or Redis keys.
/// </summary>
public interface IGuestExpiryIndex
{
    // The optional server-issued token is never written in plaintext. Redis
    // implementations seal its one-way digest inside the expiry work item,
    // allowing a targeted, authenticated cleanup after an RDB rollback.
    Task ScheduleAsync(string trustedPlayerId, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default,
        string? trustedSessionToken = null);

    /// <summary>
    /// Persist an already-due cleanup AFTER an authoritative logout revokes
    /// the guest. Returns an opaque member for optional acknowledgement if
    /// synchronous lobby cleanup succeeds. Do not call before revocation.
    /// </summary>
    Task<string> ScheduleRevokedAsync(string trustedPlayerId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
        CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(string protectedMember,
        CancellationToken cancellationToken = default);
}

/// <summary>Opaque persisted work item and trusted server-side identity.</summary>
/// <summary>
/// A member whose protected payload cannot be authenticated or validated
/// must never be treated as a player, fenced, cleaned up or acknowledged.
/// Keep its opaque member queued for operator key-ring recovery.
/// </summary>
public sealed record DueGuestExpiry(
    string ProtectedMember, string PlayerId, bool IsUnreadable = false,
    string? SessionTokenDigest = null, bool IsPreCutover = false)
{
    public static DueGuestExpiry Unreadable(string protectedMember) =>
        new(protectedMember, string.Empty, IsUnreadable: true);
}
