using System.Globalization;

namespace JamesTools.Core.Diagnostics;

public sealed record FrameTelemetry(double OffsetSeconds, double ReceiptSeconds, string TimestampSource, string Stream,
    double FrameMs, double? CpuBusyMs, double? CpuWaitMs, double? GpuBusyMs, double? GpuWaitMs,
    double? GpuTimeMs, double? GpuLatencyMs, double? RenderPresentLatencyMs, double? DisplayLatencyMs,
    double? InPresentApiMs, string? PresentMode, string? FrameType)
{
    public string FrameTimeSource { get; init; } = "FrameTime";
    public double? OriginalTimestamp { get; init; }
    public string? OriginalTimestampColumn { get; init; }
    public double IntervalStartSeconds => FrameTimeSource == "MsBetweenPresents" ? OffsetSeconds - FrameMs / 1000 : OffsetSeconds;
    public double IntervalEndSeconds => FrameTimeSource == "MsBetweenPresents" ? OffsetSeconds : OffsetSeconds + FrameMs / 1000;
    public const string CsvHeader = "offset_s,receipt_s,timestamp_source,stream,frame_ms,cpu_busy_ms,cpu_wait_ms,gpu_busy_ms,gpu_wait_ms,gpu_time_ms,gpu_latency_ms,render_present_latency_ms,display_latency_ms,in_present_api_ms,present_mode,frame_type,frame_time_source,original_timestamp,original_timestamp_column";
    public string ToCsv() => string.Join(",", new object?[] { OffsetSeconds, ReceiptSeconds, TimestampSource, Stream,
        FrameMs, CpuBusyMs, CpuWaitMs, GpuBusyMs, GpuWaitMs, GpuTimeMs, GpuLatencyMs, RenderPresentLatencyMs,
        DisplayLatencyMs, InPresentApiMs, PresentMode, FrameType, FrameTimeSource, OriginalTimestamp, OriginalTimestampColumn }.Select(v => v is double d ? d.ToString("R", CultureInfo.InvariantCulture) :
            v is null ? "" : "\"" + v.ToString()!.Replace("\"", "\"\"") + "\""));
}

public sealed record FrameFinding(string Observation, string PossibleCause, string ConfidenceAndLimitations);

public static class FrameDiagnosis
{
    public static FrameFinding Investigate(FrameTelemetry f)
    {
        static string M(double? d) => d?.ToString("F2", CultureInfo.InvariantCulture) ?? "Unavailable";
        var observation = $"Frame {f.FrameMs:F2} ms; CPU Busy/Wait {M(f.CpuBusyMs)}/{M(f.CpuWaitMs)} ms; GPU Busy/Wait {M(f.GpuBusyMs)}/{M(f.GpuWaitMs)} ms; GPU duration/latency {M(f.GpuTimeMs)}/{M(f.GpuLatencyMs)} ms; render-present/display latency {M(f.RenderPresentLatencyMs)}/{M(f.DisplayLatencyMs)} ms; Present API {M(f.InPresentApiMs)} ms; mode {f.PresentMode ?? "Unavailable"}; type {f.FrameType ?? "Unavailable"}.";
        var causes = new List<string>();
        // Evidence flags, not a partition of frame duration: CPU and GPU work overlap.
        if (f.GpuBusyMs is { } gpu && gpu >= f.FrameMs * .8) causes.Add("GPU rendering work is consistent with a GPU-limited frame");
        if (f.CpuBusyMs is { } cpu && cpu >= f.FrameMs * .8) causes.Add("CPU preparation is consistent with a CPU-limited frame");
        if (f.CpuWaitMs >= f.FrameMs * .5 || f.InPresentApiMs >= f.FrameMs * .5)
            causes.Add("presentation, synchronisation or an intentional FPS cap may contribute");
        if (f.GpuWaitMs >= f.FrameMs * .5) causes.Add("GPU idle gaps may reflect dependencies or scheduling");
        if (f.GpuBusyMs < f.FrameMs * .3) causes.Add("low GPU work during this slow frame leaves CPU preparation, streaming or pacing as possibilities");
        return new(observation, causes.Count == 0 ? "Unknown/inconclusive; no clear rendering or preparation evidence." : string.Join("; ", causes) + ".",
            "Limited confidence: CPU and GPU work overlap and are not additive. Busy/wait ratios are evidence flags, not definitive bottleneck classifications. Waiting can be normal. Stream identity, neighbouring frames, caps and workload must be checked; hardware samples are not per-frame measurements.");
    }
}

public sealed record RollingFpsSummary(int WindowSeconds, long Windows, double? Minimum, double? Average, double? Maximum);
public sealed record SlowCluster(double StartSeconds, double EndSeconds, long Frames, double MaximumMs);
public sealed record PacingSummary(double ThresholdMs, double TargetFps, double WarmupSeconds, long SlowFrames,
    double MeasuredFrameSeconds, double? StuttersPerMinute, double? StandardDeviationMs, double BelowTargetSeconds,
    RollingFpsSummary OneSecond, RollingFpsSummary FiveSeconds, long ClusterCount, IReadOnlyList<SlowCluster> WorstClusters,
    IReadOnlyDictionary<string, long> Histogram, long RollingFramesOmitted);

