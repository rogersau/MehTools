using System.Diagnostics;
using System.Text.Json;
using JamesTools.Core.Diagnostics;

internal static class InvestigationIntegrationTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static Task Run()
    {
        var clock = new InvestigationClock(5_000_000_000, 10_000_000, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Check(Math.Abs(clock.FromQpc(5_040_100_000) - 4.01) < 1e-12, "QPC must subtract the recording origin before scaling");
        Check(Math.Abs(clock.FromUtc(clock.OriginUtc.AddMilliseconds(4010)) - 4.01) < 1e-12, "UTC anchor conversion");
        var options = new DiagnosticOptions { InvestigateStutter = true, DeepInvestigation = true, DeepUsbTracing = true, InvestigationDurationSeconds = 90 };
        var copied = options.Copy(); options.InvestigationDurationSeconds = 30;
        Check(copied.InvestigationDurationSeconds == 90 && copied.DeepInvestigation && copied.DeepUsbTracing && copied.InvestigateStutter, "Investigation options must survive copy without sharing mutable preferences");
        foreach (var invalid in new[] { new DiagnosticOptions { DeepInvestigation = true }, new DiagnosticOptions { DeepUsbTracing = true }, new DiagnosticOptions { InvestigationDurationSeconds = 301 } })
        {
            try { invalid.Validate(); throw new Exception("Invalid investigation options accepted"); }
            catch (ArgumentException) { }
        }
        var report = InvestigationAnalysis.Analyze([], [], [], [], [], 60);
        var text = "# Performance recording\n## Machine summary (JSON)\n```json\n" + JsonSerializer.Serialize(new { schemaVersion = 3, stutterInvestigation = report }) + "\n```\n";
        Check(InvestigationExports.Parse(text).DurationSeconds == 60, "Optional investigation section must load from legacy-compatible format 3");
        Check(InvestigationExports.Parse(InvestigationReportWriter.ToJson(report)).Metrics.FrameCount == 0, "Standalone report roundtrip");
        using (var disabled = new DiagnosticRawWriter(false))
        {
            disabled.InvestigationState("recording", clock, 60); disabled.InvestigationSummary(report); disabled.Finish("report", "{}", new { });
            Check(disabled.DirectoryPath is null && disabled.Error is null, "MCP raw-disabled capture must not create diagnostic/ETL exports");
        }
        try { InvestigationExports.Parse("# Performance recording\nNo investigation collected"); throw new Exception("Legacy report invented evidence"); }
        catch (InvalidDataException) { }
        return Task.CompletedTask;
    }

    public static async Task Smoke()
    {
        Console.WriteLine("Windows investigation smoke: local event subscriptions, bounded logger, cancellation and unprivileged deep fallback.");
        string? raw = null;
        await using (var capture = await PerformanceLogger.StartAsync(null, null, null, new NetworkProbeOptions(false, []),
            new DiagnosticOptions { InvestigateStutter = true, InvestigationDurationSeconds = 3 }))
        {
            raw = capture.RawDataDirectory;
            var deadline = Stopwatch.StartNew();
            while (capture.IsRecording && deadline.Elapsed.TotalSeconds < 15) await Task.Delay(100);
            Check(!capture.IsRecording, "Investigation did not stop at its configured duration");
            var text = await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            var report = capture.CompletedInvestigation ?? throw new Exception("No completed investigation report");
            Check(report.DurationSeconds <= 12, "Bounded capture deadline was missed");
            Check(InvestigationExports.Parse(text).FormatVersion == 1, "Completed text report does not reopen");
            Check(raw is not null && File.Exists(Path.Combine(raw, "investigation.json")), "Structured investigation missing");
            Check(InvestigationExports.InterruptedCaptureNotice(raw!).StartsWith("Capture completed"), "Completed capture incorrectly marked interrupted");
            Console.WriteLine($"NORMAL devices={report.Devices.Count}, events={report.Events.Count}, duration={report.DurationSeconds:F2}s");
            foreach (var status in report.Collectors) Console.WriteLine($"  {status.Source}: {status.Availability}; loss={status.EventsLost}; drops={status.EventsDropped}; collector_ms={status.CollectionMilliseconds:F2}; {status.Detail}");
            var reopened = InvestigationExports.Read(Path.Combine(raw!, "investigation.json"));
            Check(reopened.Devices.Count == report.Devices.Count, "Device inventory did not survive export");
        }
        using var cancellation = new CancellationTokenSource();
        await using (var capture = await PerformanceLogger.StartAsync(null, null, null, new NetworkProbeOptions(false, []),
            new DiagnosticOptions { InvestigateStutter = true, DeepInvestigation = true, DeepUsbTracing = true, InvestigationDurationSeconds = 30 }, cancellation.Token))
        {
            cancellation.Cancel();
            await capture.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(!capture.IsRecording && capture.CompletedInvestigation is not null, "Cancellation did not finish all investigation collectors");
            var deep = capture.CompletedInvestigation!.Collectors.Where(c => c.Source.Contains("ETW", StringComparison.OrdinalIgnoreCase) || c.Source.Contains("Deep", StringComparison.OrdinalIgnoreCase));
            foreach (var status in deep) Console.WriteLine($"DEEP {status.Source}: {status.Availability}; {status.Detail}");
        }
        Console.WriteLine("PASS investigation Windows lifecycle smoke. Captures retained in the local Diagnostics folder for inspection.");
    }

    public static async Task Overhead()
    {
        using var process = Process.GetCurrentProcess();
        async Task<(double CpuMs, long PrivateBytes, double Seconds)> Measure(Func<Task> action)
        {
            process.Refresh(); var cpu = process.TotalProcessorTime; var bytes = process.PrivateMemorySize64; var wall = Stopwatch.StartNew();
            await action(); process.Refresh();
            return ((process.TotalProcessorTime - cpu).TotalMilliseconds, process.PrivateMemorySize64 - bytes, wall.Elapsed.TotalSeconds);
        }
        var idle = await Measure(() => Task.Delay(5000));
        var clock = InvestigationClock.FromElapsed(Stopwatch.StartNew());
        long events = 0;
        InvestigationCollectorStatus[] statuses = [];
        var collection = await Measure(async () =>
        {
            await using var collector = new InvestigationDeviceCollector(clock, _ => Interlocked.Increment(ref events));
            await Task.Delay(10000); statuses = collector.Status.ToArray();
        });
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            baseline = new { idle.CpuMs, idle.Seconds, privateDeltaMiB = idle.PrivateBytes / 1048576d },
            normalDeviceCollector = new { collection.CpuMs, collection.Seconds, privateDeltaMiB = collection.PrivateBytes / 1048576d,
                machineCpuPercent = collection.CpuMs / (collection.Seconds * 10 * Environment.ProcessorCount), events,
                callbackAndCollectionMilliseconds = statuses.Sum(s => s.CollectionMilliseconds), lost = statuses.Sum(s => s.EventsLost), dropped = statuses.Sum(s => s.EventsDropped) },
            limitation = "One idle-machine run, includes subscription startup and disposal. Process CPU excludes Windows services/kernel ETW work. No physical USB/audio transitions induced; callback receipt is not a source-latency measurement."
        }));
    }
}
