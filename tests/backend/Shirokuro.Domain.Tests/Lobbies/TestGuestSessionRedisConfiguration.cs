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