/// <summary>Streaming statistics; rolling windows and retained event context have hard limits.</summary>
internal sealed class FramePacing
{
    private readonly DiagnosticOptions _options;
    private readonly Queue<FrameTelemetry> _recent = new();
    private readonly List<List<FrameTelemetry>> _events = new();
    private readonly Queue<(double Start, double End)>[] _rolling = [new(), new()];
    private readonly PerformanceLogger.Statistics[] _fps = [new(), new()];
    private readonly List<SlowCluster> _clusters = new();
    private readonly long[] _histogram = new long[7];
    private SlowCluster? _active;
    private long _count, _slow, _clusterCount, _omitted;
    private double _totalMs, _mean, _m2, _below;
    private double? _last, _lastEnd;
    public double[] EventOffsets => _events.Select(e => e[0].OffsetSeconds).ToArray();
    public FramePacing(DiagnosticOptions? options = null) => _options = options?.Copy() ?? new();
    public IReadOnlyList<IReadOnlyList<FrameTelemetry>> Events => _events.OrderByDescending(e => e[0].FrameMs).Select(e => (IReadOnlyList<FrameTelemetry>)e.ToArray()).ToArray();
    public void Add(FrameTelemetry f)
    {
        var t = f.OffsetSeconds;
        // CPUStart corresponds to the start of this interval. Context is retained at true frame timestamps.
        foreach (var e in _events)
            if (t > e[0].OffsetSeconds && t <= EventPeak(e).OffsetSeconds + 5 && e.Count < 4096) e.Add(f);
        _recent.Enqueue(f);
        while (_recent.Count > 4096 || (_recent.Count > 0 && _recent.Peek().OffsetSeconds < t - 5)) _recent.Dequeue();
        if (f.OffsetSeconds >= _options.WarmupSeconds && f.FrameMs > _options.StutterThresholdMs && (_events.Count < 10 || f.FrameMs > _events.Min(e => EventPeak(e).FrameMs)))
        {
            var context = _recent.ToList();
            // Keep the event's target explicit, even when neighbouring frames are slower.
            context.Remove(f); context.Insert(0, f);
            _events.Add(context);
            if (_events.Count > 10) _events.Remove(_events.MinBy(e => e[0].FrameMs)!);
        }
        if (f.OffsetSeconds < _options.WarmupSeconds) return;
        _count++; _totalMs += f.FrameMs;
        var delta = f.FrameMs - _mean; _mean += delta / _count; _m2 += delta * (f.FrameMs - _mean);
        if (f.FrameMs > 1000 / _options.TargetFps) _below += f.FrameMs / 1000;
        _histogram[f.FrameMs <= 8.333 ? 0 : f.FrameMs <= 16.667 ? 1 : f.FrameMs <= 33.333 ? 2 : f.FrameMs <= 50 ? 3 : f.FrameMs <= 100 ? 4 : f.FrameMs <= 250 ? 5 : 6]++;
        if (f.FrameMs > _options.StutterThresholdMs)
        {
            _slow++;
            if (_active is null || _lastEnd is null || f.IntervalStartSeconds - _lastEnd > .1) { FinishCluster(); _active = new(Math.Max(0, f.IntervalStartSeconds), f.IntervalEndSeconds, 1, f.FrameMs); }
            else _active = _active with { EndSeconds = f.IntervalEndSeconds, Frames = _active.Frames + 1, MaximumMs = Math.Max(_active.MaximumMs, f.FrameMs) };
        }
        else FinishCluster();
        for (var i = 0; i < 2; i++)
        {
            var seconds = i == 0 ? 1 : 5;
            var q = _rolling[i];
            var end = f.IntervalEndSeconds;
            var intervalStart = f.IntervalStartSeconds;
            if (_last > t || (_lastEnd is { } priorEnd && intervalStart - priorEnd > .1)) q.Clear();
            q.Enqueue((intervalStart, end));
            while (q.Count > 0 && q.Peek().End <= end - seconds) q.Dequeue();
            if (q.Count > 20000) { q.Dequeue(); _omitted++; }
            if (q.Count > 0 && q.Peek().Start <= end - seconds && _omitted == 0)
            {
                // Count completed frames in the trailing wall-clock window. A long frame gives low sustained throughput.
                _fps[i].Add(q.Count / (double)seconds);
            }
        }
        _last = t;
        _lastEnd = f.IntervalEndSeconds;
    }
    private static FrameTelemetry EventPeak(List<FrameTelemetry> e) => e[0];
    private void FinishCluster()
    {
        if (_active is null) return;
        _clusterCount++; _clusters.Add(_active); _active = null;
        if (_clusters.Count > 10) _clusters.Remove(_clusters.MinBy(c => c.Frames)!);
    }
    public PacingSummary Summary()
    {
        RollingFpsSummary R(int i) => new(i == 0 ? 1 : 5, _fps[i].Count, _fps[i].Count > 0 ? _fps[i].Min : null,
            _fps[i].Count > 0 ? _fps[i].Average : null, _fps[i].Count > 0 ? _fps[i].Max : null);
        string[] bins = ["<=8.333ms", "8.333–16.667ms", "16.667–33.333ms", "33.333–50ms", "50–100ms", "100–250ms", ">250ms"];
        return new(_options.StutterThresholdMs, _options.TargetFps, _options.WarmupSeconds, _slow, _totalMs / 1000,
            _totalMs > 0 ? _slow * 60000 / _totalMs : null, _count > 0 ? Math.Sqrt(_m2 / _count) : null, _below,
            R(0), R(1), _clusterCount + (_active is null ? 0 : 1), _clusters.Concat(_active is null ? [] : new[] { _active }).OrderByDescending(c => c.Frames).Take(10).ToArray(),
            bins.Select((b, i) => (b, i)).ToDictionary(p => p.b, p => _histogram[p.i]), _omitted);
    }
}
