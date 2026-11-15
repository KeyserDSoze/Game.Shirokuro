using Microsoft.AspNetCore.Http.Features;
using Shirokuro.Api.Lobbies;

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

    /// <summary>
    /// ASP.NET Core's AcceptsMatcherPolicy can replace a JSON DTO endpoint
    /// with a synthetic 415 *during routing*. Its endpoint metadata is then
    /// lost, BEFORE the usual pre-auth metadata guard can reject the media
    /// type. Recognize that exact framework rejection on ONLY the five
    /// server-defined JSON lobby methods. This never grants authorization.
    ///
    /// Return an invalid room ID flag so canonical 404 still precedes 415.
    /// All path strings are used only to identify the framework fallback,
    /// never exported as metric labels or trusted as a guest identity.
    /// </summary>
    public static bool IsFrameworkUnsupportedLobbyRoute(
        HttpContext context, out bool invalidRoomId)
    {
        ArgumentNullException.ThrowIfNull(context);
        invalidRoomId = false;
        if (!string.Equals(context.GetEndpoint()?.DisplayName,
                "415 HTTP Unsupported Media Type", StringComparison.Ordinal) ||
            !context.Request.Headers.ContainsKey("Content-Type") ||
            Allows(context))
            return false;

        var path = context.Request.Path.Value?.TrimEnd('/');
        if (string.IsNullOrEmpty(path))
            return false;

        if (HttpMethods.IsPost(context.Request.Method) &&
            string.Equals(path, "/api/lobbies", StringComparison.OrdinalIgnoreCase))
            return true;

        const string prefix = "/api/lobbies/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var tail = path.AsSpan(prefix.Length);
        var separator = tail.IndexOf('/');
        if (separator < 0)
            return false;
        var room = tail[..separator];
        var action = tail[(separator + 1)..];

        var method = context.Request.Method;
        var isKnown = (HttpMethods.IsPost(method) &&
                       action.Equals("join".AsSpan(), StringComparison.OrdinalIgnoreCase)) ||
                      (HttpMethods.IsPatch(method) &&
                       (action.Equals("hero".AsSpan(), StringComparison.OrdinalIgnoreCase) ||
                        action.Equals("team".AsSpan(), StringComparison.OrdinalIgnoreCase))) ||
                      (HttpMethods.IsDelete(method) &&
                       action.Equals("membership".AsSpan(), StringComparison.OrdinalIgnoreCase));
        if (!isKnown)
            return false;

        invalidRoomId = !CanonicalLobbyId.IsValid(room.ToString());
        return true;
    }
}
