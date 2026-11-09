using Shirokuro.Domain.Geometry;

namespace Shirokuro.Domain.Matches;

/// <summary>
/// Private server-side state. Never serialize this type in a response to players.
/// Placement and intent validity must be checked by future gameplay handlers.
/// </summary>
public sealed record AuthoritativeHeroState
{
    public string PlayerId { get; }
    public string TeamId { get; }
    public string HeroId { get; }
    public HexCoordinate Position { get; }
    public HexCoordinate? PlannedDestination { get; }

    public AuthoritativeHeroState(
        string playerId,
        string teamId,
        string heroId,
        HexCoordinate position,
        HexCoordinate? plannedDestination)
    {
        PlayerId = RequireId(playerId, nameof(playerId));
        TeamId = RequireId(teamId, nameof(teamId));
        HeroId = RequireId(heroId, nameof(heroId));
        Position = position;
        PlannedDestination = plannedDestination;
    }

    private static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value != value.Trim())
        {
            throw new ArgumentException("Identifiers may not contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }
}

