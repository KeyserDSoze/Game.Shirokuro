using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Shirokuro.Api.Readiness;
using Shirokuro.Api.Security;

namespace Shirokuro.Api.Hubs;

/// <summary>
/// Tracks only live transport contexts, never player identifiers, cookies,
/// lobby IDs or invitation codes. On *preStop* or host shutdown, abort
/// existing SignalR connections so a draining node does not keep old
/// WebSockets open throughout the load-balancer grace period.
/// The ingress fence separately refuses new handshakes before cookie/Redis.
/// </summary>
public sealed class LobbyConnectionDrainRegistry : IDisposable
{
    private readonly IReadinessDrainSignal _drain;
    private readonly ConcurrentDictionary<string, TrackedConnection> _connections = new();

    // A callback may snapshot a tracked context while TryTrack is also
    // completing its post-insert drain check. Both paths must coordinate a
    // single Abort() attempt. Untrack marks the entry disconnected before
    // removal so a stale snapshot does not abort a departed socket.
    private sealed class TrackedConnection(HubCallerContext context)
    {
        private const int Active = 0;
        private const int Disconnected = 1;
        private const int AbortAttempted = 2;
        private int _state;

        public HubCallerContext Context { get; } = context;

        public void MarkDisconnected() =>
            Interlocked.CompareExchange(ref _state, Disconnected, Active);

        public void AbortOnce(SignalRDrainAbortReason reason)
        {
            if (Interlocked.CompareExchange(ref _state, AbortAttempted, Active)
                != Active)
                return;

            // One metric only for an Abort() actually attempted. Never
            // record the socket ID or any request/member data.
            MultiplayerMetrics.RecordSignalRDrainAbortAttempt(reason);
            try
            {
                Context.Abort();
            }
            catch (Exception)
            {
                // A closing transport must not prevent the remaining
                // tracked sockets from being drained.
            }
        }
    }
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private readonly CancellationTokenRegistration _preStopRegistration;
    private int _stopping;

    public LobbyConnectionDrainRegistry(
        IReadinessDrainSignal drain, IHostApplicationLifetime lifetime)
    {
        _drain = drain;
        // Host SIGTERM remains the unconditional fallback even without
        // the optional Linux preStop. If SIGUSR1 ran before this registry
        // was first resolved, CancellationToken.Register invokes the
        // callback immediately. Both subscriptions are safe to repeat.
        _stoppingRegistration = lifetime.ApplicationStopping.Register(
            () => BeginDraining(SignalRDrainAbortReason.HostShutdown));
        if (drain is IPreStopDrainNotification notification)
            _preStopRegistration = notification.PreStopStarted.Register(
                () => BeginDraining(SignalRDrainAbortReason.PreStop));
        if (_drain.IsDraining)
            BeginDraining(drain is IPreStopDrainNotification notified &&
                          notified.PreStopStarted.IsCancellationRequested
                ? SignalRDrainAbortReason.PreStop
                : SignalRDrainAbortReason.HostShutdown);
    }

    public bool IsDraining =>
        Volatile.Read(ref _stopping) != 0 || _drain.IsDraining;

    /// <summary>
    /// A connection racing with preStop/ApplicationStopping is always
    /// aborted: either a drain callback sees it or the post-insert check does.
    /// </summary>
    public bool TryTrack(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tracked = new TrackedConnection(context);
        if (IsDraining)
        {
            tracked.AbortOnce(SignalRDrainAbortReason.LateConnection);
            return false;
        }

        _connections[context.ConnectionId] = tracked;
        if (!IsDraining)
            return true;

        // BeginDraining may have already aborted this entry through its
        // dictionary snapshot. The same atomic state prevents a second
        // Abort() and duplicate metrics in this post-insert race.
        _connections.TryRemove(context.ConnectionId, out _);
        tracked.AbortOnce(SignalRDrainAbortReason.LateConnection);
        return false;
    }

    public void Untrack(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Mark disconnected BEFORE removal: a drain callback may already
        // hold a snapshot of this entry. Connection IDs are unique for the
        // lifetime of a SignalR transport; never store a player/room ID.
        if (_connections.TryGetValue(context.ConnectionId, out var tracked) &&
            ReferenceEquals(tracked.Context, context))
        {
            tracked.MarkDisconnected();
            _connections.TryRemove(context.ConnectionId, out _);
        }
    }

    private void BeginDraining(SignalRDrainAbortReason reason)
    {
        // PreStop and SIGTERM can both fire, but existing sockets should
        // never be aborted twice by redundant lifecycle notifications.
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
            return;
        foreach (var connection in _connections.Values)
            connection.AbortOnce(reason);
        _connections.Clear();
    }

    public void Dispose()
    {
        _preStopRegistration.Dispose();
        _stoppingRegistration.Dispose();
        _connections.Clear();
    }
}
