using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using JamesTools.Core.Diagnostics;
using Path = System.IO.Path;

internal static class DiagnosticIntegration
{
    public static async Task Run()
    {
        var presentMon = PresentMonLocator.Resolve(null);
        var executable = Environment.ProcessPath;
        if (presentMon is null || executable is null) { Console.WriteLine("SKIP PresentMon integration: executable not available."); return; }
        var canvas = new Canvas();
        var moving = new Rectangle { Width = 50, Height = 50, Fill = Brushes.DodgerBlue };
        canvas.Children.Add(moving);
        var window = new Window { Title = "Meh Tools diagnostic integration fixture", Width = 320, Height = 200, Content = canvas, ShowInTaskbar = false };
        PerformanceLogger? logger = null;
        string? raw = null;
        try
        {
            window.Show();
            moving.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(0, 200, TimeSpan.FromSeconds(1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            logger = await Task.Run(() => PerformanceLogger.StartAsync(executable, presentMon, null, new NetworkProbeOptions(false, []),
                new DiagnosticOptions { InvestigateStutter = true, InvestigationDurationSeconds = 15 }));
            raw = logger.RawDataDirectory;
            await Task.Delay(5500);
            var report = await logger.StopAsync();
            var investigation = logger.CompletedInvestigation ?? throw new Exception("Investigation did not complete with the existing performance logger");
            if (InvestigationExports.Parse(report).FormatVersion != 1) throw new Exception("Integrated investigation report failed to reopen");
            Console.WriteLine($"INVESTIGATION real Windows: devices={investigation.Devices.Count}, events={investigation.Events.Count}, frameCoverage={investigation.Coverage.FrameCoverageFraction:P1}");
            var root = DiagnosticComparison.MachineSummary(report) ?? throw new Exception("Missing integration summary");
            var frames = DiagnosticComparison.Number(root, "frameCount");
            if (frames is > 0)
            {
                using var reader = new StreamReader(Path.Combine(raw!, "frames.csv"));
                var header = reader.ReadLine(); var first = reader.ReadLine();
                if (header != FrameTelemetry.CsvHeader || first is null || !first.Contains("QPC")) throw new Exception("Live frames missing normalized QPC raw data");
                Console.WriteLine($"PASS real PresentMon/WPF integration: {frames} selected-stream frames, format {root.GetProperty("schemaVersion")}, QPC raw export present.");
            }
            else
            {
                var at = report.IndexOf("## Frame pacing (PresentMon", StringComparison.Ordinal);
                var lines = at < 0 ? "No frame status" : string.Join(" ", report[at..].Split('\n').Take(3));
                Console.WriteLine("SKIP real PresentMon frame validation: " + lines + " CPU/memory recording remained available.");
            }
        }
        finally
        {
            window.Close();
            if (logger is not null) await logger.DisposeAsync();
            if (raw is not null)
            {
                var expectedRoot = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JamesTools", "Diagnostics"));
                var resolved = Path.GetFullPath(raw);
                if (Directory.GetParent(resolved)?.FullName == expectedRoot && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }
    }
}
