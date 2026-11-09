using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Shirokuro.Api.Security;

/// <summary>Server-generated pseudonymous guest sessions, not verified accounts.</summary>
public static class GuestSessions
{
    public const string SessionKindClaim = "shirokuro:session_kind";
    public const string GuestKind = "guest";
    public const string SessionIdClaim = "shirokuro:session_id";

    public static string NewPlayerId()
        => "guest-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static ClaimsPrincipal CreatePrincipal(string serverGeneratedPlayerId, string serverGeneratedSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverGeneratedPlayerId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(serverGeneratedPlayerId,
            "^guest-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Not a generated guest ID.", nameof(serverGeneratedPlayerId));
        ArgumentException.ThrowIfNullOrWhiteSpace(serverGeneratedSessionId);
        if (!System.Text.RegularExpressions.Regex.IsMatch(serverGeneratedSessionId,
            "^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Not a generated session token.", nameof(serverGeneratedSessionId));

        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, serverGeneratedPlayerId),
            new Claim(SessionKindClaim, GuestKind),
            new Claim(SessionIdClaim, serverGeneratedSessionId)
        ], CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static bool IsGuest(ClaimsPrincipal? user)
    {
        if (AuthenticatedPlayerIdentity.FromPrincipal(user) is null)
            return false;

        var authenticated = user!.Identities.Single(identity => identity.IsAuthenticated);
        var kinds = authenticated.FindAll(SessionKindClaim).ToArray();
        return kinds.Length == 1 &&
               string.Equals(kinds[0].Value, GuestKind, StringComparison.Ordinal);
    }
}


