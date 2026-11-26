using System.IO;
using System.Windows;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;
using JamesTools.Core.Sessions;

namespace JamesTools;
public partial class MainWindow
{
    BenchmarkWindow? _benchmarkWindow;
    bool _benchmarkBusy;
    private void BenchmarkRecipes_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkWindow != null) { _benchmarkWindow.Activate(); return; }
        if (SelectedGame(PerformanceGame) is not { } selected) { PerformanceStatus.Text = "Select a game for benchmark recipes."; return; }
        try
        {
            var game = selected.Copy();
            var store = new BenchmarkStore(_dataDirectory);
            var network = NetworkProbeOptions.Create(_appliedSettings.NetworkDiagnosticsEnabled, _appliedSettings.NetworkTargets);
            var presentMon = PresentMonLocator.Resolve(_settings.PresentMonPath);
            _benchmarkWindow = new BenchmarkWindow(store, SessionLibraryStore.NormalizeExecutable(game.ExecutablePath), game.Name,
                (recipe, run, runner, checkpoint, ct) => CaptureBenchmark(game, recipe, run, runner, checkpoint, presentMon, network, ct), path => ShowReportTimeline(File.ReadAllText(path), Path.GetFileName(path))) { Owner = this };
            _benchmarkWindow.Closed += (_, _) => _benchmarkWindow = null;
            _benchmarkWindow.Show();
        }
        catch (Exception ex) { PerformanceStatus.Text = ex.Message; }
    }
    async Task<BenchmarkRun> CaptureBenchmark(GameRule game, BenchmarkRecipe recipe, BenchmarkRun run, BenchmarkRunner runner, Action<BenchmarkRun> checkpoint, string? presentMon, NetworkProbeOptions network, CancellationToken ct)
    {
        if (_benchmarkBusy || _performanceBusy || _performanceLogger != null) return run with { Status = "Incomplete", Reason = "Another benchmark or manual performance log is active." };
        _benchmarkBusy = true; StartPerformanceButton.IsEnabled = false;
        PerformanceLogger? logger = null;
        string? report = null, target = null; Guid? session = null, diagnostic = null;
        int[] ids = []; string? failure = null;
        try
        {
            var outcome = await runner.RunAsync(recipe, async token =>
            {
                var processes = await Task.Run(ProcessSnapshot.Capture, token).WaitAsync(TimeSpan.FromSeconds(3),token);
                target = AutomationPolicy.ResolveObservedTarget(game, processes, out var notice);
                if (target == null) throw new InvalidOperationException(notice);
                ids = processes.Where(p => string.Equals(p.FullPath, target, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).Order().ToArray();
                if (ids.Length != 1) throw new InvalidOperationException("Exactly one accessible matching process is required for a game benchmark.");
                session = _sessionTracker?.FindActive(game.ExecutablePath)?.Id;
                if (session == null && _sessionStore != null) { diagnostic = _sessionStore.Create(game.Name + " (benchmark diagnostic)", recipe.GameKey, DateTime.UtcNow).Id; session = diagnostic; }
                if (session == null) throw new InvalidOperationException("Session library unavailable; cannot pin benchmark report.");
                checkpoint(run with { Status = "Capturing", Target = target, SessionId = session });
                logger = await Task.Run(async () => await PerformanceLogger.StartAsync(target, presentMon, null, network, _appliedSettings.Diagnostics, token));
                logger.SetContext(recipe.Instructions + "\n" + recipe.Settings, "Benchmark recipe: " + recipe.Name + "; run " + run.Index);
            }, async () =>
            {
                if (logger != null) report = await logger.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }, async token =>
            {
                var processes = await Task.Run(ProcessSnapshot.Capture, token);
                var observed = AutomationPolicy.ResolveObservedTarget(game, processes, out _);
                return observed != null && processes.Count(p => string.Equals(p.FullPath, observed, StringComparison.OrdinalIgnoreCase)) == 1 && (target == null || string.Equals(target, observed, StringComparison.OrdinalIgnoreCase) && ids.SequenceEqual(processes.Where(p => string.Equals(p.FullPath, target, StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).Order()));
            }, ct);
            if (ct.IsCancellationRequested) outcome = (false, "Cancelled by user.");
            if (logger?.SamplingError is { } samplingError) outcome = (false, samplingError);
            run = run with { Status = outcome.Complete ? "Completed" : "Incomplete", Reason = outcome.Reason, Target = target, SessionId = session };
        }
        catch (Exception ex) { failure = ex.Message; run = run with { Status = "Incomplete", Reason = failure, Target = target, SessionId = session }; }
        finally
        {
            if (logger != null) { try { await logger.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); } catch (Exception ex) { failure = ex.Message; } }
            _benchmarkBusy = false; StartPerformanceButton.IsEnabled = true;
        }
        if (failure != null) run = run with { Status = "Incomplete", Reason = failure };
        if (report != null)
        {
            var summary = ReportTimeline.ReadSummary(report);
            run = run with { Summary = summary, ActualSeconds = summary.DurationSeconds ?? 0 };
            if (summary.DurationSeconds is not double actual || Math.Abs(actual - recipe.CaptureSeconds) > 3)
                run = run with { Status = "Incomplete", Reason = (run.Reason + " Actual capture duration outside timing tolerance.").Trim() };
            try
            {
                var directory = Path.Combine(_dataDirectory, "Diagnostics"); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "benchmark-" + run.Id.ToString("N") + "-" + Guid.NewGuid().ToString("N") + ".md");
                using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew))) await writer.WriteAsync(report);
                run = run with { ReportPath = path };
                if (_sessionStore?.AttachExplicitly(session!.Value, SessionAttachmentKind.PerformanceReport, path, DateTime.UtcNow) != true) throw new IOException("Pinned session no longer available.");
            }
            catch (Exception ex) { run = run with { Status = run.ReportPath == null ? "Incomplete" : run.Status, Reason = (run.Reason + " Report save/attachment: " + ex.Message).Trim() }; }
        }
        if (diagnostic.HasValue) { try { _sessionStore?.End(diagnostic.Value, DateTime.UtcNow, SessionStatus.Completed); } catch (Exception ex) { run = run with { Reason = run.Reason + " Session close: " + ex.Message }; } }
        RefreshSessionList(); return run;
    }
}
