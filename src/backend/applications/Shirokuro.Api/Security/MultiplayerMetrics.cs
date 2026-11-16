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
    public const string IngressRejectionsName = "shirokuro.ingress.rejections";
    public const string GuestCookieAuthorityName = "shirokuro.session.cookie_authority";
    public const string RosterPostReadAuthorityName = "shirokuro.roster.post_read_authority";
    public const string ReadinessProbesName = "shirokuro.readiness.probes";
    public const string NodeDrainTransitionsName = "shirokuro.node.drain.transitions";
    public const string SignalRDrainAbortAttemptsName =
        "shirokuro.signalr.drain_abort_attempts";
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
    private static readonly Counter<long> IngressRejections = Meter.CreateCounter<long>(
        IngressRejectionsName, unit: "{rejection}",
        description: "Early server-side ingress guard rejections before guest Redis validation and quota.");
    private static readonly Counter<long> GuestCookieAuthority = Meter.CreateCounter<long>(
        GuestCookieAuthorityName, unit: "{validation}",
        description: "Actual guest-cookie authority validation outcomes; not unique guests.");
    private static readonly Counter<long> RosterPostReadAuthority = Meter.CreateCounter<long>(
        RosterPostReadAuthorityName, unit: "{validation}",
        description: "Completed guest authority checks after authorized roster reads, not unique guests or rooms.");
    private static readonly Counter<long> ReadinessProbes = Meter.CreateCounter<long>(
        ReadinessProbesName, unit: "{probe}",
        description: "Executed node-readiness probes, excluding cache hits.");
    private static readonly Counter<long> NodeDrainTransitions =
        Meter.CreateCounter<long>(NodeDrainTransitionsName, unit: "{transition}",
            description: "First server-owned transition into drain on a node, not readiness probe count.");
    private static readonly Counter<long> SignalRDrainAbortAttempts =
        Meter.CreateCounter<long>(SignalRDrainAbortAttemptsName, unit: "{attempt}",
            description: "SignalR transport abort attempts made by the local connection registry, not confirmed network closes.");
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

    /// <summary>
    /// Early drops can bypass both Redis quota and guest auth; report them
    /// without request-derived labels. Exactly one finite reason per denied
    /// request, never its path, Origin, IP, cookie or room identifier.
    /// </summary>
    public static void RecordIngressRejection(IngressRejectionReason reason)
    {
        var value = reason switch
        {
            IngressRejectionReason.RoomId => "room-id",
            IngressRejectionReason.MutationOrigin => "mutation-origin",
            IngressRejectionReason.HubOrigin => "hub-origin",
            IngressRejectionReason.LobbyBodySize => "lobby-body-size",
            IngressRejectionReason.LobbyMediaType => "lobby-media-type",
            IngressRejectionReason.GuestBody => "session-body",
            IngressRejectionReason.Host => "host",
            IngressRejectionReason.Transport => "transport",
            IngressRejectionReason.UnknownApiRoute => "unknown-route",
            IngressRejectionReason.MethodNotAllowed => "method-not-allowed",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        IngressRejections.Add(1, new KeyValuePair<string, object?>("reason", value));
    }

    /// <summary>
    /// Exactly one decision for each signed cookie validated or rejected
    /// by ASP.NET cookie authentication. Early ingress rejections,
    /// anonymous traffic and public probes must not record false validations.
    /// No session, token, path, IP or exception is an attribute.
    /// </summary>
    public static void RecordGuestCookieAuthority(GuestCookieValidationOutcome outcome)
    {
        var value = outcome switch
        {
            GuestCookieValidationOutcome.Active => "active",
            GuestCookieValidationOutcome.Inactive => "inactive",
            GuestCookieValidationOutcome.Unavailable => "unavailable",
            GuestCookieValidationOutcome.RecoveryCutoff => "recovery-cutoff",
            GuestCookieValidationOutcome.UnsupportedTicket => "unsupported-ticket",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        GuestCookieAuthority.Add(1,
            new KeyValuePair<string, object?>("outcome", value));
    }

    /// <summary>
    /// Count an actually completed guest revalidation AFTER a member-scoped
    /// roster read, not cookie middleware auth, requests, unique users or
    /// successful deliveries. Both dimensions are closed server-side enums;
    /// never attach room ID, invitation, identity, IP, path or exception text.
    /// </summary>
    public static void RecordRosterPostReadAuthority(
        RosterPostReadSurface surface, GuestSessionAuthorityStatus outcome)
    {
        var surfaceName = surface switch
        {
            RosterPostReadSurface.HttpRefresh => "http-refresh",
            RosterPostReadSurface.HttpPeek => "http-peek",
            RosterPostReadSurface.SignalR => "signalr",
            _ => throw new ArgumentOutOfRangeException(nameof(surface))
        };
        var outcomeName = outcome switch
        {
            GuestSessionAuthorityStatus.Active => "active",
            GuestSessionAuthorityStatus.Inactive => "inactive",
            GuestSessionAuthorityStatus.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
        RosterPostReadAuthority.Add(1,
            new KeyValuePair<string, object?>("surface", surfaceName),
            new KeyValuePair<string, object?>("outcome", outcomeName));
    }

    public static void RecordReadinessProbe(bool ready) =>
        ReadinessProbes.Add(1, new KeyValuePair<string, object?>(
            "outcome", ready ? "ready" : "unavailable"));

    /// <summary>
    /// Exactly one *first* lifecycle transition per node. Callers enforce
    /// the one-shot state with Interlocked; the enum is the only tag input.
    /// SIGTERM after SIGUSR1 must not emit a second transition.
    /// </summary>
    public static void RecordNodeDrain(NodeDrainCause cause)
    {
        var label = cause switch
        {
            NodeDrainCause.PreStop => "prestop",
            NodeDrainCause.HostShutdown => "host-shutdown",
            _ => throw new ArgumentOutOfRangeException(nameof(cause))
        };
        NodeDrainTransitions.Add(1,
            new KeyValuePair<string, object?>("cause", label));
    }

    /// <summary>
    /// Only a local LobbyConnectionDrainRegistry Abort() attempt is counted:
    /// no socket-closure guarantee, player ID, room, IP, cookie or connection
    /// ID ever enters this instrument. Calls after the preStop snapshot
    /// are classified as 'late-connection' regardless of the node cause.
    /// </summary>
    public static void RecordSignalRDrainAbortAttempt(SignalRDrainAbortReason reason)
    {
        var label = reason switch
        {
            SignalRDrainAbortReason.PreStop => "prestop",
            SignalRDrainAbortReason.HostShutdown => "host-shutdown",
            SignalRDrainAbortReason.LateConnection => "late-connection",
            _ => throw new ArgumentOutOfRangeException(nameof(reason))
        };
        SignalRDrainAbortAttempts.Add(1,
            new KeyValuePair<string, object?>("reason", label));
    }

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

public enum RosterPostReadSurface { HttpRefresh, HttpPeek, SignalR }

public enum MutationOutcome { Allowed, Limited, Unavailable }

public enum NodeDrainCause { PreStop, HostShutdown }
public enum SignalRDrainAbortReason { PreStop, HostShutdown, LateConnection }

/// <summary>Finite results of a real signed guest cookie validation.</summary>
public enum GuestCookieValidationOutcome
{
    Active, Inactive, Unavailable, RecoveryCutoff, UnsupportedTicket
}

/// <summary>Fixed ingress rejection categories, never raw input data.</summary>
public enum IngressRejectionReason
{
    RoomId,
    MutationOrigin,
    HubOrigin,
    LobbyBodySize,
    LobbyMediaType,
    GuestBody,
    Host,
    Transport,
    UnknownApiRoute,
    MethodNotAllowed
}

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
