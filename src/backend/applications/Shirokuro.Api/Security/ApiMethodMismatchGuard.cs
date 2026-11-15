namespace Shirokuro.Api.Security;

/// <summary>
/// The ASP.NET Core router creates a synthetic method-mismatch endpoint when
/// a path exists but none of its handlers accept the request method. That
/// endpoint executes no application handler and carries no authorization
/// metadata: a signed guest cookie need not consult Redis to return its 405.
/// Keep this check restricted to the framework-owned fallback on API/Hub
/// namespaces; real endpoints must continue to validate the cookie.
/// </summary>
public static class ApiMethodMismatchGuard
{
    public static bool IsFrameworkMethodNotAllowed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        return endpoint is
               {
                   DisplayName: "405 HTTP Method Not Supported",
                   Metadata.Count: 0
               } &&
               (context.Request.Path.StartsWithSegments(
                    "/api", StringComparison.OrdinalIgnoreCase) ||
                context.Request.Path.StartsWithSegments(
                    "/hubs", StringComparison.OrdinalIgnoreCase));
    }
}
