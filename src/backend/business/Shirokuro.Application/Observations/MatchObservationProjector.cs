using Shirokuro.Domain.Matches;

namespace Shirokuro.Application.Observations;

/// <summary>
/// Converts a private match snapshot into a default-deny per-player DTO.
/// Authentication and reveal decisions must happen inside the trusted backend
/// before this method is called. No public endpoint exists yet.
/// </summary>
public sealed class MatchObservationProjector
{
    public MatchObservation ForObserver(
        MatchStateSnapshot state,
        string observerPlayerId,
        IReadOnlyCollection<AuthorizedHeroReveal> authorizedReveals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(observerPlayerId);
        ArgumentNullException.ThrowIfNull(authorizedReveals);

        var self = state.Heroes.SingleOrDefault(
            hero => StringComparer.Ordinal.Equals(hero.PlayerId, observerPlayerId));
        if (self is null)
        {
            throw new UnauthorizedAccessException("Observer is not a participant in this match.");
        }

        var allHeroes = state.Heroes.ToDictionary(hero => hero.PlayerId, StringComparer.Ordinal);
        var visible = new List<OtherHeroObservation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reveal in authorizedReveals)
        {
            if (reveal is null || string.IsNullOrWhiteSpace(reveal.PlayerId))
            {
                throw new ArgumentException("Invalid visibility grant.", nameof(authorizedReveals));
            }

            if (!seen.Add(reveal.PlayerId))
            {
                throw new ArgumentException("Duplicate visibility grant.", nameof(authorizedReveals));
            }

            if (!allHeroes.TryGetValue(reveal.PlayerId, out var other))
            {
                throw new ArgumentException("Visibility grant references unknown participant.", nameof(authorizedReveals));
            }

            if (StringComparer.Ordinal.Equals(reveal.PlayerId, observerPlayerId))
            {
                // The observer already receives their own information in Self.
                continue;
            }

            visible.Add(new OtherHeroObservation(
                other.PlayerId,
                other.TeamId,
                other.HeroId,
                reveal.RevealPosition ? other.Position : null));
        }

        return new MatchObservation(
            state.MatchId,
            new OwnHeroObservation(
                self.PlayerId,
                self.TeamId,
                self.HeroId,
                self.Position,
                self.PlannedDestination),
            Array.AsReadOnly(visible.ToArray()));
    }
}

