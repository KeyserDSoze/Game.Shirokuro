using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        });
    }
}
