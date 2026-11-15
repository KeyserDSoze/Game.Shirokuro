using Microsoft.AspNetCore.Http.Features;

namespace Shirokuro.Api.Security;

/// <summary>
/// Server-owned endpoint marker: guest issuance takes no request body.
/// Reject attempts before guest-cookie authority and admission quotas.
/// </summary>
public sealed record GuestSessionEmptyBodyRequired();

public static class GuestSessionBodyGuard
{
    /// <summary>
    /// HTTP/2 and HTTP/3 can carry DATA without Content-Length or the
    /// HTTP/1.1 Transfer-Encoding header. Use the server's body-detection
    /// feature in addition to framing hints. Do not read or buffer the body.
    /// </summary>
    public static bool Allows(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.ContentLength is not > 0 &&
               !context.Request.Headers.ContainsKey("Transfer-Encoding") &&
               context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody != true;
    }
}
