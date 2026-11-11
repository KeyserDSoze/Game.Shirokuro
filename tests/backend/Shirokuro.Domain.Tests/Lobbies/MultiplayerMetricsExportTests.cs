using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>Metrics-only, private and disabled-by-default OTLP wiring.</summary>
public sealed class MultiplayerMetricsExportTests
{
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Shirokuro.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private static IConfiguration Settings(
        string? enabled = null, string? endpoint = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [MultiplayerMetricsExport.EnabledKey] = enabled,
                [MultiplayerMetricsExport.EndpointKey] = endpoint
            }).Build();

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(Uri Uri, string MediaType, byte[] Body)> Calls = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Calls.Enqueue((
                request.RequestUri!,
                request.Content.Headers.ContentType?.MediaType ?? "",
                body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void OffByDefaultExplicitFalseAndDevelopmentDoNotRegisterAnExporter()
    {
        foreach (var (name, enabled, endpoint) in new[]
                 {
                     (Environments.Production, (string?)null, (string?)null),
                     (Environments.Production, "false",
                         "https://metrics.example/v1/metrics"),
                     (Environments.Development, "true",
                         "https://metrics.example/v1/metrics")
                 })
        {
            var services = new ServiceCollection();
            MultiplayerMetricsExport.Configure(
                services, Settings(enabled, endpoint), new EnvironmentStub(name));
            Assert.Empty(services);
            Assert.Null(MultiplayerMetricsExport.ReadEndpoint(
                Settings(enabled, endpoint), new EnvironmentStub(name)));
        }
    }

    [Theory]
    [InlineData("http://metrics.example/v1/metrics")]
    [InlineData("https://metrics.example")]
    [InlineData("https://metrics.example/v1/traces")]
    [InlineData("https://metrics.example/v1/metrics/")]
    [InlineData("https://metrics.example/v1/metrics?token=secret")]
    [InlineData("https://metrics.example/v1/metrics#token")]
    [InlineData("https://user:private@metrics.example/v1/metrics")]
    [InlineData("https://metrics.example/v1/metrics ")]
    [InlineData("//metrics.example/v1/metrics")]
    [InlineData("ftp://metrics.example/v1/metrics")]
    [InlineData("secret private")]
    public void InvalidOrUnsafeEndpointsFailBeforeExporterRegistration(
        string address)
    {
        var services = new ServiceCollection();
        var problem = Assert.Throws<InvalidOperationException>(() =>
            MultiplayerMetricsExport.Configure(
                services, Settings("true", address),
                new EnvironmentStub(Environments.Production)));
        Assert.Equal(MultiplayerMetricsExport.InvalidConfiguration,
            problem.Message);
        Assert.DoesNotContain(address, problem.ToString(),
            StringComparison.Ordinal);
        Assert.Empty(services);
    }

    [Theory]
    [InlineData("truish")]
    [InlineData("1")]
    [InlineData("false;enabled=true")]
    public void MalformedEnableSwitchDoesNotAccidentallyExport(string enabled)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MultiplayerMetricsExport.ReadEndpoint(Settings(enabled,
                    "https://metrics.example/v1/metrics"),
                new EnvironmentStub(Environments.Production)));
        Assert.Equal(MultiplayerMetricsExport.InvalidConfiguration, error.Message);
    }

    [Fact]
    public void ExplicitHttpsCollectorMustHaveMetricsOnlyPath()
    {
        var endpoint = MultiplayerMetricsExport.ReadEndpoint(
            Settings("true", "https://metrics.example:8443/v1/metrics"),
            new EnvironmentStub(Environments.Production));
        Assert.Equal("https://metrics.example:8443/v1/metrics",
            endpoint!.AbsoluteUri);
    }

    [Fact]
    public void EnabledExporterTransmitsOnlyAggregateMetricsThroughHttpsOtlp()
    {
        var collector = new CaptureHandler();
        var services = new ServiceCollection();
        MultiplayerMetricsExport.Configure(
            services,
            Settings("true", "https://metrics.example:8443/v1/metrics"),
            new EnvironmentStub(Environments.Production));

        // Only the test injects an in-memory HTTP transport, without
        // replacing any TLS client or adding insecure certificate callbacks
        // to the Production implementation.
        services.PostConfigureAll<OtlpExporterOptions>(options =>
        {
            options.HttpClientFactory = () =>
                new HttpClient(collector, disposeHandler: false);
        });

        using var provider = services.BuildServiceProvider();
        var meter = provider.GetRequiredService<MeterProvider>();

        MultiplayerMetrics.RecordReadinessProbe(true);
        MultiplayerMetrics.RecordReadinessDuration(
            true, ReadinessProbeStage.Success, 0.12);
        MultiplayerMetrics.RecordReadinessDuration(
            false, ReadinessProbeStage.Timeout, 2.0);

        Assert.True(meter.ForceFlush(5_000));
        Assert.NotEmpty(collector.Calls);
        foreach (var (uri, mediaType, bytes) in collector.Calls)
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("/v1/metrics", uri.AbsolutePath);
            Assert.Equal("application/x-protobuf", mediaType);
            Assert.NotEmpty(bytes);
            var payload = Encoding.UTF8.GetString(bytes);
            Assert.Contains(MultiplayerMetrics.ReadinessDurationSecondsName, payload);
            Assert.Contains(MultiplayerMetrics.ReadinessProbesName, payload);
            Assert.DoesNotContain("password=", payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-private", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task DevelopmentDoesNotExposeMetricsEndpoint()
    {
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder => builder.UseEnvironment(Environments.Development));
        using var client = host.CreateClient();
        using var reply = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
    }
}
