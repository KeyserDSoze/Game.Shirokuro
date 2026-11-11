using Shirokuro.Application.Lobbies;
using Shirokuro.Api.Lobbies;

namespace Shirokuro.Api.Security;

/// <summary>
/// Prunes expired server-side guest tickets and their pre-match memberships.
/// Does not expose guest identities, invites or room rosters over HTTP/logs.
/// </summary>
public sealed class GuestSessionExpiryWorker(
    GuestSessionRegistry registry, InMemoryLobbyCoordinator coordinator,
    TimeProvider clock, IAsyncGuestLobby? lobby = null,
    IGuestExpiryIndex? expiryIndex = null) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// One deterministic maintenance pass, also used by fake-clock tests.
    /// This is not a user action and does not refresh surviving room TTLs.
    /// </summary>
    public int SweepOnce()
    {
        try
        {
            var removedMemberships = SweepLocalCore();
            MultiplayerMetrics.RecordExpirySweep(
                sharedRedis: false, success: true, removedMemberships);
            return removedMemberships;
        }
        catch
        {
            MultiplayerMetrics.RecordExpirySweep(
                sharedRedis: false, success: false, membershipsRemoved: 0);
            throw;
        }
    }

    private int SweepLocalCore()
    {
        var removed = 0;
        foreach (var playerId in registry.DrainExpiredPlayerIds())
            removed += coordinator.RemovePlayerFromAllRooms(playerId);
        return removed;
    }

    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        // The hosted worker uses this path. A failure must be visible to
        // operations without losing the durable retry/ack semantics.
        var sharedRedis = expiryIndex is not null;
        if (lobby is null && !sharedRedis)
            return SweepOnce(); // Counts a single local attempt.

        try
        {
            if (expiryIndex is not null)
            {
                // Shared encrypted Redis index survives an issuer-node crash.
                // Ack only AFTER idempotent lobby cleanup has succeeded.
                if (lobby is null)
                    throw new InvalidOperationException("Distributed cleanup needs a lobby.");
                var removed = 0;
                foreach (var item in await expiryIndex.ReadDueAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    removed += await lobby.RemovePlayerFromAllRoomsAsync(
                        item.PlayerId, cancellationToken);
                    await expiryIndex.AcknowledgeAsync(
                        item.ProtectedMember, cancellationToken);
                }
                registry.DrainExpiredPlayerIds();
                MultiplayerMetrics.RecordExpirySweep(
                    sharedRedis: true, success: true, removed);
                return removed;
            }

            var identities = registry.DrainExpiredPlayerIds();
            try
            {
                var removed = 0;
                foreach (var id in identities)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    removed += await lobby!.RemovePlayerFromAllRoomsAsync(
                        id, cancellationToken);
                }
                MultiplayerMetrics.RecordExpirySweep(
                    sharedRedis: false, success: true, removed);
                return removed;
            }
            catch
            {
                // No maintenance identity is lost on a transient local
                // failure; the metric below records the failed attempt.
                registry.RequeueExpiredPlayerIds(identities);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown is not an infrastructure failure.
            throw;
        }
        catch
        {
            MultiplayerMetrics.RecordExpirySweep(
                sharedRedis, success: false, membershipsRemoved: 0);
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            // A newly started node can recover work left by a crashed issuer
            // immediately, without waiting for the first periodic tick.
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Maintenance retries on the next tick, never logs identities.
            }
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
