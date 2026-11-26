using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JamesTools.Core.Diagnostics;

internal sealed class FrameReportCollector : IDisposable
{
    private readonly object _gate = new();
    private readonly Process _process;
    private readonly Task _reader;
    private readonly string _executable, _session;
    private readonly Stopwatch _elapsed;
    private readonly FrameCaptureData _data;
    private readonly CancellationTokenSource _readStop = new();
    private readonly Dictionary<uint, (SafeProcessHandle Handle, string Start)> _identities = new();
    private string? _captureError;
    private bool _complete, _disposed;
    private string _status = "Collecting frame data…";

    public FrameReportCollector(string presentMonPath, string gamePath, Stopwatch elapsed, DiagnosticOptions? options = null, Action<FrameTelemetry>? rawFrame = null, InvestigationClock? clock = null)
    {
        _elapsed = elapsed;
        var origin = clock?.OriginQpc ?? Stopwatch.GetTimestamp() - (long)(elapsed.Elapsed.TotalSeconds * Stopwatch.Frequency);
        _data = new FrameCaptureData(options, origin, rawFrame, pid =>
        {
            try
            {
                if (_identities.TryGetValue(pid, out var cached))
                {
                    if (WaitForSingleObject(cached.Handle, 0) == 258) return cached.Start;
                    cached.Handle.Dispose(); _identities.Remove(pid);
                }
                using var target = Process.GetProcessById((int)pid);
                if (!string.Equals(JamesTools.Core.Platform.ProcessSnapshot.GetImagePath((int)pid), gamePath, StringComparison.OrdinalIgnoreCase)) { target.Dispose(); return null; }
                var identity = target.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                var handle = OpenProcess(0x101000, false, (int)pid); // Synchronize + limited query, never VM access.
                if (handle.IsInvalid) { handle.Dispose(); return null; }
                if (_identities.Count >= 32) { foreach (var value in _identities.Values) value.Handle.Dispose(); _identities.Clear(); }
                _identities[pid] = (handle, identity);
                return identity;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return null; }
        });
        _executable = presentMonPath;
        _session = "MehToolsRpt-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo { FileName = presentMonPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--process_name", Path.GetFileName(gamePath), "--output_stdout", "--session_name", _session, "--no_track_input", "--qpc_time" }) start.ArgumentList.Add(arg);
        var timedCaptureSupported = false;
        try
        {
            using var help = Process.Start(new ProcessStartInfo { FileName = presentMonPath, Arguments = "--help", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (help is not null)
            {
                var output = help.StandardOutput.ReadToEndAsync();
                var errors = help.StandardError.ReadToEndAsync();
                if (help.WaitForExit(1500))
                {
                    var flags = output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult();
                    timedCaptureSupported = flags.Contains("--timed", StringComparison.Ordinal) && flags.Contains("--terminate_after_timed", StringComparison.Ordinal);
                    if (flags.Contains("--track_frame_type", StringComparison.Ordinal)) start.ArgumentList.Add("--track_frame_type");
                    if (!flags.Contains("--qpc_time", StringComparison.Ordinal)) start.ArgumentList.Remove("--qpc_time");
                }
                else { try { help.Kill(); } catch { } }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { }
        if (options?.InvestigateStutter == true)
        {
            // The child enforces its own deadline even if Meh Tools crashes before StopAsync can run.
            if (!timedCaptureSupported) throw new NotSupportedException("Investigation frame capture requires PresentMon timed termination support.");
            start.ArgumentList.Add("--timed");
            start.ArgumentList.Add(Math.Max(1, (int)Math.Ceiling(options.InvestigationDurationSeconds - elapsed.Elapsed.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--terminate_after_timed");
        }
        _process = Process.Start(start) ?? throw new InvalidOperationException("PresentMon could not be started.");
        _reader = Task.Run(ReadAsync);
    }

    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    private async Task ReadAsync()
    {
        // Drain stderr without putting executable paths or other unsanitized tool output in a shared report.
        var token = _readStop.Token;
        var errors = _process.StandardError.ReadToEndAsync(token);
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                lock (_gate) { if (!_disposed) _data.AddCsv(line, _elapsed.Elapsed.TotalSeconds); }
            lock (_gate) { if (!_complete) _captureError = "Frame collection ended before the requested capture finished."; }
            await _process.WaitForExitAsync(token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            lock (_gate) _status = _process.ExitCode == 0 ? "PresentMon stopped." : "PresentMon exited with code " + _process.ExitCode + "; capture may be incomplete.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or Win32Exception)
        { lock (_gate) { _status = "Frame capture stopped (" + ex.GetType().Name + ")."; if (!_complete) _captureError = _status; } }
        finally { try { await errors.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { } }
    }

    public void CompleteCapture() { lock (_gate) { _complete = true; _data.EndSeconds = _elapsed.Elapsed.TotalSeconds; } }
    public FrameInterval TakeInterval() { lock (_gate) return _data.TakeInterval(_elapsed.Elapsed.TotalSeconds); }
    public FrameSummary GetSummary() { lock (_gate) return _data.GetSummary(_status); }
    public FrameDiagnosticSummary GetDiagnostics() { lock (_gate) return _data.GetDiagnostics(); }
    public double[] EventOffsets() { lock (_gate) return _data.EventOffsets(); }
    public string? CaptureError { get { lock (_gate) return _captureError; } }

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
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { _readStop.Cancel(); try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { } }
    }

    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _readStop.Cancel(); _process.Dispose(); foreach (var identity in _identities.Values) identity.Handle.Dispose(); _identities.Clear(); if(_reader.IsCompleted) _readStop.Dispose(); } }
}

/// <summary>Separates presentation streams so auxiliary swapchains cannot inflate game FPS.</summary>
internal sealed class FrameCaptureData
{
    private readonly Dictionary<string, StreamData> _streams = new();
    private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);
    private readonly DiagnosticOptions _options;
    private readonly long? _qpcOrigin;
    private readonly Action<FrameTelemetry>? _rawFrame;
    private readonly Func<uint, string?>? _processIdentity;
    private long _ignoredFrames;
    private double _intervalStart;
    public double? EndSeconds { get; set; }
    public FrameCaptureData(DiagnosticOptions? options = null, long? qpcOrigin = null, Action<FrameTelemetry>? rawFrame = null, Func<uint, string?>? processIdentity = null)
    { _options = options?.Copy() ?? new(); _qpcOrigin = qpcOrigin; _rawFrame = rawFrame; _processIdentity = processIdentity; }

    public void AddCsv(string line, double offsetSeconds)
    {
        var fields = LiveFrameSampler.ParseCsv(line);
        if (fields.Contains("ProcessID") && fields.Any(n => n is "FrameTime" or "MsBetweenAppStart" or "MsBetweenPresents"))
        {
            _columns.Clear();
            for (var i = 0; i < fields.Length; i++) _columns.TryAdd(fields[i].Trim(), i);
            return;
        }
        string? S(params string[] names)
        {
            foreach (var n in names) if (_columns.TryGetValue(n, out var i) && i < fields.Length) return fields[i].Trim();
            return null;
        }
        double? D(params string[] names) => S(names) is { } v && Number(v, out var d) ? d : null;
        var frameTimeSource = new[] { "FrameTime", "MsBetweenAppStart", "MsBetweenPresents" }.FirstOrDefault(_columns.ContainsKey);
        var ms = frameTimeSource is null ? null : D(frameTimeSource);
        if (ms is null or <= 0 || !double.IsFinite(offsetSeconds) || offsetSeconds < 0) return;
        var receipt = offsetSeconds;
        var source = "CSV receipt (approximate; buffering unknown)";
        if (_qpcOrigin is { } origin && D("CPUStartQPC", "QPCTime") is { } qpc)
        { offsetSeconds = (qpc - origin) / Stopwatch.Frequency; source = "QPC"; }
        else if (_qpcOrigin is { } originMs && D("CPUStartQPCTime") is { } qpcMs)
        { offsetSeconds = qpcMs / 1000 - originMs / (double)Stopwatch.Frequency; source = "QPC milliseconds"; }
        // Relative timestamps cannot be anchored exactly to this recording without a common QPC origin.
        if (offsetSeconds < 0 || !double.IsFinite(offsetSeconds) || EndSeconds is { } cutoff && offsetSeconds > cutoff) return;
        var pid = uint.TryParse(S("ProcessID"), out var id) ? id.ToString(CultureInfo.InvariantCulture) : "unknown";
        var chain = S("SwapChainAddress") ?? "";
        if (chain.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) chain = chain[2..];
        chain = ulong.TryParse(chain, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) ? address.ToString("X", CultureInfo.InvariantCulture) : "unknown";
        var key = pid + ":" + chain;
        var frameType = S("FrameType");
        if (frameType is not null and not "NA" and not "NotSet" and not "Not Set")
            key += ":" + new string(frameType.Where(char.IsLetterOrDigit).Take(40).ToArray());
        if (_processIdentity is not null)
        {
            var identity = _processIdentity(id);
            if (identity is null) { _ignoredFrames++; return; }
            key += ":" + identity; // A reused PID/swapchain after restart is a distinct stream.
        }
        if (!_streams.TryGetValue(key, out var stream))
        {
            if (_streams.Count >= 16) { _ignoredFrames++; return; }
            _streams[key] = stream = new StreamData(_options);
        }
        static string? Safe(string? value) => value is null or "NA" or "N/A" ? null : new string(value.Where(c => char.IsLetterOrDigit(c) || c is ' ' or ':' or '_' or '-' or '.').Take(100).ToArray());
        var f = new FrameTelemetry(offsetSeconds, receipt, source, key, ms.Value,
            D("CPUBusy", "MsCPUBusy"), D("CPUWait", "MsCPUWait"), D("GPUBusy", "MsGPUBusy"), D("GPUWait", "MsGPUWait"),
            D("GPUTime", "MsGPUTime"), D("GPULatency", "MsGPULatency"), D("MsRenderPresentLatency", "RenderPresentLatency", "MsUntilRenderComplete"),
            D("DisplayLatency", "MsDisplayLatency"), D("MsInPresentAPI"), Safe(S("PresentMode")), Safe(S("FrameType")))
        { FrameTimeSource = frameTimeSource!, OriginalTimestamp = D("CPUStartQPC", "CPUStartQPCTime", "QPCTime", "CPUStartTime", "TimeInSeconds"),
          OriginalTimestampColumn = new[] { "CPUStartQPC", "CPUStartQPCTime", "QPCTime", "CPUStartTime", "TimeInSeconds" }.FirstOrDefault(_columns.ContainsKey) };
        stream.ApplicationFrames = frameType is "Application" or "ApplicationFrame" or "Application Frame";
        _rawFrame?.Invoke(f);
        stream.Pacing.Add(f);
        if (offsetSeconds < _options.WarmupSeconds) return;
        stream.Count++;
        if (f.PresentMode is { } mode && (stream.Modes.Count < 32 || stream.Modes.ContainsKey(mode))) stream.Modes[mode] = stream.Modes.GetValueOrDefault(mode) + 1;
        var type = f.FrameType ?? "Unknown / not instrumented";
        if (stream.Types.Count < 32 || stream.Types.ContainsKey(type)) stream.Types[type] = stream.Types.GetValueOrDefault(type) + 1;
        stream.FrameTimeSources[f.FrameTimeSource] = stream.FrameTimeSources.GetValueOrDefault(f.FrameTimeSource) + 1;
        stream.Analysis.Add(ms.Value, offsetSeconds);
        stream.FrameMs.Add(ms);
        stream.IntervalCount++;
        stream.IntervalTotal += ms.Value;
        stream.IntervalMax = Math.Max(stream.IntervalMax, ms.Value);
        if (ms > 50) stream.IntervalOver50++;
        stream.Latency.Add(f.DisplayLatencyMs);
        var values = new double?[] { f.CpuBusyMs, f.CpuWaitMs, f.GpuBusyMs, f.GpuWaitMs, f.GpuTimeMs, f.GpuLatencyMs, f.RenderPresentLatencyMs, f.DisplayLatencyMs, f.InPresentApiMs };
        for (var i = 0; i < values.Length; i++) stream.Metrics[i].Add(values[i]);
    }

    public double[] EventOffsets() => _streams.Values.SelectMany(s => s.Pacing.EventOffsets).ToArray();

    public FrameDiagnosticSummary GetDiagnostics()
    {
        var best = _streams.Count == 0 ? default : _streams.OrderByDescending(p => p.Value.ApplicationFrames && p.Value.Count > 0).ThenByDescending(p => p.Value.Count).First();
        var stream = best.Value;
        string[] names = ["CPU Busy", "CPU Wait", "GPU Busy", "GPU Wait", "GPU duration", "GPU latency", "Render/present latency", "Display latency", "Present API duration"];
        return new(best.Key, stream?.Pacing.Summary(), stream?.Pacing.Events ?? [],
            names.Select((n, i) => new FrameMetricSummary(n, "ms", stream?.Metrics[i].Count ?? 0,
                stream?.Metrics[i].Count > 0 ? stream.Metrics[i].Average : null)).ToArray(), _columns.Keys.ToArray()) {
            PresentModes = stream is null ? new Dictionary<string, long>() : new Dictionary<string, long>(stream.Modes),
            FrameTypes = _streams.Values.SelectMany(s => s.Types).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value)),
            FrameTimeSources = stream is null ? new Dictionary<string, long>() : new Dictionary<string, long>(stream.FrameTimeSources)
        };
    }

    public FrameInterval TakeInterval(double end)
    {
        var best = _streams.Count == 0 ? default : _streams.OrderByDescending(p => p.Value.ApplicationFrames && p.Value.IntervalCount > 0).ThenByDescending(p => p.Value.IntervalCount).First();
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
        var best = _streams.Count == 0 ? default : _streams.OrderByDescending(p => p.Value.ApplicationFrames && p.Value.Count > 0).ThenByDescending(p => p.Value.Count).First();
        var stream = best.Value;
        if (stream is null || stream.Count == 0) return new(status, null, null, null, null, null, null, null, null, 0, _streams.Count, _ignoredFrames, 0);
        var analysis = stream.Analysis.GetSummary();
        return new(status, best.Key, analysis, 1000 / stream.FrameMs.Max, 1000 / stream.FrameMs.Min,
            stream.FrameMs.Min, stream.FrameMs.Max, stream.Latency.Count == 0 ? null : stream.Latency.Min,
            stream.Latency.Count == 0 ? null : stream.Latency.Average, stream.Latency.Count,
            _streams.Count, _ignoredFrames, _streams.Values.Sum(s => s.Count) - stream.Count);
    }

    private static bool Number(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0;
    private sealed class StreamData
    {
        public StreamData(DiagnosticOptions options) { Pacing = new(options); }
        public readonly FrameAnalysis Analysis = new();
        public readonly FramePacing Pacing;
        public readonly PerformanceLogger.Statistics[] Metrics = Enumerable.Range(0, 9).Select(_ => new PerformanceLogger.Statistics()).ToArray();
        public readonly PerformanceLogger.Statistics FrameMs = new(), Latency = new();
        public readonly Dictionary<string, long> Modes = new(), Types = new(), FrameTimeSources = new();
        public bool ApplicationFrames;
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

public sealed record FrameMetricSummary(string Name, string Unit, int Readings, double? Average)
{
    public string Source => "PresentMon CSV; detected header fields";
    public string Scope => "selected process lifetime / swapchain / instrumented frame type";
    public string Sampling => "per frame; actual QPC where available, otherwise receipt time";
    public string Availability => Readings > 0 ? "Available" : "Unavailable / no valid readings";
}
public sealed record FrameDiagnosticSummary(string? Stream, PacingSummary? Pacing,
    IReadOnlyList<IReadOnlyList<FrameTelemetry>> Events, IReadOnlyList<FrameMetricSummary> Metrics, IReadOnlyList<string> PresentMonColumns)
{
    public IReadOnlyDictionary<string, long> PresentModes { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> FrameTypes { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> FrameTimeSources { get; init; } = new Dictionary<string, long>();
}
