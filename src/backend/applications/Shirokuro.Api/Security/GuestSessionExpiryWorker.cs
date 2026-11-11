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
    IGuestExpiryIndex? expiryIndex = null,
    IGuestRevocationFence? revocationFence = null) : BackgroundService
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
        // A successful item is acknowledged independently. A transient
        // failure on one guest must not block the other guests already read
        // in this bounded, encrypted Redis batch.
        var sharedRedis = expiryIndex is not null;
        if (lobby is null && !sharedRedis)
            return SweepOnce(); // Counts a single local attempt.

        var removed = 0;
        try
        {
            if (expiryIndex is not null)
            {
                if (lobby is null)
                    throw new InvalidOperationException("Distributed cleanup needs a lobby.");

                var failedItems = 0;
                // Read a bounded, shared rotating window. Damaged encrypted
                // members are represented explicitly as unprocessable items,
                // not as identities and not as a silent successful cleanup.
                var due = await expiryIndex.ReadDueAsync(cancellationToken);
                foreach (var item in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (item.IsUnreadable)
                    {
                        // Never invent an identity, fence or ack an
                        // unauthenticated encrypted entry. Continue other
                        // due work but report a degraded sweep at the end.
                        failedItems++;
                        MultiplayerMetrics.RecordUnreadableExpiryItem();
                        continue;
                    }
                    try
                    {
                        // Fence before removing: a stale in-flight guest
                        // request must not resurrect a removed membership.
                        if (revocationFence is not null)
                            await revocationFence.FenceGuestAsync(
                                item.PlayerId, cancellationToken);
                        removed += await lobby.RemovePlayerFromAllRoomsAsync(
                            item.PlayerId, cancellationToken);
                        // The opaque item is acked only after cleanup.
                        await expiryIndex.AcknowledgeAsync(
                            item.ProtectedMember, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Host shutdown stops the batch promptly; leave all
                        // remaining items durable for another node.
                        throw;
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Do not log or surface the guest ID, invite, key, or
                        // exception message. This item stays in Redis while
                        // other independent due items can still make progress.
                        failedItems++;
                    }
                }

                registry.DrainExpiredPlayerIds();
                if (failedItems > 0)
                    throw new InvalidOperationException(
                        "Guest expiry maintenance has retryable items.");

                MultiplayerMetrics.RecordExpirySweep(
                    sharedRedis: true, success: true, removed);
                return removed;
            }

            var identities = registry.DrainExpiredPlayerIds();
            try
            {
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
                // No local maintenance identity is lost after a transient
                // failure. Development behaviour remains unchanged.
                registry.RequeueExpiredPlayerIds(identities);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown is not a failed infrastructure sweep.
            throw;
        }
        catch
        {
            // Count the successful partial removals even on a failed sweep;
            // the next pass will not double-count idempotent CAS cleanup.
            MultiplayerMetrics.RecordExpirySweep(
                sharedRedis, success: false, membershipsRemoved: removed);
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
