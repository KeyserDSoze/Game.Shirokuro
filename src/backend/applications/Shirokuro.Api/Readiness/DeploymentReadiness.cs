using System.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Shirokuro.Api.Security;
using StackExchange.Redis;

namespace Shirokuro.Api.Readiness;

/// <summary>
/// Node readiness is distinct from process liveness. No diagnostics, guest
/// identities or Redis connection details are exposed by this interface.
/// </summary>
public interface IDeploymentReadiness
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
 /// A round-trip through the configured ASP.NET Core Data Protection key ring.
 /// Separated from the Redis network probe so failures can be isolated in tests
 /// without replacing cookie authentication's own cryptographic provider.
 /// </summary>
public interface IReadinessKeyVerifier
{
    void Verify();
}

public sealed class ReadinessKeyVerifier(IDataProtectionProvider dataProtection)
    : IReadinessKeyVerifier
{
    public void Verify()
    {
        var protector = dataProtection.CreateProtector("Shirokuro.Readiness.v1");
        var encrypted = protector.Protect("ready");
        if (!string.Equals(protector.Unprotect(encrypted),
                "ready", StringComparison.Ordinal))
            throw new InvalidOperationException("Key ring round-trip failed.");
    }
}

/// <summary>Local preview does not depend on Redis.</summary>
public sealed class DevelopmentReadiness : IDeploymentReadiness
{
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MultiplayerMetrics.RecordReadinessProbe(ready: true);
        return Task.FromResult(true);
    }
}

/// <summary>
/// Non-Development readiness checks both independent Redis clients (room state
/// and guest session cache) plus the configured Data Protection key ring.
/// Probes require real Redis write capability on BOTH connectors. Technical
/// short-lived keys are never guest state; work is coalesced for a short time
/// so an anonymous endpoint cannot trigger unbounded Redis writes.
/// </summary>
public sealed class RedisDeploymentReadiness(
    IConnectionMultiplexer redis, IDistributedCache guestSessions,
    IReadinessKeyVerifier keyVerifier, TimeProvider clock) : IDeploymentReadiness
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CachedFor = TimeSpan.FromSeconds(3);
    private const string CacheProbeKey = "readiness-write-probe-v2";
    private const string LobbyProbeKey = "shirokuro:readiness:write-probe-v2";
    private static readonly byte[] ProbeValue = [1];
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(10);
    // A public readiness burst shares a single bounded probe, rather than
    // allocating one queued semaphore waiter per HTTP request.
    private readonly object _stateGate = new();
    private Task<bool>? _inFlight;
    private DateTimeOffset _nextProbeUtc = DateTimeOffset.MinValue;
    private bool _lastResult;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<bool> probe;
        TaskCompletionSource<bool>? started = null;
        lock (_stateGate)
        {
            if (clock.GetUtcNow() < _nextProbeUtc)
                return Task.FromResult(_lastResult);

            if (_inFlight is null)
            {
                // Publish the promise before starting any async work: an
                // immediately completed operation must not race a new caller.
                started = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight = started.Task;
            }

            probe = _inFlight;
        }

        if (started is not null)
            _ = CompleteProbeAsync(started);

        // Client disconnect/cancellation affects only this waiter. The one
        // Redis/Data Protection probe still serves the other clients.
        return probe.WaitAsync(cancellationToken);
    }

    private async Task CompleteProbeAsync(TaskCompletionSource<bool> completion)
    {
        // Stopwatch is monotonic and cannot be distorted by clock sync or
        // a TimeProvider used to advance the 3-second cache in tests.
        var startedAt = Stopwatch.GetTimestamp();
        bool ready;
        var stage = ReadinessProbeStage.RedisWrite;
        using var timeout = new CancellationTokenSource(ProbeTimeout);
        try
        {
            // PING/GET alone would incorrectly report read-only replicas
            // ready. Both independent Production connectors must be writable.
            // These fixed technical keys contain no user data or identifiers.
            Task<bool> lobbyWrite = redis.GetDatabase().StringSetAsync(
                LobbyProbeKey, "1", expiry: ProbeTtl);
            Task cacheWrite = guestSessions.SetAsync(
                CacheProbeKey, ProbeValue,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ProbeTtl
                }, timeout.Token);
            await Task.WhenAll(lobbyWrite, cacheWrite).WaitAsync(timeout.Token);
            if (!await lobbyWrite)
                throw new InvalidOperationException("Lobby probe write denied.");

            stage = ReadinessProbeStage.SessionRead;
            var observed = await guestSessions.GetAsync(
                CacheProbeKey, timeout.Token).WaitAsync(timeout.Token);
            if (observed is null || !observed.AsSpan().SequenceEqual(ProbeValue))
                throw new InvalidOperationException("Session probe write unreadable.");

            // The configured, shared encrypted key ring must be usable too.
            stage = ReadinessProbeStage.KeyRing;
            keyVerifier.Verify();
            ready = true;
            stage = ReadinessProbeStage.Success;
        }
        catch (Exception)
        {
            // Never disclose a Redis endpoint, password or key-ring path.
            // Labels are bounded to phases; simultaneous Redis writes do not
            // identify which connector failed. A timeout takes precedence.
            ready = false;
            if (timeout.IsCancellationRequested)
                stage = ReadinessProbeStage.Timeout;
        }

        lock (_stateGate)
        {
            _lastResult = ready;
            _nextProbeUtc = clock.GetUtcNow().Add(CachedFor);
            _inFlight = null;
        }

        // Count a real probe only once, regardless of HTTP fan-out/cancel.
        MultiplayerMetrics.RecordReadinessProbe(ready);
        MultiplayerMetrics.RecordReadinessDuration(
            ready, stage, Stopwatch.GetElapsedTime(startedAt).TotalSeconds);
        completion.TrySetResult(ready);
    }
}
