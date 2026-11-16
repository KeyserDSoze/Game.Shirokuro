using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Shirokuro.Api.Security;
using StackExchange.Redis;
using Xunit;

namespace Shirokuro.Domain.Tests.Lobbies;

/// <summary>
/// The real encrypted Redis expiry index must only acknowledge a due work
/// item once absence of that precise sorted-set member is observed. A fake
/// Redis connection can model silent ZREM loss without a nondeterministic
/// network/failover fixture.
/// </summary>
public sealed class RedisGuestExpiryAckPersistenceTests
{
    public class FaultyAckDatabase : DispatchProxy
    {
        private readonly Dictionary<string, double> _scores =
            new(StringComparer.Ordinal);

        public bool IgnoreRemove { get; set; }
        public bool ForceFalseRemoveAck { get; set; }
        public bool ReturnStaleScore { get; set; }
        public bool ThrowOnScore { get; set; }
        public bool BlockScore { get; set; }
        public CancellationTokenSource? CancelAfterRemove { get; set; }
        public int RemoveCalls { get; private set; }
        public int ScoreCalls { get; private set; }
        public RedisKey? LastRemoveKey { get; private set; }
        public RedisValue? LastRemoveMember { get; private set; }
        public RedisKey? LastScoreKey { get; private set; }
        public RedisValue? LastScoreMember { get; private set; }

