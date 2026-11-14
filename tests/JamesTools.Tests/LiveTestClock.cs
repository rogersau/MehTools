using System.Threading;
// Auto-advancing fake clock: each requested delay advances virtual time and completes asynchronously.
internal sealed class LiveTestClock : TimeProvider
{
    private long ticks;
    public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Add(ref ticks, dueTime.Ticks);
        var timer = new TimerStub(); ThreadPool.QueueUserWorkItem(_ => { if (!timer.Disposed) callback(state); }); return timer;
    }
    private sealed class TimerStub : ITimer
    {
        internal volatile bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }
}
