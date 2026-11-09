using System.Security.Cryptography;
using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Application.Lobbies;

/// <summary>
/// Pre-match invitation-only mutations. Callers MUST provide identities from
/// verified server authentication, never a playerId sent by the browser.
/// </summary>
public sealed class GuestLobbyCommands(
    InMemoryLobbyCoordinator coordinator,
    LobbyRosterReader reader)
{
    public const int MaxRooms = 256;
    public const int MaxParticipants = 16;
    public const int MinTeams = 2;
    public const int MaxTeams = 4;

    public LobbyRosterView Create(string trustedPlayerId, IReadOnlyList<string>? teamIds, string? initialTeam)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedPlayerId);
        ValidateTeams(teamIds);

        if (initialTeam is null || !teamIds!.Contains(initialTeam, StringComparer.Ordinal))
            throw new ArgumentException("Initial team must be in the configured list.");

        // Invite codes are 128-bit opaque bearer references. Never accept one
        // supplied by the creator; no enumeration or guessable sequential IDs.
        var roomId = "room-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        coordinator.CreateWithOwner(roomId, teamIds, trustedPlayerId, initialTeam, MaxRooms);
        return reader.ForMember(roomId, trustedPlayerId);
    }

    public LobbyRosterView Join(
        string roomId, string trustedPlayerId, string? teamId, long expectedRevision)
    {
        if (teamId is null || teamId.Length == 0 || teamId.Length > 24)
            throw new ArgumentException("Invalid team.");
        coordinator.JoinBounded(roomId, trustedPlayerId, teamId,
            expectedRevision, MaxParticipants);
        return reader.ForMember(roomId, trustedPlayerId);
    }

    public LobbyRosterView ChooseHero(
        string roomId, string trustedPlayerId, string? heroId, long expectedRevision)
    {
        RequireMember(roomId, trustedPlayerId);
        if (heroId is null)
            throw new ArgumentException("Hero is required.");
        coordinator.ChooseHero(roomId, trustedPlayerId, heroId, expectedRevision);
        return reader.ForMember(roomId, trustedPlayerId);
    }

    public LobbyRosterView ChangeTeam(
        string roomId, string trustedPlayerId, string? teamId, long expectedRevision)
    {
        RequireMember(roomId, trustedPlayerId);
        if (teamId is null)
            throw new ArgumentException("Team is required.");
        coordinator.ChangeTeam(roomId, trustedPlayerId, teamId, expectedRevision);
        return reader.ForMember(roomId, trustedPlayerId);
    }

    public void Leave(string roomId, string trustedPlayerId, long expectedRevision)
    {
        RequireMember(roomId, trustedPlayerId);
        coordinator.LeaveAndPrune(roomId, trustedPlayerId, expectedRevision);
    }

    private void RequireMember(string roomId, string playerId)
    {
        // Member/absent failures intentionally indistinguishable on the wire.
        reader.ForMember(roomId, playerId);
    }

    private static void ValidateTeams(IReadOnlyList<string>? teams)
    {
        if (teams is null || teams.Count is < MinTeams or > MaxTeams)
            throw new ArgumentException("A preview room requires 2–4 team identifiers.");

        if (teams.Any(team => team is null || team.Length is < 1 or > 24 ||
            team.Any(ch => !((ch >= 'a' && ch <= 'z') ||
                             (ch >= '0' && ch <= '9') || ch == '-'))) ||
            teams.Distinct(StringComparer.Ordinal).Count() != teams.Count)
            throw new ArgumentException("Team IDs must be unique lower-case ASCII slugs.");
    }
}

