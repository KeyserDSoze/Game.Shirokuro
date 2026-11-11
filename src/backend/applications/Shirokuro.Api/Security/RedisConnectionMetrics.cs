using StackExchange.Redis;

namespace Shirokuro.Api.Security;

/// <summary>
/// Observe only finite event *types* from each independent Production Redis
/// multiplexer. No IP address, endpoint, connection string, exception, error
/// message or player data is read from event args or put in telemetry.
/// </summary>
public static class RedisConnectionMetrics
{
    public static IConnectionMultiplexer Observe(
        IConnectionMultiplexer connection, RedisConnector connector)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connector is not (RedisConnector.Lobby or RedisConnector.GuestSessions))
            throw new ArgumentOutOfRangeException(nameof(connector));

        connection.ConnectionFailed += (_, _) =>
            MultiplayerMetrics.RecordRedisConnection(
                connector, RedisConnectionTransition.Failed);
        connection.ConnectionRestored += (_, _) =>
            MultiplayerMetrics.RecordRedisConnection(
                connector, RedisConnectionTransition.Restored);
        connection.ConfigurationChanged += (_, _) =>
            MultiplayerMetrics.RecordRedisConnection(
                connector, RedisConnectionTransition.ConfigurationChanged);
        return connection;
    }
}
