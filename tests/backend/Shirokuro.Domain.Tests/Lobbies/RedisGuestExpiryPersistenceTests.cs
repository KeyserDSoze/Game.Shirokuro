using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
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
/// A deterministic Redis adapter can acknowledge an opaque ZADD without
/// persisting it. The real production index must not equate acknowledgement
/// with a durable encrypted expiry/cleanup item.
/// </summary>
public sealed class RedisGuestExpiryPersistenceTests
{
    public class FaultyExpiryDatabase : DispatchProxy
    {
        private readonly Dictionary<string, double> _scores =
            new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool IgnoreWrites { get; set; }
        public bool ReturnFalseAck { get; set; }
        public string ReadFault { get; set; } = "";
        public CancellationTokenSource? CancelAfterAdd { get; set; }
        public TaskCompletionSource<double?> BlockedScore { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Adds { get; private set; }
        public int Reads { get; private set; }
        public int StoredCount
        {
            get { lock (_gate) return _scores.Count; }
        }

        private static string MemberKey(object?[] args) =>
            args[0]!.ToString() + "|" + args[1]!.ToString();

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "SortedSetAddAsync" &&
                method.ReturnType == typeof(Task<bool>))
            {
                var key = MemberKey(args!);
                var score = Convert.ToDouble(args![2]);
                lock (_gate)
                {
                    Adds++;
                    if (!IgnoreWrites) _scores[key] = score;
                }
                CancelAfterAdd?.Cancel();
                return Task.FromResult(!ReturnFalseAck);
            }

            if (method?.Name == "SortedSetScoreAsync" &&
                method.ReturnType == typeof(Task<double?>))
            {
                double? stored;
                lock (_gate)
                {
                    Reads++;
                    stored = _scores.TryGetValue(MemberKey(args!), out var value)
                        ? value : null;
                }
                return ReadFault switch
                {
                    "missing" => Task.FromResult<double?>(null),
                    "corrupt" => Task.FromResult<double?>((stored ?? 0d) + 1d),
                    "throw" => Task.FromException<double?>(
                        new IOException("private Redis host and token")),
                    "blocked" => BlockedScore.Task,
                    _ => Task.FromResult(stored)
                };
            }

