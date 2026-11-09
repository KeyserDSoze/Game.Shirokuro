using Shirokuro.Domain.Geometry;

namespace Shirokuro.Application.Observations;

/// <summary>Viewer's own, non-authoritative observation.</summary>
public sealed record OwnHeroObservation(
    string PlayerId,
    string TeamId,
    string HeroId,
    HexCoordinate Position,
    HexCoordinate? PlannedDestination);

/// <summary>
/// Only fields approved for another player, with no PlannedDestination field.
/// </summary>
public sealed record OtherHeroObservation(
    string PlayerId,
    string TeamId,
    string HeroId,
    HexCoordinate? Position);

/// <summary>
/// Client-facing DTO. Never add a reference to authoritative snapshot objects.
/// </summary>
public sealed record MatchObservation(
    string MatchId,
    OwnHeroObservation Self,
    IReadOnlyList<OtherHeroObservation> Others);

