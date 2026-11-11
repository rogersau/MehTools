using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
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

    private async void StartPerformance_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceBusy || _performanceLogger is not null) return;
        if (!_performanceReportSaved && MessageBox.Show(this, "Your last report isn't saved. Start a new log anyway?", "Unsaved report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _performanceBusy = true;
        StartPerformanceButton.IsEnabled = false;
        try
        {
            PerformanceStatus.Text = "Starting…";
            var game = PerformanceGame.SelectedItem as GameRule;
            _performanceLogger = await PerformanceLogger.StartAsync(game?.ExecutablePath);
            _performanceReport = null;
            _performanceReportSaved = true;
            SavePerformanceButton.IsEnabled = false;
            PerformanceGame.IsEnabled = false;
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
        }
        finally { _performanceBusy = false; }
    }

    private void PerformanceTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _performanceStarted;
        PerformanceStatus.Text = $"Logging ({(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}). Play until the problem happens.";
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
            var report = await _performanceLogger.StopAsync();
            await _performanceLogger.DisposeAsync();
            _performanceLogger = null;
            _performanceTimer?.Stop();
            var notes = PerformanceNotes.Text.Trim();
            if (notes.Length > 4000) notes = notes[..4000];
            _performanceReport = string.IsNullOrWhiteSpace(notes) ? report : report + "\n\n## Player notes\n\n" + notes + "\n";
            _performanceReportSaved = false;
            SavePerformanceButton.IsEnabled = true;
            StartPerformanceButton.IsEnabled = true;
            PerformanceGame.IsEnabled = true;
            PerformanceStatus.Text = "Report ready. Click Save.";
            return !save || SavePerformanceReport();
        }
        catch (Exception ex)
        {
            PerformanceStatus.Text = "Couldn't create the report.";
            StopPerformanceButton.IsEnabled = _performanceLogger is not null;
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
            return true;
        }
        if (!_performanceReportSaved && MessageBox.Show(this, "Your report hasn't been saved. Quit without saving it?", "Unsaved report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        return true;
    }
}
