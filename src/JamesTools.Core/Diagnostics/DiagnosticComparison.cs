using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JamesTools.Core.Diagnostics;

public static class DiagnosticComparison
{
    public static JsonElement? MachineSummary(string text)
    {
        var heading = text.IndexOf("## Machine summary (JSON)", StringComparison.OrdinalIgnoreCase);
        if (heading < 0) return null;
        var start = text.IndexOf("```json", heading, StringComparison.OrdinalIgnoreCase);
        var end = start < 0 ? -1 : text.IndexOf("```", start + 7, StringComparison.Ordinal);
        if (end < 0) return null;
        try { using var doc = JsonDocument.Parse(text[(start + 7)..end]); return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null; }
        catch (JsonException) { return null; }
    }
    public static double? Number(JsonElement? root, string name) => root is { } r && r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    public static double? RatePerMinute(double? count, double? seconds) => count is >= 0 && seconds is > 0 ? 60 * count / seconds : null;
    public static string Build(string baseline, string retest, string nameA, string nameB)
    {
        var a = MachineSummary(baseline); var b = MachineSummary(retest);
        var sa = ReportTimeline.ReadSummary(baseline); var sb = ReportTimeline.ReadSummary(retest);
        var assessment = ReportComparison.Assess(sa, sb);
        var text = new StringBuilder("# Performance report comparison\n");
        text.AppendLine($"Baseline: {nameA}; retest: {nameB}");
        static string F(double? d) => d?.ToString("F2", CultureInfo.InvariantCulture) ?? "Unavailable";
        text.AppendLine($"Duration: {F(sa.DurationSeconds)} → {F(sb.DurationSeconds)} s; system sample coverage: {sa.SystemSampleCount?.ToString() ?? "Unavailable"} → {sb.SystemSampleCount?.ToString() ?? "Unavailable"}; frame counts: {sa.FrameCount?.ToString() ?? "Unavailable"} → {sb.FrameCount?.ToString() ?? "Unavailable"}.");
        text.AppendLine($"Baseline context: {sa.Context ?? "Not provided"}\nRetest context: {sb.Context ?? "Not provided"}");
        text.AppendLine($"Baseline symptoms/changes: {sa.Symptoms ?? "Not provided"}\nRetest symptoms/changes: {sb.Symptoms ?? "Not provided"}");
        var hasConditions = a is { } ar && ar.TryGetProperty("conditions", out var ac) ? ac.GetRawText() : null;
        var otherConditions = b is { } br && br.TryGetProperty("conditions", out var bc) ? bc.GetRawText() : null;
        text.AppendLine(assessment.WorkloadContextMatches && hasConditions == otherConditions
            ? "Declared context matches; this does not verify that the scenes or workload were controlled."
            : "Test conditions differ or are incomplete. Workloads are not established as comparable.");
        if (hasConditions is not null) text.AppendLine("Baseline structured conditions: " + hasConditions);
        if (otherConditions is not null) text.AppendLine("Retest structured conditions: " + otherConditions);
        if (Number(a, "warmupSeconds") != Number(b, "warmupSeconds") || Number(a, "stutterThresholdMs") != Number(b, "stutterThresholdMs"))
            text.AppendLine("Warm-up or configured stutter thresholds differ or were not recorded. Configured pacing metrics are not directly comparable.");
        text.AppendLine("Different recording durations change exposure to loading, rare stutters and sustained conditions. Normalised rates reduce exposure bias but do not control scene differences.");
        text.AppendLine("A GPU temperature reduction is thermal evidence. FPS changes cannot be attributed to cleaning or another hardware change without controlled scenes; temperature alone does not prove thermal throttling. Memory growth alone does not prove a memory leak.");
        text.AppendLine().AppendLine("| Metric | Baseline | Retest | Absolute change | Change % | Coverage (valid readings) |");
        text.AppendLine("|---|---:|---:|---:|---:|---|");
        void Row(string label, double? x, double? y, bool percent = true, string coverage = "Not recorded in legacy summaries")
        {
            var delta = x is not null && y is not null ? y - x : null;
            var pct = percent && x is not null and not 0 && delta is not null ? 100 * delta / Math.Abs(x.Value) : null;
            text.AppendLine($"| {label} | {F(x)} | {F(y)} | {F(delta)} | {(percent ? F(pct) : "Not meaningful")} | {coverage} |");
        }
        double? Value(JsonElement? root, string report, string key, string? legacyLabel)
        {
            var value = Number(root, key); if (value is not null || string.IsNullOrWhiteSpace(legacyLabel)) return value;
            var line = report.Split('\n').FirstOrDefault(l => l.StartsWith("- " + legacyLabel + ":", StringComparison.OrdinalIgnoreCase));
            var parts = line?.Split('/');
            if (parts is not { Length: >= 3 }) return null;
            var part = key.EndsWith("MaxC", StringComparison.Ordinal) ? parts[2].Trim().Split(' ')[0] : parts[1].Trim();
            return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
        }
        foreach (var (key, label, legacy) in new[] {
            ("fpsAvg", "Average FPS", "FPS (from frame intervals)"), ("frameP50Ms", "Median frame time (ms)", ""),
            ("frameP95Ms", "P95 frame time (ms)", ""), ("frameP99Ms", "P99 frame time (ms)", ""), ("frameP999Ms", "P99.9 frame time (ms)", ""),
            ("framesOver33Ms", "Frames >33.333 ms", ""), ("framesOver50Ms", "Frames >50 ms", ""), ("framesOver100Ms", "Frames >100 ms", ""),
            ("gpuTempAvgC", "Average GPU temperature (°C)", "GPU temperature"), ("gpuTempMaxC", "Maximum GPU temperature (°C)", "GPU temperature"),
            ("gpuAvg", "GPU utilisation (%)", "GPU utilization"), ("gpuPowerAvgW", "GPU power (W)", "GPU power"), ("gpuClockAvgMHz", "GPU graphics clock (MHz)", "GPU graphics clock"),
            ("gpuMemAvgMiB", "Device VRAM allocation (MiB)", "GPU memory used"), ("cpuAvg", "System CPU (%)", "Total CPU"),
            ("gameCpuAvg", "Selected process CPU (%)", "Selected processes CPU (share of total system capacity)"),
            ("memUsedAvgMiB", "RAM used (MiB)", "Physical memory used"), ("commitUsedAvgMiB", "Commit used (MiB)", "System committed memory"),
            ("diskReadAvgMiBps", "System disk read (MiB/s)", "All physical disks read throughput"), ("diskWriteAvgMiBps", "System disk write (MiB/s)", "All physical disks write throughput"),
            ("pagesInputAvgPerSecond", "System page-ins (pages/s)", "System pages read from disk"),
            ("processHardFaultCount", "Process ETW hard faults (partial when events lost)", ""), ("processHardFaultReadMiB", "Process ETW hard-fault read volume (MiB)", "") })
        {
            if (key == "fpsAvg" && !assessment.FpsMethodsCompatible) { text.AppendLine("| Average FPS | Incompatible averaging methods | Unavailable | Unavailable | Unavailable | Legacy methods differ |"); continue; }
            Row(label, Value(a, baseline, key, legacy), Value(b, retest, key, legacy), !key.Contains("Temp"));
        }
        foreach (var threshold in new[] { 33, 50, 100 }) Row($"Frames >{(threshold == 33 ? "33.333" : threshold)} ms per recording minute",
            RatePerMinute(Number(a, $"framesOver{threshold}Ms"), sa.DurationSeconds), RatePerMinute(Number(b, $"framesOver{threshold}Ms"), sb.DurationSeconds));
        JsonElement? Nested(JsonElement? root, string name) => root is { } r && r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;
        foreach (var key in new[] { "SlowFrames", "StuttersPerMinute", "BelowTargetSeconds", "StandardDeviationMs", "ClusterCount" })
            Row("Pacing " + key, Number(Nested(a, "pacing"), key), Number(Nested(b, "pacing"), key));
        foreach (var window in new[] { "OneSecond", "FiveSeconds" })
            foreach (var metric in new[] { "Minimum", "Average", "Maximum" }) Row($"Rolling {window} FPS {metric}", Number(Nested(Nested(a, "pacing"), window), metric), Number(Nested(Nested(b, "pacing"), window), metric));
        Dictionary<string, JsonElement> Metrics(JsonElement? root) => root is { } r && r.TryGetProperty("metrics", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(m => m.TryGetProperty("Key", out _)).GroupBy(m => m.GetProperty("Key").GetString()!).ToDictionary(g => g.Key, g => g.Last()) : new();
        var ma = Metrics(a); var mb = Metrics(b);
        foreach (var key in ma.Keys.Union(mb.Keys).Where(k => !k.StartsWith("cpuFrequencyMHz:")).Order())
        {
            var x = ma.TryGetValue(key, out var mx) ? (JsonElement?)mx : null; var y = mb.TryGetValue(key, out var my) ? (JsonElement?)my : null;
            var (label, unit, _, _) = DiagnosticMeasurements.Describe(key);
            Row(label + " (" + unit + ")", Number(x, "Average"), Number(y, "Average"), unit != "°C", $"{F(Number(x, "Readings"))} → {F(Number(y, "Readings"))}");
        }
        text.AppendLine("Missing values mean Unavailable, never zero. Hardware averages are sample averages, not duration-weighted. Pacing rates use measured frame-interval time; legacy per-recording-minute rates use whole recording duration. Warm-up and capture gaps affect their denominators.");
        return text.ToString();
    }
}
