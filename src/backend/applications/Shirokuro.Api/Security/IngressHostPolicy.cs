using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Shirokuro.Api.Security;

/// <summary>
/// A strict ingress Host policy. Non-Development hosts must be explicitly
/// approved; X-Forwarded-Host never participates in authorization.
/// </summary>
public sealed class IngressHostPolicy
{
    private readonly HashSet<string>? _allowedHosts;

    private IngressHostPolicy(HashSet<string>? allowedHosts) =>
        _allowedHosts = allowedHosts;

    public static IngressHostPolicy Create(
        IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Development keeps the existing Vite/local browser workflow.
        // Its cookie-backed writes still require loopback/Vite Origin (#28).
        if (isDevelopment)
            return new IngressHostPolicy(null);

        var configured = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                "AllowedHosts must explicitly list trusted hostnames outside Development.");

        var entries = configured.Split(';');
        if (entries.Length > 32)
            throw new InvalidOperationException("AllowedHosts has too many entries.");

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!TryCanonicalAuthority(entry, out var canonical) ||
                !allowed.Add(canonical))
                throw new InvalidOperationException(
                    "AllowedHosts requires unique, canonical hostnames; wildcards are not allowed.");
        }
        return new IngressHostPolicy(allowed);
    }

    public bool Allows(HttpRequest request)
    {
        if (_allowedHosts is null)
            return true;

        // Validate the raw Host header, rather than a request URI synthesized
        // from it. A missing, duplicate or comma-joined Host is rejected.
        if (!request.Headers.TryGetValue("Host", out var headers) ||
            headers.Count != 1 ||
            !TryCanonicalAuthority(headers[0], out var actual))
            return false;

        return _allowedHosts.Contains(actual);
    }

    private static bool TryCanonicalAuthority(string? authority, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(authority) ||
            authority.Length > 255 ||
            authority.Any(ch => ch > 127 || char.IsWhiteSpace(ch) || char.IsControl(ch)) ||
            authority.Contains('*') || authority.Contains(',') ||
            authority.Contains('\\') || authority.EndsWith('.'))
            return false;

        // Require exactly a URI authority, no path, query, userinfo, hashes,
        // encoded delimiters, or loose Uri transformations (e.g. port 080).
        if (!Uri.TryCreate("http://" + authority, UriKind.Absolute, out var parsed) ||
            parsed.HostNameType is not (UriHostNameType.Dns or
                                        UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            parsed.UserInfo.Length > 0 ||
            parsed.AbsolutePath != "/" ||
            parsed.Query.Length != 0 ||
            parsed.Fragment.Length != 0 ||
            parsed.Port <= 0 ||
            !string.Equals(authority, parsed.Authority, StringComparison.OrdinalIgnoreCase))
            return false;

        var hostname = parsed.Host;
        if (hostname.StartsWith('.') || hostname.EndsWith('.') ||
            hostname.Contains(".."))
            return false;
        if (parsed.HostNameType == UriHostNameType.Dns &&
            (hostname.Split('.').Any(label => label.Length == 0 ||
                 label.StartsWith('-') || label.EndsWith('-') ||
                 label.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))))
            return false;

        canonical = parsed.Authority.ToLowerInvariant();
        return true;
    }
}

/// <summary>Options resolved at host startup so TestServer injected config is final.</summary>
public sealed class IngressHostPolicyOptions
{
    public IngressHostPolicy Policy { get; set; } = null!;
}

public static class IngressHostPolicyRegistration
{
    public static void AddIngressHostPolicy(this IServiceCollection services)
    {
        services.AddOptions<IngressHostPolicyOptions>()
            .Configure<IConfiguration, IHostEnvironment>((options, configuration, env) =>
                options.Policy = IngressHostPolicy.Create(
                    configuration, env.IsDevelopment()))
            .ValidateOnStart();
    }
}
