using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Configuration is an input, not a security grant: the Production lobby and
/// independent guest-cache Redis clients must stay TLS-only and non-admin.
/// </summary>
public sealed class GuestSessionRedisSecurityConfigurationTests
{
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Shirokuro.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static IConfiguration Settings(string connection) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [GuestSessionRedisConfiguration.ConnectionStringKey] = connection
            }).Build();

    [Theory]
    [InlineData("localhost:6380,ssl=true,user=game,password=game-secret,allowAdmin=true")]
    [InlineData("localhost:6380,ssl=false,user=game,password=game-secret,allowAdmin=false")]
    [InlineData("localhost:26380,ssl=true,serviceName=game,user=game,password=game-secret,sentinelUser=discovery,sentinelPassword=discovery-secret,allowAdmin=true")]
    public void UnsafeGameConnectionFailsStartupWithoutDisclosingCredentials(
        string connection)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRedisConfiguration.ReadTlsOptions(Settings(connection)));
        Assert.Equal(GuestSessionRedisConfiguration.InvalidConnection, error.Message);
        Assert.DoesNotContain("game-secret", error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("discovery-secret", error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:", error.ToString(),
            StringComparison.Ordinal);

        var services = new ServiceCollection();
        var startup = Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRedisConfiguration.Configure(services,
                Settings(connection), new EnvironmentStub(Environments.Production)));
        Assert.Equal(GuestSessionRedisConfiguration.InvalidConnection, startup.Message);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("localhost:6381,ssl=true,allowAdmin=true,user=late,password=late-secret")]
    [InlineData("localhost:6381,ssl=false,allowAdmin=false,user=late,password=late-secret")]
    public async Task LateCacheOverrideCannotEnableAdminOrDowngradeTls(
        string overrideConnection)
    {
        var services = new ServiceCollection();
        GuestSessionRedisConfiguration.Configure(services,
            Settings("localhost:6380,ssl=true,user=game,password=game-secret"),
            new EnvironmentStub(Environments.Production));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        Assert.NotNull(options.ConnectionMultiplexerFactory);

        // The factory closes over RedisCacheOptions, and is intentionally
        // resolved *after* registration. No Redis daemon is contacted here.
        options.ConfigurationOptions = ConfigurationOptions.Parse(overrideConnection);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await options.ConnectionMultiplexerFactory!());
        Assert.Equal(GuestSessionRedisConfiguration.InvalidConnection, error.Message);
        Assert.DoesNotContain("late-secret", error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyLateOverrideIsRejectedBeforeOpeningAnySocket()
    {
        var services = new ServiceCollection();
        GuestSessionRedisConfiguration.Configure(services,
            Settings("localhost:6380,ssl=true"),
            new EnvironmentStub(Environments.Staging));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        options.ConfigurationOptions = new ConfigurationOptions { Ssl = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await options.ConnectionMultiplexerFactory!());
        Assert.Equal(GuestSessionRedisConfiguration.InvalidConnection, error.Message);
    }

    [Theory]
    [InlineData("localhost:6381,ssl=true,sslHost=localhost")]
    [InlineData("localhost:26380,ssl=true,serviceName=shirokuro-main,user=game,password=game-secret,sentinelUser=discovery,sentinelPassword=other-secret")]
    public void SafeDirectAndSentinelConfigurationsRemainSupported(
        string connection)
    {
        var options = GuestSessionRedisConfiguration.ReadTlsOptions(
            Settings(connection));
        Assert.True(options.Ssl);
        Assert.False(options.AllowAdmin);
        Assert.NotEmpty(options.EndPoints);
    }
}
