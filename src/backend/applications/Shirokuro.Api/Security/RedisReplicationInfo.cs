using System.Globalization;

namespace Shirokuro.Api.Security;

/// <summary>
/// Parse a bounded Redis INFO replication payload without retaining server
/// addresses, replication IDs, client data, or credentials. This is the
/// reported replica acknowledgement lag, NOT byte offset lag / RPO.
/// </summary>
public static class RedisReplicationInfo
{
    public const string InvalidInfo = "Redis replication information is unavailable.";
    private const int MaxInfoLength = 65_536;

    public static RedisReplicationSnapshot Parse(string? info)
    {
        if (string.IsNullOrWhiteSpace(info) || info.Length > MaxInfoLength)
            throw new FormatException(InvalidInfo);

        RedisReplicationRole? role = null;
        RedisReplicaLink? link = null;
        int? connected = null;
        int? maxLag = null;
        // A primary reports exactly one slaveN row per connected replica.
        // Refuse incomplete or duplicate rows rather than reporting success.
        var replicaIndices = new HashSet<int>();

        foreach (var line in info.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
                continue;
            var key = trimmed[..colon];
            var value = trimmed[(colon + 1)..];
            switch (key)
            {
                case "role":
                    if (role is not null)
                        throw new FormatException(InvalidInfo);
                    role = value switch
                    {
                        "master" => RedisReplicationRole.Primary,
                        "slave" or "replica" => RedisReplicationRole.Replica,
                        _ => throw new FormatException(InvalidInfo)
                    };
                    break;
                case "connected_slaves":
                    if (connected is not null ||
                        !int.TryParse(value, NumberStyles.None,
                            CultureInfo.InvariantCulture, out var replicas))
                        throw new FormatException(InvalidInfo);
                    connected = replicas;
                    break;
                case "master_link_status":
                    if (link is not null)
                        throw new FormatException(InvalidInfo);
                    link = value switch
                    {
                        "up" => RedisReplicaLink.Up,
                        "down" => RedisReplicaLink.Down,
                        _ => throw new FormatException(InvalidInfo)
                    };
                    break;
                default:
                    // Redis INFO contains replica addresses and IDs.
                    // Only the fixed slaveN rows and their state/lag fields
                    // may contribute to the non-identifying ACK-lag metric.
                    if (!key.StartsWith("slave", StringComparison.Ordinal) ||
                        key.Length <= 5 ||
                        !key[5..].All(c => c is >= '0' and <= '9'))
                        break;
                    if (!int.TryParse(key.AsSpan(5), NumberStyles.None,
                            CultureInfo.InvariantCulture, out var replicaIndex) ||
                        !replicaIndices.Add(replicaIndex))
                        throw new FormatException(InvalidInfo);

                    string? state = null;
                    int? lag = null;
                    foreach (var field in value.Split(','))
                    {
                        if (field.StartsWith("state=", StringComparison.Ordinal))
                        {
                            if (state is not null)
                                throw new FormatException(InvalidInfo);
                            state = field["state=".Length..];
                        }
                        else if (field.StartsWith("lag=", StringComparison.Ordinal))
                        {
                            if (lag is not null ||
                                !int.TryParse(field["lag=".Length..],
                                    NumberStyles.None, CultureInfo.InvariantCulture,
                                    out var seconds))
                                throw new FormatException(InvalidInfo);
                            lag = seconds;
                        }
                    }
                    if (string.IsNullOrEmpty(state))
                        throw new FormatException(InvalidInfo);
                    if (state == "online")
                    {
                        if (lag is null)
                            throw new FormatException(InvalidInfo);
                        maxLag = Math.Max(maxLag ?? 0, lag.Value);
                    }
                    break;
            }
        }

        return role switch
        {
            RedisReplicationRole.Primary when connected is not null &&
                connected == replicaIndices.Count && link is null => new RedisReplicationSnapshot(
                    RedisReplicationRole.Primary,
                    RedisReplicaLink.NotApplicable, connected, maxLag),
            RedisReplicationRole.Replica when link is not null =>
                new RedisReplicationSnapshot(
                    RedisReplicationRole.Replica, link.Value, null, null),
            _ => throw new FormatException(InvalidInfo)
        };
    }
}
