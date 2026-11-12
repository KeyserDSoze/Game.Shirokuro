namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Server-issued invitation identifiers have exactly 128 bits of random
/// entropy, rendered as lowercase hex after the room- prefix. Reject input
/// that can never identify a room before hashing it or consulting Redis.
/// No case folding, trimming or Unicode normalization is permitted.
/// </summary>
public static class CanonicalLobbyId
{
    public static bool IsValid(string? value)
    {
        if (value is not { Length: 37 } ||
            !value.StartsWith("room-", StringComparison.Ordinal))
            return false;

        foreach (var digit in value.AsSpan(5))
        {
            if (digit is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Server-owned endpoint metadata: validate the named route parameter before
/// authentication so impossible room codes do not spend guest Redis reads.
/// Never attach to room creation or to an endpoint using a different ID.
/// </summary>
public sealed record CanonicalLobbyIdRequired(string RouteParameter);
