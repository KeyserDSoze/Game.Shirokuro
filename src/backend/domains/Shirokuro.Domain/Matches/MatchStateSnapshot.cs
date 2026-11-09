namespace Shirokuro.Domain.Matches;

/// <summary>
/// Read-only snapshot of all participants. This contains secrets and must not
/// leave the trusted backend; use MatchObservationProjector for client output.
/// </summary>
public sealed class MatchStateSnapshot
{
    public string MatchId { get; }
    public IReadOnlyList<AuthoritativeHeroState> Heroes { get; }

    public MatchStateSnapshot(string matchId, IEnumerable<AuthoritativeHeroState> heroes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        ArgumentNullException.ThrowIfNull(heroes);

        var copied = heroes.ToArray();
        if (copied.Any(hero => hero is null))
        {
            throw new ArgumentException("A match snapshot cannot contain null heroes.", nameof(heroes));
        }

        if (copied.Select(hero => hero.PlayerId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("A match snapshot cannot contain duplicate player IDs.", nameof(heroes));
        }

        MatchId = matchId;
        Heroes = Array.AsReadOnly(copied);
    }
}

