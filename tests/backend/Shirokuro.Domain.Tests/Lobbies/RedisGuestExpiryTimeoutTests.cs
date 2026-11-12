using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// Tests the *real* encrypted Redis expiry queue with Redis commands that
/// never complete. No network is needed, and no untrusted IDs leave process.
/// </summary>
public sealed class RedisGuestExpiryTimeoutTests
{
    public class StalledDatabase : DispatchProxy
    {
        private int _addCalls;
        private int _readCalls;
        private int _ackCalls;
        public int AddCalls => Volatile.Read(ref _addCalls);
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public int AckCalls => Volatile.Read(ref _ackCalls);

        public TaskCompletionSource<bool> Add { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RedisResult> Read { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Ack { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "SortedSetAddAsync" when Interlocked.Increment(ref _addCalls) > 0
                    => Add.Task,
                "ScriptEvaluateAsync" when Interlocked.Increment(ref _readCalls) > 0
                    => Read.Task,
                "SortedSetRemoveAsync" when Interlocked.Increment(ref _ackCalls) > 0
                    => Ack.Task,
                _ => throw new InvalidOperationException("Unexpected test Redis command.")
            };
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _keys;
        private readonly IDatabase _database;
        public StalledDatabase Pending { get; }
        public RedisGuestExpiryIndex Index { get; }

        public Fixture()
        {
            _database = DispatchProxy.Create<IDatabase, StalledDatabase>();
            Pending = (StalledDatabase)(object)_database;
            var services = new ServiceCollection();
            services.AddDataProtection();
            _keys = services.BuildServiceProvider();
            Index = new RedisGuestExpiryIndex(_database,
                _keys.GetRequiredService<IDataProtectionProvider>(),
                TimeProvider.System, namespaceName: "timeout-test");
        }

        public int TotalCalls =>
            Pending.AddCalls + Pending.ReadCalls + Pending.AckCalls;

        public Task Call(string operation, CancellationToken cancellationToken)
        {
            var id = GuestSessions.NewPlayerId();
            return operation switch
            {
                "issue" => Index.ScheduleAsync(id,
                    DateTimeOffset.UtcNow.AddHours(1), cancellationToken),
                "revoke" => Index.ScheduleRevokedAsync(id, cancellationToken),
                "read" => Index.ReadDueAsync(cancellationToken),
                "ack" => Index.AcknowledgeAsync(
                    "opaque-expiry-member", cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
        }

        public bool IsPending(string operation) => operation switch
        {
            "issue" or "revoke" => !Pending.Add.Task.IsCompleted,
            "read" => !Pending.Read.Task.IsCompleted,
            "ack" => !Pending.Ack.Task.IsCompleted,
            _ => false
        };

        public void Dispose() => _keys.Dispose();
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("revoke")]
    [InlineData("read")]
    [InlineData("ack")]
    public async Task PreCancelledOperationCannotSendAnyRedisCommand(
        string operation)
    {
        using var fixture = new Fixture();
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Call(operation, aborted.Token));
        Assert.Equal(0, fixture.TotalCalls);
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("revoke")]
    [InlineData("read")]
    [InlineData("ack")]
    public async Task HostOrClientAbortStopsAnAlreadyDispatchedCommand(
        string operation)
    {
        using var fixture = new Fixture();
        using var aborted = new CancellationTokenSource();
        var pending = fixture.Call(operation, aborted.Token);
        Assert.Equal(1, fixture.TotalCalls);

        aborted.Cancel();
        var completed = await Task.WhenAny(pending,
            Task.Delay(TimeSpan.FromMilliseconds(1500)));
        Assert.Same(pending, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.TotalCalls);
        Assert.True(fixture.IsPending(operation));
    }

    [Theory]
    [InlineData("issue")]
    [InlineData("revoke")]
    [InlineData("read")]
    [InlineData("ack")]
    public async Task StalledRedisTimesOutRatherThanConfirmingPersistenceOrAck(
        string operation)
    {
        using var fixture = new Fixture();
        var pending = fixture.Call(operation, CancellationToken.None);
        var completed = await Task.WhenAny(pending,
            Task.Delay(TimeSpan.FromSeconds(8)));
        Assert.Same(pending, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, fixture.TotalCalls);
        // The Redis Task has NOT completed; no issuance, cleanup or ack is
        // fabricated in the caller. The server may still execute later.
        Assert.True(fixture.IsPending(operation));
    }
}
