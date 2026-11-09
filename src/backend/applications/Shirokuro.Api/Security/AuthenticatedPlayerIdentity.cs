using System.Security.Claims;

namespace Shirokuro.Api.Security;

/// <summary>
/// Only a validated ASP.NET Core ClaimsPrincipal is acceptable.
/// Never trust X-Player-Id, user-controlled HTTP headers or query parameters.
/// Reject ambiguous/missing identity claims even in a signed-in session.
/// </summary>
public static class AuthenticatedPlayerIdentity
{
    public static string? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var identifiers = principal.FindAll(ClaimTypes.NameIdentifier).ToArray();
        if (identifiers.Length != 1)
        {
            return null;
        }

        var value = identifiers[0].Value;
        return string.IsNullOrWhiteSpace(value) ||
               !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            ? null
            : value;
    }
}

