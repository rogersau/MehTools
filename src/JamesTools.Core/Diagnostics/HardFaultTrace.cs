using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace JamesTools.Core.Diagnostics;

public sealed record HardFaultObservation(double OffsetSeconds, int ProcessId, int ThreadId, int ReadBytes, double DurationMs);

/// <summary>Explicit opt-in real-time kernel tracing. Never elevates, injects, logs paths or writes an ETL.</summary>
internal sealed class HardFaultTrace : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Queue<HardFaultObservation> _events = new();
    private readonly Dictionary<int, long> _targets = new();
    private readonly Action<HardFaultObservation>? _raw;
    private TraceEventSession? _session;
    private Task? _reader;
    private long _origin;
    private volatile bool _stopping;
    private double? _endSeconds;
    public void EndAt(double seconds) { lock (_gate) _endSeconds = seconds; }
    public string Status { get; private set; } = "Not measured (advanced hard-fault tracing disabled).";
    public long Omitted { get; private set; }
    public long Unattributed { get; private set; }
    public long EventsLost { get; private set; }
    public bool Started { get; private set; }
    public long SelectedFaultCount { get; private set; }
    public long SelectedReadBytes { get; private set; }
    public HardFaultTrace(bool enabled, Stopwatch elapsed, Action<HardFaultObservation>? raw = null, bool? hasTraceAccess = null, InvestigationClock? clock = null)
    {
        _raw = raw;
        if (!enabled) return;
        try
        {
            // No UAC prompt or privilege changes. Users can still record without ETW access.
            if ((hasTraceAccess ?? TraceEventSession.IsElevated()) != true) { Status = "Unavailable: kernel ETW access requires existing elevated permissions. Normal recording remains active."; return; }
            _origin = clock?.OriginQpc ?? Stopwatch.GetTimestamp() - (long)(elapsed.Elapsed.TotalSeconds * Stopwatch.Frequency);
            _session = new TraceEventSession("MehToolsFault-" + Guid.NewGuid().ToString("N")) { StopOnDispose = true, BufferSizeMB = 8 };
            var source = _session.Source;
            var kernel = new KernelTraceEventParser(source, KernelTraceEventParser.ParserTrackingOptions.ThreadToProcess);
            // Relative trace time has a different origin. QPC is required for cross-collector alignment.
#pragma warning disable CS0618
            kernel.MemoryHardFault += e => Observe(new((e.TimeStampQPC - _origin) / (double)Stopwatch.Frequency,
                e.ProcessID, e.ThreadID, e.ByteCount, e.ElapsedTimeMSec), e.TimeStamp.ToUniversalTime().Ticks);
#pragma warning restore CS0618
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.MemoryHardFaults | KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.Process);
            Started = true;
            Status = "Active: process-attributed ETW hard faults; no file names collected.";
            _reader = Task.Run(() =>
            {
                try { source.Process(); if (!_stopping) Status = "ETW ended early; hard-fault coverage is incomplete."; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException) { Status = "ETW stopped: " + ex.GetType().Name; }
                finally { EventsLost = source.EventsLost; }
            });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception or InvalidOperationException or NotSupportedException)
        { Status = "Unavailable: ETW startup failed (" + ex.GetType().Name + "). Normal recording remains active."; _session?.Dispose(); _session = null; }
    }
    public void SetTargets(IEnumerable<KeyValuePair<int, (long Start, TimeSpan Cpu)>> targets)
    { lock (_gate) { _targets.Clear(); foreach (var t in targets) _targets[t.Key] = t.Value.Start; } }
    internal void Observe(HardFaultObservation e, long eventUtcTicks)
    {
        lock (_gate)
        {
            if (!double.IsFinite(e.OffsetSeconds) || !double.IsFinite(e.DurationMs) || e.DurationMs < 0 || e.ReadBytes < 0 || _endSeconds is { } end && e.OffsetSeconds > end) return;
            if (e.ProcessId < 0) { Unattributed++; return; }
            if (!_targets.TryGetValue(e.ProcessId, out var started) || eventUtcTicks < started || e.OffsetSeconds < 0) return;
            if (_events.Count == 10000) { _events.Dequeue(); Omitted++; }
            SelectedFaultCount++; SelectedReadBytes += e.ReadBytes;
            _events.Enqueue(e); _raw?.Invoke(e);
        }
    }
    public HardFaultObservation[] Snapshot() { lock (_gate) return _events.ToArray(); }
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        _session?.Dispose();
        if (_reader is not null) try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException) { Status = "ETW consumer cleanup timed out after session stop; coverage incomplete."; }
        _session = null;
    }
}
