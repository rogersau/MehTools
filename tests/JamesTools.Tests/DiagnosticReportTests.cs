using System.Globalization;
using JamesTools.Core.Diagnostics;

internal static class DiagnosticReportTests
{
    public static Task Run()
    {
        var capture = new FrameCaptureData();
        Assert(capture.GetSummary("empty").Count == 0, "empty capture reported frames");
        var emptyWindow = capture.TakeInterval(1);
        Assert(emptyWindow.Count == 0 && emptyWindow.Fps is null && emptyWindow.Stream is null, "empty interval invented FPS");
        capture.AddCsv("PresentMon startup message", 1);
        capture.AddCsv("Application,ProcessID,SwapChainAddress,FrameTime,DisplayLatency", 1);
        capture.AddCsv("game.exe,10,0xA,10,12", 1.1);
        capture.AddCsv("game.exe,10,0xA,40,NA", 1.2);
        capture.AddCsv("game.exe,10,0xB,1,1", 1.3);
        capture.AddCsv("game.exe,10,0xA,NaN,12", 1.4);
        capture.AddCsv("malformed", 1.5);
        var summary = capture.GetSummary("ok");
        Assert(summary.Count == 2 && summary.Stream == "10:A" && summary.StreamCount == 2 && summary.OtherStreamFrames == 1,
            "capture combined presentation streams");
        Assert(summary.FpsAvg == 40 && summary.FrameMsAvg == 25 && summary.LatencyAvg == 12 && summary.LatencyCount == 1,
            "frame or latency aggregates were incorrect");
        var window = capture.TakeInterval(2);
        Assert(window.StartSeconds == 1 && window.EndSeconds == 2 && window.Count == 2 && window.Fps == 40 && window.MaxMs == 40,
            "frame window did not cover its interval");
        Assert(capture.TakeInterval(3).Fps is null, "frame window reused stale data");
        capture.AddCsv("game.exe,10,0xB,60,1", 3.1);
        var next = capture.TakeInterval(4);
        Assert(next.Stream == "10:B" && next.Over50Ms == 1 && next.Count == 1, "active stream/window did not change correctly");
        Assert(capture.GetSummary("ok").Analysis!.WorstFrames[0].OffsetSeconds == 1.2, "worst-frame receipt offset changed");

        var bounded = new FrameCaptureData();
        bounded.AddCsv("ProcessID,SwapChainAddress,MsBetweenPresents", 0);
        for (var id = 0; id < 17; id++) bounded.AddCsv($"{id},0xA,16.67", 1);
        var limit = bounded.GetSummary("ok");
        Assert(limit.StreamCount == 16 && limit.IgnoredFrames == 1, "stream retention was not bounded or losses were hidden");

        var stats = new PerformanceLogger.Statistics();
        stats.Add(10);
        stats.ClearCurrent();
        Assert(stats.Current is null && stats.Count == 1 && stats.Last == 10, "missing sample was filled with stale data");
        stats.Add((double?)null);
        stats.Add(double.NaN);
        Assert(stats.Count == 1, "missing/invalid statistics counted as readings");
        stats.Add(30);
        Assert(stats.First == 10 && stats.Last == 30 && stats.Average == 20, "first/last memory trend was incorrect");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var row = DiagnosticRow.ForFrames(new FrameInterval(1.25, 2.5, "10:A", 2, 40.5, 60.25, 1));
            var fields = row.ToCsv().Split(',');
            Assert(fields.Length == 28 && fields[0] == "2.500" && fields[24] == "40.500", "CSV columns or decimal separator changed with locale");
            Assert(fields[2] == "" && fields[10] == "" && fields[11] == "", "frame-only timeline row invented hardware/process measurements");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
