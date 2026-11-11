using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
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

/// <summary>Local preview does not depend on Redis.</summary>
public sealed class DevelopmentReadiness : IDeploymentReadiness
{
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(true);
    }
}

/// <summary>
/// Non-Development readiness checks both independent Redis clients (room state
/// and guest session cache) plus the configured Data Protection key ring.
/// Probes are bounded, read-only against Redis, and coalesced for a short time
/// so an anonymous health endpoint cannot issue a Redis command per request.
/// </summary>
public sealed class RedisDeploymentReadiness(
    IConnectionMultiplexer redis, IDistributedCache guestSessions,
    IDataProtectionProvider dataProtection, TimeProvider clock) : IDeploymentReadiness
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan CachedFor = TimeSpan.FromSeconds(3);
    private const string CacheProbeKey = "readiness-probe-v1";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _nextProbeUtc = DateTimeOffset.MinValue;
    private bool _lastResult;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (clock.GetUtcNow() < _nextProbeUtc)
                return _lastResult;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(ProbeTimeout);

            try
            {
                // Both checks must complete: the lobby connector and the
                // separate IDistributedCache session connector can fail
                // independently. Neither operation modifies user state.
                Task lobbyPing = redis.GetDatabase().PingAsync();
                Task sessionRead = guestSessions.GetAsync(CacheProbeKey, timeout.Token);
                await Task.WhenAll(lobbyPing, sessionRead).WaitAsync(timeout.Token);

                // A missing or unreadable persisted encrypted key ring must
                // make a node unready even when Redis answers successfully.
                var protector = dataProtection.CreateProtector("Shirokuro.Readiness.v1");
                var encrypted = protector.Protect("ready");
                _lastResult = string.Equals(
                    protector.Unprotect(encrypted), "ready", StringComparison.Ordinal);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No exception text or network address is logged or emitted.
                _lastResult = false;
            }

            _nextProbeUtc = clock.GetUtcNow().Add(CachedFor);
            return _lastResult;
        }
        finally
        {
            _gate.Release();
        }
    }
}
