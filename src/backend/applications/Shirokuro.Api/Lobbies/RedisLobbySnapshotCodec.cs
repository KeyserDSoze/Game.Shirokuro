using System.Text.Json;
using System.Text.Json.Serialization;
using Shirokuro.Domain.Lobbies;

namespace Shirokuro.Api.Lobbies;

/// <summary>
/// Internal, versioned persistence representation. Not an API response and
/// never a replacement for member-authorized LobbyRosterView projection.
/// </summary>
public sealed record LobbySnapshot(
    LobbyState State, DateTimeOffset LastActivityUtc, string? CreatorId);

public static class RedisLobbySnapshotCodec
{
    private const int Version = 1;
    public const int MaxBytes = 32768;

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Payload(
        int Version, string LobbyId, string[] TeamIds, string[] HeroIds,
        long Revision, LobbyParticipant[] Participants,
        string? CreatorId, long LastActivityUtcTicks);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Encode(LobbySnapshot snapshot)
    {
        Validate(snapshot);
        var state = snapshot.State;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Payload(
            Version, state.Definition.LobbyId, state.Definition.TeamIds.ToArray(),
            state.Definition.HeroIds.ToArray(), state.Revision,
            state.Participants.ToArray(), snapshot.CreatorId,
            snapshot.LastActivityUtc.UtcTicks), JsonOptions);
        if (bytes.Length > MaxBytes)
            throw new InvalidDataException("Lobby snapshot exceeds storage limit.");
        return bytes;
    }

    public static LobbySnapshot Decode(ReadOnlySpan<byte> bytes, string expectedLobbyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLobbyId);
        if (bytes.Length is 0 or > MaxBytes)
            throw new InvalidDataException("Invalid lobby snapshot size.");
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Null persisted lobby.");
            if (payload.Version != Version ||
                !string.Equals(payload.LobbyId, expectedLobbyId, StringComparison.Ordinal) ||
                payload.TeamIds is null || payload.HeroIds is null ||
                payload.Participants is null)
                throw new InvalidDataException("Invalid persisted lobby version or identity.");

            var definition = new LobbyDefinition(
                payload.LobbyId, payload.TeamIds, payload.HeroIds);
            var state = LobbyState.Restore(
                definition, payload.Revision, payload.Participants);
            var lastActivity = new DateTimeOffset(
                payload.LastActivityUtcTicks, TimeSpan.Zero);
            var result = new LobbySnapshot(state, lastActivity, payload.CreatorId);
            Validate(result);
            return result;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException
                                   or OverflowException)
        {
            throw new InvalidDataException("Invalid persisted lobby snapshot.", ex);
        }
    }

    private static void Validate(LobbySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.State);
        if (snapshot.LastActivityUtc.Offset != TimeSpan.Zero ||
            snapshot.LastActivityUtc < DateTimeOffset.UnixEpoch ||
            snapshot.State.Revision < 0 ||
            snapshot.State.Definition.TeamIds.Count is < 2 or > 4 ||
            snapshot.State.Definition.HeroIds.Count is < 1 or > 128 ||
            snapshot.State.Participants.Count > 16 ||
            (snapshot.CreatorId is not null &&
                string.IsNullOrWhiteSpace(snapshot.CreatorId)))
            throw new InvalidDataException("Invalid persisted lobby metadata.");

        // Reconstruction deliberately repeats the same domain-level checks
        // used on read: never persist a state that cannot be rehydrated.
        LobbyState.Restore(snapshot.State.Definition, snapshot.State.Revision,
            snapshot.State.Participants);
    }
}
