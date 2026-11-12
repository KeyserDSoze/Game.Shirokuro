using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Shirokuro.Api.Security;

/// <summary>
/// Server-owned endpoint marker for all cookie-backed write operations.
/// Evaluated before authentication: untrusted Origin/intent must never
/// trigger even a guest Redis ticket lookup.
/// </summary>
public sealed record WebMutationGuardRequired();

/// <summary>
/// Cookie-backed write protection. An arbitrary HTTP Host or forwarded Host
/// is never a trust source: production origins require explicit configuration.
/// </summary>
public static class WebMutationGuard
{
    public const string RequestHeaderName = "X-Shirokuro-Request";
    public const string RequestHeaderValue = "1";

    private static readonly string[] DevelopmentViteOrigins =
    [
        "http://localhost:5173",
        "http://127.0.0.1:5173"
    ];

    public static bool Allows(HttpContext context, IHostEnvironment environment)
    {
        var request = context.Request;
        if (!request.Headers.TryGetValue(RequestHeaderName, out var intent) ||
            intent.Count != 1 || intent[0] != RequestHeaderValue ||
            !request.Headers.TryGetValue("Origin", out var origins) ||
            origins.Count != 1 ||
            !TryParseOrigin(origins[0], out var origin))
            return false;

        if (environment.IsDevelopment())
        {
            // Development is deliberately limited to loopback traffic:
            // the direct API same-origin and the two explicitly known Vite
            // development origins. A matching attacker-controlled Host must
            // not grant access to evil.example or other remote hosts.
            if (!origin!.IsLoopback)
                return false;

            if (DevelopmentViteOrigins.Any(value =>
                string.Equals(value, origin.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase)))
                return true;

            var localRequestOrigin = request.Scheme + "://" + request.Host.Value;
            return TryParseOrigin(localRequestOrigin, out var own) &&
                   own!.IsLoopback && SameOrigin(origin, own);
        }

        // Fail closed in Production, Staging and any other non-Development
        // environment when Security:AllowedOrigins is missing or malformed.
        // Only explicitly configured HTTPS origins are trusted; do not read
        // X-Forwarded-Host/Proto or base trust on request.Host.
        if (origin!.Scheme != Uri.UriSchemeHttps)
            return false;

        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        return configuration.GetSection("Security:AllowedOrigins").GetChildren()
            .Any(entry => TryParseOrigin(entry.Value, out var allowed) &&
                          allowed!.Scheme == Uri.UriSchemeHttps &&
                          SameOrigin(origin, allowed));
    }

    private static bool SameOrigin(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;

    /// <summary>
    /// Origin headers must contain exactly one canonical scheme://authority,
    /// never a path, userinfo, wildcard, query, fragment or comma-list.
    /// Comparing with the parsed authority also rejects Uri normalizations
    /// which might otherwise reinterpret an attacker-supplied string.
    /// </summary>
    private static bool TryParseOrigin(string? value, out Uri? origin)
    {
        origin = null;
        if (string.IsNullOrEmpty(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            parsed.UserInfo.Length != 0 ||
            parsed.AbsolutePath != "/" ||
            parsed.Query.Length != 0 ||
            parsed.Fragment.Length != 0 ||
            !string.Equals(value, parsed.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase))
            return false;

        origin = parsed;
        return true;
    }
}
