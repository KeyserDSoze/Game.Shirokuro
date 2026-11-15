using Microsoft.Extensions.Hosting;

namespace Shirokuro.Api.Readiness;

/// <summary>
/// A server-owned, monotonic drain indicator. Client headers and query data
/// never select this state; only the host lifetime can initiate shutdown.
/// A separate interface makes readiness transitions testable without actually
/// stopping the ASP.NET Core TestServer process.
/// </summary>
public interface IReadinessDrainSignal
{
    bool IsDraining { get; }
}

/// <summary>
/// Trusted routing marker for the two member-only HTTP roster GETs. Using
/// endpoint metadata (not a URL prefix) prevents a draining node from
/// changing unknown paths or framework 405 replies into misleading 503s.
/// </summary>
public sealed class LobbyRosterReadDrainRequired { }

/// <summary>
/// Once ApplicationStopping is cancelled, this node must not advertise ready.
/// This does not implement an ingress fence or a load-balancer grace period.
/// </summary>
public sealed class HostLifetimeReadinessDrainSignal(
    IHostApplicationLifetime lifetime) : IReadinessDrainSignal
{
    public bool IsDraining => lifetime.ApplicationStopping.IsCancellationRequested;
}
