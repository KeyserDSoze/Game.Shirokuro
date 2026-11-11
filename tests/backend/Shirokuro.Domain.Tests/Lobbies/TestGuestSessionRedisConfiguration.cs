using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Lobbies;
using Shirokuro.Api.Security;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// All Production TestServers receive TLS-Redis settings, but inject a
/// deterministic in-process IDistributedCache instead of opening a socket.
/// Supply the SAME cache to two hosts to emulate shared Redis state.
/// </summary>
internal static class TestGuestSessionRedisConfiguration
{
    public const string FakeTlsRedisConnection = "localhost:6380,ssl=true,abortConnect=true";

    public static void Apply(IWebHostBuilder builder, IDistributedCache? shared = null)
    {
        builder.UseSetting(
            "Security:GuestSessions:RedisConnectionString", FakeTlsRedisConnection);
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:GuestSessions:RedisConnectionString"] = FakeTlsRedisConnection
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IDistributedCache>();
            // These isolated security TestServers intentionally avoid live Redis;
            // real expiry-index integration tests use independent connections.
            services.RemoveAll<IGuestExpiryIndex>();
            services.AddSingleton<IGuestExpiryIndex, TestGuestExpiryIndex>();
            // Isolated security TestServers do not run a real Redis. A
            // test-only fence keeps their hosted worker deterministic;
            // the dedicated cross-node integration suite restores the
            // Redis-backed fence on its own namespaced snapshot store.
            services.RemoveAll<IGuestRevocationFence>();
            services.AddSingleton<IGuestRevocationFence, TestGuestRevocationFence>();
            if (shared is null)
                services.AddDistributedMemoryCache();
            else
                services.AddSingleton(shared);

            // Security-focused non-Development tests use a deterministic
            // local room fixture, not a live Redis. Dedicated multi-instance
            // tests explicitly replace this with the production Redis runtime.
            services.RemoveAll<IAsyncGuestLobby>();
            services.AddSingleton<IAsyncGuestLobby, LocalGuestLobbyRuntime>();

            // All other Production TestServers intentionally use an isolated
            // deterministic fake for only the Redis limits, never the
            // production fallback. Real multi-node tests explicitly restore
            // RedisGuestMutationLimiter with real Redis.
            services.RemoveAll<IGuestMutationLimiter>();
            services.AddSingleton<IGuestMutationLimiter, TestGuestMutationLimiter>();
        });
    }
}


/// <summary>
/// TestServer-only deterministic limiter; never registered by production DI.
/// Used by other ingress/security tests without requiring live Redis.
/// </summary>
internal sealed class TestGuestMutationLimiter(TimeProvider clock) : IGuestMutationLimiter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (int Used, DateTimeOffset Started)> _windows = new();

    public Task<GuestMutationDecision> AcquireAsync(
        GuestMutationRateLimit policy, System.Net.IPAddress? ip,
        CancellationToken cancellationToken = default,
        string? trustedSessionToken = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = RedisGuestMutationLimiter.Key(policy, ip);
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_windows.TryGetValue(key, out var entry) ||
                now - entry.Started >= TimeSpan.FromMinutes(1))
                entry = (0, now);
            var remaining = Math.Max(0, (long)Math.Ceiling(
                (entry.Started.AddMinutes(1) - now).TotalMilliseconds));
            var allowed = entry.Used < policy.PermitLimit;
            if (allowed)
                entry.Used++;
            _windows[key] = entry;
            return Task.FromResult(new GuestMutationDecision(allowed, remaining));
        }
    }
}

internal sealed class TestGuestRevocationFence : IGuestRevocationFence
{
    public Task FenceGuestAsync(string trustedGuestId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (trustedGuestId is not { Length: 38 } ||
            !trustedGuestId.StartsWith("guest-", StringComparison.Ordinal))
            throw new ArgumentException("Invalid test guest ID.", nameof(trustedGuestId));
        return Task.CompletedTask;
    }
}

internal sealed class TestGuestExpiryIndex(TimeProvider clock) : IGuestExpiryIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string PlayerId, DateTimeOffset Expires)> _pending = new();

    public Task ScheduleAsync(string trustedPlayerId, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _pending.Add(Guid.NewGuid().ToString("N"), (trustedPlayerId, expiresUtc));
        return Task.CompletedTask;
    }

    public Task<string> ScheduleRevokedAsync(string trustedPlayerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Guid.NewGuid().ToString("N");
        lock (_gate)
            _pending.Add(key, (trustedPlayerId, clock.GetUtcNow()));
        return Task.FromResult(key);
    }

    public Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<DueGuestExpiry>>(
                _pending.Where(x => x.Value.Expires <= clock.GetUtcNow())
                    .Take(RedisGuestExpiryIndex.BatchSize)
                    .Select(x => new DueGuestExpiry(x.Key, x.Value.PlayerId)).ToArray());
    }

    public Task AcknowledgeAsync(string protectedMember,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _pending.Remove(protectedMember);
        return Task.CompletedTask;
    }
}
