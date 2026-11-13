using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JamesTools.Core.Diagnostics;

public sealed record ReportMarker(double OffsetSeconds, string Note);

public sealed record ReportTimelineRow(
    double? SampleStartSeconds, double? SampleEndSeconds, double? CpuPercent, double? GpuPercent,
    double? MemoryUsedMiB, double? PagesInputPerSecond, double? DiskReadMiBPerSecond, double? DiskWriteMiBPerSecond,
    double? DiskLatencyMs, double? FrameWindowStartSeconds, double? FrameWindowEndSeconds,
    string? FrameStream, int? FrameCount, double? Fps, double? FrameMaxMs, int? FramesOver50Ms);

public sealed record ReportMachineSummary(int? SchemaVersion, double? DurationSeconds, string? FpsMethod,
    double? FpsAverage, double? FrameMsAverage, double? CpuAverage, double? GpuAverage, double? MemoryAverageMiB,
    int? SystemSampleCount, int? FrameCount, string? StartedUtc, string? Context, string? Symptoms,
    string? FrameStream = null, int? FrameStreamCount = null, string? NetworkConfiguration = null, double? FrameP99Ms = null, int? FramesOver50Ms = null, string? DriverVersion = null);

public sealed record ParsedPerformanceReport(ReportMachineSummary Summary,
    IReadOnlyList<ReportTimelineRow> Rows, IReadOnlyList<ReportMarker> Markers)
{
    public NetworkCapture? Network { get; init; }
}

public sealed record ReportExclusion(double StartSeconds, double EndSeconds, string Reason);

public sealed record TimelineAnalysis(double StartSeconds, double EndSeconds, int HardwareSamples,
    int FrameWindows, int Frames, double? AverageCpuPercent, double? AverageGpuPercent,
    double? AverageMemoryMiB, double? AverageDiskReadMiBPerSecond, double? AverageDiskWriteMiBPerSecond,
    double? AverageFrameTimeMs, int? FramesOver50Ms, IReadOnlyList<string> Limitations);

public sealed record ReportComparisonContext(bool FpsMethodsCompatible, string BaselineFpsMethod,
    string RetestFpsMethod, bool WorkloadContextMatches);

