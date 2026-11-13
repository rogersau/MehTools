using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JamesTools.Core.Configuration;
using JamesTools.Core.Recording;
using JamesTools.Core.Sessions;
using JamesTools.Core.Storage;

namespace JamesTools;

public partial class MainWindow
{
    private readonly DispatcherTimer _obsPollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ObsWebSocketClient? _obsClient;
    private string? _lastObsRecordingPath;
    private string? _lastObsClipPath;
    private Guid? _obsRecordingSessionId;
    private bool _obsRecordingOwnerCaptured;
    private Guid? _obsReplaySessionId;
    private bool _obsReplayOwnerCaptured;
    private bool _obsPollInProgress;
    private bool _obsCommandInProgress;
    private int _storageRefreshVersion;
    private int _cleanupRefreshVersion;
    private CleanupPreview? _cleanupPreview;

    private void InitializeRecordingPanel()
    {
        _obsPollTimer.Tick += ObsPollTimer_Tick;
        ObsConnectionStatus.Text = "Not connected";
        ObsOutputState.Text = "Unknown";
        ObsReplayState.Text = "Unknown";
        RefreshRecordingStorageTargets();
        UpdateObsControls();
        _ = RefreshStorageVisibilityAsync();
    }

    private void RefreshRecordingStorageTargets()
    {
        if (RecordingStorageTarget is null) return;
        var selected = RecordingStorageTarget.SelectedItem as RecordingStorageTargetOption;
        var selectedPath = selected?.Game?.ExecutablePath;
        var options = new List<RecordingStorageTargetOption> { new(null, "Global recording folder") };
        options.AddRange(_settings.Games.Select(game => new RecordingStorageTargetOption(game, game.Name)));
        RecordingStorageTarget.ItemsSource = options;
        RecordingStorageTarget.SelectedItem = options.FirstOrDefault(o => string.Equals(o.Game?.ExecutablePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? options[0];
    }

    private void RecordingStorageTarget_SelectionChanged(object sender, SelectionChangedEventArgs e) => _ = RefreshStorageVisibilityAsync();
    private async void RefreshStorageVisibility_Click(object sender, RoutedEventArgs e)
    {
        RefreshRecordingStorageTargets();
        await RefreshStorageVisibilityAsync();
    }

    private void OrganiseCapturesBox_Changed(object sender, RoutedEventArgs e) => _ = RefreshStorageVisibilityAsync();

    private async Task RefreshStorageVisibilityAsync()
    {
        if (_exiting || StorageBasePath is null) return;
        var version = ++_storageRefreshVersion;
        var game = (RecordingStorageTarget.SelectedItem as RecordingStorageTargetOption)?.Game;
        var basePath = game is null ? _settings.RecordingDirectory : EffectiveGameOptionsResolver.Resolve(_settings, game).RecordingDirectory;
        RecordingFolderPlan? plan = null;
        try { if (!string.IsNullOrWhiteSpace(basePath)) plan = RecordingFolderPlanner.Plan(basePath, game?.Name, DateTime.Now, _settings.OrganiseCapturesByGameAndDate); }
        catch (Exception ex) { StorageCapacityText.Text = "Space: unknown — " + ex.Message; }
        StorageBasePath.Text = string.IsNullOrWhiteSpace(basePath) ? "No recording folder is configured." : basePath;
        StoragePlannedPath.Text = plan?.PlannedDirectory ?? "No planned folder is available.";
        if (plan is null) { StorageCapacityText.Text = "Space: unknown — choose a valid absolute recording folder."; return; }
        StorageCapacityText.Text = "Checking configured OBS storage…";
        var result = await Task.Run(() => new FileSystemStorageCapacityProvider().Query(plan.PlannedDirectory));
        if (_exiting || version != _storageRefreshVersion) return;
        StorageCapacityText.Text = result.Capacity is { } capacity
            ? $"Space on configured OBS storage: {FormatStorageBytes(capacity.AvailableBytes)} free of {FormatStorageBytes(capacity.TotalBytes)} (checked at {capacity.QueriedPath})."
            : "Space: unknown — " + (result.Reason ?? "capacity unavailable");
    }

    private static string FormatStorageBytes(long bytes) => $"{bytes / 1073741824d:0.0} GiB";

    private async void RefreshCleanupPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting) return;
        if (_sessionStore is null) { CleanupPreviewSummary.Text = "Session history is unavailable."; return; }
        var version = ++_cleanupRefreshVersion;
        _cleanupPreview = null;
        CleanupPreviewGrid.ItemsSource = null;
        try
        {
            var sessions = _sessionStore.Snapshot();
            var roots = RecordingRoots(_settings);
            var now = DateTime.UtcNow;
            var ageDays = _settings.CleanupSuggestionAgeDays;
            var budget = _settings.CleanupSuggestionBudgetGiB;
            CleanupPreviewSummary.Text = "Scanning referenced media metadata…";
            var preview = await Task.Run(() => CleanupPreviewBuilder.Build(sessions, roots, now, ageDays, budget));
            if (_exiting || version != _cleanupRefreshVersion) return;
            _cleanupPreview = preview;
            CleanupPreviewGrid.ItemsSource = preview.Entries.Select(e => new CleanupPreviewRow(e)).ToArray();
            var reclaimable = preview.PotentialReclaimableBytes is long size ? FormatStorageBytes(size) : "unknown";
            CleanupPreviewSummary.Text = $"Suggestions only · {preview.Entries.Count(e => e.Candidate)} candidate(s) · potential reclaimable: {reclaimable}. Refresh to re-evaluate; nothing is removed.";
        }
        catch (Exception ex)
        {
            if (!_exiting && version == _cleanupRefreshVersion)
                CleanupPreviewSummary.Text = "Couldn't refresh cleanup suggestions: " + ex.Message;
        }
    }

