using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;
using JamesTools.Core.Sessions;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace JamesTools;

public partial class MainWindow
{
    private PerformanceLogger? _performanceLogger;
    private string? _performanceReport;
    private bool _performanceReportSaved = true;
    private bool _performanceBusy;
    private DateTime _performanceStarted;
    private DispatcherTimer? _performanceTimer;
    private Guid? _performanceSessionId;
    private Guid? _performanceDiagnosticSessionId;
    private string? _baselineReportPath;
    private string? _performanceTargetNotice;

    private async void StartPerformance_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceBusy || _performanceLogger is not null) return;
        if (!string.IsNullOrWhiteSpace(_baselineReportPath) && string.IsNullOrWhiteSpace(PerformanceSetup.Text))
        {
            PerformanceStatus.Text = "Record the retest game settings and test scene before starting.";
            PerformanceSetup.Focus();
            return;
        }
        if (!string.IsNullOrWhiteSpace(_baselineReportPath) && string.IsNullOrWhiteSpace(RetestChangeBox.Text))
        {
            PerformanceStatus.Text = "Describe the one setting changed for this retest before starting.";
            RetestChangeBox.Focus();
            return;
        }
        if (!string.IsNullOrWhiteSpace(_baselineReportPath) && !File.Exists(_baselineReportPath))
        {
            PerformanceStatus.Text = "The selected baseline report is missing. Choose it again before starting the retest.";
            return;
        }
        if (!_performanceReportSaved && MessageBox.Show(this, "Your last report isn't saved. Start a new log anyway?", "Unsaved report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _performanceBusy = true;
        StartPerformanceButton.IsEnabled = false;
        PerformanceGame.IsEnabled = false;
        try
        {
            PerformanceStatus.Text = "Starting…";
            var game = (PerformanceGame.SelectedItem as GameRule)?.Copy();
            var presentMon = PresentMonLocator.Resolve(ReportPresentMonPath?.Text ?? _settings.PresentMonPath);
            string? targetPath = null;
            _performanceTargetNotice = null;
            if (game is not null)
            {
                var processes = await Task.Run(ProcessSnapshot.Capture);
                targetPath = AutomationPolicy.ResolveObservedTarget(game, processes, out var targetNotice);
                _performanceTargetNotice = targetNotice;
            }
            _performanceLogger = await PerformanceLogger.StartAsync(targetPath, presentMon,
                targetPath is null && game is not null ? _performanceTargetNotice : null);
            _performanceSessionId = null;
            _performanceDiagnosticSessionId = null;
            if (_sessionStore is not null)
            {
                try
                {
                    var detected = game is null ? null : _sessionTracker?.FindActive(game.ExecutablePath);
                    if (detected is not null) _performanceSessionId = detected.Id;
                    else
                    {
                        var name = game?.Name ?? "Unassigned diagnostic";
                        var key = game is null ? "" : SessionLibraryStore.NormalizeExecutable(game.ExecutablePath);
                        var diagnostic = _sessionStore.Create(name + (game is null ? "" : " (diagnostic)"), key, DateTime.UtcNow);
                        _performanceSessionId = diagnostic.Id;
                        _performanceDiagnosticSessionId = diagnostic.Id;
                        RefreshSessionList();
                    }
                }
                catch (Exception ex) { SessionsStatus.Text = "Session association unavailable: " + ex.Message; }
            }
            SetPerformanceContext();
            _performanceReport = null;
            _performanceReportSaved = true;
            SavePerformanceButton.IsEnabled = false;
            ViewPerformanceReportButton.IsEnabled = false;
            CompareCurrentBaselineButton.IsEnabled = false;
            PerformanceGame.IsEnabled = false;
            AddMarkerButton.IsEnabled = true;
            _performanceStarted = DateTime.UtcNow;
            _performanceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _performanceTimer.Tick -= PerformanceTimer_Tick;
            _performanceTimer.Tick += PerformanceTimer_Tick;
            _performanceTimer.Start();
            StopPerformanceButton.IsEnabled = true;
            PerformanceTimer_Tick(null, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            PerformanceStatus.Text = "Couldn't start logging.";
            MessageBox.Show(this, ex.Message, "Couldn't start", MessageBoxButton.OK, MessageBoxImage.Warning);
            StartPerformanceButton.IsEnabled = true;
            PerformanceGame.IsEnabled = true;
        }
        finally { _performanceBusy = false; }
    }

    private void PerformanceTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _performanceStarted;
        PerformanceStatus.Text = $"Logging ({(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}). Play until the problem happens." +
            (_performanceTargetNotice is null ? "" : " " + _performanceTargetNotice);
    }

    private async void StopPerformance_Click(object sender, RoutedEventArgs e) => await StopPerformanceAsync(true);

    private async Task<bool> StopPerformanceAsync(bool save)
    {
        if (_performanceBusy || _performanceLogger is null) return false;
        _performanceBusy = true;
        StopPerformanceButton.IsEnabled = false;
        PerformanceStatus.Text = "Creating report…";
        try
        {
            SetPerformanceContext();
            var report = await _performanceLogger.StopAsync();
            await _performanceLogger.DisposeAsync();
            _performanceLogger = null;
            _performanceTimer?.Stop();
            _performanceReport = report;
            FinishPerformanceDiagnosticSession();
            _performanceReportSaved = false;
            SavePerformanceButton.IsEnabled = true;
            ViewPerformanceReportButton.IsEnabled = true;
            CompareCurrentBaselineButton.IsEnabled = !string.IsNullOrWhiteSpace(_baselineReportPath);
            StartPerformanceButton.IsEnabled = true;
            PerformanceGame.IsEnabled = true;
            AddMarkerButton.IsEnabled = false;
            PerformanceStatus.Text = "Report ready. Click Save.";
            var saved = !save || SavePerformanceReport();
            if (!string.IsNullOrWhiteSpace(_baselineReportPath) && File.Exists(_baselineReportPath) &&
                MessageBox.Show(this, "The retest report is ready. Compare it with the selected baseline now?", "Baseline retest", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                CompareCurrentBaseline_Click(this, new RoutedEventArgs());
            return saved;
        }
        catch (Exception ex)
        {
            PerformanceStatus.Text = "Couldn't create the report.";
            StopPerformanceButton.IsEnabled = _performanceLogger is not null;
            AddMarkerButton.IsEnabled = _performanceLogger is not null;
            MessageBox.Show(this, ex.Message, "Couldn't finish", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { _performanceBusy = false; }
    }

    private void SavePerformance_Click(object sender, RoutedEventArgs e) => SavePerformanceReport();

    private bool SavePerformanceReport()
    {
        if (_performanceReport is null) return false;
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "Save performance report", Filter = "Text report (*.txt)|*.txt|Markdown report (*.md)|*.md", FileName = $"Meh-Tools-report-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", DefaultExt = ".txt" };
        if (picker.ShowDialog(this) != true) return false;
        try
        {
            File.WriteAllText(picker.FileName, _performanceReport, new UTF8Encoding(false));
            _performanceReportSaved = true;
            PerformanceStatus.Text = "Report saved. You can open it or share it for help.";
            AddSessionAttachment(_performanceSessionId, SessionAttachmentKind.PerformanceReport, picker.FileName,
                "Unassigned diagnostic report");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save report", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task<bool> PreparePerformanceForExitAsync()
    {
        if (_performanceBusy) return false;
        if (_performanceLogger is not null)
        {
            var answer = MessageBox.Show(this, "A performance log is running. Stop and save it before quitting?", "Performance log", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) return await StopPerformanceAsync(true);
            await _performanceLogger.DisposeAsync();
            _performanceLogger = null;
            FinishPerformanceDiagnosticSession();
            return true;
        }
        if (!_performanceReportSaved && MessageBox.Show(this, "Your report hasn't been saved. Quit without saving it?", "Unsaved report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        return true;
    }

    private void BrowseReportPresentMon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose the PresentMon program", Filter = "Executable files (*.exe)|*.exe" };
        if (picker.ShowDialog(this) == true) { ReportPresentMonPath.Text = picker.FileName; _settings.PresentMonPath = picker.FileName; }
    }
    private void ClearReportPresentMon_Click(object sender, RoutedEventArgs e) { ReportPresentMonPath.Clear(); _settings.PresentMonPath = ""; }

    private void AddMarker_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceLogger is null) return;
        var note = MarkerNoteBox.Text?.Trim();
        _performanceLogger.AddMarker(string.IsNullOrWhiteSpace(note) ? null : note);
        MarkerNoteBox.Clear();
        AddActivity("Marker added: " + (string.IsNullOrWhiteSpace(note) ? "Marker" : note));
    }

    private void SetPerformanceContext()
    {
        var symptoms = PerformanceNotes.Text ?? "";
        if (!string.IsNullOrWhiteSpace(RetestChangeBox.Text))
            symptoms = (symptoms.TrimEnd() + Environment.NewLine + "Retest change (self-reported): " + RetestChangeBox.Text.Trim()).Trim();
        _performanceLogger?.SetContext(PerformanceSetup.Text, symptoms);
    }

    private void ViewPerformanceReport_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceReport is not null) ShowReportTimeline(_performanceReport, "Current performance report");
    }

    private void OpenPerformanceReport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Open saved performance report", Filter = "Reports (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try { ShowReportTimeline(File.ReadAllText(picker.FileName), Path.GetFileName(picker.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't open report", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShowReportTimeline(string text, string title)
    {
        if (!ReportTimeline.TryParse(text, out var parsed, out var error) || parsed is null)
        {
            MessageBox.Show(this, error + " You can still open or compare the full report text.", "Timeline unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        new PerformanceTimelineWindow(parsed, title) { Owner = this }.Show();
    }

    private void ChooseBaselineReport_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose baseline performance report", Filter = "Reports (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(picker.FileName);
            if (!text.Contains("# Performance recording", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The selected file does not look like a performance report.");
            _baselineReportPath = picker.FileName;
            BaselineReportPathBox.Text = picker.FileName;
            CompareCurrentBaselineButton.IsEnabled = _performanceReport is not null;
            PerformanceStatus.Text = "Baseline selected. Keep the scene comparable and describe the one setting changed for the retest.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't read baseline report", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ClearBaselineReport_Click(object sender, RoutedEventArgs e)
    {
        _baselineReportPath = null;
        BaselineReportPathBox.Clear();
        RetestChangeBox.Clear();
        CompareCurrentBaselineButton.IsEnabled = false;
        PerformanceStatus.Text = "Baseline cleared. Ready for a normal performance log.";
    }

    private void CompareCurrentBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceReport is null || string.IsNullOrWhiteSpace(_baselineReportPath)) return;
        try
        {
            var baseline = File.ReadAllText(_baselineReportPath);
            var comparison = BuildComparison(baseline, _performanceReport, Path.GetFileName(_baselineReportPath), "Current retest");
            ShowComparison(comparison, "Baseline comparison");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't compare reports", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ShowComparison(string comparison, string title)
    {
        var window = new Window { Title = title, Owner = this, Width = 760, Height = 620, MinWidth = 520, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 33, 37)), Foreground = System.Windows.Media.Brushes.White };
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        var box = new System.Windows.Controls.TextBox { Text = comparison, IsReadOnly = true, AcceptsReturn = true, TextWrapping = System.Windows.TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 42, 47)), Foreground = System.Windows.Media.Brushes.White };
        var save = new System.Windows.Controls.Button { Content = "Save comparison…", HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        save.Click += (_, _) => { var picker = new Microsoft.Win32.SaveFileDialog { Title = "Save comparison", Filter = "Text report (*.txt)|*.txt|Markdown report (*.md)|*.md", FileName = $"Meh-Tools-comparison-{DateTime.Now:yyyy-MM-dd-HHmm}.txt" }; if (picker.ShowDialog(window) == true) try { File.WriteAllText(picker.FileName, comparison, new UTF8Encoding(false)); } catch (Exception ex) { MessageBox.Show(window, ex.Message, "Couldn't save comparison", MessageBoxButton.OK, MessageBoxImage.Warning); } };
        grid.Children.Add(box); System.Windows.Controls.Grid.SetRow(save, 1); grid.Children.Add(save); window.Content = grid; window.Show();
    }

    private void FinishPerformanceDiagnosticSession()
    {
        var id = _performanceDiagnosticSessionId;
        if (id is null) return;
        try { _sessionStore?.End(id.Value, DateTime.UtcNow, SessionStatus.Completed); }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't finish diagnostic session: " + ex.Message; }
        _performanceDiagnosticSessionId = null;
        RefreshSessionList();
    }

    private void ComparePerformance_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var a = new Microsoft.Win32.OpenFileDialog { Title = "Choose first (baseline) report", Filter = "Reports (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*" };
            if (a.ShowDialog(this) != true) return;
            var b = new Microsoft.Win32.OpenFileDialog { Title = "Choose second (comparison) report", Filter = "Reports (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*" };
            if (b.ShowDialog(this) != true) return;
            var ta = File.ReadAllText(a.FileName);
            var tb = File.ReadAllText(b.FileName);
            var comparison = BuildComparison(ta, tb, Path.GetFileName(a.FileName), Path.GetFileName(b.FileName));
            var outPath = Path.Combine(Path.GetDirectoryName(b.FileName) ?? _dataDirectory, $"Meh-Tools-comparison-{DateTime.Now:yyyy-MM-dd-HHmm}.txt");
            File.WriteAllText(outPath, comparison, new UTF8Encoding(false));
            MessageBox.Show(this, "Comparison saved:\n" + outPath, "Reports compared", MessageBoxButton.OK, MessageBoxImage.Information);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(outPath) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't compare reports", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string BuildComparison(string a, string b, string nameA, string nameB)
    {
        var ja = ExtractMachineSummary(a);
        var jb = ExtractMachineSummary(b);
        var sb = new StringBuilder();
        sb.AppendLine("# Performance report comparison");
        sb.AppendLine($"- Baseline: {nameA}");
        sb.AppendLine($"- Comparison: {nameB}");
        sb.AppendLine($"- Source: {(ja is not null && jb is not null ? "machine summary (JSON)" : "parsed report text")}");
        sb.AppendLine().AppendLine("## Metric deltas (comparison - baseline, averages where available)");
        var summaryA = ReportTimeline.ReadSummary(a);
        var summaryB = ReportTimeline.ReadSummary(b);
        var compatibility = ReportComparison.Assess(summaryA, summaryB);
        sb.AppendLine($"- Baseline duration / start: {summaryA.DurationSeconds?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} s / {summaryA.StartedUtc ?? "unavailable"}; frame capture: {(summaryA.FrameCount is > 0 ? summaryA.FrameCount + " frames" : "unavailable")}");
        sb.AppendLine($"- Retest duration / start: {summaryB.DurationSeconds?.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable"} s / {summaryB.StartedUtc ?? "unavailable"}; frame capture: {(summaryB.FrameCount is > 0 ? summaryB.FrameCount + " frames" : "unavailable")}");
        sb.AppendLine($"- Baseline scene/settings: {summaryA.Context ?? "unavailable"}");
        sb.AppendLine($"- Retest scene/settings: {summaryB.Context ?? "unavailable"}");
        sb.AppendLine($"- Baseline symptoms / retest change: {summaryA.Symptoms ?? "unavailable"}");
        sb.AppendLine($"- Retest symptoms / changed setting: {summaryB.Symptoms ?? "unavailable"}");
        if (!compatibility.WorkloadContextMatches) sb.AppendLine("- Workload/context differs or is incomplete; metric changes cannot establish an improvement.");
        void Row(string label, string jsonKey, string textKey)
        {
            var va = ja is not null ? GetJsonDouble(ja, jsonKey) : ExtractAvg(a, textKey);
            var vb = jb is not null ? GetJsonDouble(jb, jsonKey) : ExtractAvg(b, textKey);
            if (va is double da && vb is double db)
                sb.AppendLine($"- {label}: {da:F2} -> {db:F2} (Δ {db - da:+0.00;-0.00;0.00})");
            else if (va is not null || vb is not null)
                sb.AppendLine($"- {label}: {(va?.ToString("F2") ?? "n/a")} -> {(vb?.ToString("F2") ?? "n/a")}");
        }
        Row("Total CPU (%)", "cpuAvg", "Total CPU:");
        Row("Physical memory used (MiB)", "memUsedAvgMiB", "Physical memory used:");
        Row("GPU utilization (%)", "gpuAvg", "GPU utilization:");
        Row("GPU power (W)", "gpuPowerAvgW", "GPU power:");
        Row("GPU temperature (°C)", "gpuTempAvgC", "GPU temperature:");
        Row("Selected process CPU (%)", "gameCpuAvg", "Selected processes CPU");
        Row("Selected process memory (MiB)", "gameMemAvgMiB", "Selected processes working set:");
        Row("Selected process private allocation (MiB)", "gamePrivateAvgMiB", "Selected processes private bytes:");
        if (compatibility.FpsMethodsCompatible) Row("FPS", "fpsAvg", "FPS (from frame intervals):");
        else sb.AppendLine($"- FPS: not compared because averaging methods differ ({compatibility.BaselineFpsMethod} versus {compatibility.RetestFpsMethod}).");
        Row("Frame time (ms)", "frameMsAvg", "Frame time:");
        Row("P99 frame time (ms)", "frameP99Ms", "No legacy P99 metric");
        Row("Display latency (ms)", "displayLatencyAvgMs", "Display latency (PresentMon):");
        sb.AppendLine().AppendLine("## Notes");
        sb.AppendLine("- Deltas are computed from reported average values when present (preferring embedded machine summaries). Different workloads, durations, or missing measurements can make comparisons misleading.");
        sb.AppendLine("- Format-2 frame summaries select the presentation stream with most frames. Older reports combined streams; check capture context before interpreting frame-time changes.");
        return sb.ToString();
    }

    private static System.Text.Json.JsonElement? ExtractMachineSummary(string text)
    {
        const string marker = "## Machine summary (JSON)";
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = text.IndexOf("```json", i, StringComparison.OrdinalIgnoreCase);
        var end = start >= 0 ? text.IndexOf("```", start + 7, StringComparison.Ordinal) : -1;
        if (start < 0 || end < 0) return null;
        var json = text[(start + 7)..end].Trim();
        try { using var doc = System.Text.Json.JsonDocument.Parse(json); return doc.RootElement.Clone(); } catch { return null; }
    }

    private static double? GetJsonDouble(System.Text.Json.JsonElement? root, string name)
    {
        if (root is not { } r || !r.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == System.Text.Json.JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
        return null;
    }

    private static double? ExtractAvg(string text, string key)
    {
        var i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, i);
        var line = lineEnd < 0 ? text[i..] : text[i..lineEnd];
        // format: label: min / avg / max unit (n readings)
        var parts = line.Split('/');
        if (parts.Length < 2) return null;
        var avgPart = parts[1].Trim();
        var num = new string(avgPart.TakeWhile(c => char.IsDigit(c) || c == '.' || c == '-' || c == ',').ToArray());
        if (double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
        if (double.TryParse(num.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
        return null;
    }
}
