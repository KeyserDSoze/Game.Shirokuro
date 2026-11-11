using System.Diagnostics.Metrics;

namespace Shirokuro.Api.Security;

/// <summary>
/// Aggregate security/availability metrics. Every label value comes from a
/// closed server-defined enum; no request paths, IPs, sessions, player IDs,
/// invitation codes or exception messages are accepted as metric attributes.
/// The OTLP exporter is disabled by default and can be opted into only
/// with an explicit validated HTTPS collector outside Development.
/// </summary>
public static class MultiplayerMetrics
{
    public const string MeterName = "Shirokuro.Api.Multiplayer";
    public const string MeterVersion = "0.0.1";
    public const string MutationDecisionsName = "shirokuro.guest_mutation.decisions";
    public const string ReadinessProbesName = "shirokuro.readiness.probes";
    public const string ReadinessDurationSecondsName = "shirokuro.readiness.duration";
    public const string RedisConnectionEventsName = "shirokuro.redis.connection.events";
    public const string RedisReplicationObservationsName =
        "shirokuro.redis.replication.observations";
    public const string RedisConnectedReplicasName =
        "shirokuro.redis.replication.connected_replicas";
    public const string RedisReplicaAckLagSecondsName =
        "shirokuro.redis.replication.replica_ack_lag_seconds";
    public const string ExpirySweepsName = "shirokuro.guest_expiry.sweeps";
    public const string ExpiryMembershipsRemovedName = "shirokuro.guest_expiry.memberships_removed";
    public const string ExpiryUnreadableItemsName = "shirokuro.guest_expiry.unreadable_items";

    private static readonly Meter Meter = new(MeterName, MeterVersion);
    private static readonly Counter<long> MutationDecisions = Meter.CreateCounter<long>(
        MutationDecisionsName, unit: "{decision}",
        description: "Server-side guest session and lobby creation limit decisions.");
    private static readonly Counter<long> ReadinessProbes = Meter.CreateCounter<long>(
        ReadinessProbesName, unit: "{probe}",
        description: "Executed node-readiness probes, excluding cache hits.");
    private static readonly Histogram<double> ReadinessDurationSeconds =
        Meter.CreateHistogram<double>(ReadinessDurationSecondsName, unit: "s",
            description: "Elapsed time of executed Redis/Data Protection readiness probes only.");
    private static readonly Counter<long> RedisConnectionEvents =
        Meter.CreateCounter<long>(RedisConnectionEventsName, unit: "{event}",
            description: "Production Redis connector lifecycle events; no endpoint or identity labels.");
    private static readonly Counter<long> RedisReplicationObservations =
        Meter.CreateCounter<long>(RedisReplicationObservationsName, unit: "{observation}",
            description: "Explicit INFO replication probe result, including failed polls.");
    private static readonly Histogram<double> RedisConnectedReplicas =
        Meter.CreateHistogram<double>(RedisConnectedReplicasName, unit: "{replica}",
            description: "Connected replica count reported in successful primary INFO samples, not a gauge.");
    private static readonly Histogram<double> RedisReplicaAckLagSeconds =
        Meter.CreateHistogram<double>(RedisReplicaAckLagSecondsName, unit: "s",
            description: "Maximum ONLINE replica acknowledgement lag reported by primary INFO, not replication RPO.");
    private static readonly Counter<long> ExpirySweeps = Meter.CreateCounter<long>(
        ExpirySweepsName, unit: "{sweep}",
        description: "Guest-session expiry cleanup sweep outcomes.");
    private static readonly Counter<long> ExpiryMembershipsRemoved = Meter.CreateCounter<long>(
        ExpiryMembershipsRemovedName, unit: "{membership}",
        description: "Aggregate stale lobby memberships removed, including partial failed sweeps.");
    private static readonly Counter<long> ExpiryUnreadableItems = Meter.CreateCounter<long>(
        ExpiryUnreadableItemsName, unit: "{observation}",
        description: "Unreadable guest expiry work item observations; not distinct entries.");

