namespace Shirokuro.Api.Security;

/// <summary>Prunes expired server-side guest tickets without exposing IDs.</summary>
public sealed class GuestSessionExpiryWorker(
    GuestSessionRegistry registry, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                registry.PruneExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}

