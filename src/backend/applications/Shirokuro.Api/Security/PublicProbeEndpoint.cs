namespace Shirokuro.Api.Security;

/// <summary>
/// Server-owned marker for public liveness/readiness endpoints that never
/// consume the authenticated principal. Cookie validation skips the optional
/// guest Redis lookup ONLY on these endpoints; all protected API and SignalR
/// operations still verify the authoritative session registry.
/// </summary>
public sealed record PublicProbeEndpoint();

/// <summary>
/// Separate, server-owned marker for the public deterministic hex board
/// preview. The response uses no cookie identity or private match data.
/// Unlike protected lobby or match routes, it must not need a guest Redis
/// authority lookup merely because the browser attaches a signed cookie.
/// Do not use this marker for any endpoint that consumes HttpContext.User.
/// </summary>
public sealed record PublicBoardPreviewEndpoint();