        public TaskCompletionSource<double?> PendingScore { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static string Key(object?[] args) =>
            args[0]!.ToString() + "|" + args[1]!.ToString();

        public void Seed(string key, string member, double score = 250d) =>
            _scores[key + "|" + member] = score;

        public bool Contains(string key, string member) =>
            _scores.ContainsKey(key + "|" + member);

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == "SortedSetRemoveAsync" &&
                method.ReturnType == typeof(Task<bool>))
            {
                RemoveCalls++;
                LastRemoveKey = (RedisKey)args![0]!;
                LastRemoveMember = (RedisValue)args[1]!;
                var key = Key(args);
                var wasPresent = _scores.ContainsKey(key);
                if (!IgnoreRemove) _scores.Remove(key);
                CancelAfterRemove?.Cancel();
                return Task.FromResult(ForceFalseRemoveAck ? false : wasPresent);
            }
            if (method?.Name == "SortedSetScoreAsync" &&
                method.ReturnType == typeof(Task<double?>))
            {
                ScoreCalls++;
                LastScoreKey = (RedisKey)args![0]!;
                LastScoreMember = (RedisValue)args[1]!;
                return ThrowOnScore
                    ? Task.FromException<double?>(
                        new IOException("private Redis password and address"))
                    : BlockScore
                    ? PendingScore.Task
                    : Task.FromResult<double?>(ReturnStaleScore
                        ? 42d
                        : _scores.TryGetValue(Key(args), out var score)
                            ? score : null);
            }
            throw new InvalidOperationException(
                "Unexpected fake Redis operation: " + method?.Name);
        }
    }

    private static (IDatabase Database, FaultyAckDatabase Fake) FakeDatabase()
    {
        var database = DispatchProxy.Create<IDatabase, FaultyAckDatabase>();
        return (database, (FaultyAckDatabase)(object)database);
    }

    private static (RedisGuestExpiryIndex Index, ServiceProvider Keys)
        Index(IDatabase database)
    {
        var services = new ServiceCollection();
        services.AddDataProtection();
        var provider = services.BuildServiceProvider();
        return (new RedisGuestExpiryIndex(database,
            provider.GetRequiredService<IDataProtectionProvider>(),
            TimeProvider.System, namespaceName: "verified-ack"), provider);
    }

    private const string Member = "opaque-protected-member";
    private const string QueueKey =
        "shirokuro:guest-expiry:{verified-ack}:v1:pending";

    [Theory]
    [InlineData("ignored")]
    [InlineData("stale-score")]
    [InlineData("exception")]
    [InlineData("blocked")]
    public async Task UnconfirmedAckFailsClosedAndRetryCanConfirmAbsence(
        string fault)
    {
        var (database, fake) = FakeDatabase();
        var (index, provider) = Index(database);
        using (provider)
        {
            fake.Seed(QueueKey, Member);
            fake.IgnoreRemove = fault == "ignored";
            fake.ReturnStaleScore = fault == "stale-score";
            fake.ThrowOnScore = fault == "exception";
            fake.BlockScore = fault == "blocked";

            var pending = index.AcknowledgeAsync(Member);
            Assert.Same(pending, await Task.WhenAny(
                pending, Task.Delay(TimeSpan.FromSeconds(8))));
            var failed = await Record.ExceptionAsync(() => pending);
            Assert.NotNull(failed);
            Assert.Equal(1, fake.RemoveCalls);
            Assert.Equal(1, fake.ScoreCalls);
            Assert.Equal(QueueKey, fake.LastRemoveKey!.Value.ToString());
            Assert.Equal(QueueKey, fake.LastScoreKey!.Value.ToString());
            Assert.Equal(Member, fake.LastRemoveMember!.Value.ToString());
            Assert.Equal(Member, fake.LastScoreMember!.Value.ToString());
            if (fault == "ignored")
                Assert.True(fake.Contains(QueueKey, Member));
            if (fault == "blocked")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(failed);
                Assert.False(fake.PendingScore.Task.IsCompleted);
            }

            // The first ZREM may already have succeeded when SCORE errored.
            // Either way, a later sweep must be able to retry idempotently.
            fake.IgnoreRemove = false;
            fake.ReturnStaleScore = false;
            fake.ThrowOnScore = false;
            fake.BlockScore = false;
            await index.AcknowledgeAsync(Member);
            Assert.False(fake.Contains(QueueKey, Member));
            Assert.Equal(2, fake.RemoveCalls);
            Assert.Equal(2, fake.ScoreCalls);
            await index.AcknowledgeAsync(Member);
            Assert.Equal(3, fake.RemoveCalls);
            Assert.Equal(3, fake.ScoreCalls);
        }
    }

    [Fact]
    public async Task AlreadyAbsentMemberCanBeAckedEvenWithFalseZremReturn()
    {
        var (database, fake) = FakeDatabase();
        var (index, provider) = Index(database);
        using (provider)
        {
            fake.ForceFalseRemoveAck = true;
            await index.AcknowledgeAsync(Member);
            Assert.Equal(1, fake.RemoveCalls);
            Assert.Equal(1, fake.ScoreCalls);

            // Even a false ZREM response on a present member is nonauthoritative
            // when the readback proves it was actually removed.
            fake.Seed(QueueKey, Member);
            await index.AcknowledgeAsync(Member);
            Assert.False(fake.Contains(QueueKey, Member));
            Assert.Equal(2, fake.RemoveCalls);
            Assert.Equal(2, fake.ScoreCalls);
        }
    }

    [Fact]
    public async Task CancellationBetweenZremAndScoreNeverDispatchesReadback()
    {
        var (database, fake) = FakeDatabase();
        var (index, provider) = Index(database);
        using (provider)
        using (var cancelled = new CancellationTokenSource())
        {
            fake.Seed(QueueKey, Member);
            fake.CancelAfterRemove = cancelled;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => index.AcknowledgeAsync(Member, cancelled.Token));
            Assert.Equal(1, fake.RemoveCalls);
            Assert.Equal(0, fake.ScoreCalls);
        }
    }

    [Fact]
    public async Task PreCancelledAckDoesNotDispatchRedis()
    {
        var (database, fake) = FakeDatabase();
        var (index, provider) = Index(database);
        using (provider)
        using (var cancelled = new CancellationTokenSource())
        {
            fake.Seed(QueueKey, Member);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => index.AcknowledgeAsync(Member, cancelled.Token));
            Assert.Equal(0, fake.RemoveCalls);
            Assert.Equal(0, fake.ScoreCalls);
            Assert.True(fake.Contains(QueueKey, Member));
        }
    }
}
