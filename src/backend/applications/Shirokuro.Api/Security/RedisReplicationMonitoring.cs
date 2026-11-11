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
            // INFO is read-only and supported by the existing scoped Redis
            // application ACL. Never use CONFIG, ROLE mutation or admin auth.
            // StackExchange.Redis routes this command via its managed
            // Sentinel-aware multiplexer after a primary switch.
            var result = await redis.GetDatabase().ExecuteAsync(
                "INFO", "replication").WaitAsync(limit.Token);
            var observation = RedisReplicationInfo.Parse(result.ToString());
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
