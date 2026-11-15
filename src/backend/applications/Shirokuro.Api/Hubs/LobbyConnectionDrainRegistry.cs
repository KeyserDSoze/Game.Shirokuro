using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Shirokuro.Api.Readiness;

namespace Shirokuro.Api.Hubs;

/// <summary>
/// Tracks only live transport contexts, never player identifiers, cookies,
/// lobby IDs or invitation codes. On host shutdown, abort existing SignalR
/// connections so a draining node does not continue serving roster reads.
/// The ingress fence separately refuses new handshakes before cookie/Redis.
/// </summary>
public sealed class LobbyConnectionDrainRegistry : IDisposable
{
    private readonly IReadinessDrainSignal _drain;
    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private int _stopping;

    public LobbyConnectionDrainRegistry(
        IReadinessDrainSignal drain, IHostApplicationLifetime lifetime)
    {
        _drain = drain;
        _stoppingRegistration =
            lifetime.ApplicationStopping.Register(BeginDraining);
        if (_drain.IsDraining)
            BeginDraining();
    }

    public bool IsDraining =>
        Volatile.Read(ref _stopping) != 0 || _drain.IsDraining;

    /// <summary>
    /// A connection racing with ApplicationStopping is always aborted:
    /// either the shutdown callback sees it, or the post-insert check does.
    /// </summary>
    public bool TryTrack(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (IsDraining)
        {
            AbortSafely(context);
            return false;
        }

        _connections[context.ConnectionId] = context;
        if (!IsDraining)
            return true;

        _connections.TryRemove(context.ConnectionId, out _);
        AbortSafely(context);
        return false;
    }

    public void Untrack(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // SignalR connection IDs are unique for a live connection, so this
        // removes exactly the disconnected context rather than a player.
        _connections.TryRemove(context.ConnectionId, out _);
    }

    private void BeginDraining()
    {
        Interlocked.Exchange(ref _stopping, 1);
        foreach (var connection in _connections.Values)
            AbortSafely(connection);
        _connections.Clear();
    }

    private static void AbortSafely(HubCallerContext context)
    {
        try
        {
            context.Abort();
        }
        catch (Exception)
        {
            // A transport already closing must not prevent other sockets
            // from being aborted or block the server's shutdown callback.
        }
    }

    public void Dispose()
    {
        _stoppingRegistration.Dispose();
        _connections.Clear();
    }
}
