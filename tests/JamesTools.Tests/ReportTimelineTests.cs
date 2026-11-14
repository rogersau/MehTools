using JamesTools.Core.Diagnostics;

internal static class ReportTimelineTests
{
    public static Task Run()
    {
        var text = Fixture();
        Assert(ReportTimeline.TryParse(text, out var report, out var error) && report is not null, "synthetic timeline rejected: " + error);
        Assert(report!.Rows.Count == 3, "malformed time row was not discarded");
        Assert(report.Rows[0].FrameStream == "pid,chain", "quoted stream name did not parse");
        Assert(report.Rows[0].GpuPercent is null && report.Rows[0].MemoryUsedMiB is null, "missing counters were converted to zero");
        Assert(report.Markers.Count == 2 && report.Markers[0].Note == "Stutter" && report.Markers[1].Note == "Loading", "structured or legacy markers did not parse");
        var all = ReportTimeline.Analyze(report, 0, 6);
        Assert(all.FrameWindows == 3 && all.Frames == 60, "frame windows or counts were not retained");
        Assert(Math.Abs(all.AverageFrameTimeMs!.Value - 700.0 / 60) < 0.001, "average frame time was not frame-count weighted");
        Assert(all.AverageCpuPercent == 20 && all.AverageGpuPercent is null, "hardware sample averages ignored missing readings");
        Assert(all.Limitations.Any(x => x.Contains("P99", StringComparison.Ordinal)), "selection did not explain percentile limits");
        var selected = ReportTimeline.Analyze(report, 2, 6);
        Assert(selected.FrameWindows == 2 && selected.Frames == 50, "range boundaries were applied incorrectly");
        var excluded = ReportTimeline.Analyze(report, 0, 6, [new ReportExclusion(3, 5, "alt-tab")]);
        Assert(excluded.FrameWindows == 1 && excluded.Frames == 10, "overlap exclusions were applied incorrectly");
        Assert(excluded.AverageFrameTimeMs == 20, "excluded windows affected weighted frame time");
        var boundary = ReportTimeline.Analyze(report, 0, 6, [new ReportExclusion(2, 4, "loading")]);
        Assert(boundary.HardwareSamples == 2 && boundary.AverageCpuPercent == 25,
            "sample at the start of an exclusion was retained or its end was excluded");
        var framesOnly = report with { Rows = report.Rows.Select(r => r with
        {
            CpuPercent = null, GpuPercent = null, MemoryUsedMiB = null, DiskReadMiBPerSecond = null,
            DiskWriteMiBPerSecond = null, FramesOver50Ms = null
        }).ToArray() };
        var framesOnlySummary = ReportTimeline.Analyze(framesOnly, 0, 6);
        Assert(framesOnlySummary.HardwareSamples == 0 && framesOnlySummary.FramesOver50Ms is null,
            "missing hardware/stutter counters were counted as available");
        Assert(ReportTimeline.TryParse(text.Replace("\n", "\r\n", StringComparison.Ordinal), out _, out _), "CRLF report did not parse");
        var shortened = Fixture().Replace("4,6,30,,4,6,\"pid,chain\",30,100,20,2,500,5,6,last", "4,6,30,,4,6,\"pid,chain\",30,100,20,2", StringComparison.Ordinal);
        Assert(ReportTimeline.TryParse(shortened, out var shortReport, out _) && shortReport!.Rows[2].DiskReadMiBPerSecond is null,
            "row with trailing omitted counters was not tolerated");
        var overlapping = Fixture().Replace("4,6,30,,4,6,\"pid,chain\"", "4,6,30,,1.5,6,\"pid,chain\"", StringComparison.Ordinal);
        Assert(ReportTimeline.TryParse(overlapping, out var overlapReport, out _) &&
            ReportTimeline.Analyze(overlapReport!, 0, 6).Limitations.Any(x => x.Contains("overlapping frame window")),
            "overlapping windows were not omitted and disclosed");
        Assert(!ReportTimeline.TryParse("# Legacy report\n## Measurements\n- CPU: 1 / 2 / 3", out _, out var legacyError) && legacyError.Contains("no supported sample timeline"), "legacy report did not fail gracefully");
        Assert(!ReportTimeline.TryParse(text.Replace("Report format: 2", "Report format: 99", StringComparison.Ordinal), out _, out var unsupportedError) && unsupportedError.Contains("not supported"), "unsupported timeline version did not fail gracefully");
        Assert(ReportTimeline.TryParse(Fixture("\"stream,one\"", "\"stream,two\""), out var streams, out _), "different stream fixture rejected");
        Assert(ReportTimeline.Analyze(streams!, 0, 6).Limitations.Any(x => x.Contains("incomparable stream IDs")), "multiple stream IDs were combined without a limitation");
        Assert(ReportTimeline.Analyze(streams!, 0, 6).AverageFrameTimeMs is null, "incomparable streams produced a combined frame time");
        var baselineSummary = ReportTimeline.ReadSummary(text);
        var legacySummary = ReportTimeline.ReadSummary(text.Replace("\"1000/meanFrameMs\"", "\"legacy-per-frame-average\"", StringComparison.Ordinal));
        var compatible = ReportComparison.Assess(baselineSummary, baselineSummary);
        Assert(compatible.FpsMethodsCompatible && compatible.WorkloadContextMatches, "matching comparison method/context was rejected");
        var mismatch = ReportComparison.Assess(baselineSummary, legacySummary);
        Assert(!mismatch.FpsMethodsCompatible && mismatch.WorkloadContextMatches, "mismatched FPS method or matching context was misclassified");
        var changedContext = ReportTimeline.ReadSummary(text.Replace("Scene A", "Scene B", StringComparison.Ordinal));
        Assert(!ReportComparison.Assess(baselineSummary, changedContext).WorkloadContextMatches, "changed workload context was not identified");
        var badRangeThrown = false;
        try { ReportTimeline.Analyze(report, 4, 2); } catch (ArgumentOutOfRangeException) { badRangeThrown = true; }
        Assert(badRangeThrown, "invalid selection boundaries were accepted");
        return Task.CompletedTask;
    }

