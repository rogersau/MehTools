using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace JamesTools.Core.Diagnostics;

public sealed record InvestigationLiveSnapshot(double DurationSeconds, bool Recording,
    IReadOnlyList<FrameTelemetry> Frames, IReadOnlyList<InvestigationEvent> Events,
    IReadOnlyList<DiagnosticHardwareSample> Hardware, IReadOnlyList<InvestigationCollectorStatus> Collectors);

public sealed partial class PerformanceLogger
{
    private readonly object _investigationGate = new();
    private readonly Queue<FrameTelemetry> _investigationFrames = new();
    private readonly Queue<InvestigationEvent> _investigationEvents = new();
    private readonly List<InvestigationCollectorStatus> _investigationFailures = new();
    private InvestigationClock _investigationClock = null!;
    private InvestigationDeviceCollector? _deviceCollector;
    private InvestigationDeepTrace? _deepTrace;
    private Timer? _investigationTimer;
    private InvestigationReport? _investigationReport;
    private long _investigationFrameDrops, _investigationEventDrops;
    private double? _investigationCutoff;
    private TimeSpan _observerCpuStart;
    private long _observerMemoryStart;
    private bool _observerBaseline;

    public bool IsRecording => _elapsed.IsRunning;
    public InvestigationReport? CompletedInvestigation => _investigationReport;

