using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Shirokuro.Api.Security;

/// <summary>
/// Reject untrusted Hosts outside the application pipeline, BEFORE the
/// framework's automatic HostFilteringMiddleware can return its own 400.
/// Preserve that framework filter as a second independently configured layer.
/// The raw Host is never emitted in the reason-only security counter.
/// </summary>
public sealed class EarlyHostIngressStartupFilter : IStartupFilter
{
    public static void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // IStartupFilter execution follows service registration order.
        // The web-host defaults register HostFilteringStartupFilter already;
        // inserting at the front ensures ours runs OUTSIDE it, not after it.
        services.Insert(0,
            ServiceDescriptor.Transient<IStartupFilter, EarlyHostIngressStartupFilter>());
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use(async (context, continuePipeline) =>
            {
                var policy = context.RequestServices
                    .GetRequiredService<IOptions<IngressHostPolicyOptions>>()
                    .Value.Policy;
                if (!policy.Allows(context.Request))
                {
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    MultiplayerMetrics.RecordIngressRejection(IngressRejectionReason.Host);
                    return;
                }

                await continuePipeline(context);
            });
            next(app);
        };
    }
}
