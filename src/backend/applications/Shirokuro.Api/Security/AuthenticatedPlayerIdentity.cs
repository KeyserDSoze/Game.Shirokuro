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
        if (principal is null)
        {
            return null;
        }

        // Claims in an unauthenticated secondary identity are untrusted.
        // Multiple authenticated identities are ambiguous until a single
        // real identity provider and policy have been explicitly selected.
        var authenticated = principal.Identities
            .Where(identity => identity.IsAuthenticated)
            .ToArray();
        if (authenticated.Length != 1)
        {
            return null;
        }

        var identifiers = authenticated[0].FindAll(ClaimTypes.NameIdentifier).ToArray();
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