            // The background worker may attempt an initial expiry scan. Its
            // own retry loop owns this failure, not the issuance HTTP path.
            if (method?.Name == "ScriptEvaluateAsync")
                return Task.FromException<RedisResult>(
                    new IOException("test-only maintenance scan unavailable"));
            throw new InvalidOperationException(
                "Unexpected Redis test database method: " + method?.Name);
        }
    }

    private static (IDatabase Database, FaultyExpiryDatabase Fake) Simulated()
    {
        var database = DispatchProxy.Create<IDatabase, FaultyExpiryDatabase>();
        return (database, (FaultyExpiryDatabase)(object)database);
    }

    private static (RedisGuestExpiryIndex Index, ServiceProvider Keys)
        Index(IDatabase database)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var provider = services.BuildServiceProvider();
        return (new RedisGuestExpiryIndex(database,
            provider.GetRequiredService<IDataProtectionProvider>(),
            TimeProvider.System, namespaceName: "schedule-readback-test"), provider);
    }

    private static Task Schedule(RedisGuestExpiryIndex index, string kind)
    {
        var player = GuestSessions.NewPlayerId();
        return kind == "issue"
            ? index.ScheduleAsync(player, DateTimeOffset.UtcNow.AddHours(1))
            : index.ScheduleRevokedAsync(player);
    }

    [Theory]
    [InlineData("issue", "ignored")]
    [InlineData("revoke", "ignored")]
    [InlineData("issue", "missing")]
    [InlineData("revoke", "missing")]
    [InlineData("issue", "corrupt")]
    [InlineData("revoke", "corrupt")]
    [InlineData("issue", "throw")]
    [InlineData("revoke", "throw")]
    [InlineData("issue", "blocked")]
    [InlineData("revoke", "blocked")]
    public async Task ScheduleNeedsExactPersistedMemberScoreAndCanRecover(
        string kind, string fault)
    {
        var (database, fake) = Simulated();
        var (index, keys) = Index(database);
        using (keys)
        {
            fake.IgnoreWrites = fault == "ignored";
            fake.ReadFault = fault == "ignored" ? "" : fault;
            var pending = Schedule(index, kind);
            Assert.Same(pending, await Task.WhenAny(
                pending, Task.Delay(TimeSpan.FromSeconds(8))));
            var error = await Record.ExceptionAsync(() => pending);
            Assert.NotNull(error);
            Assert.Equal(1, fake.Adds);
            Assert.Equal(1, fake.Reads);
            if (fault == "ignored")
                Assert.Equal(0, fake.StoredCount);
            if (fault == "blocked")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(error);
                Assert.False(fake.BlockedScore.Task.IsCompleted);
            }

            // A failed scheduling operation may have left an orphan cipher-
            // text member in Redis. Recovery only uses a NEW random member,
            // and can never retroactively authorize a previously denied
            // guest cookie.
            fake.IgnoreWrites = false;
            fake.ReadFault = "";
            await Schedule(index, kind);
            Assert.Equal(2, fake.Adds);
            Assert.Equal(2, fake.Reads);
            Assert.InRange(fake.StoredCount, 1, 2);
        }
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("revoke")]
    public async Task SchedulingChecksCancellationBeforeReadback(string kind)
    {
        var (database, fake) = Simulated();
        var (index, keys) = Index(database);
        using (keys)
        using (var abort = new CancellationTokenSource())
        {
            fake.CancelAfterAdd = abort;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => kind == "issue"
                    ? index.ScheduleAsync(GuestSessions.NewPlayerId(),
                        DateTimeOffset.UtcNow.AddHours(1), abort.Token)
                    : index.ScheduleRevokedAsync(GuestSessions.NewPlayerId(),
                        abort.Token));
            Assert.Equal(1, fake.Adds);
            Assert.Equal(0, fake.Reads);
        }
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("revoke")]
    public async Task AcknowledgementFlagIsNotTheAuthorityIfReadbackIsExact(
        string kind)
    {
        var (database, fake) = Simulated();
        var (index, keys) = Index(database);
        using (keys)
        {
            fake.ReturnFalseAck = true; // Redis may claim member was already there.
            await Schedule(index, kind);
            Assert.Equal(1, fake.Adds);
            Assert.Equal(1, fake.Reads);
            Assert.Equal(1, fake.StoredCount);
        }
    }

    private static HttpRequestMessage Issue()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions/guest");
        request.Headers.Add("Origin", "https://game.example");
        request.Headers.Add("X-Shirokuro-Request", "1");
        return request;
    }

    [Fact]
    public async Task ProductionGuestIssuanceRejectsSilentlyLostEncryptedExpiryJob()
    {
        var (database, fake) = Simulated();
        fake.IgnoreWrites = true;
        using var server = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
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
                    services.AddSingleton<IGuestExpiryIndex>(sp =>
                        new RedisGuestExpiryIndex(database,
                            sp.GetRequiredService<IDataProtectionProvider>(),
                            sp.GetRequiredService<TimeProvider>(),
                            namespaceName: "production-issuer-readback"));
                });
            });
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://game.example"),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        using (var request = Issue())
        using (var rejected = await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.False(rejected.Headers.Contains("Set-Cookie"));
            Assert.True(rejected.Headers.CacheControl?.NoStore == true);
            var body = await rejected.Content.ReadAsStringAsync();
            Assert.DoesNotContain("redis", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("guest-", body, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, fake.Adds);
        Assert.Equal(1, fake.Reads);
        Assert.Equal(0, fake.StoredCount);
        Assert.Equal(0, server.Services.GetRequiredService<GuestSessionRegistry>()
            .StoredSessionCount);

        fake.IgnoreWrites = false;
        using var healthyRequest = Issue();
        using var allowed = await client.SendAsync(healthyRequest);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Single(allowed.Headers.GetValues("Set-Cookie"));
        Assert.Equal(2, fake.Adds);
        Assert.Equal(2, fake.Reads);
        Assert.Equal(1, fake.StoredCount);
    }
}
