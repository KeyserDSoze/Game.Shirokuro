using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Shirokuro.Api.Security;

/// <summary>
/// Exact-IP, explicitly opted-in trust for a single reverse-proxy hop.
/// Never trusts client-supplied X-Forwarded-Host or a wildcard proxy network.
/// </summary>
public static class TrustedProxyForwarding
{
    public static bool Configure(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var entries = configuration.GetSection("Security:KnownProxies")
            .GetChildren().Select(entry => entry.Value).ToArray();
        if (entries.Length == 0)
            return false; // In particular, don't implicitly trust loopback.

        if (entries.Length > 32)
            throw new InvalidOperationException("Too many configured trusted proxies.");

        var addresses = new HashSet<IPAddress>();
        foreach (var text in entries)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                !IPAddress.TryParse(text, out var address) ||
                !string.Equals(text, address.ToString(), StringComparison.OrdinalIgnoreCase) ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.Broadcast) ||
                address.Equals(IPAddress.IPv6None) ||
                address.IsIPv6Multicast ||
                !addresses.Add(address))
            {
                throw new InvalidOperationException(
                    "Security:KnownProxies must contain unique, exact, canonical IP addresses.");
            }
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in addresses)
                options.KnownProxies.Add(address);

            options.ForwardedHeaders =
                ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.RequireHeaderSymmetry = true;
            // Intentionally DO NOT enable XForwardedHost: Origin trust is
            // explicit (#28), while Host validation remains separate (#21).
        });
        return true;
    }
}
