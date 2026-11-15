using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shirokuro.Api.Readiness;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

public sealed class ReadinessDrainTests
{
    private sealed class ManualDrain : IReadinessDrainSignal
    {
        private int _stopping;
        public bool IsDraining => Volatile.Read(ref _stopping) != 0;
        public void Stop() => Interlocked.Exchange(ref _stopping, 1);
    }

    private sealed class ManualReadiness(Task<bool>? pending = null)
        : IDeploymentReadiness
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            Started.TrySetResult(true);
            return pending ?? Task.FromResult(true);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();

        public void Dispose()
        {
            _started.Dispose();
            _stopping.Dispose();
            _stopped.Dispose();
        }
    }

    private static WebApplicationFactory<Program> Host(
        string environment, IReadinessDrainSignal drain,
        Action<IServiceCollection> configureReadiness) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (environment == Environments.Production)
            {
                TestDataProtectionConfiguration.Apply(builder);
                TestGuestSessionRedisConfiguration.Apply(builder);
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["AllowedHosts"] = "game.example",
                        ["Security:AllowedOrigins:0"] = "https://game.example"
                    }));
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IReadinessDrainSignal>();
                services.AddSingleton<IReadinessDrainSignal>(drain);
                services.RemoveAll<IDeploymentReadiness>();
                configureReadiness(services);
            });
        });

    private static HttpClient Client(WebApplicationFactory<Program> server,
        string environment) =>
        server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(environment == Environments.Production
                ? "https://game.example" : "http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

    private static async Task AssertReadyResponse(
        HttpResponseMessage response, HttpStatusCode expected, string status)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("Retry-After"));
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        Assert.Equal(status, document.RootElement.GetProperty("status").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public void RealHostLifetimeSignalBecomesMonotonicallyUnreadyOnShutdown()
    {
        using var lifetime = new FakeLifetime();
        var signal = new HostLifetimeReadinessDrainSignal(lifetime);
        Assert.False(signal.IsDraining);
        Assert.False(signal.IsDraining);

        lifetime.StopApplication();
        Assert.True(signal.IsDraining);
        Assert.True(signal.IsDraining);
    }

    [Fact]
    public void PreStopIsMonotonicAndDoesNotTriggerHostShutdown()
    {
        using var lifetime = new FakeLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(lifetime);
        Assert.False(signal.IsDraining);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);

        signal.BeginPreStopDrain();
        Assert.True(signal.IsDraining);
        // The orchestrator can let the LB remove this node while it stays
        // alive. SIGTERM/ApplicationStopping is still a separate phase.
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        signal.BeginPreStopDrain(); // Safe if preStop is delivered twice.
        Assert.True(signal.IsDraining);
        lifetime.StopApplication();
        Assert.True(signal.IsDraining);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData(" ")]
    public void InvalidPreStopOptionAlwaysFailsClosed(string malformed)
    {
        using var lifetime = new FakeLifetime();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostLifetimeReadinessDrainSignal.EnablePreStopSignalKey] =
                    malformed
            }).Build();
        Assert.Throws<InvalidOperationException>(() =>
            new HostLifetimeReadinessDrainSignal(lifetime, configuration));
    }

    [Fact]
    public void OptInLinuxSignalCanBeInstalledWithoutStoppingTheHost()
    {
        using var lifetime = new FakeLifetime();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostLifetimeReadinessDrainSignal.EnablePreStopSignalKey] =
                    "true"
            }).Build();
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() =>
                new HostLifetimeReadinessDrainSignal(lifetime, configuration));
            return;
        }

        // Register the same real POSIX handler that the host installs during
        // startup. Do not send a global signal to the xUnit test process.
        using var signal = new HostLifetimeReadinessDrainSignal(
            lifetime, configuration);
        Assert.False(signal.IsDraining);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        signal.BeginPreStopDrain(); // The POSIX callback's only transition.
        Assert.True(signal.IsDraining);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task EarlyPreStopTurnsOffReadinessAndAdmissionButKeepsHostAlive(
        string environment)
    {
        using var lifetime = new FakeLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(lifetime);
        var readiness = new ManualReadiness();
        using var server = Host(environment, signal, services =>
            services.AddSingleton<IDeploymentReadiness>(readiness));
        using var client = Client(server, environment);

        using (var ready = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(ready, HttpStatusCode.OK, "ready");
        Assert.Equal(1, readiness.Calls);

        signal.BeginPreStopDrain();
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        using (var draining = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(draining,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(1, readiness.Calls); // Not a new Redis/keyring probe.

        using var post = new HttpRequestMessage(HttpMethod.Post,
            "/api/sessions/guest");
        post.Headers.TryAddWithoutValidation("Origin", environment ==
            "Production" ? "https://game.example" : "http://localhost");
        post.Headers.TryAddWithoutValidation(
            "X-Shirokuro-Request", "1");
        using var rejected = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal("no-store", rejected.Headers.CacheControl?.ToString());
        Assert.False(rejected.Headers.Contains("Set-Cookie"));
        Assert.False(rejected.Headers.Contains("Retry-After"));
        Assert.False(rejected.Headers.Contains("Location"));
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task EarlyPreStopWinsAgainstInFlightReadinessDependency(
        string environment)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new FakeLifetime();
        using var signal = new HostLifetimeReadinessDrainSignal(lifetime);
        var readiness = new ManualReadiness(completion.Task);
        using var server = Host(environment, signal, services =>
            services.AddSingleton<IDeploymentReadiness>(readiness));
        using var client = Client(server, environment);
        var pending = client.GetAsync("/api/ready");
        await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        signal.BeginPreStopDrain();
        completion.SetResult(true);
        using (var denied = await pending.WaitAsync(TimeSpan.FromSeconds(10)))
            await AssertReadyResponse(denied,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(1, readiness.Calls);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task DrainingNodeNeverResolvesReadinessOrStartsRedisProbe(
        string environment)
    {
        var drain = new ManualDrain();
        drain.Stop();
        var resolved = 0;
        using var server = Host(environment, drain, services =>
            services.AddSingleton<IDeploymentReadiness>(_ =>
            {
                Interlocked.Increment(ref resolved);
                throw new InvalidOperationException(
                    "Readiness dependencies must not be resolved while draining.");
            }));
        using var client = Client(server, environment);

        using (var draining = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(draining,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        using (var repeated = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(repeated,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(0, Volatile.Read(ref resolved));

        // Process liveness stays separate: the process may still answer
        // health checks during an orderly HTTP-connection drain.
        using var healthy = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Equal("no-store", healthy.Headers.CacheControl?.ToString());
        Assert.False(healthy.Headers.Contains("Set-Cookie"));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task SuccessfulOrCachedReadyCannotSurviveHostShutdown(
        string environment)
    {
        var drain = new ManualDrain();
        var readiness = new ManualReadiness();
        using var server = Host(environment, drain, services =>
            services.AddSingleton<IDeploymentReadiness>(readiness));
        using var client = Client(server, environment);

        using (var healthy = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(healthy, HttpStatusCode.OK, "ready");
        Assert.Equal(1, readiness.Calls);

        drain.Stop();
        using (var rejected = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(rejected,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        using (var repeated = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(repeated,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(1, readiness.Calls); // No further store/keyring work.

        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ProbeCompletingAfterShutdownCannotPublishReady(
        string environment)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new ManualDrain();
        var readiness = new ManualReadiness(completion.Task);
        using var server = Host(environment, drain, services =>
            services.AddSingleton<IDeploymentReadiness>(readiness));
        using var client = Client(server, environment);

        var pending = client.GetAsync("/api/ready");
        await readiness.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, readiness.Calls);

        // A Redis/DP probe may have begun while the process was ready.
        // Host shutdown between the request and the completed probe must
        // win even when that probe returns a legitimate cached 200 result.
        drain.Stop();
        completion.SetResult(true);
        using (var response = await pending)
            await AssertReadyResponse(response,
                HttpStatusCode.ServiceUnavailable, "unavailable");

        using (var rejected = await client.GetAsync("/api/ready"))
            await AssertReadyResponse(rejected,
                HttpStatusCode.ServiceUnavailable, "unavailable");
        Assert.Equal(1, readiness.Calls);
    }
}
