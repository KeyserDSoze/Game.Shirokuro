using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestSessionRecoveryCutoffTests
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Shirokuro";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(string? raw) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            raw is null ? new Dictionary<string, string?>() :
            new Dictionary<string, string?>
            {
                [GuestSessionRecoveryCutoff.ConfigurationKey] = raw
            }).Build();

    [Fact]
    public void UnconfiguredCutoffPreservesLegacySessionsWithoutCrossStoreLookup()
    {
        var cutoff = GuestSessionRecoveryCutoff.Read(Config(null),
            new Environment(Environments.Production));
        Assert.Null(cutoff);
        Assert.True(GuestSessionRecoveryCutoff.Accepts(null, cutoff));
        Assert.True(GuestSessionRecoveryCutoff.Accepts(
            DateTimeOffset.UtcNow.AddDays(-1), cutoff));
    }

    [Theory]
    [InlineData("2026-11-11T14:00:00Z")]
    [InlineData("2026-11-11T14:00:00.1234567+00:00")]
    public void RequiresExplicitUtcAndTreatsTheThresholdAsExclusive(string raw)
    {
        var cutoff = GuestSessionRecoveryCutoff.Read(Config(raw),
            new Environment(Environments.Production));
        Assert.NotNull(cutoff);
        Assert.Equal(TimeSpan.Zero, cutoff.Value.Offset);
        Assert.False(GuestSessionRecoveryCutoff.Accepts(null, cutoff));
        Assert.False(GuestSessionRecoveryCutoff.Accepts(cutoff.Value, cutoff));
        Assert.False(GuestSessionRecoveryCutoff.Accepts(
            cutoff.Value.AddTicks(-1), cutoff));
        Assert.True(GuestSessionRecoveryCutoff.Accepts(
            cutoff.Value.AddTicks(1), cutoff));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2026-11-11")]
    [InlineData("2026-11-11T14:00:00")]
    [InlineData("2026-11-11T14:00:00+01:00")]
    [InlineData("2026-11-11T14:00:00.12345678Z")]
    [InlineData("2026-99-99T14:00:00Z")]
    [InlineData("2026-11-11T14:00:00Z ")]
    [InlineData("garbage")]
    public void InvalidConfiguredCutoffsFailStartupWithoutEchoingValue(string raw)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRecoveryCutoff.Read(Config(raw),
                new Environment(Environments.Production)));
        Assert.Equal("Invalid configured guest session recovery cutoff.", error.Message);
        if (raw.Length >= 3)
            Assert.DoesNotContain(raw, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DevelopmentIgnoresProductionCutoffEvenIfItIsMalformed()
    {
        Assert.Null(GuestSessionRecoveryCutoff.Read(Config("garbage"),
            new Environment(Environments.Development)));
    }
}
