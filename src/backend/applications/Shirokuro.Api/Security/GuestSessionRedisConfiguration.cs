using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Shirokuro.Api.Security;

/// <summary>
/// Mandatory TLS-only Redis guest-session authority outside Development.
/// Neither the Redis password nor the connection string is logged.
/// </summary>
public static class GuestSessionRedisConfiguration
{
    public const string ConnectionStringKey = "Security:GuestSessions:RedisConnectionString";

    public static void Configure(
        IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
            return; // Local Dev continues to use the tested in-memory registry.

        var value = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "A TLS Redis guest-session store is required outside Development.");

        ConfigurationOptions parsed;
        try
        {
            parsed = ConfigurationOptions.Parse(value);
            if (!parsed.Ssl || parsed.EndPoints.Count == 0)
                throw new InvalidOperationException();
        }
        catch (Exception)
        {
            // Do not include connection strings/passwords in exception messages.
            throw new InvalidOperationException(
                "Guest-session Redis configuration requires a valid TLS connection.");
        }

        services.AddStackExchangeRedisCache(options =>
        {
            options.ConfigurationOptions = parsed;
            options.InstanceName = "shirokuro:";
        });
    }
}
