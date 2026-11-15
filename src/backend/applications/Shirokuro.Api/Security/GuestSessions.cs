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

    // Per-request, server-owned signal: a signed cookie contained a principal
    // this application cannot authenticate (no verified account scheme).
    // Prevent the public guest issuance route from silently replacing it.
    public static readonly object UnsupportedTicketRequestMarker = new();

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

    /// <summary>
    /// This application's only configured cookie scheme currently issues
    /// server-generated guest sessions; there is NO verified account provider.
    /// Reject legacy/non-guest principals instead of accidentally treating an
    /// otherwise valid Data Protection ticket as an authenticated account.
    /// A future account provider must use an explicit separate authentication
    /// scheme and authorization policy, not this guest-cookie fallback.
    /// </summary>
    public static bool IsCanonicalGuest(ClaimsPrincipal? user)
    {
        if (!IsGuest(user))
            return false;

        var id = AuthenticatedPlayerIdentity.FromPrincipal(user);
        if (id is not { Length: 38 } ||
            !id.StartsWith("guest-", StringComparison.Ordinal))
            return false;

        foreach (var digit in id.AsSpan(6))
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;

        return true;
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


