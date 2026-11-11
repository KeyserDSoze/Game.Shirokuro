using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
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
        public readonly ConcurrentQueue<(
            Uri Uri, string MediaType, byte[] Body,
            bool HasAuthorization, bool HasCookie)> Calls = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Calls.Enqueue((
                request.RequestUri!,
                request.Content.Headers.ContentType?.MediaType ?? "",
                body,
                request.Headers.Contains("Authorization") ||
                    request.Headers.ProxyAuthorization is not null,
                request.Headers.Contains("Cookie")));
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
        foreach (var (uri, mediaType, bytes, hasAuthorization, hasCookie)
                 in collector.Calls)
        {
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("/v1/metrics", uri.AbsolutePath);
            Assert.Equal("application/x-protobuf", mediaType);
            Assert.NotEmpty(bytes);
            // Application guest cookies and API authentication must never
            // become OTLP transport headers.
            Assert.False(hasAuthorization);
            Assert.False(hasCookie);
        }

        // Metric batches may be split by the SDK. Assert names across all
        // received protobuf payloads, not an incidental packet boundary.
        var payload = string.Concat(collector.Calls
            .Select(call => Encoding.UTF8.GetString(call.Body)));
        Assert.Contains(MultiplayerMetrics.ReadinessDurationSecondsName, payload);
        Assert.Contains(MultiplayerMetrics.ReadinessProbesName, payload);
        Assert.DoesNotContain("password=", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guest-private", payload, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class UnavailableCollector : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public async Task ProductionCollectorOutageDoesNotBlockGuestLoginOrCreatePublicMetricsRoute()
    {
        var collector = new UnavailableCollector();
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(
            builder =>
            {
                builder.UseEnvironment(Environments.Production);
                TestDataProtectionConfiguration.Apply(builder);
                TestGuestSessionRedisConfiguration.Apply(builder);
                builder.UseSetting(MultiplayerMetricsExport.EnabledKey, "true");
                builder.UseSetting(MultiplayerMetricsExport.EndpointKey,
                    "https://metrics.example/v1/metrics");
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [MultiplayerMetricsExport.EnabledKey] = "true",
                        [MultiplayerMetricsExport.EndpointKey] =
                            "https://metrics.example/v1/metrics",
                        ["AllowedHosts"] = "game.example",
                        ["Security:AllowedOrigins:0"] = "https://game.example"
                    }));
                builder.ConfigureTestServices(services =>
                    services.PostConfigureAll<OtlpExporterOptions>(options =>
                    {
                        options.HttpClientFactory = () =>
                            new HttpClient(collector, disposeHandler: false);
                    }));
            });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });

        using (var health = await client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var metrics = await client.GetAsync("/metrics"))
            Assert.Equal(HttpStatusCode.NotFound, metrics.StatusCode);

        string cookie;
        using (var request = new HttpRequestMessage(
                   HttpMethod.Post, "/api/sessions/guest"))
        {
            request.Headers.Add("Origin", "https://game.example");
            request.Headers.Add("X-Shirokuro-Request", "1");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"))
                .Split(';')[0];
        }

        using var member = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        member.DefaultRequestHeaders.Add("Cookie", cookie);
        using (var session = await member.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using (var create = new HttpRequestMessage(HttpMethod.Post, "/api/lobbies"))
        {
            create.Headers.Add("Origin", "https://game.example");
            create.Headers.Add("X-Shirokuro-Request", "1");
            create.Content = System.Net.Http.Json.JsonContent.Create(
                new { teamIds = new[] { "white", "black" }, teamId = "white" });
            using var response = await member.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var meter = host.Services.GetRequiredService<MeterProvider>();
        MultiplayerMetrics.RecordReadinessProbe(true);
        // Failing OTLP delivery must not throw into the game API pipeline.
        _ = meter.ForceFlush(5_000);
        Assert.True(Volatile.Read(ref collector.Requests) > 0);
        using (var afterFailure = await client.GetAsync("/api/health"))
            Assert.Equal(HttpStatusCode.OK, afterFailure.StatusCode);
        using (var active = await member.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);

        // Revoke on the game server, not in the telemetry pipeline.
        using (var logout = new HttpRequestMessage(
                   HttpMethod.Delete, "/api/sessions/guest"))
        {
            logout.Headers.Add("Origin", "https://game.example");
            logout.Headers.Add("X-Shirokuro-Request", "1");
            using var response = await member.SendAsync(logout);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using (var revoked = await member.GetAsync("/api/sessions/me"))
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
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
