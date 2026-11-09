namespace Shirokuro.Domain.Lobbies;

/// <summary>Immutable, non-secret pre-match roster entry.</summary>
public sealed record LobbyParticipant(string PlayerId, string TeamId, string? HeroId);

