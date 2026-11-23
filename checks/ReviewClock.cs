// Advance only the model's clock: Git and the operating system retain real time.
sealed class ReviewClock : TimeProvider
{
    private readonly List<ReviewTimer> _timers = [];
    public TimeSpan Elapsed { get; private set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Elapsed.Ticks;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ReviewTimer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        Elapsed += amount;
        foreach (var timer in _timers.ToArray()) timer.Fire();
    }
    private sealed class ReviewTimer(ReviewClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan? _due;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot timers are used");
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Elapsed + dueTime;
            return true;
        }
        public void Fire() { if (_due <= clock.Elapsed) { _due = null; callback(state); } }
        public void Dispose() => _due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
