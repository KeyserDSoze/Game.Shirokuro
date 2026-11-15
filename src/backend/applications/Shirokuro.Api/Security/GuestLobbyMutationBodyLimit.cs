using Microsoft.AspNetCore.Http.Features;

namespace Shirokuro.Api.Security;

/// <summary>
/// Server-owned marker used only on JSON pre-match lobby mutations.
/// Applied before guest Redis authorization and room-creation quota checks.
/// </summary>
public sealed record GuestLobbyMutationBodyLimitRequired();

/// <summary>
/// Lobby mutation DTOs are small. Reject a declared oversized body without
/// reading it or performing a guest Redis lookup, and configure the server
/// body-size feature to cap streaming/chunked bodies as they are consumed.
/// This is load shedding, not an anti-bot or authorization policy.
/// </summary>
public static class GuestLobbyMutationBodyLimit
{
    public const long MaxBytes = 4096;

    public static bool TryApply(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Request.ContentLength is > MaxBytes)
            return false;

        // Kestrel enforces MaxRequestBodySize while reading even if
        // Content-Length is absent (e.g., Transfer-Encoding: chunked).
        // Do not relax a smaller host-wide limit.
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false } &&
            (feature.MaxRequestBodySize is null or > MaxBytes))
            feature.MaxRequestBodySize = MaxBytes;

        return true;
    }
}

/// <summary>
/// JSON-bound lobby commands never support an explicitly non-JSON media type.
/// Match ASP.NET Core's JSON body binder before reading the signed guest
/// ticket from Redis. Missing Content-Type stays with the normal binder.
/// </summary>
public static class GuestLobbyMutationMediaTypeGuard
{
    public static bool Allows(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.Request.Headers.ContainsKey("Content-Type") ||
               context.Request.HasJsonContentType();
    }
}
