using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Hubs;
using Shirokuro.Api.Readiness;

// Launched only by the Linux CI smoke. The real Shirokuro production
// signal and SignalR registry run in a separate OS process so the
// test runner never receives SIGUSR1 or SIGTERM itself.
// No Redis, browser identity, cookie, secret, network port or game rule.
if (!OperatingSystem.IsLinux())
    throw new PlatformNotSupportedException("Linux SIGUSR1 smoke is Linux-only.");

var builder = Host.CreateApplicationBuilder(args);
builder.Environment.EnvironmentName = Environments.Production;
builder.Configuration[HostLifetimeReadinessDrainSignal.EnablePreStopSignalKey] =
    "true";
builder.Services.AddSingleton<HostLifetimeReadinessDrainSignal>();
builder.Services.AddSingleton<IReadinessDrainSignal>(
    sp => sp.GetRequiredService<HostLifetimeReadinessDrainSignal>());
builder.Services.AddSingleton<LobbyConnectionDrainRegistry>();

using var host = builder.Build();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
var signal = host.Services.GetRequiredService<HostLifetimeReadinessDrainSignal>();
var registry = host.Services.GetRequiredService<LobbyConnectionDrainRegistry>();
var socket = new SmokeHubCallerContext();
if (!registry.TryTrack(socket) || signal.IsDraining)
    throw new InvalidOperationException("Initial SignalR socket could not be tracked.");

await host.StartAsync();
Console.WriteLine("SHIROKURO_SIGNAL_ARMED");
Console.Out.Flush();

// This token is cancelled only by the REAL registered SIGUSR1 handler.
try
{
    await Task.Delay(Timeout.InfiniteTimeSpan, signal.PreStopStarted);
}
catch (OperationCanceledException) when (signal.PreStopStarted.IsCancellationRequested)
{
}

// CancellationTokenSource.Cancel publishes its cancellation before all
// registered callbacks have necessarily finished. The Task.Delay above may
// resume before LobbyConnectionDrainRegistry's synchronous callback has
// aborted the tracked transport; checking AbortCount immediately is a
// scheduling race, not a valid post-drain assertion. Require the signal to
// have started drain and the host to still be alive right away.
if (!signal.IsDraining || lifetime.ApplicationStopping.IsCancellationRequested)
    throw new InvalidOperationException(
        "PreStop must start drain without stopping the host.");

// Then give the live socket registry a bounded opportunity to finish its
// already-dispatched cancellation callback. A missing Abort still fails
// (no blind sleep or swallowed failure), and SIGTERM must not arrive early.
for (var attempt = 0;
     attempt < 500 &&
     (!registry.IsDraining || socket.AbortCount == 0) &&
     !lifetime.ApplicationStopping.IsCancellationRequested;
     attempt++)
{
    await Task.Delay(TimeSpan.FromMilliseconds(10));
}
if (!signal.IsDraining ||
    lifetime.ApplicationStopping.IsCancellationRequested ||
    socket.AbortCount != 1 ||
    !registry.IsDraining)
{
    throw new InvalidOperationException(
        "PreStop must abort the tracked socket exactly once while the host remains alive.");
}
Console.WriteLine("SHIROKURO_SIGNAL_DRAINED");
Console.Out.Flush();

// A second SIGUSR1 and a subsequent normal SIGTERM must not duplicate
// Abort(), while SIGTERM must still complete the ordinary host lifecycle.
await host.WaitForShutdownAsync();
if (!lifetime.ApplicationStopping.IsCancellationRequested ||
    socket.AbortCount != 1 ||
    !signal.IsDraining)
{
    throw new InvalidOperationException(
        "SIGTERM must stop the host without repeating the socket abort.");
}
Console.WriteLine("SHIROKURO_SIGNAL_STOPPED");
Console.Out.Flush();

internal sealed class SmokeHubCallerContext : HubCallerContext
{
    private int _abortCount;
    public int AbortCount => Volatile.Read(ref _abortCount);
    public override string ConnectionId => "ci-socket";
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal User { get; } = new(new ClaimsIdentity());
    public override IDictionary<object, object?> Items { get; } =
        new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } =
        new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;
    public override void Abort() => Interlocked.Increment(ref _abortCount);
}
