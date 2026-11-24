namespace OpenXLR.Tests;

/// <summary>
/// A clock that moves only when the test moves it, and fires the timers
/// it made on the test's own thread at the moment they are due. What the
/// sender and the echo wait do then depends on the steps of the test,
/// never on how busy the machine is.
/// </summary>
internal sealed class ManualTime(long timestampFrequency = TimeSpan.TicksPerSecond) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private long _now;
    private long _wallStep;

    public override long TimestampFrequency => timestampFrequency;

    public override long GetTimestamp() => (long)((Int128)Interlocked.Read(ref _now) * TimestampFrequency / TimeSpan.TicksPerSecond);

    /// <summary>Wall time moves with the clock, from a fixed start, plus every <see cref="StepWallClock"/>.</summary>
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)
        .AddTicks(Interlocked.Read(ref _now) + Interlocked.Read(ref _wallStep));

    /// <summary>Set the wall clock forward or back, as a time sync does; the timestamps and timers do not move.</summary>
    public void StepWallClock(TimeSpan by) => Interlocked.Add(ref _wallStep, by.Ticks);

    public TimeSpan Elapsed => TimeSpan.FromTicks(Interlocked.Read(ref _now));

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Move the clock on, firing each timer at its due time on the way.</summary>
    public void Advance(TimeSpan by)
    {
        long target = _now + by.Ticks;
        while (_timers.Where(t => t.Due is { } due && due <= target).OrderBy(t => t.Due).FirstOrDefault() is { } next)
        {
            Interlocked.Exchange(ref _now, Math.Max(_now, next.Due!.Value));
            next.Fire();
        }
        Interlocked.Exchange(ref _now, target);
    }

    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public long? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : time._now + dueTime.Ticks;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan ? null : time._now + _period.Ticks;
            callback(state);
        }

        public void Dispose() => time._timers.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
