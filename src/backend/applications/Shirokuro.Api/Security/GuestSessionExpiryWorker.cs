using Shirokuro.Application.Lobbies;
using Shirokuro.Api.Lobbies;

namespace Shirokuro.Api.Security;

/// <summary>
/// Prunes expired server-side guest tickets and their pre-match memberships.
/// Does not expose guest identities, invites or room rosters over HTTP/logs.
/// </summary>
public sealed class GuestSessionExpiryWorker(
    GuestSessionRegistry registry, InMemoryLobbyCoordinator coordinator,
    TimeProvider clock, IAsyncGuestLobby? lobby = null) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One deterministic maintenance pass, also used by fake-clock tests.
    /// This is not a user action and does not refresh surviving room TTLs.
    /// </summary>
    public int SweepOnce()
    {
        var removedMemberships = 0;
        foreach (var trustedPlayerId in registry.DrainExpiredPlayerIds())
            removedMemberships += coordinator.RemovePlayerFromAllRooms(trustedPlayerId);
        return removedMemberships;
    }

    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        if (lobby is null)
            return SweepOnce();

        var identities = registry.DrainExpiredPlayerIds();
        try
        {
            var removed = 0;
            foreach (var id in identities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                removed += await lobby.RemovePlayerFromAllRoomsAsync(id, cancellationToken);
            }
            return removed;
        }
        catch
        {
            // Best-effort retried on next sweep; a Redis outage must never
            // permanently discard expired identities from local maintenance.
            registry.RequeueExpiredPlayerIds(identities);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepOnceAsync(stoppingToken);
                }
                catch (Exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // Retry next sweep, no identifiers or invite details logged.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. No player or room identifiers in logs.
        }
    }
}
