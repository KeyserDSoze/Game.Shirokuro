using System.Diagnostics.Metrics;

namespace Shirokuro.Api.Security;

/// <summary>
/// Aggregate security/availability metrics. Every label value comes from a
/// closed server-defined enum; no request paths, IPs, sessions, player IDs,
/// invitation codes or exception messages are accepted as metric attributes.
/// Exporters are deliberately NOT registered by the web application.
/// </summary>
public static class MultiplayerMetrics
{
    public const string MeterName = "Shirokuro.Api.Multiplayer";
    public const string MeterVersion = "0.0.1";
    public const string MutationDecisionsName = "shirokuro.guest_mutation.decisions";
    public const string ReadinessProbesName = "shirokuro.readiness.probes";
    public const string ExpirySweepsName = "shirokuro.guest_expiry.sweeps";
    public const string ExpiryMembershipsRemovedName = "shirokuro.guest_expiry.memberships_removed";

    private static readonly Meter Meter = new(MeterName, MeterVersion);
    private static readonly Counter<long> MutationDecisions = Meter.CreateCounter<long>(
        MutationDecisionsName, unit: "{decision}",
        description: "Server-side guest session and lobby creation limit decisions.");
    private static readonly Counter<long> ReadinessProbes = Meter.CreateCounter<long>(
        ReadinessProbesName, unit: "{probe}",
        description: "Executed node-readiness probes, excluding cache hits.");
    private static readonly Counter<long> ExpirySweeps = Meter.CreateCounter<long>(
        ExpirySweepsName, unit: "{sweep}",
        description: "Guest-session expiry cleanup sweep outcomes.");
    private static readonly Counter<long> ExpiryMembershipsRemoved = Meter.CreateCounter<long>(
        ExpiryMembershipsRemovedName, unit: "{membership}",
        description: "Aggregate stale lobby memberships removed by guest expiry cleanup.");

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

    public static void RecordExpirySweep(bool sharedRedis, bool success, int membershipsRemoved)
    {
        if (membershipsRemoved < 0)
            throw new ArgumentOutOfRangeException(nameof(membershipsRemoved));
        var mode = sharedRedis ? "redis" : "local";
        ExpirySweeps.Add(1,
            new KeyValuePair<string, object?>("mode", mode),
            new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
        if (success && membershipsRemoved > 0)
            ExpiryMembershipsRemoved.Add(membershipsRemoved,
                new KeyValuePair<string, object?>("mode", mode));
    }
}

public enum MutationOutcome { Allowed, Limited, Unavailable }
