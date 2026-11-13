using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace JamesTools.Core.Diagnostics;

internal sealed class FrameReportCollector : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _process;
    private readonly Task _reader;
    private readonly string _executable, _session;
    private readonly Stopwatch _elapsed;
    private readonly FrameCaptureData _data = new();
    private bool _complete, _disposed;
    private string _status = "Collecting frame data…";

    public FrameReportCollector(string presentMonPath, string gamePath, Stopwatch elapsed)
    {
        _elapsed = elapsed;
        _executable = presentMonPath;
        _session = "MehToolsRpt-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo { FileName = presentMonPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--process_name", Path.GetFileName(gamePath), "--output_stdout", "--session_name", _session, "--no_track_input" }) start.ArgumentList.Add(arg);
        _process = Process.Start(start) ?? throw new InvalidOperationException("PresentMon could not be started.");
        _reader = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        // Drain stderr without putting executable paths or other unsanitized tool output in a shared report.
        var errors = _process.StandardError.ReadToEndAsync();
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                lock (_gate) { if (!_complete) _data.AddCsv(line, _elapsed.Elapsed.TotalSeconds); }
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            lock (_gate) _status = _process.ExitCode == 0 ? "PresentMon stopped." : "PresentMon exited with code " + _process.ExitCode + "; capture may be incomplete.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or Win32Exception)
        { lock (_gate) _status = "Frame capture stopped (" + ex.GetType().Name + ")."; }
    }

    public void CompleteCapture() { lock (_gate) _complete = true; }
    public FrameInterval TakeInterval() { lock (_gate) return _data.TakeInterval(_elapsed.Elapsed.TotalSeconds); }
    public FrameSummary GetSummary() { lock (_gate) return _data.GetSummary(_status); }

    public async Task StopAsync()
    {
        CompleteCapture();
        try
        {
            var stop = new ProcessStartInfo { FileName = _executable, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--terminate_existing_session", "--session_name", _session }) stop.ArgumentList.Add(arg);
            using var stopper = Process.Start(stop);
            if (stopper is not null) { try { await stopper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { try { stopper.Kill(true); } catch { } } }
        }
        catch { }
        try { if (!_process.HasExited) { try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { try { _process.Kill(true); } catch { } } } } catch { }
        try { await _reader.ConfigureAwait(false); } catch { }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _process.Dispose(); }
}

/// <summary>Separates presentation streams so auxiliary swapchains cannot inflate game FPS.</summary>
internal sealed class FrameCaptureData
{
    private readonly Dictionary<string, StreamData> _streams = new();
    private int _frame = -1, _latency = -1, _pid = -1, _chain = -1;
    private bool _hasHeader;
    private long _ignoredFrames;
    private double _intervalStart;

    public void AddCsv(string line, double offsetSeconds)
    {
        var fields = LiveFrameSampler.ParseCsv(line);
        if (!_hasHeader)
        {
            var names = fields.Select(s => s.Trim()).ToArray();
            _frame = Array.FindIndex(names, s => s is "FrameTime" or "MsBetweenAppStart" or "MsBetweenPresents");
            _latency = Array.FindIndex(names, s => s is "DisplayLatency" or "MsDisplayLatency");
            _pid = Array.IndexOf(names, "ProcessID");
            _chain = Array.IndexOf(names, "SwapChainAddress");
            _hasHeader = _frame >= 0;
            return;
        }
        if (_frame >= fields.Length || !Number(fields[_frame], out var ms) || ms <= 0 || !double.IsFinite(offsetSeconds) || offsetSeconds < 0) return;
        // Only numeric IDs are exported, never arbitrary text from a CSV stream.
        var pid = _pid >= 0 && _pid < fields.Length && uint.TryParse(fields[_pid], out var id) ? id.ToString(CultureInfo.InvariantCulture) : "unknown";
        var chain = "unknown";
        if (_chain >= 0 && _chain < fields.Length)
        {
            var value = fields[_chain].Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            if (ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)) chain = address.ToString("X", CultureInfo.InvariantCulture);
        }
        var key = pid + ":" + chain;
        if (!_streams.TryGetValue(key, out var stream))
        {
            if (_streams.Count >= 16) { _ignoredFrames++; return; }
            _streams[key] = stream = new StreamData();
        }
        stream.Count++;
        stream.Analysis.Add(ms, offsetSeconds);
        stream.FrameMs.Add(ms);
        stream.IntervalCount++;
        stream.IntervalTotal += ms;
        stream.IntervalMax = Math.Max(stream.IntervalMax, ms);
        if (ms > 50) stream.IntervalOver50++;
        if (_latency >= 0 && _latency < fields.Length && Number(fields[_latency], out var latency)) stream.Latency.Add(latency);
    }

    public FrameInterval TakeInterval(double end)
    {
        var best = _streams.Count == 0 ? default : _streams.MaxBy(p => p.Value.IntervalCount);
        var stream = best.Value;
        var interval = stream is null || stream.IntervalCount == 0
            ? new FrameInterval(_intervalStart, end, null, 0, null, null, 0)
            : new FrameInterval(_intervalStart, end, best.Key, stream.IntervalCount,
                1000 * stream.IntervalCount / stream.IntervalTotal, stream.IntervalMax, stream.IntervalOver50);
        foreach (var value in _streams.Values) { value.IntervalCount = 0; value.IntervalTotal = 0; value.IntervalMax = 0; value.IntervalOver50 = 0; }
        _intervalStart = end;
        return interval;
    }

    public FrameSummary GetSummary(string status)
    {
        var best = _streams.Count == 0 ? default : _streams.MaxBy(p => p.Value.Count);
        var stream = best.Value;
        if (stream is null) return new(status, null, null, null, null, null, null, null, null, 0, _streams.Count, _ignoredFrames, 0);
        var analysis = stream.Analysis.GetSummary();
        return new(status, best.Key, analysis, 1000 / stream.FrameMs.Max, 1000 / stream.FrameMs.Min,
            stream.FrameMs.Min, stream.FrameMs.Max, stream.Latency.Count == 0 ? null : stream.Latency.Min,
            stream.Latency.Count == 0 ? null : stream.Latency.Average, stream.Latency.Count,
            _streams.Count, _ignoredFrames, _streams.Values.Sum(s => s.Count) - stream.Count);
    }

    private static bool Number(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0;
    private sealed class StreamData
    {
        public readonly FrameAnalysis Analysis = new();
        public readonly PerformanceLogger.Statistics FrameMs = new(), Latency = new();
        public long Count, IntervalCount, IntervalOver50;
        public double IntervalTotal, IntervalMax;
    }
}

internal sealed record FrameInterval(double StartSeconds, double EndSeconds, string? Stream, long Count, double? Fps, double? MaxMs, long Over50Ms);
internal sealed record FrameSummary(string Status, string? Stream, FrameAnalysisSummary? Analysis, double? FpsMin, double? FpsMax,
    double? FrameMsMin, double? FrameMsMax, double? LatencyMin, double? LatencyAvg, int LatencyCount, int StreamCount, long IgnoredFrames, long OtherStreamFrames)
{
    public long Count => Analysis?.Count ?? 0;
    public double? FpsAvg => Analysis?.EffectiveFps;
    public double? FrameMsAvg => Analysis?.MeanMs;
}