    private static string Fixture(string firstStream = "\"pid,chain\"", string secondStream = "\"pid,chain\"") => $$"""
# Performance recording
- Report format: 2
- Started (UTC): 2026-01-01 00:00:00 UTC
- Duration: 00:00:06

## Player context (self-reported)
### Game settings and test scene
Scene A

### Symptoms and target performance
Stutter

## Markers
- 2.250 s — Stutter [marker-json:{"offsetSeconds":2.25,"note":"Stutter"}] (latest sample: CPU 20%)
- 4.000 s — Loading (latest sample: CPU 30%)

## Machine summary (JSON)
```json
{"schemaVersion":2,"durationSeconds":6,"fpsMethod":"1000/meanFrameMs","fpsAvg":80,"frameMsAvg":12.5,"cpuAvg":20,"gpuAvg":null,"memUsedAvgMiB":null,"frameCount":40}
```

## Sample timeline (CSV)
```csv
sample_start_s,sample_end_s,cpu_pct,gpu_pct,frame_window_start_s,frame_window_end_s,frame_stream,frame_count,fps,frame_max_ms,frames_over_50ms,ram_used_mib,disk_read_mib_per_s,disk_write_mib_per_s,unknown_column
0,2,10,,0,2,{{firstStream}},10,50,60,1,,1,2,ignored
2,4,20,,2,4,{{secondStream}},20,100,40,0,400,3,4,extra
4,6,30,,4,6,{{secondStream}},30,100,20,2,500,5,6,last
bad,7,not-a-number,,6,7,broken,4,60,30,1,400,7,8,bad-time
```
""";

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
