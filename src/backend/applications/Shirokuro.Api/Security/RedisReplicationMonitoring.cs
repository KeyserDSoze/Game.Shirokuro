using StackExchange.Redis;

namespace Shirokuro.Api.Security;

/// <summary>
/// Disabled-by-default, non-Development read-only replication telemetry.
/// StackExchange.Redis requires AllowAdmin=true to issue INFO. Never set it
/// on the lobby or guest-cache connections: a distinct, least-privilege
/// Redis ACL identity is required by startup configuration instead.
/// </summary>
public static class RedisReplicationMonitoring
{
    public const string EnabledKey = "Security:Monitoring:RedisReplicationEnabled";
    public const string ConnectionStringKey =
        "Security:Monitoring:RedisReplicationConnectionString";
    public const string InvalidConfiguration =
        "Redis replication monitoring requires an explicit boolean setting.";
    public const string InvalidConnection =
        "Redis replication monitoring requires a distinct restricted TLS ACL identity.";

    public static bool IsEnabled(
        IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        if (environment.IsDevelopment())
            return false;
        var setting = configuration[EnabledKey];
        if (string.IsNullOrWhiteSpace(setting))
            return false;
        if (!bool.TryParse(setting, out var enabled))
            throw new InvalidOperationException(InvalidConfiguration);
        return enabled;
    }

    /// <summary>
    /// Validate restricted credentials without granting client admin mode.
    /// Never expose the raw config, credentials or StackExchange.Redis
    /// parse/connection exceptions in error messages.
    /// </summary>
    public static ConfigurationOptions ReadRestrictedOptions(
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        try
        {
            var value = configuration[ConnectionStringKey];
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException();
            var game = GuestSessionRedisConfiguration.ReadTlsOptions(configuration);
            var options = ConfigurationOptions.Parse(value);
            if (!options.Ssl || options.EndPoints.Count == 0 ||
                options.AllowAdmin || string.IsNullOrWhiteSpace(options.User) ||
                string.Equals(options.User, "default", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(options.Password) ||
                string.Equals(options.User, game.User, StringComparison.Ordinal) ||
                string.Equals(options.Password, game.Password, StringComparison.Ordinal) ||
                !string.Equals(options.ServiceName, game.ServiceName, StringComparison.Ordinal) ||
                (options.ServiceName is not null &&
                    (string.IsNullOrWhiteSpace(options.SentinelUser) ||
                     string.IsNullOrWhiteSpace(options.SentinelPassword))))
                throw new InvalidOperationException();
            return options;
        }
        catch (Exception)
        {
            throw new InvalidOperationException(InvalidConnection);
        }
    }

    public static void Configure(
        IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!IsEnabled(configuration, environment))
            return;
        var restricted = ReadRestrictedOptions(configuration);

        // AllowAdmin is ONLY a StackExchange.Redis client-side INFO guard.
        // The server ACL MUST restrict the observer to INFO + handshake,
        // deny all game keys and destructive/admin commands. Never expose
        // the observer's mux as IConnectionMultiplexer in application DI.
        // This separate connection is owned/disposed with its sampler.
        services.AddSingleton(_ =>
        {
            var monitorOptions = restricted.Clone();
            monitorOptions.AllowAdmin = true;
            return new RedisReplicationSampler(
                ConnectionMultiplexer.Connect(monitorOptions),
                ownsConnection: true);
        });
        services.AddHostedService<RedisReplicationMonitoringWorker>();
    }
}

public sealed class RedisReplicationSampler : IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly bool _ownsConnection;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public RedisReplicationSampler(
        IConnectionMultiplexer redis, bool ownsConnection = false)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _ownsConnection = ownsConnection;
    }

    public void Dispose()
    {
        if (_ownsConnection)
            _redis.Dispose();
    }

    public async Task SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // INFO is a server command. Select the connected primary when
            // possible; a direct replica connection reports its own role.
            // Never log/reveal server endpoint or raw INFO, which includes IPs.
            var servers = _redis.GetEndPoints(configuredOnly: false)
                .Select(endpoint => _redis.GetServer(endpoint))
                .Where(server => server.IsConnected)
                .ToArray();
            var current = servers.FirstOrDefault(server => !server.IsReplica)
                ?? servers.FirstOrDefault();
            if (current is null)
                throw new InvalidOperationException();

            // A distinct restricted observer uses client-side AllowAdmin,
            // but its Redis ACL allows only INFO. The authoritative game
            // multiplexers retain AllowAdmin=false and separate identities.
            var raw = await current.InfoRawAsync("replication")
                .WaitAsync(limit.Token);
            var observation = RedisReplicationInfo.Parse(raw);
            MultiplayerMetrics.RecordRedisReplication(
                observation, RedisReplicationPollOutcome.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            MultiplayerMetrics.RecordRedisReplication(
                null, RedisReplicationPollOutcome.Timeout);
        }
        catch (FormatException)
        {
            MultiplayerMetrics.RecordRedisReplication(
                null, RedisReplicationPollOutcome.Invalid);
        }
        catch (Exception)
        {
            // INFO ACL/NOPERM, TLS, network and Redis exceptions must never
            // affect API readiness, auth or expose server details in logs.
            MultiplayerMetrics.RecordRedisReplication(
                null, RedisReplicationPollOutcome.Unavailable);
        }
    }
}

public sealed class RedisReplicationMonitoringWorker(IServiceProvider services)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // No synchronous Redis connect in host startup.
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await services.GetRequiredService<RedisReplicationSampler>()
                        .SampleOnceAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    MultiplayerMetrics.RecordRedisReplication(
                        null, RedisReplicationPollOutcome.Unavailable);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
