namespace Shirokuro.Domain.Tests.Support;

/// <summary>
/// Test-only virtual clock which can deterministically tick ASP.NET Core
/// BackgroundServices using PeriodicTimer, with no wall-clock sleeps.
/// </summary>
public sealed class ManualTickTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _utcMilliseconds =
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public override DateTimeOffset GetUtcNow()
        => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _utcMilliseconds));

    public void Advance(TimeSpan duration)
        => Interlocked.Add(ref _utcMilliseconds, (long)duration.TotalMilliseconds);

    public override ITimer CreateTimer(
        TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        lock (_gate)
            _timers.Add(timer);
        return timer;
    }

    public void Tick()
    {
        ManualTimer[] timers;
        lock (_gate)
            timers = [.. _timers];

        foreach (var timer in timers)
            timer.Fire();
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => Volatile.Read(ref _disposed) == 0;

        public void Fire()
        {
            if (Volatile.Read(ref _disposed) == 0)
                callback(state);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

