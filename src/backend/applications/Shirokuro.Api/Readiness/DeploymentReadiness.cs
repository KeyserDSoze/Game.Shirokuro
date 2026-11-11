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
                // A ping / GET alone incorrectly reports replicas ready.
                // Both independently configured Production connectors must
                // be WRITABLE before a node can accept multiplayer traffic.
                // The fixed probe keys contain no user data, expire rapidly,
                // and cannot collide with the room/session/expiry keyspaces.
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

                var observed = await guestSessions.GetAsync(
                    CacheProbeKey, timeout.Token).WaitAsync(timeout.Token);
                if (observed is null || !observed.AsSpan().SequenceEqual(ProbeValue))
                    throw new InvalidOperationException("Session probe write unreadable.");

                // A missing or unreadable persisted encrypted key ring must
                // make a node unready even when Redis answers successfully.
                keyVerifier.Verify();
                _lastResult = true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No exception text or network address is logged or emitted.
                _lastResult = false;
            }

            _nextProbeUtc = clock.GetUtcNow().Add(CachedFor);
            // Only a fresh probe emits a measurement. Cached requests do not
            // pretend to be independent Redis / Data Protection checks.
            MultiplayerMetrics.RecordReadinessProbe(_lastResult);
            return _lastResult;
        }
        finally
        {
            _gate.Release();
        }
    }
}
