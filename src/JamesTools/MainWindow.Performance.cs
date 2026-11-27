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
    private string? _performanceReportPath;
    private bool _performanceBusy;
    private DateTime _performanceStarted;
    private DispatcherTimer? _performanceTimer;
    private Guid? _performanceSessionId;
    private Guid? _performanceDiagnosticSessionId;
    private string? _baselineReportPath;
    private string? _performanceTargetNotice;

    private void BrowsePresentMon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose the PresentMon program", Filter = "Executable files (*.exe)|*.exe" };
        if (picker.ShowDialog(this) == true) PresentMonPath.Text = picker.FileName;
    }

    private void ClearPresentMon_Click(object sender, RoutedEventArgs e) => PresentMonPath.Clear();

    private GameRule? SelectedGame(System.Windows.Controls.ComboBox picker) =>
        picker.SelectedItem as GameRule ?? (_settings.Games.Count == 1 ? _settings.Games[0] : null);

    private void InitializeDiagnosticConditions()
    {
        var fields = new List<System.Windows.Controls.StackPanel>();
        foreach (var property in typeof(RecordingConditions).GetProperties())
        {
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            var label = System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z])([A-Z])", "$1 $2");
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) });
            var box = new System.Windows.Controls.TextBox { MaxLength = 2000 };
            box.SetBinding(System.Windows.Controls.TextBox.TextProperty, new System.Windows.Data.Binding("Diagnostics.Conditions." + property.Name) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged });
            panel.Children.Add(box); fields.Add(panel);
        }
        DiagnosticConditionsFields.ItemsSource = fields;
    }

    private async void StartPerformance_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceBusy || _performanceLogger is not null) return;
        if (!string.IsNullOrWhiteSpace(_baselineReportPath) && string.IsNullOrWhiteSpace(PerformanceSetup.Text))
        {
            PerformanceStatus.Text = "Record the retest game settings and test scene before starting.";
            RevealEditor(PerformanceSetup);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_baselineReportPath) && string.IsNullOrWhiteSpace(RetestChangeBox.Text))
        {
            PerformanceStatus.Text = "Describe the one setting changed for this retest before starting.";
            RevealEditor(RetestChangeBox);
            return;
        }
        if (!string.IsNullOrWhiteSpace(_baselineReportPath) && !File.Exists(_baselineReportPath))
        {
            PerformanceStatus.Text = "The selected baseline report is missing. Choose it again before starting the retest.";
            return;
        }
        if (_performanceReport is not null && _performanceReportPath is null &&
            MessageBox.Show(this, "Your last report isn't saved. Start a new log anyway?", "Unsaved report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _performanceBusy = true;
        StartPerformanceButton.IsEnabled = false;
        PerformanceGame.IsEnabled = false;
        try
        {
            PerformanceStatus.Text = "Starting…";
            var game = SelectedGame(PerformanceGame)?.Copy();
            var presentMon = PresentMonLocator.Resolve(_settings.PresentMonPath);
            string? targetPath = null;
            _performanceTargetNotice = null;
            if (game is not null)
            {
                var processes = await Task.Run(ProcessSnapshot.Capture);
                targetPath = AutomationPolicy.ResolveObservedTarget(game, processes, out var targetNotice);
                _performanceTargetNotice = targetNotice;
            }
            _performanceLogger = await PerformanceLogger.StartAsync(targetPath, presentMon,
                targetPath is null && game is not null ? _performanceTargetNotice : null,
                NetworkProbeOptions.Create(_appliedSettings.NetworkDiagnosticsEnabled, _appliedSettings.NetworkTargets),
                _settings.Diagnostics);
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
            _performanceReportPath = null;
            PerformanceReportActions.Visibility = Visibility.Collapsed;
            SavePerformanceButton.IsEnabled = false;
            CopyPerformanceForAiButton.IsEnabled = false;
            ViewPerformanceReportButton.IsEnabled = false;
            CompareCurrentBaselineButton.IsEnabled = false;
            PerformanceGame.IsEnabled = false;
            AddMarkerButton.IsEnabled = true;
            _performanceStarted = DateTime.UtcNow;
            _performanceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _performanceTimer.Tick -= PerformanceTimer_Tick;
            _performanceTimer.Tick += PerformanceTimer_Tick;
            _performanceTimer.Start();
            StartPerformanceButton.Visibility = Visibility.Collapsed;
            StopPerformanceButton.Visibility = Visibility.Visible;
            PerformanceMarkerOptions.Visibility = Visibility.Visible;
            StopPerformanceButton.IsEnabled = true;
            UpdateTrayMenuText();
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

    private async void StopPerformance_Click(object sender, RoutedEventArgs e) => await StopPerformanceAsync();

    private async Task<bool> StopPerformanceAsync()
    {
        if (_performanceBusy || _performanceLogger is null) return false;
        _performanceBusy = true;
        StopPerformanceButton.IsEnabled = false;
        PerformanceStatus.Text = "Creating report…";
        try
        {
            SetPerformanceContext();
            var report = await _performanceLogger.StopAsync();
            var localReport = _performanceLogger.RawExportError is null && _performanceLogger.RawDataDirectory is { } directory
                ? Path.Combine(directory, "report.md") : null;
            await _performanceLogger.DisposeAsync();
            _performanceLogger = null;
            _performanceTimer?.Stop();
            _performanceReport = report;
            FinishPerformanceDiagnosticSession();
            SavePerformanceButton.IsEnabled = true;
            CopyPerformanceForAiButton.IsEnabled = true;
            ViewPerformanceReportButton.IsEnabled = true;
            UpdateTrayMenuText();
            CompareCurrentBaselineButton.IsEnabled = !string.IsNullOrWhiteSpace(_baselineReportPath);
            StartPerformanceButton.IsEnabled = true;
            PerformanceGame.IsEnabled = true;
            AddMarkerButton.IsEnabled = false;
            StartPerformanceButton.Visibility = Visibility.Visible;
            StopPerformanceButton.Visibility = Visibility.Collapsed;
            PerformanceMarkerOptions.Visibility = Visibility.Collapsed;
            PerformanceReportActions.Visibility = Visibility.Visible;
            OpenLatestReportButton.IsEnabled = false;
            try
            {
                if (localReport is null || !File.Exists(localReport))
                {
                    var diagnostics = Path.Combine(_dataDirectory, "Diagnostics");
                    Directory.CreateDirectory(diagnostics);
                    localReport = Path.Combine(diagnostics, $"Meh-Tools-report-{DateTime.Now:yyyy-MM-dd-HHmmss}-{Guid.NewGuid():N}.md");
                    File.WriteAllText(localReport, report, new UTF8Encoding(false));
                }
                RegisterSavedPerformanceReport(localReport);
                PerformanceStatus.Text = "Report saved locally.";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                PerformanceStatus.Text = "Couldn't save the report automatically. Export a copy to keep it. " + ex.Message;
                return false;
            }
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

    private void SavePerformance_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceReport is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export performance report", Filter = "Text report (*.txt)|*.txt|Markdown report (*.md)|*.md", FileName = $"Meh-Tools-report-{DateTime.Now:yyyy-MM-dd-HHmm}.txt", DefaultExt = ".txt" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(picker.FileName, _performanceReport, new UTF8Encoding(false));
            if (_performanceReportPath is null || !File.Exists(_performanceReportPath)) RegisterSavedPerformanceReport(picker.FileName);
            PerformanceStatus.Text = "Report exported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Couldn't save report", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RegisterSavedPerformanceReport(string path)
    {
        _performanceReportPath = path;
        OpenLatestReportButton.IsEnabled = true;
        AddSessionAttachment(_performanceSessionId, SessionAttachmentKind.PerformanceReport, path, "Unassigned diagnostic report");
    }

    private void OpenLatestReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!File.Exists(_performanceReportPath)) throw new FileNotFoundException("The report is missing. Export a copy to save it again.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_performanceReportPath!) { UseShellExecute = true });
        }
        catch (Exception ex) { PerformanceStatus.Text = "Couldn't open the report. " + ex.Message; }
    }

    private async Task<bool> PreparePerformanceForExitAsync()
    {
        if (_performanceBusy) return false;
        if (_performanceLogger is not null)
        {
            var answer = MessageBox.Show(this, "A performance log is running. Stop and save it before quitting?", "Performance log", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return false;
            if (answer == MessageBoxResult.Yes) return await StopPerformanceAsync();
            await _performanceLogger.DisposeAsync();
            _performanceLogger = null;
            FinishPerformanceDiagnosticSession();
            return true;
        }
        if (_performanceReport is not null && _performanceReportPath is null &&
            MessageBox.Show(this, "Your report hasn't been saved. Quit without saving it?", "Unsaved report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        return true;
    }

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

    private void CopyPerformanceForAi_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_performanceReport)) return;
        try
        {
            System.Windows.Clipboard.SetText(_performanceReport);
            PerformanceStatus.Text = "Report copied to clipboard — ready to paste into an AI chat.";
            StatusNotice.Text = "Performance report copied to clipboard.";
        }
        catch (Exception ex) { PerformanceStatus.Text = "Couldn't copy report: " + ex.Message; }
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
            RetestOptions.Visibility = Visibility.Visible;
            ClearBaselineButton.IsEnabled = true;
            CompareCurrentBaselineButton.IsEnabled = _performanceReport is not null;
            PerformanceStatus.Text = "Baseline selected. Keep the scene comparable and describe the one setting changed for the retest.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Couldn't read baseline report", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void ClearBaselineReport_Click(object sender, RoutedEventArgs e)
    {
        _baselineReportPath = null;
        BaselineReportPathBox.Text = "";
        RetestChangeBox.Clear();
        RetestOptions.Visibility = Visibility.Collapsed;
        ClearBaselineButton.IsEnabled = false;
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
        var window = new Window { Title = title, Owner = this, Width = 760, Height = 620, MinWidth = 520, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        WindowTheme.Apply(window);
        var grid = new System.Windows.Controls.Grid { Margin = new Thickness(12) };
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        var box = new System.Windows.Controls.TextBox { Text = comparison, IsReadOnly = true, AcceptsReturn = true, TextWrapping = System.Windows.TextWrapping.Wrap, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
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
        return DiagnosticComparison.Build(a, b, nameA, nameB);
    }

}
