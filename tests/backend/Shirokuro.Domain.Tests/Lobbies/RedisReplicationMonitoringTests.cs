using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Bounded Redis INFO parsing and opt-in telemetry contract: never export
/// replica IP/host, error messages, passwords or guessed healthy values.
/// </summary>
public sealed class RedisReplicationMonitoringTests
{
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Shirokuro.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static IConfiguration Settings(string? enabled) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [RedisReplicationMonitoring.EnabledKey] = enabled
            }).Build();

    [Fact]
    public void StandalonePrimaryReportsZeroReplicasWithoutFabricatingLag()
    {
        var sample = RedisReplicationInfo.Parse(
            "# Replication\r\nrole:master\r\nconnected_slaves:0\r\n" +
            "master_repl_offset:25\r\nmaster_replid:private-value\r\n");
        Assert.Equal(RedisReplicationRole.Primary, sample.Role);
        Assert.Equal(RedisReplicaLink.NotApplicable, sample.Link);
        Assert.Equal(0, sample.ConnectedReplicas);
        Assert.Null(sample.MaxOnlineAckLagSeconds);
    }

    [Fact]
    public void MultiReplicaPrimaryReportsOnlyMaximumOnlineAckLag()
    {
        var sample = RedisReplicationInfo.Parse(
            "# Replication\nrole:master\nconnected_slaves:3\n" +
            "slave0:ip=secret-private-host,port=6383,state=online,offset=18,lag=2\n" +
            "slave1:ip=192.0.2.71,port=6384,state=online,offset=12,lag=8\n" +
            "slave2:ip=192.0.2.72,port=6385,state=wait_bgsave,offset=1,lag=700\n");
        Assert.Equal(3, sample.ConnectedReplicas);
        Assert.Equal(8, sample.MaxOnlineAckLagSeconds);
        Assert.DoesNotContain("192.0.2.71", sample.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-private-host", sample.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("up", RedisReplicaLink.Up)]
    [InlineData("down", RedisReplicaLink.Down)]
    public void ReplicaHasLinkStateButNeverPretendsToKnowPrimaryReplicaCount(
        string linkState, RedisReplicaLink expected)
    {
        var sample = RedisReplicationInfo.Parse(
            "role:slave\nmaster_host=secret.example\n" +
            "master_host:private.example\nmaster_port:6382\n" +
            "master_link_status:" + linkState + "\n" +
            "slave_repl_offset:450\nmaster_replid:secret\n");
        Assert.Equal(RedisReplicationRole.Replica, sample.Role);
        Assert.Equal(expected, sample.Link);
        Assert.Null(sample.ConnectedReplicas);
        Assert.Null(sample.MaxOnlineAckLagSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# Replication\nrole:master")]
    [InlineData("role:slave\n")]
    [InlineData("role:unknown\nconnected_slaves:0")]
    [InlineData("role:master\nconnected_slaves:-3")]
    [InlineData("role:master\nconnected_slaves:2147483648")]
    [InlineData("role:master\nconnected_slaves:2\nslave0:ip=secret,state=online,lag=-10")]
    [InlineData("role:master\nconnected_slaves:2\nslave0:state=online")]
    [InlineData("role:master\nconnected_slaves:1\nslave0:state=online,lag=2147483648")]
    [InlineData("role:master\nconnected_slaves:0\nrole:master")]
    [InlineData("role:slave\nmaster_link_status:up\nmaster_link_status:down")]
    [InlineData("role:master\nconnected_slaves:0\nmaster_link_status:up")]
    [InlineData("role:slave\nmaster_link_status:broken")]
    public void InvalidReplicationInfoFailsWithConstantNonSensitiveMessage(string raw)
    {
        var failure = Assert.Throws<FormatException>(() =>
            RedisReplicationInfo.Parse(raw));
        Assert.Equal(RedisReplicationInfo.InvalidInfo, failure.Message);
        Assert.DoesNotContain("secret", failure.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OversizedOrUnknownReplicationInfoNeverBecomesTelemetry()
    {
        Assert.Equal(RedisReplicationInfo.InvalidInfo,
            Assert.Throws<FormatException>(() =>
                RedisReplicationInfo.Parse(new string('x', 70_000))).Message);
        Assert.Equal(RedisReplicationInfo.InvalidInfo,
            Assert.Throws<FormatException>(() =>
                RedisReplicationInfo.Parse("role:master\nconnected_slaves:secret")).Message);
    }

    [Fact]
    public void ReplicationMetricsKeepOutcomesAndAggregateNumericSamplesDistinct()
    {
        using var metrics = new MultiplayerMetricCapture();
        var master = RedisReplicationInfo.Parse(
            "role:master\nconnected_slaves:2\n" +
            "slave0:ip=private.example,port=6381,state=online,offset=6,lag=3\n" +
            "slave1:ip=private.example,port=6382,state=online,offset=8,lag=1");
        var replica = RedisReplicationInfo.Parse(
            "role:slave\nmaster_host:private.example\nmaster_link_status:down");
        var primaryZero = RedisReplicationInfo.Parse(
            "role:master\nconnected_slaves:0");

        MultiplayerMetrics.RecordRedisReplication(master, RedisReplicationPollOutcome.Ok);
        MultiplayerMetrics.RecordRedisReplication(replica, RedisReplicationPollOutcome.Ok);
        MultiplayerMetrics.RecordRedisReplication(primaryZero, RedisReplicationPollOutcome.Ok);
        MultiplayerMetrics.RecordRedisReplication(null, RedisReplicationPollOutcome.Unavailable);
        MultiplayerMetrics.RecordRedisReplication(null, RedisReplicationPollOutcome.Invalid);
        MultiplayerMetrics.RecordRedisReplication(null, RedisReplicationPollOutcome.Timeout);

        var results = metrics.For(MultiplayerMetrics.RedisReplicationObservationsName);
        Assert.Equal(6, results.Length);
        Assert.Contains(results, r => r.Tags["role"] == "master" &&
            r.Tags["link"] == "na" && r.Tags["outcome"] == "ok");
        Assert.Contains(results, r => r.Tags["role"] == "replica" &&
            r.Tags["link"] == "down" && r.Tags["outcome"] == "ok");
        foreach (var outcome in new[] { "timeout", "unavailable", "invalid" })
            Assert.Contains(results, r => r.Tags["role"] == "unknown" &&
                r.Tags["link"] == "na" && r.Tags["outcome"] == outcome);
        Assert.All(results, r =>
        {
            Assert.Equal(1, r.Value);
            Assert.Equal(new[] { "link", "outcome", "role" },
                r.Tags.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var label in r.Tags.Values)
            {
                Assert.DoesNotContain("private", label, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("password", label, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("guest-", label, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("192.", label, StringComparison.OrdinalIgnoreCase);
            }
        });

        var counts = metrics.ForDurations(
            MultiplayerMetrics.RedisConnectedReplicasName);
        Assert.Equal(new[] { 0d, 2d },
            counts.Select(x => x.Seconds).OrderBy(x => x));
        Assert.All(counts, x => Assert.Empty(x.Tags));
        var ack = Assert.Single(metrics.ForDurations(
            MultiplayerMetrics.RedisReplicaAckLagSecondsName));
        Assert.Equal(3, ack.Seconds);
        Assert.Empty(ack.Tags); // No addresses or replica IDs.
    }

    [Fact]
    public void RejectUnknownMetricsOrInconsistentSamplesBeforeRecording()
    {
        using var metrics = new MultiplayerMetricCapture();
        var primary = RedisReplicationInfo.Parse(
            "role:master\nconnected_slaves:1");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiplayerMetrics.RecordRedisReplication(null,
                (RedisReplicationPollOutcome)777));
        Assert.Throws<ArgumentException>(() =>
            MultiplayerMetrics.RecordRedisReplication(primary,
                RedisReplicationPollOutcome.Timeout));
        Assert.Throws<ArgumentException>(() =>
            MultiplayerMetrics.RecordRedisReplication(null,
                RedisReplicationPollOutcome.Ok));
        Assert.Throws<ArgumentException>(() =>
            MultiplayerMetrics.RecordRedisReplication(
                new RedisReplicationSnapshot(RedisReplicationRole.Replica,
                    RedisReplicaLink.Up, 4, null), RedisReplicationPollOutcome.Ok));
        Assert.Throws<ArgumentException>(() =>
            MultiplayerMetrics.RecordRedisReplication(
                new RedisReplicationSnapshot(RedisReplicationRole.Primary,
                    RedisReplicaLink.NotApplicable, -1, null),
                RedisReplicationPollOutcome.Ok));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MultiplayerMetrics.RecordRedisReplication(
                new RedisReplicationSnapshot((RedisReplicationRole)700,
                    RedisReplicaLink.Up, null, null), RedisReplicationPollOutcome.Ok));
        Assert.Empty(metrics.For(MultiplayerMetrics.RedisReplicationObservationsName));
        Assert.Empty(metrics.ForDurations(MultiplayerMetrics.RedisConnectedReplicasName));
    }

    [Fact]
    public void MonitorRequiresExplicitTrueAndNeverRunsInDevelopment()
    {
        foreach (var (envName, flag) in new[]
                 {
                     (Environments.Production, (string?)null),
                     (Environments.Production, "false"),
                     (Environments.Development, "true"),
                     (Environments.Development, "untrusted")
                 })
        {
            var services = new ServiceCollection();
            RedisReplicationMonitoring.Configure(
                services, Settings(flag), new EnvironmentStub(envName));
            Assert.Empty(services);
        }

        var enabledServices = new ServiceCollection();
        RedisReplicationMonitoring.Configure(enabledServices,
            Settings("true"), new EnvironmentStub(Environments.Production));
        Assert.Contains(enabledServices, service =>
            service.ServiceType == typeof(RedisReplicationSampler));
        Assert.Contains(enabledServices, service =>
            service.ServiceType == typeof(IHostedService) &&
            service.ImplementationType == typeof(RedisReplicationMonitoringWorker));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("false,true")]
    public void InvalidProductionFlagCannotLeakInputOrAccidentallyEnable(string value)
    {
        var services = new ServiceCollection();
        var failure = Assert.Throws<InvalidOperationException>(() =>
            RedisReplicationMonitoring.Configure(services, Settings(value),
                new EnvironmentStub(Environments.Production)));
        Assert.Equal(RedisReplicationMonitoring.InvalidConfiguration,
            failure.Message);
        Assert.DoesNotContain(value, failure.ToString(), StringComparison.Ordinal);
        Assert.Empty(services);
    }
}
