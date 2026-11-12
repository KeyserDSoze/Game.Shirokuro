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
    public const string InvalidConnection =
        "Guest-session Redis configuration requires a valid TLS connection.";

    public static void Configure(
        IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
            return; // Local Dev continues to use the tested in-memory registry.

        var parsed = ReadTlsOptions(configuration);

        services.AddStackExchangeRedisCache(options =>
        {
            options.ConfigurationOptions = parsed;
            options.InstanceName = "shirokuro:";
            // RedisCache owns this *independent* lazily-connected multiplexer.
            // Resolve options at connection time: TestServer and operations
            // may replace ConfigurationOptions via later named DI overrides.
            // Do not replace the shared authoritative cache with the lobby
            // multiplexer; only observe the cache's own connection lifecycle.
            options.ConnectionMultiplexerFactory = async () =>
            {
                var current = options.ConfigurationOptions ?? parsed;
                // Validate the effective, potentially later-overridden options
                // before any socket opens. Only the separately ACL-scoped INFO
                // observer (#63) may use client-side AllowAdmin.
                EnsureGameConnectionSecurity(current);
                var connection = await ConnectionMultiplexer.ConnectAsync(current.Clone());
                return RedisConnectionMetrics.Observe(
                    connection, RedisConnector.GuestSessions);
            };
        });
    }

    /// <summary>
    /// Both independent Production Redis connectors must use the same
    /// validated TLS configuration. StackExchange.Redis interprets an
    /// optional serviceName as Sentinel managed-primary discovery and
    /// updates the managed multiplexer on Sentinel switch events.
    /// Never log raw connection options or expose parsing errors.
    /// </summary>
    public static ConfigurationOptions ReadTlsOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "A TLS Redis guest-session store is required outside Development.");

        ConfigurationOptions parsed;
        try
        {
            parsed = ConfigurationOptions.Parse(value);
            EnsureGameConnectionSecurity(parsed);
        }
        catch (Exception)
        {
            // Do not include connection strings/passwords in exception messages.
            throw new InvalidOperationException(InvalidConnection);
        }

        return parsed;
    }

    // The same validator is applied during startup and immediately before
    // connecting the lazy guest cache, including options replaced by DI.
    // Never expose raw options in error messages or use administrative Redis
    // commands from guest or lobby clients, regardless of operator input.
    private static void EnsureGameConnectionSecurity(ConfigurationOptions options)
    {
        if (!options.Ssl || options.EndPoints.Count == 0 || options.AllowAdmin)
            throw new InvalidOperationException(InvalidConnection);
    }
}
