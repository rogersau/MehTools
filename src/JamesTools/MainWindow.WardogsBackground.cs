using System.Windows;
using System.Windows.Threading;
using JamesTools.Core.Scanning;
using JamesTools.Core.Sessions;
using JamesTools.Core.Configuration;

namespace JamesTools;

public partial class MainWindow
{
    private readonly DispatcherTimer _wardogsBackgroundTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
    private bool? _wardogsGameDetected;
    private CancellationTokenSource? _wardogsBackgroundCancel;
    private Task? _wardogsBackgroundTask;
    private DateTime _nextWardogsRecordingRecovery;

    private void InitializeWardogsBackground()
    {
        _wardogsBackgroundTimer.Tick += (_, _) => UpdateWardogsBackground();
        WardogsBackgroundOption.IsChecked = _appliedSettings.BackgroundWardogsOcr;
        UpdateWardogsBackground();
    }

    private void ToggleWardogsBackground_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = _appliedSettings.Snapshot();
            settings.BackgroundWardogsOcr = WardogsBackgroundOption.IsChecked == true;
            _store.Save(settings);
            _settings.BackgroundWardogsOcr = _appliedSettings.BackgroundWardogsOcr = settings.BackgroundWardogsOcr;
            _wardogsGameDetected = null;
            _nextWardogsRecordingRecovery = DateTime.MinValue;
            _automation.UpdateSettings(settings);
            UpdateWardogsBackground();
        }
        catch (Exception ex)
        {
            WardogsBackgroundOption.IsChecked = _appliedSettings.BackgroundWardogsOcr;
            ShowWardogsBackgroundStatus("Couldn't save background scan settings. " + ex.Message);
        }
    }

    private void ShowWardogsBackgroundStatus(string? message)
    {
        WardogsBackgroundStatus.Text = message ?? "";
        WardogsBackgroundStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateWardogsBackground()
    {
        if (_exiting) return;
        if (!_appliedSettings.BackgroundWardogsOcr)
        {
            _wardogsBackgroundTimer.Stop();
            _wardogsBackgroundCancel?.Cancel();
            ShowWardogsBackgroundStatus(null);
            return;
        }
        _wardogsBackgroundTimer.Start();
        if (_wardogsGameDetected != false || _obsRecordingOutputActive)
        {
            _wardogsBackgroundCancel?.Cancel();
            ShowWardogsBackgroundStatus(_wardogsGameDetected switch
            {
                true => "Background scanning paused while a game is detected.",
                null => "Waiting for game detection before scanning…",
                _ => "Background scanning paused while OBS is recording."
            });
            return;
        }
        if (_sessionStore is null)
        {
            ShowWardogsBackgroundStatus("Session history is unavailable.");
            return;
        }
        if (_wardogsBackgroundTask is { IsCompleted: false }) return;
        _wardogsBackgroundTask = ScanOneWardogsRecordingAsync(_sessionStore, _appliedSettings.Snapshot());
    }

    private async Task ScanOneWardogsRecordingAsync(SessionLibraryStore sessions, AppSettings settings)
    {
        using var cancel = new CancellationTokenSource();
        _wardogsBackgroundCancel = cancel;
        var token = cancel.Token;
        try
        {
            var store = WardogsStore();
            await store.InitializeAsync();
            var now = DateTime.UtcNow;
            if (now >= _nextWardogsRecordingRecovery)
            {
                _nextWardogsRecordingRecovery = now.AddSeconds(30);
                int linked = await Task.Run(() => WardogsRecordingLinks.Recover(sessions, settings, now, token), token);
                if (linked > 0 && !_exiting) RefreshSessionList();
            }
            var scan = await Task.Run(() => store.NextBackgroundScanAsync(sessions.Snapshot(), settings.MediaToolsFolder, token), token);
            token.ThrowIfCancellationRequested();
            if (scan is null)
            {
                ShowWardogsBackgroundStatus("Waiting for an unscanned WARDOGS recording in Sessions.");
                return;
            }
            await using (WindowsWardogsOcr.Create()) { }
            var fileName = Path.GetFileName(scan.Source);
            ShowWardogsBackgroundStatus($"Preparing background scan of {fileName}…");
            var progress = new Progress<WardogsProgress>(sample =>
            {
                if (!ReferenceEquals(_wardogsBackgroundCancel, cancel) || token.IsCancellationRequested) return;
                var percent = (int)Math.Min(100, sample.Seconds / sample.Duration * 100);
                var eta = sample.EstimatedRemaining is { Ticks: > 0 } remaining
                    ? $" · about {WardogsReviewWindow.FormatRemaining(remaining)} left" : "";
                ShowWardogsBackgroundStatus($"Scanning {fileName} in the background · {percent}%{eta}");
            });
            await store.RunAsync(scan.Id, progress, token);
            if (_exiting) return;
            if (_wardogsWindow is { } window) await window.RefreshFromBackgroundAsync();
            var finished = (await store.SnapshotAsync()).Scans.FirstOrDefault(item => item.Id == scan.Id);
            if (token.IsCancellationRequested) return;
            var session = (SessionsList.SelectedItem as SessionListEntry)?.Session;
            _ = ShowSessionWardogsMoneyAsync(session);
            if (finished?.Status == ScanStatus.Completed) ShowWardogsBackgroundStatus($"Background scan finished for {fileName}.");
            if (finished?.Status == ScanStatus.Failed) ShowWardogsBackgroundStatus($"Couldn't scan {fileName}. {finished.Error}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) ShowWardogsBackgroundStatus("Couldn't run background OCR. " + ex.Message); }
        finally { _wardogsBackgroundCancel = null; }
    }

    private async Task StopWardogsBackgroundAsync()
    {
        _wardogsBackgroundTimer.Stop();
        _wardogsBackgroundCancel?.Cancel();
        if (_wardogsBackgroundTask is { } task) await task;
    }
}
