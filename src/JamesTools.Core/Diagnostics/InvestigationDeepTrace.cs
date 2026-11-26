using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace JamesTools.Core.Diagnostics;

/// <summary>Opt-in, bounded ETW evidence. The caller must not also run HardFaultTrace.</summary>
public sealed class InvestigationDeepTrace : IAsyncDisposable
{
    internal const int AggregateLimit = 2048, QueueLimit = 128, ThreadLimit = 32768, ModuleLimit = 8192;
    private readonly object _gate = new();
    private readonly InvestigationClock _clock;
    private readonly Action<InvestigationEvent> _publish;
    private readonly Channel<InvestigationEvent> _output = Channel.CreateBounded<InvestigationEvent>(new BoundedChannelOptions(QueueLimit)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, InvestigationCollectorStatus> _status = new();
    private readonly Dictionary<AggregateKey, Aggregate> _aggregates = new();
    private readonly Dictionary<int, ThreadObservation> _threads = new();
    private readonly Dictionary<int, (int Thread, long Qpc)> _running = new();
    private readonly Dictionary<int, SortedList<ulong, Module>> _modules = new();
    private readonly HashSet<int> _targets = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly bool _filtered;
    private readonly double _started;
    private readonly int _duration;
    private IInvestigationDeepTraceSession? _session;
    private IInvestigationDeepTraceWatchdog? _watchdog;
    private ITimer? _deadline;
    private Task? _reader, _timer, _delivery, _dispose;
    private long _lost, _dropped, _stacks;
    private long _aggregationTicks;
    private double _window = double.NaN;
    private int _moduleCount;
    private bool _stopping;
    private bool _coverageIncomplete;
    private bool _completed, _sessionStarted;
    private double? _endSeconds;

    public InvestigationDeepTrace(InvestigationClock clock, string? executablePath, int durationSeconds, bool usbTracing,
        string? rawDirectory, Action<InvestigationEvent> publish)
        : this(clock, executablePath, durationSeconds, usbTracing, rawDirectory, publish, new InvestigationDeepTracePlatform()) { }

    internal InvestigationDeepTrace(InvestigationClock clock, string? executablePath, int durationSeconds, bool usbTracing,
        string? rawDirectory, Action<InvestigationEvent> publish, IInvestigationDeepTracePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(publish);
        if (clock.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(clock));
        _clock = clock; _publish = publish; _filtered = !string.IsNullOrWhiteSpace(executablePath);
        _duration = ConstrainDuration(durationSeconds); _started = clock.NowSeconds;
        SetStatus("Symbols", "uncollected", "No symbol download or function resolution. Module+offset identifies an address, never a function.");
        SetStatus("Stacks", "unavailable", "CPU stack capture requested only if tracing starts; completeness is not guaranteed.");
        SetStatus("USB", usbTracing ? "pending" : "disabled", usbTracing ? "Explicit USB provider events requested." : "Explicit USB tracing disabled.");
        SetStatus("Raw ETL", rawDirectory is null ? "disabled" : "pending", rawDirectory is null ? "No raw ETL requested." : "Private local circular ETL requested; never included in portable evidence.");
        foreach (var channel in new[] { "Scheduling", "CPU", "DPC/ISR", "Disk/File/Memory", "GPU" }) SetStatus(channel, "pending", "Deep ETW channel requested.");
        try
        {
            if (!platform.HasTraceAccess)
            {
                SetStatus("Deep ETW", "unavailable", "Existing elevated permissions required; no automatic elevation.");
                MarkUnstartedChannels();
                return;
            }
            var name = InvestigationDeepTraceOwnership.NewName(Environment.ProcessId, platform.ProcessStartUtcTicks);
            // Arm before enabling any provider: fail closed if an independent cleanup process cannot start.
            _watchdog = platform.ArmWatchdog(name, _duration);
            _session = platform.CreateSession(name, executablePath, rawDirectory);
            _session.Start(this, usbTracing);
            _sessionStarted = true;
            SetStatus("Deep ETW", "active", $"Bounded {_duration}s capture; scheduling requires observed pairs. System activity is not proof of causation.");
            lock (_gate)
            {
                _deadline = platform.TimeProvider.CreateTimer(state => { _ = DisposeAsync(); }, null, TimeSpan.FromSeconds(_duration), Timeout.InfiniteTimeSpan);
                _delivery = Task.Run(DeliverAsync);
                _reader = Task.Run(Read);
                _timer = Task.Run(TimerAsync);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Deep ETW", "unavailable", "Startup failed (" + ex.GetType().Name + "); normal recording can continue.");
            MarkUnstartedChannels();
            // Always use the same cleanup path, including partially enabled sessions.
            lock (_gate) _dispose ??= Task.Run(StopAsync);
        }
    }

    /// <summary>Snapshots contain no raw file paths, executable paths, device identities or session names.</summary>
    public IReadOnlyList<InvestigationCollectorStatus> Status
    {
        get
        {
            lock (_gate) return _status.Values.Select(s => s.Source == "Deep ETW"
                ? s with { Availability = s.Availability == "stopped" && (_lost + _dropped > 0 || _status.Values.Any(v => v.Availability == "incomplete")) ? "incomplete" : s.Availability,
                    EventsLost = _lost, EventsDropped = _dropped,
                    CollectionMilliseconds = Interlocked.Read(ref _aggregationTicks) * 1000d / Stopwatch.Frequency,
                    ActiveSeconds = _sessionStarted ? Math.Max(0, Math.Min(_duration, (_endSeconds ?? _clock.NowSeconds) - _started)) : 0,
                    Detail = s.Detail + " Collection cost measures aggregation wall time only; excludes ETW parsing, native callbacks, address lookup and kernel tracing. Active duration is reported separately." }
                : s).ToArray();
        }
    }

    /// <summary>Stops only strictly named sessions whose recorded owner process is provably gone (PID reuse checked).</summary>
    public static IReadOnlyList<InvestigationCollectorStatus> RecoverOrphanedSessions() => InvestigationDeepTraceOwnership.Recover();

    internal static int ConstrainDuration(int seconds) => seconds <= 0 ? 60 : Math.Clamp(seconds, 30, 300);
    internal void SetStatus(string source, string availability, string detail)
    { lock (_gate) _status[source] = new(source, availability, detail); }
    private void MarkUnstartedChannels()
    {
        lock (_gate)
            foreach (string source in _status.Where(p => p.Value.Availability == "pending").Select(p => p.Key).ToArray())
                SetStatus(source, "unavailable", "Deep ETW did not start; channel not collected.");
    }

    private void Read()
    {
        try
        {
            _session!.Process();
            lock (_gate) if (!_stopping) SetStatus("Deep ETW", "incomplete", "ETW consumer ended early.");
        }
        catch (Exception ex) { SetStatus("Deep ETW", "incomplete", "Consumer failed (" + ex.GetType().Name + ")."); }
        finally { _ = DisposeAsync(); }
    }

    private async Task TimerAsync()
    {
        try
        {
            using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await tick.WaitForNextTickAsync(_cancel.Token).ConfigureAwait(false))
            {
                Poll();
                if (_clock.NowSeconds - _started >= _duration) break;
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { SetStatus("Deep ETW", "incomplete", "Health query failed (" + ex.GetType().Name + ")."); }
        _ = DisposeAsync();
    }

    internal void Poll()
    {
        lock (_gate) if (_stopping) return;
        if (_watchdog is { IsAlive: false }) throw new InvalidOperationException("Independent watchdog exited early.");
        if (_session is { } session) ObserveLoss(session.EventsLost);
        lock (_gate) Flush();
    }

    internal void ObserveLoss(long lost)
    {
        lock (_gate)
        {
            if (lost <= _lost) return;
            _lost = lost;
            // Missing switches/ready events invalidate all currently paired intervals.
            InvalidateScheduling();
        }
    }

    internal void InvalidateScheduling()
    {
        lock (_gate)
        {
            _coverageIncomplete = true;
            _running.Clear();
            foreach (var t in _threads.Values) { t.Ready = null; t.Wait = null; }
        }
    }

    internal int ProcessForThread(int pid, int tid)
    { lock (_gate) return pid >= 0 ? pid : _threads.TryGetValue(tid, out var t) ? t.Process : pid; }
    internal bool WantsProcess(int pid, int tid)
    { lock (_gate) return Selected(ProcessForThread(pid, tid)); }

    internal void ProcessStart(int pid, bool selected, bool rundown = false)
    {
        lock (_gate)
        {
            if (rundown && _targets.Contains(pid)) return;
            if (!rundown) ProcessStop(pid);
            if (selected) { if (_targets.Count < ThreadLimit) _targets.Add(pid); else _dropped++; }
        }
    }
    internal void ProcessStop(int pid)
    {
        lock (_gate)
        {
            _targets.Remove(pid);
            foreach (var tid in _threads.Where(p => p.Value.Process == pid).Select(p => p.Key).ToArray()) ThreadStop(tid);
            if (_modules.Remove(pid, out var modules)) _moduleCount -= modules.Count;
        }
    }
    internal void ThreadStart(int pid, int tid)
    {
        lock (_gate)
        {
            ThreadStop(tid);
            if (!Selected(pid)) return;
            if (_threads.Count >= ThreadLimit) { _dropped++; return; }
            _threads[tid] = new(pid);
        }
    }
    internal void ThreadStop(int tid)
    {
        lock (_gate)
        {
            _threads.Remove(tid);
            foreach (var cpu in _running.Where(p => p.Value.Thread == tid).Select(p => p.Key).ToArray()) _running.Remove(cpu);
        }
    }
    private bool Selected(int pid) => !_filtered || _targets.Contains(pid);
    internal void Ready(long qpc, int tid)
    {
        lock (_gate)
        {
            if (!Accept(qpc) || !_threads.TryGetValue(tid, out var t)) return;
            if (t.Wait is { } wait) Add(qpc, "Scheduling", "WaitUntilReady", t.Process, tid, "", Milliseconds(wait, qpc));
            t.Wait = null;
            // First ready event defines the start; duplicates do not shorten latency.
            t.Ready ??= qpc;
        }
    }
    internal void Switch(long qpc, int cpu, int oldTid, int newTid, int oldState, string waitReason)
    {
        lock (_gate)
        {
            if (!Accept(qpc)) return;
            if (_threads.TryGetValue(oldTid, out var old))
            {
                Add(qpc, "Scheduling", "SwitchOut", old.Process, oldTid, "state=" + oldState + ";reason=" + waitReason);
                if (_running.TryGetValue(cpu, out var run) && run.Thread == oldTid)
                    Add(qpc, "Scheduling", "ExecutionInterval", old.Process, oldTid, "", Milliseconds(run.Qpc, qpc));
                old.Ready = oldState is 1 or 3 ? qpc : null; // Ready or Standby; Waiting = 5.
                old.Wait = oldState == 5 ? qpc : null;
            }
            _running.Remove(cpu);
            if (_threads.TryGetValue(newTid, out var next))
            {
                Add(qpc, "Scheduling", "SwitchIn", next.Process, newTid, "");
                if (next.Ready is { } ready) Add(qpc, "Scheduling", "ReadyLatency", next.Process, newTid, "", Milliseconds(ready, qpc));
                next.Ready = null; next.Wait = null;
                if (_running.Count < 4096) _running[cpu] = (newTid, qpc); else _dropped++;
            }
        }
    }
    private double Milliseconds(long start, long end) => end >= start ? (end - start) * 1000d / _clock.Frequency : double.NaN;

    internal void Image(int pid, ulong address, ulong size, string name, bool unload = false, long? validFromQpc = null)
    {
        lock (_gate)
        {
            // PID 0/4 image rundown may describe kernel drivers needed for DPC/ISR addresses.
            if (!Selected(pid) && pid is not (0 or 4)) return;
            if (!_modules.TryGetValue(pid, out var list))
            {
                if (unload || _moduleCount >= ModuleLimit) { if (!unload) _dropped++; return; }
                _modules[pid] = list = new();
            }
            if (unload) { if (list.Remove(address)) _moduleCount--; if (list.Count == 0) _modules.Remove(pid); return; }
            if (!list.ContainsKey(address) && _moduleCount >= ModuleLimit) { _dropped++; return; }
            if (!list.ContainsKey(address)) _moduleCount++;
            list[address] = new(size, Path.GetFileName(name.Replace('\\', '/')), validFromQpc);
        }
    }
    internal string Location(int pid, ulong ip, long? qpc = null)
    {
        lock (_gate)
        {
            if (TryLocation(pid, ip, qpc, out string? location) || pid != 0 && TryLocation(0, ip, qpc, out location) || pid != 4 && TryLocation(4, ip, qpc, out location))
                return location!;
            return "unknown IP 0x" + ip.ToString("x", CultureInfo.InvariantCulture);
        }
    }
    private bool TryLocation(int pid, ulong ip, long? qpc, out string? location)
    {
        location = null;
        if (_modules.TryGetValue(pid, out var list))
        {
            int lo = 0, hi = list.Count - 1, found = -1;
            while (lo <= hi) { int mid = lo + (hi - lo) / 2; if (list.Keys[mid] <= ip) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            if (found >= 0 && ip - list.Keys[found] < list.Values[found].Size &&
                (qpc is null || list.Values[found].ValidFromQpc is not { } from || qpc.Value >= from))
            {
                location = list.Values[found].Name + "+0x" + (ip - list.Keys[found]).ToString("x", CultureInfo.InvariantCulture);
                return true;
            }
        }
        return false;
    }
    internal void Sample(long qpc, int pid, int tid, ulong ip, bool interrupt)
    {
        lock (_gate)
        {
            if (pid < 0 && _threads.TryGetValue(tid, out var t)) pid = t.Process;
            if (!interrupt && !Selected(pid)) return;
            Add(qpc, "CPU", interrupt ? "InterruptSample" : "Sample", interrupt ? null : pid, interrupt ? null : tid, Location(interrupt ? 0 : pid, ip, qpc));
        }
    }
    internal void Stack(long qpc, int pid, int tid, IReadOnlyList<ulong> ips, bool truncated)
    {
        lock (_gate)
        {
            if (pid < 0 && _threads.TryGetValue(tid, out var t)) pid = t.Process;
            if (!Selected(pid) || !Accept(qpc) || ips.Count == 0) return;
            _stacks++;
            Add(qpc, "CPU", "StackAddresses", pid, tid, string.Join(";", ips.Take(32).Select(ip => Location(pid, ip, qpc))) + (truncated ? ";truncated" : ""));
        }
    }
    internal void Activity(long qpc, string category, string type, int? pid, int? tid, double? ms = null, string location = "")
    {
        lock (_gate)
        {
            if (category is "Disk" or "File" or "Memory" && pid is { } id && !Selected(id)) return;
            Add(qpc, category, type, pid, tid, location, ms);
        }
    }
    private bool Accept(long qpc)
    {
        double seconds = _clock.FromQpc(qpc);
        return !_completed && double.IsFinite(seconds) && seconds >= _started && seconds <= Math.Min(_started + _duration, _endSeconds ?? double.PositiveInfinity);
    }
    private void Add(long qpc, string category, string type, int? pid, int? tid, string location, double? ms = null)
    {
        var begin = Stopwatch.GetTimestamp();
        try
        {
        if (!Accept(qpc)) return;
        if (ms.HasValue && (!double.IsFinite(ms.Value) || ms < 0)) { _dropped++; return; }
        double seconds = _clock.FromQpc(qpc);
        if (double.IsNaN(_window)) _window = seconds;
        if (seconds - _window >= .5) { Flush(); _window = seconds; }
        var key = new AggregateKey(category, type, pid >= 0 ? pid : null, tid >= 0 ? tid : null, location);
        if (!_aggregates.TryGetValue(key, out var a))
        {
            if (_aggregates.Count >= AggregateLimit) { _dropped++; return; }
            _aggregates[key] = a = new() { First = seconds };
        }
        a.Count++; a.First = Math.Min(a.First, seconds); a.Last = Math.Max(a.Last, seconds); a.Milliseconds += ms ?? 0; a.HasDuration |= ms.HasValue;
        }
        finally { Interlocked.Add(ref _aggregationTicks, Stopwatch.GetTimestamp() - begin); }
    }
    private void Flush()
    {
        foreach (var (key, a) in _aggregates)
        {
            var e = new InvestigationEvent
            {
                // Kernel duration events are emitted at completion. A single paired interval begins before that event.
                TimestampSeconds = a.HasDuration && a.Count == 1 ? a.First - a.Milliseconds / 1000 : a.First,
                ReceiptSeconds = _clock.NowSeconds, Source = "Deep ETW", Category = key.Category,
                EventType = key.Type, ProcessId = key.Process, ThreadId = key.Thread, DurationMs = a.HasDuration ? a.Milliseconds : null,
                Summary = key.Type + " aggregated observations", Quality = _coverageIncomplete || _lost > 0 || _dropped > 0 ? "incomplete" : "observed",
                UncertaintyMs = 1000d / _clock.Frequency,
                Details = new Dictionary<string, string>
                {
                    ["count"] = a.Count.ToString(CultureInfo.InvariantCulture), ["lastSeconds"] = a.Last.ToString("R", CultureInfo.InvariantCulture),
                    ["location"] = key.Location, ["attribution"] = "Observed addresses/activity only; symbols unresolved; no causal claim.",
                    ["durationMeaning"] = a.HasDuration ? a.Count == 1 ? "Paired interval starts at TimestampSeconds and ends at lastSeconds." : "Sum of observed paired intervals, not the entire aggregate window." : "Not measured."
                }
            };
            if (!_output.Writer.TryWrite(e)) _dropped += a.Count;
        }
        _aggregates.Clear(); _window = double.NaN;
    }
    private async Task DeliverAsync()
    {
        await foreach (var e in _output.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            long count = long.Parse(e.Details["count"], CultureInfo.InvariantCulture);
            try { _publish(e); }
            catch (Exception ex)
            {
                lock (_gate) _dropped += count;
                SetStatus("Delivery", "incomplete", "Publish callback failed (" + ex.GetType().Name + ")."); _ = DisposeAsync();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new(_dispose ??= Task.Run(StopAsync));
    }
    private async Task StopAsync()
    {
        lock (_gate) { _stopping = true; _endSeconds ??= _clock.NowSeconds; _cancel.Cancel(); _deadline?.Dispose(); }
        bool stopped = _session is null;
        if (_session is { } session)
        {
            try { ObserveLoss(session.EventsLost); } catch { SetStatus("ETW loss", "unavailable", "Final loss query unavailable; loss count is a lower bound."); }
            try { session.Stop(); stopped = true; }
            catch (Exception ex) { SetStatus("Cleanup", "incomplete", "Stop failed (" + ex.GetType().Name + "); independent watchdog remains armed."); }
            try { ObserveLoss(session.EventsLost); } catch { SetStatus("ETW loss", "unavailable", "Final loss query unavailable; loss count is a lower bound."); }
        }
        // Stop is issued independently of the delivery callback; a slow callback cannot prolong kernel tracing.
        if (_reader is { } reader)
        {
            try { await reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { SetStatus("Cleanup", "incomplete", "Consumer did not finish within five seconds of session stop."); }
        }
        if (_timer is { } timer)
        {
            try { await timer.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { SetStatus("Cleanup", "incomplete", "Health timer did not finish within one second of cancellation."); }
        }
        try { if (_session is { } drained) ObserveLoss(drained.EventsLost); }
        catch { SetStatus("ETW loss", "unavailable", "Post-drain loss query unavailable; loss count is a lower bound."); }
        try { _session?.Dispose(); } catch (Exception ex) { SetStatus("Cleanup", "incomplete", "Handle disposal failed (" + ex.GetType().Name + ")."); }
        try { if (stopped) _watchdog?.Dispose(); else _watchdog?.Detach(); }
        catch (Exception ex) { SetStatus("Cleanup", "incomplete", "Watchdog handle cleanup failed (" + ex.GetType().Name + ")."); }
        _watchdog = null;
        lock (_gate)
        {
            _completed = true;
            Flush(); _output.Writer.TryComplete();
            if (_status.TryGetValue("Deep ETW", out var s) && s.Availability == "active")
                SetStatus("Deep ETW", _coverageIncomplete || _lost + _dropped > 0 || _status.Values.Any(s => s.Availability == "incomplete") ? "incomplete" : "stopped", "Capture stopped; see loss/drop counts and individual channel coverage.");
            if (_session is not null) SetStatus("Stacks", _stacks > 0 ? "partial" : "unavailable", _stacks > 0
                ? "Observed CPU stack addresses; at most 32 frames per stack, symbols unresolved. No guaranteed completeness or reconstructed call tree."
                : "No CPU stack events observed; stacks and symbols unavailable.");
            _session = null;
            _threads.Clear(); _running.Clear(); _modules.Clear(); _targets.Clear(); _moduleCount = 0;
        }
        if (_delivery is { } delivery)
        {
            try { await delivery.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { SetStatus("Delivery", "incomplete", "Publish callback exceeded drain timeout; queued evidence may arrive after disposal."); }
        }
        _cancel.Dispose();
    }

    private sealed class ThreadObservation(int process) { public int Process = process; public long? Ready, Wait; }
    private sealed record Module(ulong Size, string Name, long? ValidFromQpc);
    private readonly record struct AggregateKey(string Category, string Type, int? Process, int? Thread, string Location);
    private sealed class Aggregate { public long Count; public double First, Last, Milliseconds; public bool HasDuration; }
}

// Narrow seams allow lifecycle/loss/privacy tests without changing machine tracing configuration.
internal interface IInvestigationDeepTracePlatform
{
    bool HasTraceAccess { get; }
    long ProcessStartUtcTicks { get; }
    TimeProvider TimeProvider => TimeProvider.System;
    IInvestigationDeepTraceWatchdog ArmWatchdog(string name, int seconds);
    IInvestigationDeepTraceSession CreateSession(string name, string? executablePath, string? rawDirectory);
}
internal interface IInvestigationDeepTraceWatchdog : IDisposable
{
    bool IsAlive { get; }
    void Detach();
}
internal interface IInvestigationDeepTraceSession : IDisposable
{
    long EventsLost { get; }
    void Start(InvestigationDeepTrace sink, bool usb);
    void Process();
    void Stop();
}
