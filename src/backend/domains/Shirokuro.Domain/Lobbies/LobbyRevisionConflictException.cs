namespace Shirokuro.Domain.Lobbies;

/// <summary>Raised when a writer is working with a stale lobby snapshot.</summary>
public sealed class LobbyRevisionConflictException : InvalidOperationException
{
    public LobbyRevisionConflictException(long expected, long actual)
        : base($"Lobby revision conflict: expected {expected}, actual {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public long Expected { get; }
    public long Actual { get; }
}

