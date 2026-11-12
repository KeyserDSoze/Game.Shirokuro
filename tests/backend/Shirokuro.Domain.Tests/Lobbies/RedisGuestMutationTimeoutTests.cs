using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// No external Redis connection: the fake interface never completes the Lua
/// command. Timeouts and request aborts must not manufacture a permit.
/// </summary>
public sealed class RedisGuestMutationTimeoutTests
{
    public class PendingRedisDatabase : DispatchProxy
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<RedisResult> Pending { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "ScriptEvaluateAsync" ||
                targetMethod.ReturnType != typeof(Task<RedisResult>))
                throw new InvalidOperationException("Unexpected test Redis command.");
            Interlocked.Increment(ref _calls);
            return Pending.Task;
        }
    }

    private static (IDatabase Database, PendingRedisDatabase Controller) NeverResponds()
    {
        var database = DispatchProxy.Create<IDatabase, PendingRedisDatabase>();
        return (database, (PendingRedisDatabase)(object)database);
    }

    private static GuestMutationRateLimit Issuance() =>
        new(GuestMutationKind.SessionIssuance);

    [Fact]
    public async Task PendingLuaResultTimesOutWithoutGrantingAnyPermit()
    {
        var (database, controller) = NeverResponds();
        var limiter = new RedisGuestMutationLimiter(database);
        var pending = limiter.AcquireAsync(Issuance(), IPAddress.Loopback);
        // Guard the test against a broken implementation that never stops.
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(pending, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, controller.Calls);
        Assert.False(controller.Pending.Task.IsCompleted);
    }

    [Fact]
    public async Task InFlightRequestCancellationStopsWaitingWithoutRetry()
    {
        var (database, controller) = NeverResponds();
        var limiter = new RedisGuestMutationLimiter(database);
        using var cancelled = new CancellationTokenSource();
        var pending = limiter.AcquireAsync(
            new GuestMutationRateLimit(GuestMutationKind.RoomCreation),
            IPAddress.Parse("192.0.2.51"), cancelled.Token);
        Assert.Equal(1, controller.Calls);
        cancelled.Cancel();
        var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(pending, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, controller.Calls);
        Assert.False(controller.Pending.Task.IsCompleted);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotSendLua()
    {
        var (database, controller) = NeverResponds();
        var limiter = new RedisGuestMutationLimiter(database);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            limiter.AcquireAsync(Issuance(), null, cancelled.Token));
        Assert.Equal(0, controller.Calls);
    }

    [Fact]
    public async Task ProductionEndpointReturnsSanitized503WithoutCookieOnRedisTimeout()
    {
        var (database, controller) = NeverResponds();
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                TestDataProtectionConfiguration.Apply(builder);
                TestGuestSessionRedisConfiguration.Apply(builder);
                builder.ConfigureAppConfiguration((_, settings) =>
                    settings.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AllowedHosts"] = "game.example",
                        ["Security:AllowedOrigins:0"] = "https://game.example"
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IGuestMutationLimiter>();
                    services.AddSingleton<IGuestMutationLimiter>(
                        new RedisGuestMutationLimiter(database));
                });
            });
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/sessions/guest");
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");

        using var response = await client.SendAsync(request)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, controller.Calls);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guest-", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }
}
