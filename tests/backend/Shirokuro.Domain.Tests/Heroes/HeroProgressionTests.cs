using Shirokuro.Domain.Heroes;
using Xunit;

namespace Shirokuro.Domain.Tests.Heroes;

public sealed class HeroProgressionTests
{
    [Fact]
    public void NewHero_StartsAtLevelOneWithoutUpgrade()
    {
        var hero = HeroProgression.Start();

        Assert.Equal(1, hero.Level);
        Assert.False(hero.HasUnlockedUpgrade);
    }

    [Fact]
    public void FirstConfirmedKill_UpgradesHeroExactlyOnce()
    {
        var firstLevel = HeroProgression.Start();
        var secondLevel = firstLevel.AfterConfirmedKill();

        Assert.Equal(1, firstLevel.Level); // Immutable original value.
        Assert.Equal(2, secondLevel.Level);
        Assert.True(secondLevel.HasUnlockedUpgrade);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(25)]
    public void RepeatedConfirmedKills_NeverExceedLevelTwo(int killCount)
    {
        var progression = HeroProgression.Start();

        for (var kill = 0; kill < killCount; kill++)
        {
            progression = progression.AfterConfirmedKill();
        }

        Assert.Equal(HeroProgression.MaximumLevel, progression.Level);
        Assert.True(progression.HasUnlockedUpgrade);
    }

    [Fact]
    public void Progression_BelongsToEachHeroIndividually()
    {
        var attacker = HeroProgression.Start();
        var ally = HeroProgression.Start();

        attacker = attacker.AfterConfirmedKill();

        Assert.Equal(2, attacker.Level);
        Assert.Equal(1, ally.Level);
    }
}

