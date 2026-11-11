using Microsoft.Extensions.Configuration;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class GuestSessionRedisSentinelConfigurationTests
{
    private static IConfiguration Settings(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            value is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?>
                {
                    [GuestSessionRedisConfiguration.ConnectionStringKey] = value
                }).Build();

    [Fact]
    public void SentinelTlsServiceNameIsRetainedForManagedPrimaryDiscovery()
    {
        var options = GuestSessionRedisConfiguration.ReadTlsOptions(Settings(
            "localhost:26380,localhost:26381,localhost:26382," +
            "serviceName=shirokuro-main,ssl=true,sslHost=localhost," +
            "abortConnect=true,connectTimeout=4000"));
        Assert.True(options.Ssl);
        Assert.Equal("shirokuro-main", options.ServiceName);
        Assert.Equal("localhost", options.SslHost);
        Assert.Equal(3, options.EndPoints.Count);
        Assert.True(options.AbortOnConnectFail);
        Assert.Equal(4000, options.ConnectTimeout);
    }

    [Fact]
    public void DistinctRedisAndSentinelAclIdentitiesAreParsedWithoutCredentialLeakage()
    {
        const string value =
            "localhost:26380,localhost:26381,localhost:26382," +
            "serviceName=shirokuro-main,ssl=true,sslHost=localhost," +
            "user=shirokuro-app,password=never-log-app-secret," +
            "sentinelUser=shirokuro-sentinel-client," +
            "sentinelPassword=never-log-sentinel-secret";
        var options = GuestSessionRedisConfiguration.ReadTlsOptions(Settings(value));
        Assert.True(options.Ssl);
        Assert.Equal("shirokuro-main", options.ServiceName);
        Assert.Equal("shirokuro-app", options.User);
        Assert.Equal("never-log-app-secret", options.Password);
        Assert.Equal("shirokuro-sentinel-client", options.SentinelUser);
        Assert.Equal("never-log-sentinel-secret", options.SentinelPassword);
        Assert.NotEqual(options.Password, options.SentinelPassword);

        var invalid = value.Replace("ssl=true", "ssl=false", StringComparison.Ordinal);
        var error = Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRedisConfiguration.ReadTlsOptions(Settings(invalid)));
        Assert.DoesNotContain("never-log-app-secret", error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("never-log-sentinel-secret", error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("localhost:", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void StandardDirectTlsRedisRemainsSupported()
    {
        var options = GuestSessionRedisConfiguration.ReadTlsOptions(
            Settings("localhost:6380,ssl=true,sslHost=localhost"));
        Assert.True(options.Ssl);
        Assert.True(string.IsNullOrEmpty(options.ServiceName));
        Assert.Single(options.EndPoints);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-valid-option")]
    [InlineData("localhost:26380,serviceName=shirokuro-main,ssl=false")]
    [InlineData("localhost:26380,serviceName=shirokuro-main,password=secret-not-to-print")]
    public void MissingOrPlaintextRedisIsRejectedWithoutLeakingSecrets(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            GuestSessionRedisConfiguration.ReadTlsOptions(Settings(value)));
        Assert.DoesNotContain("secret-not-to-print", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("localhost:", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password=", error.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
