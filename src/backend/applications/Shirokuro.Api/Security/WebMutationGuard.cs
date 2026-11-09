using Microsoft.Extensions.Hosting;

namespace Shirokuro.Api.Security;

/// <summary>
/// Browser CSRF protection for cookie-backed writes: a custom header forces
/// cross-origin CORS preflight, plus a strict Origin allow-list.
/// </summary>
public static class WebMutationGuard
{
    public const string RequestHeaderName = "X-Shirokuro-Request";
    public const string RequestHeaderValue = "1";

    public static bool Allows(HttpContext context, IHostEnvironment environment)
    {
        var request = context.Request;
        if (!request.Headers.TryGetValue(RequestHeaderName, out var intent) ||
            intent.Count != 1 || intent[0] != RequestHeaderValue ||
            !request.Headers.TryGetValue("Origin", out var origins) ||
            origins.Count != 1)
            return false;

        var origin = origins[0];
        if (string.IsNullOrWhiteSpace(origin))
            return false;

        var ownOrigin = request.Scheme + "://" + request.Host.Value;
        if (string.Equals(origin, ownOrigin, StringComparison.OrdinalIgnoreCase))
            return true;

        return environment.IsDevelopment() &&
               (string.Equals(origin, "http://localhost:5173", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(origin, "http://127.0.0.1:5173", StringComparison.OrdinalIgnoreCase));
    }
}

