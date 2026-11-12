namespace Shirokuro.Api.Security;

/// <summary>
/// Server-owned opt-out of optional cookie auth for public liveness/readiness
/// probes that never use a principal. Never attach to player routes:
/// protected APIs and SignalR must always verify the guest registry.
/// </summary>
public sealed record PublicProbeEndpoint();
