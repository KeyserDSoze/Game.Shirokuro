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
/// A Lua success with an ignored write is not a permit. The receipt must be
/// readable on the same connector before a guest ticket/room is authorized.
/// </summary>
public sealed class RedisGuestMutationReceiptTests
{
    public class ReceiptDatabase : DispatchProxy
    {
        public string Mode { get; set; } = "valid";
        public bool LuaAllowed { get; set; } = true;
        public long LuaOrdinal { get; set; } = 1;
        public int LuaCalls { get; private set; }
        public int ReceiptReads { get; private set; }
        public string? IssuedReceipt { get; private set; }
        public string? ReadReceipt { get; private set; }
        public RedisKey IssuedKey { get; private set; }
        public RedisKey ReadKey { get; private set; }
        public CancellationTokenSource? CancelAfterLua { get; set; }
        public TaskCompletionSource<RedisResult> PendingReceipt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "ScriptEvaluateAsync" &&
                targetMethod.ReturnType == typeof(Task<RedisResult>))
            {
                var values = (RedisValue[])args![2]!;
                if (values.Length == 3)
                {
                    LuaCalls++;
                    IssuedKey = ((RedisKey[])args[1]!)[0];
                    IssuedReceipt = values[2].ToString();
                    CancelAfterLua?.Cancel();
                    return Task.FromResult(RedisResult.Create(
                        new RedisValue[] { LuaAllowed ? 1 : 0, 45_000,
                                           LuaAllowed ? LuaOrdinal : 0 }));
                }

                if (values.Length == 4)
                {
                    ReceiptReads++;
                    ReadKey = ((RedisKey[])args[1]!)[0];
                    ReadReceipt = values[0].ToString();
                    Assert.Contains("HLEN", (string)args[0]!);
                    Assert.Contains("PTTL", (string)args[0]!);
                    Assert.Equal(LuaOrdinal, (long)values[1]);
                    Assert.Equal(12, (long)values[2]);
                    Assert.Equal(RedisGuestMutationLimiter.WindowMilliseconds,
                        (long)values[3]);
                    return Mode switch
                    {
                        "missing" or "wrong" or "stale-count" or
                            "missing-ttl" or "extended-ttl" or
                            "invalid-cardinality" or "over-limit" =>
                            Task.FromResult(RedisResult.Create(
                                (RedisValue)0, ResultType.Integer)),
                        "stalled" => PendingReceipt.Task,
                        "exception" => Task.FromException<RedisResult>(
                            new IOException("Fake Redis connection detail")),
                        _ => Task.FromResult(RedisResult.Create(
                            (RedisValue)1, ResultType.Integer))
                    };
                }
            }
            throw new InvalidOperationException("Unexpected test Redis command.");
        }
    }

    private static (IDatabase Database, ReceiptDatabase Proxy) Create(string mode)
    {
        var db = DispatchProxy.Create<IDatabase, ReceiptDatabase>();
        var proxy = (ReceiptDatabase)(object)db;
        proxy.Mode = mode;
        return (db, proxy);
    }

    private static readonly GuestMutationRateLimit Policy =
        new(GuestMutationKind.SessionIssuance);

    [Fact]
    public async Task SuccessfulLuaIsConfirmedBySameReceiptAndKey()
    {
        var (db, proxy) = Create("valid");
        var decision = await new RedisGuestMutationLimiter(db)
            .AcquireAsync(Policy, IPAddress.Loopback);
        Assert.True(decision.Allowed);
        Assert.Equal(45_000, decision.RetryAfterMilliseconds);
        Assert.Equal(1, proxy.LuaCalls);
        Assert.Equal(1, proxy.ReceiptReads);
        Assert.StartsWith("proof:", proxy.IssuedReceipt, StringComparison.Ordinal);
        Assert.Equal(38, proxy.IssuedReceipt!.Length);
        Assert.Equal(proxy.IssuedReceipt, proxy.ReadReceipt);
        Assert.Equal(proxy.IssuedKey, proxy.ReadKey);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("stale-count")]
    [InlineData("missing-ttl")]
    [InlineData("extended-ttl")]
    [InlineData("invalid-cardinality")]
    [InlineData("over-limit")]
    [InlineData("exception")]
    public async Task LuaSuccessWithoutDurableReceiptNeverGrantsPermit(string mode)
    {
        var (db, proxy) = Create(mode);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new RedisGuestMutationLimiter(db)
                .AcquireAsync(Policy, IPAddress.Loopback));
        Assert.Equal(1, proxy.LuaCalls);
        Assert.Equal(1, proxy.ReceiptReads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public async Task InvalidIssuedOrdinalCannotGrantPermitOrTriggerRead(long ordinal)
    {
        var (db, proxy) = Create("valid");
        proxy.LuaOrdinal = ordinal;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RedisGuestMutationLimiter(db)
                .AcquireAsync(Policy, IPAddress.Loopback));
        Assert.Equal(1, proxy.LuaCalls);
        Assert.Equal(0, proxy.ReceiptReads);
    }

    [Fact]
    public async Task DeniedLuaDoesNotAttemptReceiptRead()
    {
        var (db, proxy) = Create("missing");
        proxy.LuaAllowed = false;
        var decision = await new RedisGuestMutationLimiter(db)
            .AcquireAsync(Policy, IPAddress.Loopback);
        Assert.False(decision.Allowed);
        Assert.Equal(0, proxy.ReceiptReads);
    }

    [Fact]
    public async Task StalledReceiptReadTimesOutWithinSharedDeadlineWithoutRetry()
    {
        var (db, proxy) = Create("stalled");
        var pending = new RedisGuestMutationLimiter(db)
            .AcquireAsync(Policy, IPAddress.Loopback);
        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(pending, finished);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, proxy.LuaCalls);
        Assert.Equal(1, proxy.ReceiptReads);
        Assert.False(proxy.PendingReceipt.Task.IsCompleted);
    }

    [Fact]
    public async Task AbortedLuaAcknowledgementCannotDispatchReceiptRead()
    {
        var (db, proxy) = Create("valid");
        using var cancelled = new CancellationTokenSource();
        proxy.CancelAfterLua = cancelled;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RedisGuestMutationLimiter(db)
                .AcquireAsync(Policy, IPAddress.Loopback, cancelled.Token));
        Assert.Equal(1, proxy.LuaCalls);
        Assert.Equal(0, proxy.ReceiptReads);
    }

    [Fact]
    public async Task ProductionUnconfirmedPermitReturns503WithoutGuestCookie()
    {
        var (db, proxy) = Create("missing");
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
                        new RedisGuestMutationLimiter(db));
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

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Redis", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof:", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, proxy.LuaCalls);
        Assert.Equal(1, proxy.ReceiptReads);
    }
}