public static class ReportComparison
{
    public static ReportComparisonContext Assess(ReportMachineSummary baseline, ReportMachineSummary retest)
    {
        const string legacy = "legacy per-frame FPS average";
        var a = string.IsNullOrWhiteSpace(baseline.FpsMethod) ? legacy : baseline.FpsMethod.Trim();
        var b = string.IsNullOrWhiteSpace(retest.FpsMethod) ? legacy : retest.FpsMethod.Trim();
        return new(string.Equals(a, b, StringComparison.OrdinalIgnoreCase), a, b,
            !string.IsNullOrWhiteSpace(baseline.Context) && !string.IsNullOrWhiteSpace(retest.Context) &&
            string.Equals(baseline.Context.Trim(), retest.Context.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Read-only reader and selection analysis for format-2 performance report timelines.</summary>
public static class ReportTimeline
{
    private static readonly Regex MarkerLine = new(@"^-\s*(?<time>-?[\d.,]+)\s*s\s*[—–-]\s*(?<note>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StructuredMarker = new(@"\[marker-json:(?<json>\{.*\})\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string text, out ParsedPerformanceReport? report, out string error)
    {
        report = null; error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "Report is empty."; return false; }
        var summary = ParseSummary(text);
        var rows = new List<ReportTimelineRow>();
        var markers = ParseMarkers(text);
        var section = text.IndexOf("## Sample timeline (CSV)", StringComparison.OrdinalIgnoreCase);
        if (section < 0) { error = "This report has no supported sample timeline. The full report can still be compared."; return false; }
        var formatMatch = Regex.Match(text, @"^-\s*Report format:\s*(?<version>\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        if (!formatMatch.Success || formatMatch.Groups["version"].Value != "2")
        { error = "This report format is not supported by the timeline viewer. The full report can still be compared."; return false; }
        var fenceStart = text.IndexOf("```csv", section, StringComparison.OrdinalIgnoreCase);
        var fenceEnd = fenceStart < 0 ? -1 : text.IndexOf("```", fenceStart + 6, StringComparison.Ordinal);
        if (fenceStart < 0 || fenceEnd < 0) { error = "The report timeline is incomplete."; return false; }
        string[]? headers = null;
        foreach (var line in text[(fenceStart + 6)..fenceEnd].Split('\n'))
        {
            var values = ParseCsv(line.TrimEnd('\r'));
            if (headers is null)
            {
                if (!values.Any(v => v.Equals("sample_start_s", StringComparison.OrdinalIgnoreCase))) continue;
                headers = values.Select(v => v.Trim()).ToArray();
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (values.Length > headers.Length) continue;
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < headers.Length; i++) map.TryAdd(headers[i], i);
            double? D(string name) => map.TryGetValue(name, out var i) && i < values.Length && Number(values[i], out var n) ? n : null;
            int? I(string name) => map.TryGetValue(name, out var i) && i < values.Length && int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            string? S(string name) => map.TryGetValue(name, out var i) && i < values.Length && !string.IsNullOrWhiteSpace(values[i]) ? values[i] : null;
            var start = D("sample_start_s"); var end = D("sample_end_s");
            if (start is null || end is null || end < start) continue;
            rows.Add(new(start, end, D("cpu_pct"), D("gpu_pct"), D("ram_used_mib"), D("pages_input_per_s"),
                D("disk_read_mib_per_s"), D("disk_write_mib_per_s"), D("disk_latency_ms"),
                D("frame_window_start_s"), D("frame_window_end_s"), S("frame_stream"), I("frame_count"),
                D("fps"), D("frame_max_ms"), I("frames_over_50ms")));
        }
        if (headers is null) { error = "The report timeline has no recognized header."; return false; }
        if (rows.Count == 0) { error = "The report timeline has no valid sample rows."; return false; }
        report = new(summary, rows, markers) { Network = NetworkReports.Read(text) };
        return true;
    }

    /// <summary>Reads summary metadata even when a legacy report has no supported CSV timeline.</summary>
    public static ReportMachineSummary ReadSummary(string text) => ParseSummary(text);

    public static TimelineAnalysis Analyze(ParsedPerformanceReport report, double startSeconds, double endSeconds,
        IReadOnlyList<ReportExclusion>? exclusions = null)
    {
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds) || startSeconds < 0 || endSeconds <= startSeconds)
            throw new ArgumentOutOfRangeException(nameof(endSeconds), "Select a valid non-empty time range.");
        exclusions ??= [];
        bool Excluded(double s, double e) => exclusions.Any(x => x.EndSeconds > s && x.StartSeconds < e);
        var hardware = report.Rows.Where(r => r.SampleEndSeconds is double t && t >= startSeconds && t <= endSeconds &&
            !exclusions.Any(x => t >= x.StartSeconds && t < x.EndSeconds) &&
            (r.CpuPercent is not null || r.GpuPercent is not null || r.MemoryUsedMiB is not null ||
             r.PagesInputPerSecond is not null || r.DiskReadMiBPerSecond is not null ||
             r.DiskWriteMiBPerSecond is not null || r.DiskLatencyMs is not null)).ToArray();
        // Aggregate frame windows are indivisible. Keep only windows fully inside the selected range and outside exclusions.
        var frameCandidates = report.Rows.Where(r => r.FrameWindowStartSeconds is double s && r.FrameWindowEndSeconds is double e &&
            e > s && s >= startSeconds && e <= endSeconds && r.FrameCount is > 0 && !Excluded(s, e))
            .OrderBy(r => r.FrameWindowStartSeconds).ThenBy(r => r.FrameWindowEndSeconds).ToArray();
        var retainedFrames = new List<ReportTimelineRow>();
        var previousFrameEnd = double.NegativeInfinity;
        foreach (var candidate in frameCandidates)
        {
            if (candidate.FrameWindowStartSeconds!.Value < previousFrameEnd) continue;
            retainedFrames.Add(candidate); previousFrameEnd = candidate.FrameWindowEndSeconds!.Value;
        }
        var frames = retainedFrames.ToArray();
        static double? Average(IEnumerable<double?> values)
        {
            var actual = values.Where(v => v is not null && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
            return actual.Length == 0 ? null : actual.Average();
        }
        var count = frames.Sum(r => Math.Max(0, r.FrameCount ?? 0));
        double? weighted = null;
        var weightedWindows = frames.Where(r => r.FrameCount is > 0 && r.Fps is > 0).ToArray();
        var weight = weightedWindows.Sum(r => r.FrameCount!.Value);
        if (weight > 0) weighted = weightedWindows.Sum(r => (1000.0 / r.Fps!.Value) * r.FrameCount!.Value) / weight;
        var limits = new List<string> {
            "Selection summary uses retained hardware sample points and complete aggregate frame windows only; values are not interpolated.",
            "Frame windows are indivisible and excluded in full when they overlap the selected range boundary or an excluded interval.",
            "Frame-time average is weighted by frame count where window FPS is available; max frame time and stutter counts are window-level aggregates.",
            "No exact per-frame P99 or 1% low is available from this timeline."
        };
        var streamIds = frames.Select(r => r.FrameStream).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (streamIds.Length > 1)
        {
            weighted = null;
            limits.Add("Selected windows include incomparable stream IDs: " + string.Join(", ", streamIds) + ". A combined average frame time is unavailable.");
        }
        var stutterCounts = frames.Where(r => r.FramesOver50Ms is >= 0).ToArray();
        if (stutterCounts.Length < frames.Length) limits.Add("Stutter counts are unavailable for some selected windows; the selection total is unavailable.");
        if (frameCandidates.Length > frames.Length) limits.Add($"{frameCandidates.Length - frames.Length} overlapping frame window(s) were omitted to avoid counting intervals twice.");
        return new(startSeconds, endSeconds, hardware.Length, frames.Length, count,
            Average(hardware.Select(r => r.CpuPercent)), Average(hardware.Select(r => r.GpuPercent)),
            Average(hardware.Select(r => r.MemoryUsedMiB)), Average(hardware.Select(r => r.DiskReadMiBPerSecond)),
            Average(hardware.Select(r => r.DiskWriteMiBPerSecond)), weighted,
            frames.Length > 0 && stutterCounts.Length == frames.Length ? stutterCounts.Sum(r => r.FramesOver50Ms!.Value) : null, limits);
    }

    public static string[] ParseCsv(string line)
    {
        var fields = new List<string>(); var field = new System.Text.StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString()); return fields.ToArray();
    }

    private static bool Number(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    private static IReadOnlyList<ReportMarker> ParseMarkers(string text)
    {
        var section = text.IndexOf("## Markers", StringComparison.OrdinalIgnoreCase);
        if (section < 0) return [];
        var end = text.IndexOf("\n## ", section + 4, StringComparison.Ordinal);
        var body = text[section..(end < 0 ? text.Length : end)];
        var result = new List<ReportMarker>();
        foreach (var line in body.Split('\n'))
        {
            var match = MarkerLine.Match(line.TrimEnd('\r'));
            if (!match.Success || !Number(match.Groups["time"].Value.Replace(',', '.'), out var seconds)) continue;
            var note = match.Groups["note"].Value;
            var structured = StructuredMarker.Match(note);
            if (structured.Success)
            {
                try { using var doc = JsonDocument.Parse(structured.Groups["json"].Value); if (doc.RootElement.TryGetProperty("note", out var n)) note = n.GetString() ?? note; } catch { }
            }
            else { var context = note.IndexOf(" (latest sample:", StringComparison.Ordinal); if (context >= 0) note = note[..context]; }
            result.Add(new(seconds, note.Trim()));
        }
        return result;
    }

    private static ReportMachineSummary ParseSummary(string text)
    {
        const string marker = "## Machine summary (JSON)";
        var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            var open = text.IndexOf("```json", at, StringComparison.OrdinalIgnoreCase); var close = open < 0 ? -1 : text.IndexOf("```", open + 7, StringComparison.Ordinal);
            if (open >= 0 && close > open) try
            {
                using var doc = JsonDocument.Parse(text[(open + 7)..close]); var r = doc.RootElement;
                double? D(string key) => r.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : null;
                int? I(string key) => r.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n >= 0 ? n : null;
                string? S(string key) => r.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                return new(I("schemaVersion"), D("durationSeconds"), S("fpsMethod"), D("fpsAvg"), D("frameMsAvg"), D("cpuAvg"), D("gpuAvg"), D("memUsedAvgMiB"), I("systemSampleCount"), I("frameCount"), HeaderValue(text, "Started (UTC)"), Section(text, "### Game settings and test scene", "### Symptoms and target performance"), Section(text, "### Symptoms and target performance", null), S("frameStream"), I("frameStreamCount"), NetworkReports.Read(text) is { } network ? JsonSerializer.Serialize(new { network.Enabled, network.Targets, Addresses = network.Observations.Select(o => new { o.Target, o.Address }).Distinct().OrderBy(o => o.Target).ThenBy(o => o.Address).ToArray() }) : null, D("frameP99Ms"), I("framesOver50Ms"), S("driverVersion") ?? HeaderValue(text, "NVIDIA driver"));
            }
            catch { }
        }
        return new(null, null, null, null, null, null, null, null, null, null, HeaderValue(text, "Started (UTC)"), null, null);
    }
    private static string? HeaderValue(string text, string label) { var at = text.IndexOf("- " + label + ":", StringComparison.OrdinalIgnoreCase); if (at < 0) return null; var end = text.IndexOfAny(['\r', '\n'], at); return text[(at + label.Length + 3)..(end < 0 ? text.Length : end)].Trim(); }
    private static string? Section(string text, string heading, string? next)
    {
        var at = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase); if (at < 0) return null; at += heading.Length;
        var end = next is null ? text.IndexOf("\n## ", at, StringComparison.Ordinal) : text.IndexOf(next, at, StringComparison.OrdinalIgnoreCase);
        return text[at..(end < 0 ? text.Length : end)].Trim();
    }
}
