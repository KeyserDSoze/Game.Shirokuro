namespace Shirokuro.Api.Security;

/// <summary>
/// Server-owned marker for public liveness/readiness endpoints that never
/// consume the authenticated principal. Cookie validation skips the optional
/// guest Redis lookup ONLY on these endpoints; all protected API and SignalR
/// operations still verify the authoritative session registry.
/// </summary>
public sealed record PublicProbeEndpoint();
