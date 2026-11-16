using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Security;

namespace Shirokuro.Api.Readiness;

/// <summary>
/// A server-owned, monotonic drain indicator. The hosting lifetime always
/// triggers drain on shutdown; an explicitly configured Linux preStop signal
/// can mark the same state earlier without stopping the process. No browser
/// header, URL, cookie or query parameter can select this state.
/// The interface also supports deterministic TestServer lifecycle tests.
/// </summary>
public interface IReadinessDrainSignal
{
    bool IsDraining { get; }
}

/// <summary>
/// An in-process, server-owned one-shot notification for a preStop transition.
/// This is intentionally NOT a network command, nor a way for arbitrary
/// request code to trigger drain. Host shutdown remains separately observed
/// by IHostApplicationLifetime.ApplicationStopping.
/// </summary>
public interface IPreStopDrainNotification
{
    CancellationToken PreStopStarted { get; }
}

/// <summary>
/// Trusted routing marker for the two member-only HTTP roster GETs. Using
/// endpoint metadata (not a URL prefix) prevents a draining node from
/// changing unknown paths or framework 405 replies into misleading 503s.
/// </summary>
public sealed class LobbyRosterReadDrainRequired { }

/// <summary>
/// Trusted marker only for a new lobby membership admission via HTTP POST
/// /api/lobbies/{roomId}/join. Existing members' actions are not denied by
/// this join-specific host drain marker.
/// </summary>
public sealed class LobbyJoinDrainRequired { }

/// <summary>
/// The host's regular ApplicationStopping always forces not-ready. An
/// explicitly opted-in Linux SIGUSR1 (signal 10) can start the exact same
/// monotonic drain *before* SIGTERM, allowing the load balancer time to
/// stop routing new traffic while the server is still alive. No HTTP
/// administrative endpoint, network listener or client-owned flag exists.
/// </summary>
public sealed class HostLifetimeReadinessDrainSignal :
    IReadinessDrainSignal, IPreStopDrainNotification, IDisposable
{
    public const string EnablePreStopSignalKey =
        "Security:PreStopDrain:EnableSigusr1";

    // SIGUSR1 is signal number 10 on Linux; the PosixSignal enum only has
    // named members for a portable subset. This feature is Linux-only.
    private const int LinuxSigusr1 = 10;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly PosixSignalRegistration? _registration;
    private readonly CancellationTokenRegistration _hostStopRegistration;
    private readonly CancellationTokenSource _preStopStarted = new();
    // The token remains readable even after the source has been disposed.
    // Never fetch CancellationTokenSource.Token during host teardown.
    private readonly CancellationToken _preStopToken;
    private int _preStopDraining;
    private int _firstDrainRecorded;
    private int _disposed;

    public HostLifetimeReadinessDrainSignal(
        IHostApplicationLifetime lifetime,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null)
    {
        _lifetime = lifetime;
        // Capture BEFORE installing the OS handler, which may be called at
        // any time once PosixSignalRegistration.Create has returned.
        _preStopToken = _preStopStarted.Token;
        var raw = configuration?[EnablePreStopSignalKey];
        if (raw is not null and not "true" and not "false")
            throw new InvalidOperationException(
                "Security:PreStopDrain:EnableSigusr1 must be 'true' or 'false'.");

        if (raw == "true")
        {
            if (environment?.IsDevelopment() == true)
                throw new InvalidOperationException(
                    "PreStop SIGUSR1 must not be enabled in Development.");
            if (!OperatingSystem.IsLinux())
                throw new PlatformNotSupportedException(
                    "PreStop SIGUSR1 is supported only on Linux.");

            // SIGUSR1 prepares a node for LB removal; never intercept
            // SIGTERM. This callback is not driven by HTTP requests.
            _registration = PosixSignalRegistration.Create(
                (PosixSignal)LinuxSigusr1, context =>
                {
                    context.Cancel = true;
                    BeginPreStopDrain();
                });
        }

        // Observe the default shutdown path too, even when preStop is
        // disabled. A token already cancelled at construction invokes
        // the callback immediately. Each node records only its FIRST
        // transition; later SIGTERM after SIGUSR1 cannot double-count.
        _hostStopRegistration =
            lifetime.ApplicationStopping.Register(RecordHostShutdown);
    }

    public bool IsDraining =>
        _lifetime.ApplicationStopping.IsCancellationRequested ||
        Volatile.Read(ref _preStopDraining) != 0;

    // A subscriber registered after preStop also gets notified immediately
    // by CancellationToken.Register. The token is never reset or reused.
    public CancellationToken PreStopStarted => _preStopToken;

    /// <summary>
    /// Only in-process trusted lifecycle code calls this (the SIGUSR1 handler,
    /// or a deterministic test). It is deliberately NOT exposed through DI's
    /// IReadinessDrainSignal or any HTTP/SignalR entry point.
    /// Does not stop the host or revoke established members' sessions.
    /// </summary>
    public void BeginPreStopDrain()
    {
        // Publish the drain flag FIRST, so late connections fail even when
        // a subscriber's synchronous callback is still closing old sockets.
        // A host already stopping wins the first-transition telemetry race.
        if (_lifetime.ApplicationStopping.IsCancellationRequested)
            RecordHostShutdown();

        if (Interlocked.Exchange(ref _preStopDraining, 1) != 0)
            return;

        if (Interlocked.CompareExchange(ref _firstDrainRecorded, 1, 0) == 0)
            MultiplayerMetrics.RecordNodeDrain(NodeDrainCause.PreStop);

        // CancellationTokenSource.Cancel executes every registered callback
        // synchronously. A faulty observer can throw an AggregateException
        // even though all callbacks were invoked. Never allow that observer
        // failure to escape the POSIX signal dispatcher: the drain flag
        // stays authoritative and healthy subscribers (such as the SignalR
        // registry) must still see this transition.
        //
        // Dispose may race with an already-dispatched SIGUSR1 callback.
        // Once teardown has begun, an ObjectDisposedException from the
        // source is harmless: the normal host shutdown fence remains active.
        try
        {
            _preStopStarted.Cancel();
        }
        catch (AggregateException)
        {
            // Do not log exception messages or callback arguments in the
            // signal path; those may contain arbitrary or sensitive data.
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Cancellation began after the host disposed its notifier.
        }
    }

    private void RecordHostShutdown()
    {
        if (Interlocked.CompareExchange(ref _firstDrainRecorded, 1, 0) == 0)
            MultiplayerMetrics.RecordNodeDrain(NodeDrainCause.HostShutdown);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        // Unregister OS signals before freeing the notification source.
        // An already-running callback is protected against disposal races
        // in BeginPreStopDrain and must not bring down the host.
        _registration?.Dispose();
        _hostStopRegistration.Dispose();
        _preStopStarted.Dispose();
    }
}
