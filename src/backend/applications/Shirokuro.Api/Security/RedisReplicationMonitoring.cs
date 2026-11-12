using StackExchange.Redis;

namespace Shirokuro.Api.Security;

/// <summary>
/// Disabled-by-default, non-Development read-only replication probe. Uses
/// existing authenticated TLS lobby multiplexer and never opens a new
/// network endpoint or blocks player commands while waiting for telemetry.
/// </summary>
public static class RedisReplicationMonitoring
{
    public const string EnabledKey = "Security:Monitoring:RedisReplicationEnabled";
    public const string InvalidConfiguration =
        "Redis replication monitoring requires an explicit boolean setting.";

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

    public static void Configure(
        IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!IsEnabled(configuration, environment))
            return;
        services.AddSingleton<RedisReplicationSampler>();
        services.AddHostedService<RedisReplicationMonitoringWorker>();
    }
}

public sealed class RedisReplicationSampler(IConnectionMultiplexer redis)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task SampleOnceAsync(CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // INFO is a *server-level* command, not an IDatabase command.
            // Find a connected server from the managed multiplexer, preferring
            // its known primary. Sentinel transparently updates discovered
            // endpoints on failover. Never display the selected endpoint.
            // Production ACL already allows INFO; do NOT enable AllowAdmin.
            var servers = redis.GetEndPoints(configuredOnly: false)
                .Select(redis.GetServer)
                .Where(server => server.IsConnected)
                .ToArray();
            var current = servers.FirstOrDefault(server => !server.IsReplica)
                ?? servers.FirstOrDefault();
            if (current is null)
                throw new InvalidOperationException(
                    "Redis replication metadata unavailable.");

            var raw = await current.InfoRawAsync("replication")
                .WaitAsync(limit.Token);
            var observation = RedisReplicationInfo.Parse(raw);
            MultiplayerMetrics.RecordRedisReplication(
                observation, RedisReplicationPollOutcome.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Host shutdown is not a Redis outage.
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
            // Do not log connection strings, endpoints or Redis error text.
            // Telemetry itself is never part of guest authorization/readiness.
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
        // Hosted service startup must not synchronously connect to Redis.
        await Task.Yield();
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
                    // A freshly resolving mux can fail before the sampler
                    // exists. Monitoring never blocks or restarts the API.
                    MultiplayerMetrics.RecordRedisReplication(
                        null, RedisReplicationPollOutcome.Unavailable);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Ordinary host shutdown.
        }
    }
}
