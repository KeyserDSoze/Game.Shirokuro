namespace Shirokuro.Domain.Lobbies;

/// <summary>
/// Trusted setup for an unstarted, server-owned lobby. Team identities and
/// the hero roster are supplied by configuration, never inferred as game rules.
/// </summary>
public sealed class LobbyDefinition
{
    private readonly HashSet<string> _teamIds;
    private readonly HashSet<string> _heroIds;

    public string LobbyId { get; }
    public IReadOnlyList<string> TeamIds { get; }
    public IReadOnlyList<string> HeroIds { get; }

    public LobbyDefinition(string lobbyId, IEnumerable<string> teamIds, IEnumerable<string> heroIds)
    {
        LobbyId = RequireId(lobbyId, nameof(lobbyId));
        ArgumentNullException.ThrowIfNull(teamIds);
        ArgumentNullException.ThrowIfNull(heroIds);

        var teams = teamIds.Select(id => RequireId(id, nameof(teamIds))).ToArray();
        var heroes = heroIds.Select(id => RequireId(id, nameof(heroIds))).ToArray();

        if (teams.Length < 2)
        {
            throw new ArgumentException("A team lobby needs at least two configured teams.", nameof(teamIds));
        }

        if (heroes.Length == 0)
        {
            throw new ArgumentException("At least one selectable hero is required.", nameof(heroIds));
        }

        _teamIds = new HashSet<string>(teams, StringComparer.Ordinal);
        _heroIds = new HashSet<string>(heroes, StringComparer.Ordinal);

        if (_teamIds.Count != teams.Length || _heroIds.Count != heroes.Length)
        {
            throw new ArgumentException("Team and hero IDs must be unique and case-sensitive.");
        }

        TeamIds = Array.AsReadOnly(teams);
        HeroIds = Array.AsReadOnly(heroes);
    }

    public bool AllowsTeam(string teamId) => teamId is not null && _teamIds.Contains(teamId);
    public bool AllowsHero(string heroId) => heroId is not null && _heroIds.Contains(heroId);

    internal static string RequireId(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("IDs may not contain leading/trailing whitespace.", parameterName);
        }

        return value;
    }
}

