namespace Shirokuro.Api.Security;

/// <summary>
/// Durable server-only guest expiry maintenance. Implementations must never
/// expose these identities through HTTP, logs or Redis keys.
/// </summary>
public interface IGuestExpiryIndex
{
    Task ScheduleAsync(string trustedPlayerId, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
        CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(string protectedMember,
        CancellationToken cancellationToken = default);
}

/// <summary>Opaque persisted work item and trusted server-side identity.</summary>
public sealed record DueGuestExpiry(string ProtectedMember, string PlayerId);