    public static void RecordMutation(GuestMutationKind policy, MutationOutcome outcome)
    {
        var policyName = policy switch
        {
            GuestMutationKind.SessionIssuance => "session",
            GuestMutationKind.RoomCreation => "lobby",
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
        var outcomeName = outcome switch
        {
            MutationOutcome.Allowed => "allowed",
            MutationOutcome.Limited => "limited",
            MutationOutcome.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        MutationDecisions.Add(1,
            new KeyValuePair<string, object?>("policy", policyName),
            new KeyValuePair<string, object?>("outcome", outcomeName));
    }

    public static void RecordReadinessProbe(bool ready) =>
        ReadinessProbes.Add(1, new KeyValuePair<string, object?>(
            "outcome", ready ? "ready" : "unavailable"));

    /// <summary>
    /// Record one real Production readiness probe, not every HTTP waiter or
    /// cached result. Stage is the bounded *phase* reached when a probe
    /// failed, not a claim that one specific Redis connector caused it.
    /// Stopwatch-monotonic seconds and closed tags prevent sensitive data
    /// from becoming dimensions in external dashboards.
    /// </summary>
    public static void RecordReadinessDuration(
        bool ready, ReadinessProbeStage stage, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        if (ready != (stage == ReadinessProbeStage.Success))
            throw new ArgumentException(
                "Readiness result and technical stage must agree.", nameof(stage));

        var stageName = stage switch
        {
            ReadinessProbeStage.Success => "ok",
            ReadinessProbeStage.RedisWrite => "redis-write",
            ReadinessProbeStage.SessionRead => "session-read",
            ReadinessProbeStage.KeyRing => "key-ring",
            ReadinessProbeStage.Timeout => "timeout",
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };

        ReadinessDurationSeconds.Record(seconds,
            new KeyValuePair<string, object?>("outcome",
                ready ? "ready" : "unavailable"),
            new KeyValuePair<string, object?>("stage", stageName));
    }

    /// <summary>
    /// Physical Redis client events are NOT a health check, a primary quorum
    /// vote or proof of successful failover. Never encode endpoint, TLS
    /// principal, exception, client address, room or guest identifiers.
    /// </summary>
    public static void RecordRedisConnection(
        RedisConnector connector, RedisConnectionTransition transition)
    {
        var source = connector switch
        {
            RedisConnector.Lobby => "lobby",
            RedisConnector.GuestSessions => "session",
            _ => throw new ArgumentOutOfRangeException(nameof(connector))
        };
        var outcome = transition switch
        {
            RedisConnectionTransition.Failed => "failed",
            RedisConnectionTransition.Restored => "restored",
            RedisConnectionTransition.ConfigurationChanged => "configuration-changed",
            _ => throw new ArgumentOutOfRangeException(nameof(transition))
        };
        RedisConnectionEvents.Add(1,
            new KeyValuePair<string, object?>("client", source),
            new KeyValuePair<string, object?>("event", outcome));
    }

    /// <summary>
    /// Record one read-only INFO replication poll. The report can contain
    /// sensitive replica IPs and authentication context, but NONE of its
    /// string fields can become a metric dimension. Missing/failed samples
    /// never report a healthy zero-replica measurement.
    /// </summary>
    public static void RecordRedisReplication(
        RedisReplicationSnapshot? observation, RedisReplicationPollOutcome outcome)
    {
        var result = outcome switch
        {
            RedisReplicationPollOutcome.Ok => "ok",
            RedisReplicationPollOutcome.Unavailable => "unavailable",
            RedisReplicationPollOutcome.Timeout => "timeout",
            RedisReplicationPollOutcome.Invalid => "invalid",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        if ((outcome == RedisReplicationPollOutcome.Ok) != (observation is not null))
            throw new ArgumentException("Probe outcome and observation must agree.", nameof(observation));

        var role = observation?.Role switch
        {
            RedisReplicationRole.Primary => "master",
            RedisReplicationRole.Replica => "replica",
            null => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(observation))
        };
        var link = observation?.Link switch
        {
            RedisReplicaLink.Up => "up",
            RedisReplicaLink.Down => "down",
            RedisReplicaLink.NotApplicable or null => "na",
            _ => throw new ArgumentOutOfRangeException(nameof(observation))
        };
        if (observation is { } state &&
            (state.Role == RedisReplicationRole.Primary
                ? state.Link != RedisReplicaLink.NotApplicable ||
                  state.ConnectedReplicas is null or < 0 ||
                  state.MaxOnlineAckLagSeconds is < 0
                : state.Role != RedisReplicationRole.Replica ||
                  state.Link == RedisReplicaLink.NotApplicable ||
                  state.ConnectedReplicas is not null ||
                  state.MaxOnlineAckLagSeconds is not null))
            throw new ArgumentException("Invalid replication sample.", nameof(observation));

        RedisReplicationObservations.Add(1,
            new KeyValuePair<string, object?>("outcome", result),
            new KeyValuePair<string, object?>("role", role),
            new KeyValuePair<string, object?>("link", link));

        if (observation is { Role: RedisReplicationRole.Primary } primary)
        {
            RedisConnectedReplicas.Record(primary.ConnectedReplicas!.Value);
            if (primary.MaxOnlineAckLagSeconds is int lag)
                RedisReplicaAckLagSeconds.Record(lag);
        }
    }

    // No tags: malformed or unreadable encrypted data is never an identity,
    // an exception label or a Redis key exposed to an exporter.
    public static void RecordUnreadableExpiryItem() =>
        ExpiryUnreadableItems.Add(1);

    public static void RecordExpirySweep(bool sharedRedis, bool success, int membershipsRemoved)
    {
        if (membershipsRemoved < 0)
            throw new ArgumentOutOfRangeException(nameof(membershipsRemoved));
        var mode = sharedRedis ? "redis" : "local";
        ExpirySweeps.Add(1,
            new KeyValuePair<string, object?>("mode", mode),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
        // Successful removals in a partially failed batch must also be counted.
        // No guest identities or exception details are ever metric tags.
        if (membershipsRemoved > 0)
            ExpiryMembershipsRemoved.Add(membershipsRemoved,
                new KeyValuePair<string, object?>("mode", mode));
    }
}

public enum MutationOutcome { Allowed, Limited, Unavailable }

/// <summary>Finite technical readiness probe phase; never error text.</summary>
public enum ReadinessProbeStage
{
    Success,
    RedisWrite,
    SessionRead,
    KeyRing,
    Timeout
}

public enum RedisConnector { Lobby, GuestSessions }

public enum RedisConnectionTransition { Failed, Restored, ConfigurationChanged }

public enum RedisReplicationRole { Primary, Replica }
public enum RedisReplicaLink { NotApplicable, Up, Down }
public enum RedisReplicationPollOutcome { Ok, Unavailable, Timeout, Invalid }

public readonly record struct RedisReplicationSnapshot(
    RedisReplicationRole Role, RedisReplicaLink Link,
    int? ConnectedReplicas, int? MaxOnlineAckLagSeconds);
