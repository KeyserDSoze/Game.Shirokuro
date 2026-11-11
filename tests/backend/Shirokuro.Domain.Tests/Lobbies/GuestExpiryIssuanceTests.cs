using System.IO;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Security;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// The expiry index is part of session issuance, not best-effort telemetry:
/// a successful cookie must never escape without durable cleanup work.
/// </summary>
public sealed class GuestExpiryIssuanceTests
{
    private sealed class ToggleIndex : IGuestExpiryIndex
    {
        public bool Fail { get; set; } = true;
        public int Accepted { get; private set; }

        public Task ScheduleAsync(string trustedPlayerId, DateTimeOffset expiresUtc,
            CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new IOException("simulated expiry index outage");
            Assert.StartsWith("guest-", trustedPlayerId, StringComparison.Ordinal);
            Assert.True(expiresUtc > DateTimeOffset.UtcNow);
            Accepted++;
            return Task.CompletedTask;
        }

        public Task<string> ScheduleRevokedAsync(string trustedPlayerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("test-revoked-member");

        public Task<IReadOnlyList<DueGuestExpiry>> ReadDueAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DueGuestExpiry>>([]);

        public Task AcknowledgeAsync(string protectedMember,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static HttpRequestMessage Issue()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/guest");
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        return request;
    }

    [Fact]
    public async Task ExpiryIndexOutageDeniesCookieAndRollsBackTicketBeforeHealthyRetry()
    {
        var index = new ToggleIndex();
        using var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            TestDataProtectionConfiguration.Apply(builder);
            TestGuestSessionRedisConfiguration.Apply(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AllowedHosts"] = "game.example",
                    ["Security:AllowedOrigins:0"] = "https://game.example"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGuestExpiryIndex>();
                services.AddSingleton<IGuestExpiryIndex>(index);
            });
        });
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using (var failed = Issue())
        using (var denied = await client.SendAsync(failed))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.False(denied.Headers.Contains("Set-Cookie"));
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        }
        Assert.Equal(0, index.Accepted);
        Assert.Equal(0, server.Services.GetRequiredService<GuestSessionRegistry>()
            .StoredSessionCount);

        index.Fail = false;
        using var healthy = Issue();
        using var issued = await client.SendAsync(healthy);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        Assert.Single(issued.Headers.GetValues("Set-Cookie"));
        Assert.Equal(1, index.Accepted);
        Assert.Equal(1, server.Services.GetRequiredService<GuestSessionRegistry>()
            .StoredSessionCount);
    }
}
