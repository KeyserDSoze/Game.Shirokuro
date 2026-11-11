namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Redis-backed, server-only guest mutation barrier. The caller MUST revoke
/// the authoritative guest ticket (or verify its natural expiry) BEFORE
/// fencing. The key is hashed and shares the lobby Redis Cluster hash slot,
/// so each Redis Lua create/update/delete can check it atomically.
/// </summary>
public interface IGuestRevocationFence
{
    Task FenceGuestAsync(string trustedGuestId,
        CancellationToken cancellationToken = default);
}
