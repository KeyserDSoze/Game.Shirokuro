using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

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
    private readonly CancellationTokenSource _preStopStarted = new();
    private int _preStopDraining;

    public HostLifetimeReadinessDrainSignal(
        IHostApplicationLifetime lifetime,
        IConfiguration? configuration = null,
        IHostEnvironment? environment = null)
    {
        _lifetime = lifetime;
        var raw = configuration?[EnablePreStopSignalKey];
        if (raw is not null and not "true" and not "false")
            throw new InvalidOperationException(
                "Security:PreStopDrain:EnableSigusr1 must be 'true' or 'false'.");

        if (raw != "true")
            return;

        if (environment?.IsDevelopment() == true)
            throw new InvalidOperationException(
                "PreStop SIGUSR1 must not be enabled in Development.");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "PreStop SIGUSR1 is supported only on Linux.");

        // Cancel the OS's default SIGUSR1 termination; this is a *prepare*
        // signal, not SIGTERM. Signal registration is eagerly resolved before
        // Kestrel starts, and disposed with the DI host.
        _registration = PosixSignalRegistration.Create(
            (PosixSignal)LinuxSigusr1, context =>
            {
                context.Cancel = true;
                BeginPreStopDrain();
            });
    }

    public bool IsDraining =>
        _lifetime.ApplicationStopping.IsCancellationRequested ||
        Volatile.Read(ref _preStopDraining) != 0;

    // A subscriber registered after preStop also gets notified immediately
    // by CancellationToken.Register. The token is never reset or reused.
    public CancellationToken PreStopStarted => _preStopStarted.Token;

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
        // Cancel once: repeated SIGUSR1 events and later SIGTERM cannot
        // schedule duplicate aborts from this preStop notification.
        if (Interlocked.Exchange(ref _preStopDraining, 1) == 0)
            _preStopStarted.Cancel();
    }

    public void Dispose()
    {
        _registration?.Dispose();
        _preStopStarted.Dispose();
    }
}
