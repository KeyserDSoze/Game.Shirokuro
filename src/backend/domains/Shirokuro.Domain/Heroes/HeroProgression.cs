namespace Shirokuro.Domain.Heroes;

/// <summary>
/// Immutable two-level hero progression. Only an authoritative match handler
/// may call AfterConfirmedKill after validating the elimination.
/// This value object does not decide whether a kill was legal.
/// </summary>
public sealed record HeroProgression
{
    public const int StartingLevel = 1;
    public const int MaximumLevel = 2;

    public int Level { get; }
    public bool HasUnlockedUpgrade => Level == MaximumLevel;

    private HeroProgression(int level) => Level = level;

    public static HeroProgression Start() => new(StartingLevel);

    public HeroProgression AfterConfirmedKill() =>
        Level == MaximumLevel ? this : new HeroProgression(MaximumLevel);
}

