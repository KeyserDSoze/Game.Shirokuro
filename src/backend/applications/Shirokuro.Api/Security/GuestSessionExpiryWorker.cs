using Shirokuro.Application.Lobbies;

namespace Shirokuro.Api.Security;

/// <summary>
/// Prunes expired server-side guest tickets and their pre-match memberships.
/// Does not expose guest identities, invites or room rosters over HTTP/logs.
/// </summary>
public sealed class GuestSessionExpiryWorker(
    GuestSessionRegistry registry, InMemoryLobbyCoordinator coordinator,
    TimeProvider clock) : BackgroundService
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                SweepOnce();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. No player or room identifiers in logs.
        }
    }
}
