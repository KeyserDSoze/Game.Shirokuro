using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace Shirokuro.Api.Security;

/// <summary>
/// Optional operator-only OTLP metrics export. Nothing is exposed on an
/// incoming HTTP route; the exporter sends only this project's fixed-label
/// meter to a trusted HTTPS collector. Bad configuration fails startup
/// without ever printing endpoint URLs or credentials.
/// </summary>
public static class MultiplayerMetricsExport
{
    public const string EnabledKey = "Security:Monitoring:OtlpMetricsEnabled";
    public const string EndpointKey = "Security:Monitoring:OtlpMetricsEndpoint";
    public const string InvalidConfiguration =
        "Multiplayer metrics export requires an explicit HTTPS OTLP metrics collector.";

    /// <summary>Validate configuration before registering any exporter.</summary>
    public static Uri? ReadEndpoint(
        IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Development and absent flag do not create exporters or network
        // clients; no ambient OTEL_* setting may turn this feature on.
        if (environment.IsDevelopment())
            return null;

        var enabled = configuration[EnabledKey];
        if (string.IsNullOrWhiteSpace(enabled))
            return null;
        if (!bool.TryParse(enabled, out var requested))
            throw new InvalidOperationException(InvalidConfiguration);
        if (!requested)
            return null;

        var endpoint = configuration[EndpointKey];
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !string.Equals(endpoint, endpoint.Trim(), StringComparison.Ordinal) ||
            endpoint.Contains('?', StringComparison.Ordinal) ||
            endpoint.Contains('#', StringComparison.Ordinal) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/v1/metrics" ||
            string.IsNullOrEmpty(uri.Host) ||
            uri.HostNameType == UriHostNameType.Unknown)
            throw new InvalidOperationException(InvalidConfiguration);

        return uri;
    }

    public static void Configure(
        IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        var endpoint = ReadEndpoint(configuration, environment);
        if (endpoint is null)
            return;

        // Use the SDK's normal CA/hostname certificate verification; do not
        // introduce a custom TLS callback or HTTP redirects. An unavailable
        // collector is not a dependency of auth, Redis or /api/ready.
        // Only the explicit in-process multiplayer meter is subscribed.
        services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddMeter(MultiplayerMetrics.MeterName)
                .AddOtlpExporter(options =>
                {
                    options.Endpoint = endpoint;
                    options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    options.Headers = string.Empty;
                    options.TimeoutMilliseconds = 5_000;
                }));
    }
}
