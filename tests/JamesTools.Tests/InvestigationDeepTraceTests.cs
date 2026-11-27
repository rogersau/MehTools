using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using JamesTools.Core.Diagnostics;
using Microsoft.Diagnostics.Tracing.Session;

/// <summary>Deterministic tests require no ETW access. Program.cs registration belongs to the integrating caller.</summary>
public static class InvestigationDeepTraceTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static InvestigationClock Clock() => new(Stopwatch.GetTimestamp(), Stopwatch.Frequency, DateTimeOffset.UtcNow);
    private static long Qpc(InvestigationClock clock, double seconds) => clock.OriginQpc + (long)(seconds * clock.Frequency);
    private static InvestigationCollectorStatus Status(InvestigationDeepTrace trace) => trace.Status.Single(s => s.Source == "Deep ETW");
    public static async Task Run()
    {
        Check(InvestigationDeepTrace.ConstrainDuration(0) == 60 && InvestigationDeepTrace.ConstrainDuration(-50) == 60, "Default duration");
        Check(InvestigationDeepTrace.ConstrainDuration(1) == 30 && InvestigationDeepTrace.ConstrainDuration(90) == 90 &&
            InvestigationDeepTrace.ConstrainDuration(int.MaxValue) == 300, "Duration bounds");
        var denied = new FakePlatform { Access = false };
        await using (var trace = new InvestigationDeepTrace(Clock(), null, 30, true, null, _ => throw new Exception(), denied))
            Check(Status(trace).Availability == "unavailable" && denied.Creates == 0 && denied.Arms == 0, "Privilege check precedes all side effects");

        var noWatchdog = new FakePlatform { FailArm = true };
        await using (var trace = new InvestigationDeepTrace(Clock(), null, 30, false, null, _ => { }, noWatchdog))
            Check(Status(trace).Availability == "unavailable" && noWatchdog.Creates == 0, "Watchdog handshake fails closed");

        var broken = new FakePlatform(); broken.Session.FailStart = true;
        await using (var trace = new InvestigationDeepTrace(Clock(), null, 30, false, null, _ => { }, broken)) { }
        Check(broken.Session.Stops == 1 && broken.Session.Disposals == 1 && broken.Watchdog.Disposals == 1, "Partial startup cleanup");

        await TimingAndPrivacy();
        await BoundsAndLoss();
        await Lifecycle();
        Recovery();
        Check(Marshal.SizeOf<InvestigationDeepTraceNative.Wnode>() == 48, "Windows WNODE_HEADER ABI");
        if (nint.Size == 8)
            Check(Marshal.SizeOf<InvestigationDeepTraceNative.Properties>() == 120 && Marshal.OffsetOf<InvestigationDeepTraceNative.Properties>("LoggerThreadId").ToInt32() == 104,
                "Windows EVENT_TRACE_PROPERTIES ABI");
    }

    private static async Task TimingAndPrivacy()
    {
        var events = new ConcurrentQueue<InvestigationEvent>();
        var clock = Clock(); var platform = new FakePlatform();
        var trace = new InvestigationDeepTrace(clock, @"C:\sensitive-test-user\game.exe", 30, true, @"C:\sensitive-test-user\raw", events.Enqueue, platform);
        trace.ProcessStart(100, true); trace.ProcessStart(200, false);
        trace.ThreadStart(100, 10); trace.ThreadStart(200, 20);
        trace.Image(100, 0x1000, 0x1000, @"C:\sensitive-test-user\driver.dll");
        trace.Image(0, 0xffff0000, 0x1000, @"C:\Windows\System32\drivers\device.sys");
        Check(trace.Location(100, 0x1010) == "driver.dll+0x10" && trace.Location(200, 0x1010) == "unknown IP 0x1010", "Process-scoped address ranges");
        trace.Image(100, 0x3000, 0x1000, "late.dll", validFromQpc: Qpc(clock, 2));
        Check(trace.Location(100, 0x3010, Qpc(clock, 1)).StartsWith("unknown IP") && trace.Location(100, 0x3010, Qpc(clock, 2)) == "late.dll+0x10",
            "Module snapshots never attribute samples from before the mapping was observed");
        trace.Ready(Qpc(clock, 1), 10);
        trace.Ready(Qpc(clock, 1.1), 10); // duplicate does not reset ready time
        trace.Switch(Qpc(clock, 1.2), 0, 99, 10, 5, "Executive");
        trace.Switch(Qpc(clock, 1.3), 0, 10, 99, 5, "Executive");
        trace.Ready(Qpc(clock, 1.5), 10);
        trace.Switch(Qpc(clock, 1.6), 1, 99, 10, 5, "Executive");
        trace.Sample(Qpc(clock, 1.7), -1, 10, 0x1010, false);
        trace.Sample(Qpc(clock, 1.71), 200, 20, 0x1010, false); // unrelated process excluded
        trace.Sample(Qpc(clock, 1.72), 100, 10, 0xdead, false);
        trace.Sample(Qpc(clock, 1.73), 100, 10, 0xffff0010, true); // interrupts must not blame the interrupted game
        trace.Stack(Qpc(clock, 1.7), 100, 10, Enumerable.Repeat(0x1010UL, 40).ToArray(), true);
        trace.Activity(Qpc(clock, 1.8), "DPC", "DPC", null, null, .2, trace.Location(0, 0xffff0010));
        trace.Activity(Qpc(clock, 1.81), "File", "ReadRequest", 200, 20);
        trace.Activity(Qpc(clock, 1.82), "GPU", "QueueEvent", 200, 20);
        trace.Sample(Qpc(clock, -1), 100, 10, 1, false);
        trace.Sample(Qpc(clock, 31), 100, 10, 2, false);
        trace.Poll();
        await trace.DisposeAsync();
        var all = events.ToArray();
        var execution = all.First(e => e.EventType == "ExecutionInterval");
        Check(Math.Abs(execution.TimestampSeconds - 1.2) < .0001, "Execution interval must start at switch-in, not at its completion timestamp");
        double Sum(string type) => all.Where(e => e.EventType == type).Sum(e => e.DurationMs ?? 0);
        Check(Math.Abs(Sum("ExecutionInterval") - 100) < .01 && Math.Abs(Sum("WaitUntilReady") - 200) < .01 && Math.Abs(Sum("ReadyLatency") - 300) < .01,
            "Paired ready/wait/execution intervals with migration");
        Check(all.Any(e => e.EventType == "Sample" && e.Details["location"] == "driver.dll+0x10") &&
            all.Any(e => e.Details["location"] == "unknown IP 0xdead"), "Known offsets and unresolved IPs preserved");
        Check(all.Where(e => e.EventType == "InterruptSample").All(e => e.ProcessId is null && e.ThreadId is null), "Interrupt attribution");
        Check(all.Single(e => e.EventType == "StackAddresses").Details["location"].Contains("truncated"), "Bounded stack with explicit truncation");
        Check(!all.Any(e => e.Category == "File" || e.EventType == "Sample" && e.ProcessId == 200), "Exact target filtering");
        Check(all.Any(e => e.Category == "GPU"), "System GPU context retained without claiming game attribution");
        Check(all.All(e => e.TimestampBasis == "QPC" && e.TimestampSeconds >= 1 && e.TimestampSeconds < 30 && e.RawEvidenceReference is null), "QPC alignment and bounds");
        var portable = JsonSerializer.Serialize(new { Events = all, trace.Status });
        Check(!portable.Contains("sensitive-test-user", StringComparison.OrdinalIgnoreCase) && !portable.Contains("System32", StringComparison.OrdinalIgnoreCase), "Portable data has no paths");
        Check(trace.Status.Any(s => s.Source == "Symbols" && s.Availability == "uncollected"), "Symbols explicitly uncollected");
        trace.ProcessStop(100);
        Check(trace.Location(100, 0x1010).StartsWith("unknown IP"), "Module maps retired on process exit");
    }

    private static async Task BoundsAndLoss()
    {
        var clock = Clock(); var platform = new FakePlatform();
        using var blocked = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var trace = new InvestigationDeepTrace(clock, null, 30, false, null, _ => { entered.Set(); blocked.Wait(); }, platform);
        try
        {
            trace.Activity(Qpc(clock, 1), "GPU", "First", null, null); trace.Poll();
            Check(entered.Wait(TimeSpan.FromSeconds(3)), "Delivery worker started");
            for (int i = 0; i < InvestigationDeepTrace.AggregateLimit + 100; i++) trace.Sample(Qpc(clock, 1.1), 100, 10, (ulong)i, false);
            trace.Poll();
            Check(Status(trace).EventsDropped >= 100 + InvestigationDeepTrace.AggregateLimit - InvestigationDeepTrace.QueueLimit, "Bounded aggregation and publication queue with loss accounting");
            for (int i = 0; i <= InvestigationDeepTrace.ThreadLimit; i++) trace.ThreadStart(100, i);
            for (int i = 0; i <= InvestigationDeepTrace.ModuleLimit; i++) trace.Image(100, (ulong)i * 16, 16, "module.dll");
            Check(Status(trace).EventsDropped > 1000, "Thread/module cardinality bounded");
            trace.Ready(Qpc(clock, 2), 10);
            platform.Session.Lost = 17; trace.Poll();
            platform.Session.Lost = 3; trace.Poll();
            Check(Status(trace).EventsLost == 17, "Loss monotonic across reset/query races");
        }
        finally { blocked.Set(); await trace.DisposeAsync(); }

        var rows = new ConcurrentQueue<InvestigationEvent>(); clock = Clock(); platform = new FakePlatform();
        trace = new InvestigationDeepTrace(clock, null, 30, false, null, rows.Enqueue, platform);
        trace.ThreadStart(100, 10);
        trace.Ready(Qpc(clock, 1), 10);
        trace.Switch(Qpc(clock, 1.1), 0, 99, 10, 5, "Executive");
        trace.Switch(Qpc(clock, 1.2), 0, 10, 99, 5, "Executive");
        trace.ObserveLoss(1);
        trace.Ready(Qpc(clock, 1.3), 10); // no wait estimate across loss
        trace.ObserveLoss(2);
        trace.Switch(Qpc(clock, 1.4), 0, 99, 10, 5, "Executive"); // no ready estimate across loss
        trace.Switch(Qpc(clock, 1.5), 1, 10, 99, 1, "Executive"); // no execution estimate on an unmatched CPU
        await trace.DisposeAsync();
        Check(!rows.Any(e => e.EventType == "WaitUntilReady") && rows.Count(e => e.EventType == "ReadyLatency") == 1 &&
            rows.Where(e => e.EventType == "ExecutionInterval").Sum(e => e.DurationMs) is > 99 and < 101, "Loss and mismatched CPU cannot manufacture intervals");
        Check(Status(trace).Availability == "incomplete", "Loss coverage reported");
    }

    private static async Task Lifecycle()
    {
        var platform = new FakePlatform();
        var trace = new InvestigationDeepTrace(Clock(), null, 30, false, null, _ => { }, platform);
        var first = trace.DisposeAsync().AsTask(); var second = trace.DisposeAsync().AsTask();
        Check(ReferenceEquals(first, second), "Concurrent/repeated disposal shares one cleanup task");
        await first;
        Check(platform.Session.Stops == 1 && platform.Session.Disposals == 1 && platform.Watchdog.Disposals == 1 && platform.Session.ReaderEnded,
            "Cancellation stops session, drains reader and releases watchdog once");

        platform = new FakePlatform(); var tailRows = new ConcurrentQueue<InvestigationEvent>(); var tailClock = Clock();
        trace = new InvestigationDeepTrace(tailClock, null, 30, false, null, tailRows.Enqueue, platform);
        long tailQpc = Stopwatch.GetTimestamp();
        platform.Session.OnStop = () => { trace.Activity(tailQpc, "GPU", "BufferedTail", null, null); platform.Session.Lost = 11; };
        await trace.DisposeAsync();
        Check(tailRows.Any(e => e.EventType == "BufferedTail") && Status(trace).EventsLost == 11, "Final buffered events and final stop loss statistics retained");
        double ended = Status(trace).CollectionMilliseconds;
        await Task.Yield();
        Check(Status(trace).CollectionMilliseconds == ended, "Collection duration freezes on stop");

        platform = new FakePlatform();
        trace = new InvestigationDeepTrace(Clock(), null, 30, false, null, _ => { }, platform);
        Check(platform.Time.Duration == TimeSpan.FromSeconds(30), "Independent deadline armed");
        platform.Time.Fire(); await trace.DisposeAsync();
        Check(platform.Session.Stops == 1 && platform.Time.Disposals == 1, "Deadline cleanup is idempotent");

        platform = new FakePlatform(); platform.Session.FailRead = true;
        trace = new InvestigationDeepTrace(Clock(), null, 30, false, null, _ => { }, platform);
        await platform.Session.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await trace.DisposeAsync();
        Check(platform.Session.Stops == 1 && Status(trace).Availability == "incomplete", "Consumer error cleanup");

        platform = new FakePlatform(); platform.Session.FailStop = true;
        trace = new InvestigationDeepTrace(Clock(), null, 30, false, null, _ => { }, platform);
        await trace.DisposeAsync();
        Check(platform.Watchdog.Detaches == 1 && platform.Watchdog.Disposals == 0 && trace.Status.Any(s => s.Source == "Cleanup"),
            "Failed stop leaves independent watchdog armed while releasing local handles");

        platform = new FakePlatform(); var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        InvestigationDeepTrace? reentrant = null; var clock = Clock();
        reentrant = new InvestigationDeepTrace(clock, null, 30, false, null, e => { _ = reentrant!.DisposeAsync(); callback.TrySetResult(); }, platform);
        reentrant.Activity(Qpc(clock, 1), "GPU", "Event", null, null); reentrant.Poll();
        await callback.Task.WaitAsync(TimeSpan.FromSeconds(3)); await reentrant.DisposeAsync();
        Check(platform.Session.Stops == 1, "Publish callback may reenter disposal");

        platform = new FakePlatform(); clock = Clock();
        var failedPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        trace = new InvestigationDeepTrace(clock, null, 30, false, null, e => { failedPublish.TrySetResult(); throw new IOException(); }, platform);
        for (int i = 0; i < 10; i++) trace.Activity(Qpc(clock, 1), "GPU", "Event", null, null);
        trace.Poll();
        await failedPublish.Task.WaitAsync(TimeSpan.FromSeconds(3)); await trace.DisposeAsync();
        Check(platform.Session.Stops == 1 && Status(trace).EventsDropped == 10 && trace.Status.Any(s => s.Source == "Delivery"),
            "Callback exceptions trigger cleanup and account for omitted aggregate observations");
    }

    private static void Recovery()
    {
        long ticks = DateTime.UtcNow.Ticks;
        string dead = InvestigationDeepTraceOwnership.NewName(100, ticks), reused = InvestigationDeepTraceOwnership.NewName(200, ticks),
            live = InvestigationDeepTraceOwnership.NewName(300, ticks), unknown = InvestigationDeepTraceOwnership.NewName(400, ticks);
        var stopped = new List<string>();
        var result = InvestigationDeepTraceOwnership.Recover(new[] { dead, reused, live, unknown, "NT Kernel Logger", "WPR_initiated", "MehToolsDeep-v1-1-1-invalid", dead + "-suffix" },
            pid => pid switch { 100 => null, 200 => ticks + 1, 300 => ticks, _ => throw new UnauthorizedAccessException() }, stopped.Add);
        Check(stopped.SequenceEqual(new[] { dead, reused }), "Recovery only touches exact owned names with dead/reused owner identities");
        Check(result.Single().Availability == "incomplete" && !JsonSerializer.Serialize(result).Contains(dead), "Unknown ownership preserved and names excluded from status");
        Check(!InvestigationDeepTraceOwnership.TryOwner("MehToolsDeep-v1-2147483648-1-" + new string('a', 32), out _, out _), "Malformed ownership rejected");
        result = InvestigationDeepTraceOwnership.Recover([dead], _ => null, _ => throw new IOException());
        Check(result.Single().Availability == "incomplete" && result.Single().Detail.Contains("stop failures: 1"), "Recovery stop failure reported");
        var script = InvestigationDeepTraceOwnership.WatchdogScript(dead, 100, ticks, 30, "ready", "cancel");
        Check(script.Contains("stop " + dead + " -ets") && script.Contains("HasExited") && script.Contains("45") && !script.Contains("wpr", StringComparison.OrdinalIgnoreCase),
            "Independent watchdog has an owner handle, deadline and exact-session stop only");
    }

    /// <summary>Explicit opt-in real Windows smoke; skips without existing elevation. No registry, device or sampling-rate changes.</summary>
    public static async Task Smoke()
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true) { Console.WriteLine("SKIP Deep ETW smoke: existing elevation required."); return; }
        var before = TraceEventSession.GetActiveSessionNames().ToHashSet(StringComparer.Ordinal);
        var events = new ConcurrentQueue<InvestigationEvent>();
        using var self = Process.GetCurrentProcess();
        var trace = new InvestigationDeepTrace(Clock(), self.MainModule!.FileName, 30, true, null, events.Enqueue);
        try
        {
            Check(Status(trace).Availability == "active", JsonSerializer.Serialize(trace.Status));
            await Task.Run(() => { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < 2500) Thread.SpinWait(10000); });
        }
        finally { await trace.DisposeAsync(); }
        var after = TraceEventSession.GetActiveSessionNames();
        Check(!after.Except(before).Any(n => InvestigationDeepTraceOwnership.TryOwner(n, out _, out _)), "No owned ETW session left after smoke");
        Check(events.Any(e => e.TimestampBasis == "QPC"), "Real QPC events observed");
        Console.WriteLine(JsonSerializer.Serialize(trace.Status));
    }

    private sealed class FakePlatform : IInvestigationDeepTracePlatform
    {
        public bool Access = true, FailArm;
        public int Creates, Arms;
        public readonly FakeSession Session = new();
        public readonly FakeWatchdog Watchdog = new();
        public readonly ManualTime Time = new();
        public bool HasTraceAccess => Access;
        public long ProcessStartUtcTicks => DateTime.UtcNow.Ticks;
        public TimeProvider TimeProvider => Time;
        public IInvestigationDeepTraceWatchdog ArmWatchdog(string name, int seconds)
        { Arms++; if (FailArm) throw new InvalidOperationException(); return Watchdog; }
        public IInvestigationDeepTraceSession CreateSession(string name, string? executable, string? raw) { Creates++; return Session; }
    }
    private sealed class FakeWatchdog : IInvestigationDeepTraceWatchdog
    {
        public int Disposals, Detaches;
        public bool IsAlive => true;
        public void Dispose() => Interlocked.Increment(ref Disposals);
        public void Detach() => Interlocked.Increment(ref Detaches);
    }
    private sealed class FakeSession : IInvestigationDeepTraceSession
    {
        private readonly ManualResetEventSlim _done = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailStart, FailRead, FailStop, ReaderEnded;
        public int Stops, Disposals;
        public long Lost;
        public Action? OnStop;
        public long EventsLost => Lost;
        public void Start(InvestigationDeepTrace sink, bool usb) { if (FailStart) throw new InvalidOperationException(); }
        public void Process()
        {
            try { if (FailRead) throw new IOException(); Entered.TrySetResult(); _done.Wait(); }
            finally { ReaderEnded = true; Entered.TrySetResult(); }
        }
        public void Stop() { Interlocked.Increment(ref Stops); OnStop?.Invoke(); _done.Set(); if (FailStop) throw new IOException(); }
        public void Dispose() { Interlocked.Increment(ref Disposals); _done.Dispose(); }
    }
    private sealed class ManualTime : TimeProvider
    {
        private TimerCallback? _callback; private object? _state;
        public TimeSpan Duration;
        public int Disposals;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { _callback = callback; _state = state; Duration = dueTime; return new ManualTimer(this); }
        public void Fire() => _callback!(_state);
        private sealed class ManualTimer(ManualTime owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() => owner.Disposals++;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
