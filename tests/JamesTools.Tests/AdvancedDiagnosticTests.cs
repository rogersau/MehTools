using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;

internal static class AdvancedDiagnosticTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Near(double? actual, double expected, string message) => Check(actual is { } n && Math.Abs(n - expected) < .001, message + $": {actual} != {expected}");
    private static FrameTelemetry Frame(double t, double ms = 10, double? cpu = null, double? gpu = null) => new(t, t + .5, "QPC", "7:A", ms, cpu, null, gpu, null, null, null, null, null, null, "Hardware: Independent Flip", "Application");

    public static Task Frames()
    {
        var origin = 10 * Stopwatch.Frequency;
        var raw = new List<FrameTelemetry>();
        var data = new FrameCaptureData(new DiagnosticOptions { WarmupSeconds = 1 }, origin, raw.Add);
        data.AddCsv("ProcessID,SwapChainAddress,CPUStartQPC,FrameTime,CPUBusy,CPUWait,GPUBusy,GPUWait,GPUTime,GPULatency,DisplayLatency,MsRenderPresentLatency,PresentMode,FrameType", 0);
        void Add(double t, string gpu, string type = "Application") => data.AddCsv(string.Create(CultureInfo.InvariantCulture, $"7,0xA,{origin + (long)(t * Stopwatch.Frequency)},60,40,20,{gpu},3,45,2,64,43,Hardware: Independent Flip,{type}"), t + 10);
        Add(.5, "54"); Add(1, "NA"); Add(1.06, "54"); Add(1.12, "10", "Generated");
        var summary = data.GetSummary("ok");
        Check(raw.Count == 4 && summary.Count == 2, "warm-up not preserved raw/excluded from summaries, or frame types combined");
        Near(raw[0].OffsetSeconds, .5, "buffered receipt time replaced QPC");
        Check(raw[0].ReceiptSeconds == 10.5 && raw[0].TimestampSource == "QPC", "timestamp provenance lost");
        Check(raw[1].GpuBusyMs is null && raw[2].GpuBusyMs == 54 && raw[2].GpuTimeMs == 45, "Busy, duration or missing values conflated");
        Check(data.GetDiagnostics().Metrics.Single(m => m.Name == "GPU Busy").Readings == 1, "unsupported reading counted as zero");
        Near(data.GetDiagnostics().Metrics.Single(m => m.Name == "CPU Busy").Average, 40, "busy aggregation");
        Check(data.GetDiagnostics().Events.Count == 2, "significant spikes missing");
        data.EndSeconds = 1.15; Add(2, "30"); Check(raw.Count == 4, "frames after stop retained");
        data.EndSeconds = null;
        for (var i = 0; i < 8; i++) Add(2 + i * .06, "10", "Generated");
        Check(data.GetSummary("generated").Count == 2 && data.GetDiagnostics().FrameTypes["Generated"] == 9 && data.GetDiagnostics().PresentModes.Count == 1, "generated frame count displaced application FPS / mode distribution missing");
        var warmup = new FrameCaptureData(new DiagnosticOptions { WarmupSeconds = 10 });
        warmup.AddCsv("ProcessID,SwapChainAddress,FrameTime", 0); warmup.AddCsv("7,0xA,16", 1);
        Check(warmup.GetSummary("short").FpsAvg is null && warmup.GetSummary("short").FrameMsMin is null, "all-warm-up recording invented FPS");
        var old = new FrameCaptureData();
        old.AddCsv("ProcessID,SwapChainAddress,MsBetweenPresents,MsUntilRenderComplete,MsUntilDisplayed", 0);
        old.AddCsv("7,0xA,20,10,12", 1);
        var legacy = old.GetDiagnostics();
        Check(legacy.Metrics.Single(m => m.Name == "GPU Busy").Average is null && legacy.Metrics.Single(m => m.Name == "Display latency").Average is null, "legacy presentation latency incorrectly equated to CPU-start latency");
        Near(legacy.Metrics.Single(m => m.Name == "Render/present latency").Average, 10, "legacy render latency lost");
        var legacyRaw = new List<FrameTelemetry>();
        var oldRaw = new FrameCaptureData(rawFrame: legacyRaw.Add);
        oldRaw.AddCsv("ProcessID,SwapChainAddress,MsBetweenPresents,TimeInSeconds", 0); oldRaw.AddCsv("7,0xA,20,0.75", 1);
        Near(legacyRaw[0].IntervalStartSeconds, .98, "legacy present interval anchor");
        Check(legacyRaw[0].OriginalTimestamp == .75 && legacyRaw[0].OriginalTimestampColumn == "TimeInSeconds", "original relative PresentMon timestamp lost");
        Check(old.GetSummary("legacy").Analysis!.WorstFrames[0].OffsetSeconds == 1, "legacy fallback lost receipt timestamp");
        string? identity = "first";
        var restarted = new FrameCaptureData(processIdentity: _ => identity);
        restarted.AddCsv("ProcessID,SwapChainAddress,FrameTime", 0); restarted.AddCsv("7,0xA,10", 1);
        identity = "second"; restarted.AddCsv("7,0xA,100", 2);
        identity = null; restarted.AddCsv("7,0xA,100", 3);
        Check(restarted.GetSummary("restart").StreamCount == 2 && restarted.GetSummary("restart").OtherStreamFrames == 1, "reused PID/swapchain combined across process lifetimes");
        Check(FrameDiagnosis.Investigate(Frame(1, 100, 90, 95)).PossibleCause.Contains("GPU rendering") && FrameDiagnosis.Investigate(Frame(1, 100, 90, 95)).PossibleCause.Contains("CPU preparation"), "overlapping work forced into exclusive bottleneck");
        var unknown = FrameDiagnosis.Investigate(Frame(1, 100));
        Check(unknown.PossibleCause.Contains("inconclusive") && unknown.Observation.Contains("Unavailable") && !unknown.Observation.Contains("GPU Busy/Wait 0"), "missing telemetry invented evidence");
        Check(FrameDiagnosis.Investigate(Frame(1, 100, gpu: 95)).ConfidenceAndLimitations.Contains("not definitive"), "weak classification expressed certainty");
        return Task.CompletedTask;
    }

    public static Task Pacing()
    {
        var pacing = new FramePacing();
        for (var i = 0; i < 1000; i++) pacing.Add(Frame(i / 100d));
        var s = pacing.Summary();
        Near(s.OneSecond.Minimum, 100, "rolling 1s minimum"); Near(s.FiveSeconds.Minimum, 100, "rolling 5s minimum");
        Near(s.StandardDeviationMs, 0, "constant frame variance"); Near(s.MeasuredFrameSeconds, 10, "measured exposure");
        Check(s.SlowFrames == 0 && s.BelowTargetSeconds == 0 && s.Histogram.Values.Sum() == 1000, "constant frames pacing");
        var clusters = new FramePacing(new DiagnosticOptions { StutterThresholdMs = 50 });
        clusters.Add(Frame(0, 50)); clusters.Add(Frame(.05, 100)); clusters.Add(Frame(.15, 200));
        clusters.Add(Frame(.35, 10)); clusters.Add(Frame(.36, 100));
        var c = clusters.Summary();
        Check(c.SlowFrames == 3 && c.ClusterCount == 2 && c.WorstClusters[0].Frames == 2, "strict thresholds / consecutive clusters");
        Near(c.StuttersPerMinute, 3 * 60 / .46, "normalised measured stutter exposure");
        var warm = new FramePacing(new DiagnosticOptions { WarmupSeconds = 5 });
        warm.Add(Frame(1, 100)); warm.Add(Frame(5, 100)); Check(warm.Summary().SlowFrames == 1, "warm-up included");
        Check(warm.Events[0].Any(f => f.OffsetSeconds == 1), "warm-up removed nearby raw context");
        var context = new FramePacing();
        for (var i = 0; i < 2000; i++) context.Add(Frame(i / 100d, i == 500 ? 200 : 10));
        Check(context.Events.Count == 1 && context.Events[0][0].OffsetSeconds == 5 && context.Events[0].Any(f => f.OffsetSeconds == 10) && context.Events[0].Any(f => f.OffsetSeconds < 5), "five-second spike context incorrect");
        var bounded = new FramePacing();
        for (var i = 1; i <= 30; i++) bounded.Add(Frame(i, i * 10));
        Check(bounded.Events.Count == 10 && bounded.Events[0][0].FrameMs == 300 && bounded.Events.All(e => e.Count <= 4096), "worst ten retention");
        var partial = new FramePacing(); partial.Add(Frame(0)); Check(partial.Summary().FiveSeconds.Minimum is null, "partial capture invented sustained FPS");
        return Task.CompletedTask;
    }

    public static async Task StorageAndAttribution()
    {
        var activity = new ProcessActivity();
        ProcessActivityCounters Reading(long start, double t, ulong bytes, uint faults) => new(7, start, t, bytes, bytes, bytes / 100, bytes / 100, faults);
        Check(activity.Update([Reading(1, 0, 100, 10)], 0).ReadMiBps is null, "first sample invented rates");
        var rate = activity.Update([Reading(1, 2, 2097252, 14)], 2); Near(rate.ReadMiBps, 1, "process throughput"); Near(rate.PageFaultsPerSecond, 2, "page faults");
        Check(activity.Update([Reading(2, 3, 1000, 2)], 3).ReadMiBps is null, "PID reuse produced rate spike");
        Check(activity.Update([], 4).ReadMiBps is null, "exit appeared as zero I/O");
        Check(activity.Update([Reading(2, 5, 2000, 4)], 5).PageFaultsPerSecond is null, "missing interval reused stale counters");
        var options = new AppSettings { Diagnostics = new() { FastGpuPolling = true, WarmupSeconds = 12, Conditions = new() { Scene = "Wardogs test route", Notes = "Intake cleaned" } } };
        var store = new SettingsStore(Path.GetTempPath()); var round = store.Deserialize(store.Serialize(options));
        Check(round.Diagnostics.FastGpuPolling && round.Diagnostics.Conditions.Scene == "Wardogs test route", "preferences did not persist");
        var copy = options.Snapshot(); copy.Diagnostics.Conditions.Notes = "changed"; Check(options.Diagnostics.Conditions.Notes == "Intake cleaned", "settings conditions alias mutable objects");
        var sample = new DiagnosticHardwareSample(2, 2.1, 0, 0, null, null, new Dictionary<string, double?> { ["gpuPercent"] = null, ["processReadMiBps"] = 1, ["pagesInputPerSecond"] = 4 });
        var e = StutterInvestigator.Create([Frame(1, 100)], [sample], [], [new(1, "Noticed hitch")]);
        Check(e.Finding.Observation.Contains("Process-accounted reads") && e.Finding.PossibleCause.Contains("does not establish") && e.Markers.Count == 1, "disk coincidence asserted cause");
        Check(DiagnosticMeasurements.Describe("pagesInputPerSecond").Scope == "system-wide" && DiagnosticMeasurements.Describe("vramUsedMiB").Scope.Contains("all applications"), "system metrics labelled process-specific");
        var delayed = StutterInvestigator.Create([Frame(1, 100) with { TimestampSource = "CSV receipt (approximate; buffering unknown)" }], [sample], [], []);
        Check(!delayed.Finding.Observation.Contains("Process-accounted reads") && delayed.Finding.ConfidenceAndLimitations.Contains("buffering"), "approximate receipt timestamp asserted exact I/O correlation");
        var hot = sample with { Values = new Dictionary<string, double?> { ["gpuPercent"] = 100, ["gamePrivateMiB"] = 10000 } };
        var hotEvent = StutterInvestigator.Create([Frame(1, 100)], [hot], [], []);
        Check(hotEvent.Finding.PossibleCause.StartsWith("Unknown/inconclusive"), "utilisation or memory allocation alone invented a bottleneck/leak");
        var json = JsonSerializer.Serialize(sample); Check(json.Contains("\"gpuPercent\":null") && !json.Contains("\"gpuPercent\":0"), "missing GPU reading serialized as zero");
        var csv = Frame(1).ToCsv(); Check(ReportTimeline.ParseCsv(csv)[7] == "", "missing raw busy reading not blank");
        await using var trace = new HardFaultTrace(true, Stopwatch.StartNew(), hasTraceAccess: false);
        Check(trace.Status.Contains("Unavailable") && trace.Snapshot().Length == 0, "unprivileged ETW was required");
        trace.SetTargets(new Dictionary<int, (long Start, TimeSpan Cpu)> { [7] = (10, TimeSpan.Zero) });
        trace.Observe(new(1, 7, 99, 4096, 2), 9);
        trace.Observe(new(1, 7, 99, 4096, 2), 11);
        trace.Observe(new(1, -1, 99, 4096, 2), 11);
        trace.EndAt(1.1); trace.Observe(new(2, 7, 99, 4096, 2), 11);
        trace.SetTargets(new Dictionary<int, (long Start, TimeSpan Cpu)> { [7] = (30, TimeSpan.Zero) });
        trace.Observe(new(1, 7, 99, 4096, 2), 20);
        Check(trace.SelectedFaultCount == 1 && trace.SelectedReadBytes == 4096 && trace.Unattributed == 1, "ETW lifetime attribution/cutoff lost or fabricated fault counts");
        using var raw = new DiagnosticRawWriter(); var path = raw.DirectoryPath!;
        try
        {
            raw.Frame(Frame(.5)); raw.Hardware(sample); raw.Finish("partial report", "{\"schemaVersion\":3}", new[] { new ReportMarker(1, "Marker") });
            Check(File.ReadAllLines(Path.Combine(path, "frames.csv")).Length == 2 && File.Exists(Path.Combine(path, "summary.json")) && File.ReadAllText(Path.Combine(path, "hardware.jsonl")).Contains("null"), "buffered raw exports did not flush");
        }
        finally { Directory.Delete(path, true); }
    }

    private static int powerCalls;
    private static int InitNvml() => 0;
    private static int DeviceNvml(uint index, out IntPtr device) { device = new IntPtr(1); return 0; }
    private static int FanNvml(IntPtr device, out uint value) { value = 0; return 3; }
    private static int PowerNvml(IntPtr device, out uint value) { powerCalls++; value = powerCalls == 1 ? 150000u : 0u; return powerCalls == 2 ? 999 : 0; }
    public static Task SensorFailures()
    {
        powerCalls = 0;
        Delegate? Resolve(string name, Type type)
        {
            var method = name switch
            {
                "nvmlInit_v2" => nameof(InitNvml),
                "nvmlDeviceGetHandleByIndex_v2" => nameof(DeviceNvml),
                "nvmlDeviceGetFanSpeed" => nameof(FanNvml),
                "nvmlDeviceGetPowerUsage" => nameof(PowerNvml),
                _ => null
            };
            return method is null ? null : Delegate.CreateDelegate(type, typeof(AdvancedDiagnosticTests).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        }
        using var sensor = new NvidiaTelemetry(Resolve);
        var first = sensor.Sample(); var second = sensor.Sample(); var third = sensor.Sample();
        Near(first["gpuPowerW"], 150, "NVML milliwatts conversion");
        Check(first["gpuFanPercent"] is null && sensor.Status["gpuFanPercent"].Contains("3"), "unsupported fan sensor became zero");
        Check(second["gpuPowerW"] is null && second["gpuTemperatureC"] is null, "failed sensor reused old reading");
        Check(third["gpuPowerW"] == 0 && powerCalls == 3, "transient failure did not recover / valid zero suppressed");
        Check(third["gpuMemoryClockMHz"] is null && third["gpuThermalLimited"] is null, "missing NVML export invented a reading");
        return Task.CompletedTask;
    }

    public static void Benchmark()
    {
        const int frames = 100000;
        using var raw = new DiagnosticRawWriter();
        var data = new FrameCaptureData(rawFrame: raw.Frame);
        data.AddCsv("ProcessID,SwapChainAddress,FrameTime,CPUBusy,CPUWait,GPUBusy,GPUWait,GPUTime,GPULatency,DisplayLatency", 0);
        var watch = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < frames; i++) data.AddCsv("7,0xA,10,5,5,8,1,9,2,12", i / 100d);
        raw.Dispose(); watch.Stop();
        var bytes = new FileInfo(Path.Combine(raw.DirectoryPath!, "frames.csv")).Length;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Diagnostic fixture benchmark: {frames} parsed/raw-exported frames in {watch.Elapsed.TotalMilliseconds:F1} ms; {watch.Elapsed.TotalMilliseconds * 1000 / frames:F2} us/frame; {(GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)frames:F0} allocated bytes/frame; {bytes / (double)frames:F1} raw bytes/frame. Excludes real PresentMon/driver/ETW overhead."));
        Directory.Delete(raw.DirectoryPath!, true);
    }

    public static Task Comparisons()
    {
        string Report(int version, double duration, double temp, double fps, string context, int count = 10) => $"# Performance recording\n- Report format: {version}\n### Game settings and test scene\n{context}\n### Symptoms and target performance\nTest\n## Machine summary (JSON)\n```json\n" +
            JsonSerializer.Serialize(new { schemaVersion = version, durationSeconds = duration, gpuTempAvgC = temp, fpsAvg = fps, fpsMethod = "1000/meanFrameMs", framesOver50Ms = count, frameCount = 1000 }) + "\n```\n## Sample timeline (CSV)\n```csv\nsample_start_s,sample_end_s,cpu_pct\n0,2,40\n```\n";
        var a = Report(2, 2040, 75.4, 98.2, "Scene A"); var b = Report(3, 780, 64.8, 105.7, "Scene B");
        var comparison = DiagnosticComparison.Build(a, b, "Original", "Cleaned");
        Check(comparison.Contains("-10.60") && comparison.Contains("cannot be attributed") && comparison.Contains("durations") && comparison.Contains("conditions differ"), "thermal delta or comparability safeguards missing");
        Check(comparison.Contains("per recording minute") && comparison.Contains("Unavailable"), "normalised stutters or missing data omitted");
        Near(DiagnosticComparison.RatePerMinute(10, 120), 5, "duration normalisation");
        Check(DiagnosticComparison.RatePerMinute(10, 0) is null, "zero duration invented rate");
        Check(ReportTimeline.TryParse(a, out var old, out _) && old!.Summary.SchemaVersion == 2, "format 2 compatibility");
        Check(ReportTimeline.TryParse(b, out _, out _), "format 3 reader");
        var unsupported = Report(99, 1, 1, 1, ""); Check(!ReportTimeline.TryParse(unsupported, out _, out _), "unknown format accepted");
        Check(!ReportTimeline.TryParse(b[..^5], out _, out _), "truncated CSV accepted");
        Check(DiagnosticComparison.MachineSummary("## Machine summary (JSON)\n```json\n{") is null, "partial JSON not handled");
        var legacy = "- GPU temperature: 60 / 75.4 / 82 °C (10 readings)";
        Check(DiagnosticComparison.Build(legacy, b, "legacy", "current").Contains("75.40"), "legacy text fallback");
        return Task.CompletedTask;
    }
}
