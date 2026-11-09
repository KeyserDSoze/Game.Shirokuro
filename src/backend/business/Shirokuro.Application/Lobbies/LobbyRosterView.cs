namespace Shirokuro.Application.Lobbies;

/// <summary>
/// Explicit, pre-match roster projection. Do not add gameplay state or
/// serialize Domain match snapshots through this response type.
/// </summary>
public sealed record LobbyParticipantView(string PlayerId, string TeamId, string? HeroId);

public sealed record LobbyRosterView(
    string LobbyId,
    long Revision,
    IReadOnlyList<string> TeamIds,
    IReadOnlyList<LobbyParticipantView> Participants);

