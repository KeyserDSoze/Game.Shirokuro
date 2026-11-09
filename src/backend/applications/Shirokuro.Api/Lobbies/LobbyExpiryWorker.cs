using Shirokuro.Application.Lobbies;

namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Periodically removes abandoned process-local rooms; capacity checks and
/// individual lookups also expire rooms synchronously and atomically.
/// </summary>
public sealed class LobbyExpiryWorker(
    InMemoryLobbyCoordinator coordinator, TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                coordinator.PruneExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown; do not leak room identifiers into logs.
        }
    }
}