    private void StartInvestigationCollectors()
    {
        if (!_options.InvestigateStutter) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            _observerCpuStart = process.TotalProcessorTime;
            _observerMemoryStart = process.PrivateMemorySize64;
            _observerBaseline = true;
        }
        catch { }
        _raw.InvestigationState("recording", _investigationClock, _options.InvestigationDurationSeconds);
        try { _deviceCollector = new InvestigationDeviceCollector(_investigationClock, CaptureInvestigationEvent); }
        catch (Exception ex) { _investigationFailures.Add(new("Windows devices", "unavailable", "Startup failed: " + ex.GetType().Name)); }
        if (_options.DeepInvestigation)
        {
            _investigationFailures.AddRange(InvestigationDeepTrace.RecoverOrphanedSessions());
            try { _deepTrace = new InvestigationDeepTrace(_investigationClock, _executablePath, _options.InvestigationDurationSeconds, _options.DeepUsbTracing, _raw.DirectoryPath, CaptureInvestigationEvent); }
            catch (Exception ex) { _investigationFailures.Add(new("Deep ETW", "unavailable", "Startup failed: " + ex.GetType().Name)); }
        }
    }

    private void CaptureFrame(FrameTelemetry frame)
    {
        _raw.Frame(frame);
        if (!_options.InvestigateStutter) return;
        lock (_investigationGate)
        {
            if (_investigationCutoff is { } cutoff && frame.OffsetSeconds > cutoff) return;
            if (_investigationFrames.Count == 120000) { _investigationFrames.Dequeue(); _investigationFrameDrops++; }
            _investigationFrames.Enqueue(frame);
        }
    }

    private void CaptureInvestigationEvent(InvestigationEvent evidence)
    {
        if (!double.IsFinite(evidence.TimestampSeconds) || evidence.TimestampSeconds < 0 ||
            !double.IsFinite(evidence.ReceiptSeconds)) return;
        lock (_investigationGate)
        {
            if (_investigationCutoff is { } cutoff && evidence.TimestampSeconds > cutoff) return;
            if (_investigationEvents.Count == 30000) { _investigationEvents.Dequeue(); _investigationEventDrops++; }
            _investigationEvents.Enqueue(evidence);
        }
        _raw.InvestigationEvent(evidence);
    }

    public InvestigationLiveSnapshot GetInvestigationSnapshot()
    {
        FrameTelemetry[] frames; InvestigationEvent[] events; DiagnosticHardwareSample[] hardware;
        lock (_investigationGate)
        {
            frames = _investigationFrames.TakeLast(1000).ToArray();
            events = _investigationEvents.TakeLast(1000).OrderBy(e => e.TimestampSeconds).ToArray();
            hardware = _hardware.ToArray();
        }
        return new(_elapsed.Elapsed.TotalSeconds, IsRecording, frames, events, hardware, InvestigationStatuses());
    }

    public IReadOnlyList<InvestigationDevice> GetInvestigationDevices() => _deviceCollector?.Inventory ?? [];

    private InvestigationCollectorStatus[] InvestigationStatuses()
    {
        var result = new List<InvestigationCollectorStatus>();
        result.AddRange(_deviceCollector?.Status ?? []);
        result.AddRange(_deepTrace?.Status ?? []);
        lock (_investigationGate)
        {
            result.AddRange(_investigationFailures);
            result.Add(new("Investigation buffer", _investigationFrameDrops + _investigationEventDrops > 0 ? "partial" : "available",
                $"Retained frame limit 120000, event limit 30000. Omitted frames: {_investigationFrameDrops}; events: {_investigationEventDrops}. Raw file retention: {(_options.WriteRawExports ? "separate local evidence" : "disabled")}.")
                { EventsDropped = _investigationFrameDrops + _investigationEventDrops });
        }
        result.Add(new("PresentMon", _frames is null ? "unavailable" : _frames.CaptureError is null ? "available" : "partial", _frameStatus ?? "No capture"));
        result.Add(new("Hardware windows", _samples > 0 ? "available" : "unavailable",
            "System/process counters are interval averages; GPU readings retain collection windows and are not per-frame measurements.")
            { CollectionMilliseconds = _collectionTotalMs });
        if (!_options.DeepInvestigation) result.Add(new("Deep ETW", "disabled", "Thread scheduling, DPC/ISR, CPU stacks and deep USB events were not requested."));
        return result.ToArray();
    }

    private async Task StopInvestigationCollectorsAsync()
    {
        if (!_options.InvestigateStutter) return;
        lock (_investigationGate) _investigationCutoff = _elapsed.Elapsed.TotalSeconds;
        // Every collector gets its cleanup even if another collector fails.
        foreach (var collector in new IAsyncDisposable?[] { _deviceCollector, _deepTrace })
        {
            if (collector is null) continue;
            try { await collector.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { lock (_investigationGate) _investigationFailures.Add(new(collector.GetType().Name, "partial", "Cleanup failed: " + ex.GetType().Name)); }
        }
        if (_observerBaseline)
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                var cpuMs = (process.TotalProcessorTime - _observerCpuStart).TotalMilliseconds;
                var seconds = Math.Max(.001, _elapsed.Elapsed.TotalSeconds);
                var overhead = string.Create(CultureInfo.InvariantCulture,
                    $"Meh Tools process CPU {cpuMs:F1} ms; {cpuMs / (seconds * 10 * Environment.ProcessorCount):F3}% of total machine capacity; private memory delta {(process.PrivateMemorySize64 - _observerMemoryStart) / 1048576d:F2} MiB; end private memory {process.PrivateMemorySize64 / 1048576d:F2} MiB. Includes other work in this process and startup; excludes external PresentMon/watchdog and kernel trace overhead. Not an isolated causal overhead measurement.");
                _investigationFailures.Add(new("Observer overhead", "measured", overhead) { CollectionMilliseconds = cpuMs });
            }
            catch { _investigationFailures.Add(new("Observer overhead", "unavailable", "Process counters could not be read.")); }
        }
        FrameTelemetry[] frames; InvestigationEvent[] events; DiagnosticHardwareSample[] hardware;
        lock (_investigationGate)
        {
            frames = _investigationFrames.Where(f => f.OffsetSeconds >= _options.WarmupSeconds).ToArray();
            events = _investigationEvents.ToArray(); hardware = _hardware.ToArray();
        }
        if (_options.WarmupSeconds > 0) _investigationFailures.Add(new("Frame warm-up", "partial", $"First {_options.WarmupSeconds:F3} seconds excluded from frame analysis; device events and hardware retain the full recording interval."));
        if (_samplingError is not null) _investigationFailures.Add(new("Hardware sampler", "partial", _samplingError));
        _investigationReport = InvestigationAnalysis.Analyze(frames, events, GetInvestigationDevices(), hardware,
            InvestigationStatuses(), _elapsed.Elapsed.TotalSeconds, _options.StutterThresholdMs) with
        {
            Conditions = _options.Conditions with { }, WarmupSeconds = _options.WarmupSeconds
        };
        if (_options.WarmupSeconds > 0)
            _investigationReport = _investigationReport with
            {
                Limitations = _investigationReport.Limitations.Where(l => !l.StartsWith("Warm-up exclusion is 0", StringComparison.Ordinal))
                    .Append($"Frame warm-up exclusion: {_options.WarmupSeconds:F3} seconds. Hardware and device evidence retain the full recording interval; reduced frame coverage conservatively limits association confidence.").ToArray()
            };
        _raw.InvestigationSummary(_investigationReport);
        _raw.InvestigationState(_samplingError is null ? "completed" : "interrupted", _investigationClock, _options.InvestigationDurationSeconds);
    }
}