    private static IReadOnlyList<string> RecordingRoots(AppSettings settings) => settings.Games.Select(game => EffectiveGameOptionsResolver.Resolve(settings, game).RecordingDirectory)
        .Concat(settings.Profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.RecordingDirectory)).Select(profile => profile.RecordingDirectory!))
        .Append(settings.RecordingDirectory).Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void ExportCleanupPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupPreview is null) { CleanupPreviewSummary.Text = "Refresh the preview before exporting it."; return; }
        var picker = new Microsoft.Win32.SaveFileDialog { Title = "Export cleanup suggestions", Filter = "Text file (*.txt)|*.txt", FileName = "MehTools-cleanup-preview.txt" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, _cleanupPreview.ToText(), new System.Text.UTF8Encoding(false)); CleanupPreviewSummary.Text = "Preview exported. No recordings were changed."; }
        catch (Exception ex) { CleanupPreviewSummary.Text = "Couldn't export preview: " + ex.Message; }
    }

    private void OpenCleanupFolder_Click(object sender, RoutedEventArgs e)
    {
        if (CleanupPreviewGrid.SelectedItem is not CleanupPreviewRow row) return;
        var folder = Path.GetDirectoryName(row.Path);
        if (folder is null || !Directory.Exists(folder)) { CleanupPreviewSummary.Text = "The containing folder is unavailable."; return; }
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { CleanupPreviewSummary.Text = "Couldn't open folder: " + ex.Message; }
    }

    private sealed record RecordingStorageTargetOption(GameRule? Game, string Label);
    private sealed record CleanupPreviewRow(CleanupPreviewEntry Entry)
    {
        public string Path => Entry.Path;
        public int? AgeDays => Entry.AgeDays;
        public string SizeLabel => Entry.SizeBytes is long size ? FormatStorageBytes(size) : "unknown";
        public string Reason => Entry.Reason;
        public string Status => Entry.Candidate ? "Suggested" : "Protected / excluded";
    }

    private async void ObsConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting || _obsClient is not null) return;
        if (_settings.ObsWebSocketPort is < 1 or > 65535)
        {
            ShowObsError("Enter a WebSocket port from 1 to 65535.");
            return;
        }

        var password = ObsWebSocketPasswordBox.Password;
        ObsWebSocketPasswordBox.Clear();
        ObsRemoteError.Visibility = Visibility.Collapsed;
        ObsConnectionStatus.Text = "Connecting to OBS on this PC…";
        ObsConnectButton.IsEnabled = false;
        var client = new ObsWebSocketClient();
        client.RecordStateChanged += ObsClient_RecordStateChanged;
        _obsClient = client;
        try
        {
            await client.ConnectAsync(_settings.ObsWebSocketPort, password);
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            ObsConnectionStatus.Text = $"Connected to OBS WebSocket on localhost:{_settings.ObsWebSocketPort}.";
            _obsPollTimer.Start();
            await PollObsStatusAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_obsClient, client))
            {
                _obsClient = null;
                client.RecordStateChanged -= ObsClient_RecordStateChanged;
                await client.DisposeAsync();
            }
            if (_obsClient is null && !_exiting)
            {
                ObsConnectionStatus.Text = "Not connected";
                ObsOutputState.Text = "Unknown";
                ShowObsError(ex.Message);
            }
        }
        finally
        {
            password = string.Empty;
            UpdateObsControls();
        }
    }

    private async void ObsDisconnect_Click(object sender, RoutedEventArgs e) => await DisconnectObsAsync();

    private async Task DisconnectObsAsync()
    {
        _obsPollTimer.Stop();
        var client = _obsClient;
        _obsClient = null;
        ObsConnectionStatus.Text = "Not connected";
        ObsOutputState.Text = "Unknown";
        ObsReplayState.Text = "Unknown";
        ObsDuration.Text = "—";
        ObsDataWritten.Text = "—";
        UpdateObsControls();
        if (client is not null)
        {
            _obsRecordingSessionId = null;
            _obsRecordingOwnerCaptured = false;
            _obsReplaySessionId = null;
            _obsReplayOwnerCaptured = false;
            client.RecordStateChanged -= ObsClient_RecordStateChanged;
            await client.DisposeAsync();
        }
        UpdateObsControls();
    }

    private async void ObsPollTimer_Tick(object? sender, EventArgs e) => await PollObsStatusAsync();

    private async Task PollObsStatusAsync()
    {
        var client = _obsClient;
        if (_exiting || _obsPollInProgress || client is null) return;
        _obsPollInProgress = true;
        try
        {
            var status = await client.GetRecordStatusAsync();
            var replayStatus = await client.GetReplayBufferStatusAsync();
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            ObsOutputState.Text = status.Active ? status.Paused ? "Paused" : "Recording" : "Stopped";
            ObsReplayState.Text = replayStatus.Active ? "Running" : "Stopped";
            if (status.Active && !_obsRecordingOwnerCaptured)
            {
                // A managed OBS launch has a known game owner. External OBS
                // remains unassigned, including later pause/resume events.
                var ownerPath = _automation.RecordingGamePath;
                _obsRecordingSessionId = string.IsNullOrWhiteSpace(ownerPath) ? null : _sessionTracker?.FindActive(ownerPath)?.Id;
                _obsRecordingOwnerCaptured = true;
            }
            if (replayStatus.Active && !_obsReplayOwnerCaptured)
            {
                var ownerPath = _automation.RecordingGamePath;
                if (!string.IsNullOrWhiteSpace(ownerPath)) _obsReplaySessionId = _sessionTracker?.FindActive(ownerPath)?.Id;
                _obsReplayOwnerCaptured = true;
            }
            else if (!replayStatus.Active)
            {
                _obsReplaySessionId = null;
                _obsReplayOwnerCaptured = false;
            }
            ObsDuration.Text = TimeSpan.FromMilliseconds(Math.Max(0, status.DurationMilliseconds)).ToString(status.DurationMilliseconds >= 3_600_000 ? "h\\:mm\\:ss" : "mm\\:ss");
            ObsDataWritten.Text = FormatBytes(status.Bytes);
            ObsConnectionStatus.Text = "Connected to OBS WebSocket on localhost.";
            ObsRemoteError.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_obsClient, client))
            {
                ShowObsError(ex.Message);
                await DisconnectObsAsync();
            }
        }
        finally
        {
            _obsPollInProgress = false;
            UpdateObsControls();
        }
    }

    private async void ObsClient_RecordStateChanged(object? sender, ObsRecordStateChangedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            if (_exiting || !ReferenceEquals(sender, _obsClient)) return;
            if (e.Active && !_obsRecordingOwnerCaptured)
            {
                var ownerPath = _automation.RecordingGamePath;
                _obsRecordingSessionId = string.IsNullOrWhiteSpace(ownerPath) ? null : _sessionTracker?.FindActive(ownerPath)?.Id;
                _obsRecordingOwnerCaptured = true;
            }
            else if (!e.Active)
            {
                if (!string.IsNullOrWhiteSpace(e.OutputPath)) SetLastObsRecording(e.OutputPath, _obsRecordingSessionId);
                _obsRecordingSessionId = null;
                _obsRecordingOwnerCaptured = false;
            }
            ObsOutputState.Text = e.Active ? e.Paused ? "Paused" : "Recording" : "Stopped";
            UpdateObsControls();
        });
    }

    private async void ObsStart_Click(object sender, RoutedEventArgs e) => await RunObsCommandAsync(client => client.StartRecordAsync());

    private async void ObsStop_Click(object sender, RoutedEventArgs e)
    {
        string? outputPath = null;
        var sessionId = _obsRecordingSessionId;
        await RunObsCommandAsync(async client => outputPath = await client.StopRecordAsync());
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            SetLastObsRecording(outputPath, sessionId);
            _obsRecordingSessionId = null;
            _obsRecordingOwnerCaptured = false;
        }
    }

    private async void ObsPause_Click(object sender, RoutedEventArgs e) => await RunObsCommandAsync(client => client.PauseRecordAsync());
    private async void ObsResume_Click(object sender, RoutedEventArgs e) => await RunObsCommandAsync(client => client.ResumeRecordAsync());

    private async void ObsStartReplay_Click(object sender, RoutedEventArgs e)
    {
        var ownerPath = _automation.RecordingGamePath;
        _obsReplaySessionId = !string.IsNullOrWhiteSpace(ownerPath)
            ? _sessionTracker?.FindActive(ownerPath)?.Id
            : null;
        _obsReplayOwnerCaptured = true;
        await RunObsCommandAsync(client => client.StartReplayBufferAsync());
    }
    private async void ObsStopReplay_Click(object sender, RoutedEventArgs e) => await RunObsCommandAsync(client => client.StopReplayBufferAsync());
    private async void ObsSaveClip_Click(object sender, RoutedEventArgs e) => await SaveClipAsync(false);

    private async Task RunObsCommandAsync(Func<ObsWebSocketClient, Task> command)
    {
        var client = _obsClient;
        if (_exiting || _obsCommandInProgress || client is null) return;
        _obsCommandInProgress = true;
        UpdateObsControls();
        try
        {
            await command(client);
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            while (_obsPollInProgress) await Task.Delay(25);
            await PollObsStatusAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_obsClient, client))
            {
                ShowObsError(ex.Message);
                if (!client.IsConnected) await DisconnectObsAsync();
            }
        }
        finally
        {
            _obsCommandInProgress = false;
            UpdateObsControls();
        }
    }

    private void SetLastObsRecording(string path, Guid? sessionId)
    {
        _lastObsRecordingPath = path;
        ObsLastRecording.Text = path;
        ObsOpenLastButton.IsEnabled = File.Exists(path);
        AddSessionAttachment(sessionId, SessionAttachmentKind.Recording, path, "Unassigned recording");
    }

    private void ObsOpenLast_Click(object sender, RoutedEventArgs e)
    {
        var path = _lastObsRecordingPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ObsOpenLastButton.IsEnabled = false;
            ShowObsError("The last reported recording file no longer exists.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowObsError("Couldn't open the recording: " + ex.Message); }
    }

    private async Task SaveClipAsync(bool fromHotkey)
    {
        var client = _obsClient;
        var sessionId = _obsReplaySessionId;
        if (_exiting || _obsCommandInProgress) return;
        if (client?.IsConnected != true)
        {
            const string message = "Connect to OBS on the Recording page before saving a clip.";
            ShowObsError(message);
            ObsConnectionStatus.Text = "Connection required to save clips.";
            if (fromHotkey && _settings.ShowNotifications) _tray.ShowBalloonTip(3000, "OBS connection required", message, System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        _obsCommandInProgress = true;
        UpdateObsControls();
        try
        {
            ObsConnectionStatus.Text = "Waiting for OBS to confirm the clip save…";
            var path = await client.SaveReplayBufferAsync();
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            SetLastObsClip(path, sessionId);
            ObsConnectionStatus.Text = "Clip saved: " + path;
            ObsRemoteError.Visibility = Visibility.Collapsed;
            if (_settings.ShowNotifications) _tray.ShowBalloonTip(4000, "Clip saved", path, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_obsClient, client))
            {
                ShowObsError(ex.Message);
                ObsConnectionStatus.Text = client.IsConnected ? "Connected to OBS; clip save failed." : "OBS connection lost.";
                if (!client.IsConnected) await DisconnectObsAsync();
            }
        }
        finally
        {
            _obsCommandInProgress = false;
            UpdateObsControls();
        }
    }

    private void SetLastObsClip(string path, Guid? sessionId)
    {
        _lastObsClipPath = path;
        ObsLastClip.Text = path;
        ObsOpenLastClipButton.IsEnabled = File.Exists(path);
        AddSessionAttachment(sessionId, SessionAttachmentKind.Clip, path, "Unassigned clip");
    }

    private void ObsOpenLastClip_Click(object sender, RoutedEventArgs e)
    {
        var path = _lastObsClipPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ObsOpenLastClipButton.IsEnabled = false;
            ShowObsError("The last confirmed clip file no longer exists.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowObsError("Couldn't open the clip: " + ex.Message); }
    }

    private void UpdateObsControls()
    {
        var connected = _obsClient?.IsConnected == true;
        var busy = _obsCommandInProgress;
        ObsConnectButton.IsEnabled = !_exiting && !busy && _obsClient is null;
        ObsDisconnectButton.IsEnabled = connected && !busy;
        var state = ObsOutputState.Text;
        ObsStartButton.IsEnabled = connected && !busy && state == "Stopped";
        ObsStopButton.IsEnabled = connected && !busy && state is "Recording" or "Paused";
        ObsPauseButton.IsEnabled = connected && !busy && state == "Recording";
        ObsResumeButton.IsEnabled = connected && !busy && state == "Paused";
        ObsStartReplayButton.IsEnabled = connected && !busy && ObsReplayState.Text == "Stopped";
        ObsStopReplayButton.IsEnabled = connected && !busy && ObsReplayState.Text == "Running";
        ObsSaveClipButton.IsEnabled = connected && !busy && ObsReplayState.Text == "Running";
        ObsOpenLastButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastObsRecordingPath) && File.Exists(_lastObsRecordingPath);
        ObsOpenLastClipButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastObsClipPath) && File.Exists(_lastObsClipPath);
    }

    private void ShowObsError(string message)
    {
        ObsRemoteError.Text = message;
        ObsRemoteError.Visibility = Visibility.Visible;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
