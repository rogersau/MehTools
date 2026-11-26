using System.Diagnostics;
using System.ComponentModel;
using System.Net.WebSockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Recording;
using JamesTools.Core.Sessions;
using JamesTools.Core.Storage;

namespace JamesTools;

public partial class MainWindow
{
    private const string ReplayBufferUnavailable = "Unavailable in OBS";
    private static readonly TimeSpan ObsReconnectInterval = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _obsPollTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RecordingHealthMonitor _recordingHealth = new();
    private readonly long _recordingHealthClockOrigin = Stopwatch.GetTimestamp();
    private long _obsConnectionGeneration;
    private long _automationLifecycleGeneration;
    private AutomationRecordingLifecycle? _latestAutomationLifecycle;
    private ObsWebSocketClient? _obsClient;
    private ObsConnection? _obsConnection;
    private TimeSpan _obsReconnectAt;
    private string? _lastObsRecordingPath;
    private string? _lastObsClipPath;
    private Guid? _obsRecordingSessionId;
    private bool _obsRecordingOwnerCaptured;
    private bool _obsRecordingOutputActive;
    private Guid? _obsReplaySessionId;
    private bool _obsReplayOwnerCaptured;
    private bool _obsPollInProgress;
    private bool _obsCommandInProgress;
    private int _storageRefreshVersion;
    private int _cleanupRefreshVersion;
    private CleanupPreview? _cleanupPreview;

    private sealed class ObsConnection(int port, string password, bool? rememberPassword = null)
    {
        public int Port { get; } = port;
        public string Password { get; } = password;
        public bool? RememberPassword { get; set; } = rememberPassword;
    }

    private void InitializeRecordingPanel()
    {
        InitializeBookmarks();
        _obsPollTimer.Tick += ObsPollTimer_Tick;
        ObsConnectionStatus.Text = "Not connected";
        ObsOutputState.Text = "Unknown";
        ObsReplayState.Text = "Unknown";
        ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
        RefreshRecordingStorageTargets();
        UpdateObsControls();
        _ = RefreshStorageVisibilityAsync();
    }

    private TimeSpan RecordingHealthNow() => Stopwatch.GetElapsedTime(_recordingHealthClockOrigin);

    private void ApplyRecordingHealthSettings()
    {
        var settings = _appliedSettings;
        // Keep malformed imported values editable; Save still reports the validation error.
        var snapshot = _recordingHealth.Configure(settings.RecordingHealthAlertsEnabled, Math.Clamp(settings.RecordingHealthStallSeconds, 10, 300), RecordingHealthNow());
        ApplyRecordingHealthSnapshot(snapshot);
    }

    private void AutomationRecordingLifecycleChanged(AutomationRecordingLifecycle lifecycle)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || lifecycle.Generation < _automationLifecycleGeneration) return;
            _automationLifecycleGeneration = lifecycle.Generation;
            ApplyAutomationRecordingLifecycle(lifecycle);
        });
    }

    private void ApplyAutomationRecordingLifecycle(AutomationRecordingLifecycle lifecycle)
    {
        var now = RecordingHealthNow();
        var newLaunch = _latestAutomationLifecycle?.LaunchId != lifecycle.LaunchId;
        _latestAutomationLifecycle = lifecycle;
        if (!lifecycle.IsOwned || lifecycle.IntentionalStop || newLaunch)
            _ = StopWardogsHighlightsAsync();
        if (lifecycle.RequestedMode == CaptureMode.WardogsHighlights && lifecycle.IsOwned && !lifecycle.IntentionalStop)
            ShowWardogsHighlights(_obsClient?.IsConnected == true ? "Waiting for the replay buffer…" : "Connect to OBS to save highlights.");
        else ShowWardogsHighlights(null);
        if (lifecycle.IsOwned && newLaunch)
            _recordingHealth.BeginRequest(lifecycle.LaunchId, lifecycle.RequestedMode, now);
        // A manual recording request can replace the launch request within the same OBS process.
        // Closing that managed process is intentional for whichever output is currently observed.
        if (lifecycle.IsOwned && lifecycle.IntentionalStop && _recordingHealth.CurrentRequestId is null)
            _recordingHealth.BeginRequest(lifecycle.LaunchId, lifecycle.RequestedMode, now);
        if (_recordingHealth.CurrentRequestId is not Guid requestId)
        {
            ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
            return;
        }
        if (lifecycle.IsOwned)
        {
            _recordingHealth.SetStopIntent(requestId, lifecycle.IntentionalStop, now);
        }
        else
        {
            _recordingHealth.EndRequest(requestId, lifecycle.IntentionalStop, now);
        }
        ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
    }

    private void ApplyRecordingHealthSnapshot(RecordingHealthSnapshot snapshot)
    {
        if (RecordingHealthStateText is null || RecordingHealthReasonText is null) return;
        RecordingHealthStateText.Text = snapshot.State switch
        {
            RecordingHealthState.NeedsObsConnection => "Needs OBS connection",
            RecordingHealthState.ConnectionLost => "Connection lost · health unknown",
            RecordingHealthState.WaitingForRecording => "Waiting for recording",
            RecordingHealthState.RecordingProgressing => "Recording progressing",
            _ => snapshot.State.ToString()
        };
        RecordingHealthReasonText.Text = snapshot.Reason;
        if (snapshot.Event is not { } healthEvent) return;
        AddActivity("Recording health: " + healthEvent.Reason);
        if (healthEvent.Kind != RecordingHealthEventKind.Alert) return;
        if (_appliedSettings.ShowNotifications)
            _tray.ShowBalloonTip(4000, "Recording health needs attention", healthEvent.Reason, System.Windows.Forms.ToolTipIcon.Warning);
        if (_appliedSettings.RecordingHealthAlertsEnabled && _appliedSettings.RecordingHealthSoundEnabled)
            System.Media.SystemSounds.Exclamation.Play();
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
        CleanupActions.Visibility = Visibility.Collapsed;
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
            CleanupActions.Visibility = Visibility.Visible;
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

    private void CleanupPreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupOpenSelected is not null) CleanupOpenSelected.IsEnabled = CleanupPreviewGrid.SelectedItem is CleanupPreviewRow;
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
        if (_exiting || _obsConnection is not null) return;
        if (!TryReadObsPort(out var port))
        {
            ShowObsError("Enter a WebSocket port from 1 to 65535.");
            return;
        }

        var password = ObsWebSocketPasswordBox.Password;
        if (password.Length == 0) password = ReadSavedObsPassword(port)?.Password ?? "";
        ObsWebSocketPasswordBox.Clear();
        await BeginObsConnectionAsync(new ObsConnection(port, password, ObsRememberPasswordBox.IsChecked == true));
    }

    private async Task ResumeSavedObsConnectionAsync()
    {
        if (_exiting || _obsConnection is not null || _appliedSettings.ObsWebSocketPort is < 1 or > 65535) return;
        var port = _appliedSettings.ObsWebSocketPort;
        if (ReadSavedObsPassword(port) is { AutoConnect: true } saved) await BeginObsConnectionAsync(new ObsConnection(port, saved.Password));
    }

    private Task BeginObsConnectionAsync(ObsConnection connection)
    {
        _obsConnection = connection;
        ObsRemoteError.Visibility = Visibility.Collapsed;
        ObsConnectionStatus.Text = "Connecting to OBS on this PC…";
        return OpenObsConnectionAsync(connection);
    }

    private bool TryReadObsPort(out int port)
    {
        port = _settings.ObsWebSocketPort;
        return port is >= 1 and <= 65535 && !Validation.GetHasError(ObsWebSocketPortBox);
    }

    private void ObsPort_Changed(object sender, TextChangedEventArgs e)
    {
        if (ObsCredentialStatus is null || _obsConnection is not null) return;
        if (int.TryParse(ObsWebSocketPortBox.Text, out var port) && port is >= 1 and <= 65535) ReadSavedObsPassword(port);
        else ShowSavedObsPassword(false);
    }

    private ObsPasswordStore.SavedPassword? ReadSavedObsPassword(int port)
    {
        try
        {
            var saved = ObsPasswordStore.Read(port);
            ShowSavedObsPassword(saved is not null);
            return saved;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            // Keep the delete action accessible for a malformed or unreadable entry.
            ShowSavedObsPassword(true, "Couldn't read the saved OBS password. " + ex.Message);
            return null;
        }
    }

    private void ShowSavedObsPassword(bool saved, string? message = null)
    {
        ObsForgetPasswordButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        ObsCredentialStatus.Text = message ?? (saved ? "OBS password saved on this PC." : "");
    }

    private void ObsForgetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (_obsConnection is not null || !TryReadObsPort(out var port)) return;
        try
        {
            ObsPasswordStore.Forget(port);
            ShowSavedObsPassword(false, "Saved OBS password removed.");
        }
        catch (Win32Exception ex) { ShowSavedObsPassword(true, "Couldn't remove the saved OBS password. " + ex.Message); }
    }

    private void SaveObsPasswordAfterAuthentication(ObsConnection connection, ObsWebSocketClient client)
    {
        if (connection.RememberPassword is not { } remember) return;
        connection.RememberPassword = null;
        try
        {
            if (remember && client.UsesAuthentication)
            {
                ObsPasswordStore.Save(connection.Port, connection.Password);
                ShowSavedObsPassword(true);
            }
            else if (!remember)
            {
                ObsPasswordStore.Forget(connection.Port);
                ShowSavedObsPassword(false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            ShowSavedObsPassword(true, "Couldn't update the saved OBS password. " + ex.Message);
        }
    }

    private void PauseSavedObsConnection()
    {
        if (_obsConnection is not { } connection) return;
        try { ObsPasswordStore.PauseAutoConnect(connection.Port); }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException or ArgumentException)
        {
            ShowSavedObsPassword(true, "Couldn't pause startup connection. " + ex.Message);
        }
    }

    private async Task OpenObsConnectionAsync(ObsConnection connection)
    {
        _obsPollTimer.Stop();
        var client = new ObsWebSocketClient();
        client.RecordStateChanged += ObsClient_RecordStateChanged;
        client.RecordFileChanged += ObsClient_RecordFileChanged;
        _obsClient = client;
        var connectionGeneration = ++_obsConnectionGeneration;
        UpdateObsControls();
        try
        {
            await client.ConnectAsync(connection.Port, connection.Password);
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            SaveObsPasswordAfterAuthentication(connection, client);
            _recordingHealth.Connected(connectionGeneration, RecordingHealthNow());
            ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
            ObsConnectionStatus.Text = "Connected to OBS on this PC.";
            await PollObsStatusAsync();
        }
        catch (Exception ex)
        {
            if (!_exiting && ReferenceEquals(_obsClient, client)) await HandleObsConnectionFailureAsync(ex);
        }
        finally
        {
            if (!_exiting && ReferenceEquals(_obsConnection, connection)) _obsPollTimer.Start();
            UpdateObsControls();
        }
    }

    private async void ObsDisconnect_Click(object sender, RoutedEventArgs e)
    {
        PauseSavedObsConnection();
        await DisconnectObsAsync();
    }

    private Task HandleObsConnectionFailureAsync(Exception error)
    {
        // OBS protocol errors include rejected passwords and sessions ended explicitly in OBS.
        bool reconnect = error is WebSocketException or TimeoutException or (IOException and not ObsWebSocketException);
        if (reconnect) ObsRemoteError.Visibility = Visibility.Collapsed;
        else
        {
            PauseSavedObsConnection();
            ShowObsError(error.Message);
        }
        return DisconnectObsAsync(connectionLost: true, reconnect: reconnect);
    }

    private async Task DisconnectObsAsync(bool connectionLost = false, bool reconnect = false)
    {
        _bookmarkCapture.BreakContinuity();
        _obsPollTimer.Stop();
        var connection = reconnect && !_exiting ? _obsConnection : null;
        _obsConnection = connection;
        _obsReconnectAt = RecordingHealthNow() + ObsReconnectInterval;
        var generation = _obsConnectionGeneration;
        var client = _obsClient;
        _obsClient = null;
        var highlightsStopped = StopWardogsHighlightsAsync();
        if (ActiveWardogsCapture is not null) ShowWardogsHighlights("Connect to OBS to save highlights.");
        _recordingHealth.Disconnected(generation, connectionLost);
        ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
        ObsConnectionStatus.Text = connection is null ? "Not connected" : "Waiting for OBS to open…";
        ObsOutputState.Text = "Unknown";
        ObsReplayState.Text = "Unknown";
        ObsDuration.Text = "—";
        ObsDataWritten.Text = "—";
        UpdateObsControls();
        if (client is not null)
        {
            _obsRecordingSessionId = null;
            _obsRecordingOwnerCaptured = false;
            _obsRecordingOutputActive = false;
            _obsReplaySessionId = null;
            _obsReplayOwnerCaptured = false;
            client.RecordStateChanged -= ObsClient_RecordStateChanged;
            client.RecordFileChanged -= ObsClient_RecordFileChanged;
            try { await client.DisposeAsync(); }
            catch (Exception ex) { AddActivity("Couldn't close the OBS connection cleanly. " + ex.Message); }
        }
        await highlightsStopped;
        if (connection is not null && ReferenceEquals(connection, _obsConnection) && !_exiting) _obsPollTimer.Start();
        UpdateObsControls();
    }

    private async void ObsPollTimer_Tick(object? sender, EventArgs e)
    {
        if (_exiting) return;
        if (_obsClient is not null) await PollObsStatusAsync();
        else if (_obsConnection is { } connection && RecordingHealthNow() >= _obsReconnectAt)
            await OpenObsConnectionAsync(connection);
    }

    private async Task PollObsStatusAsync()
    {
        var client = _obsClient;
        if (_exiting || _obsPollInProgress || _bookmarkBusy || client is null) return;
        var connectionGeneration = _obsConnectionGeneration;
        var observationGeneration = _recordingHealth.ObservationGeneration;
        var bookmarkGeneration = _bookmarkCapture.Generation;
        var bookmarkStatusVersion = ++_bookmarkStatusVersion;
        _obsPollInProgress = true;
        try
        {
            var status = await client.GetRecordStatusAsync();
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            var health = _recordingHealth.Observe(connectionGeneration, status, RecordingHealthNow(), observationGeneration);
            ApplyRecordingHealthSnapshot(health);
            ObsOutputState.Text = status.Active ? status.Paused ? "Paused" : "Recording" : "Stopped";
            ObserveObsRecordingForWardogs(status.Active);
            if (status.Active && !_obsRecordingOwnerCaptured)
                CaptureObsRecordingOwner(allowSingleSession: false);
            if (bookmarkGeneration == _bookmarkCapture.Generation && bookmarkStatusVersion == _bookmarkStatusVersion)
                _bookmarkCapture.Observe(status, _obsRecordingSessionId);
            ObsDuration.Text = TimeSpan.FromMilliseconds(Math.Max(0, status.DurationMilliseconds)).ToString(status.DurationMilliseconds >= 3_600_000 ? "h\\:mm\\:ss" : "mm\\:ss");
            ObsDataWritten.Text = FormatBytes(status.Bytes);
            var replayStatus = await client.GetReplayBufferStatusAsync();
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            ObsReplayState.Text = !replayStatus.Available ? ReplayBufferUnavailable : replayStatus.Active ? "Running" : "Stopped";
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
            ObsConnectionStatus.Text = "Connected to OBS on this PC.";
            ObsRemoteError.Visibility = Visibility.Collapsed;
            await AdvanceWardogsHighlightsAsync(client, status.Active, replayStatus.Active);
        }
        catch (Exception ex)
        {
            if (!_exiting && ReferenceEquals(_obsClient, client))
            {
                if (ex is ObsWebSocketException && client.IsConnected) ShowObsError(ex.Message);
                else await HandleObsConnectionFailureAsync(ex);
            }
        }
        finally
        {
            _obsPollInProgress = false;
            UpdateObsControls();
        }
    }

    private void ObsClient_RecordStateChanged(object? sender, ObsRecordStateChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || !ReferenceEquals(sender, _obsClient)) return;
            var completedCapture = _bookmarkCapture.ObserveState(e);
            if (e.State == "OBS_WEBSOCKET_OUTPUT_STARTING")
            {
                _obsRecordingSessionId = null;
                _obsRecordingOwnerCaptured = false;
                CaptureObsRecordingOwner(allowSingleSession: true);
            }
            if (e.Active && !_obsRecordingOwnerCaptured)
                CaptureObsRecordingOwner(allowSingleSession: e.State == "OBS_WEBSOCKET_OUTPUT_STARTED");
            else if (e.State == "OBS_WEBSOCKET_OUTPUT_STOPPED")
            {
                ConfirmBookmarkPath(completedCapture, e.OutputPath);
                if (!string.IsNullOrWhiteSpace(e.OutputPath)) SetLastObsRecording(e.OutputPath, _obsRecordingSessionId);
                _obsRecordingSessionId = null;
                _obsRecordingOwnerCaptured = false;
            }
            ObserveObsRecordingForWardogs(e.Active || e.State == "OBS_WEBSOCKET_OUTPUT_STARTING");
            ObsOutputState.Text = e.Active ? e.Paused ? "Paused" : "Recording" : "Stopped";
            UpdateObsControls();
        });
    }

    private void CaptureObsRecordingOwner(bool allowSingleSession)
    {
        var ownerPath = _automation.RecordingGamePath;
        if (!string.IsNullOrWhiteSpace(ownerPath))
        {
            _obsRecordingSessionId = _sessionTracker?.FindActive(ownerPath)?.Id;
            _obsRecordingOwnerCaptured = _obsRecordingSessionId is not null;
            return;
        }
        if (allowSingleSession && _sessionTracker?.ActiveSessions is { Count: 1 } active)
            _obsRecordingSessionId = active.Values.Single();
        _obsRecordingOwnerCaptured = true;
    }

    private void ObserveObsRecordingForWardogs(bool active)
    {
        if (_obsRecordingOutputActive == active) return;
        _obsRecordingOutputActive = active;
        UpdateWardogsBackground();
    }

    private async void ObsStart_Click(object sender, RoutedEventArgs e)
    {
        var requestId = Guid.NewGuid();
        await RunObsCommandAsync(client => client.StartRecordAsync(),
            beforeCommand: () => _recordingHealth.BeginRequest(requestId, CaptureMode.Recording, RecordingHealthNow()),
            completed: succeeded =>
            {
                if (!succeeded)
                {
                    _recordingHealth.EndRequest(requestId, intentional: false, RecordingHealthNow());
                    ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
                }
            });
    }

    private async void ObsStop_Click(object sender, RoutedEventArgs e)
    {
        string? outputPath = null;
        var sessionId = _obsRecordingSessionId;
        var requestId = _recordingHealth.CurrentRequestId ?? Guid.NewGuid();
        await RunObsCommandAsync(async client => outputPath = await client.StopRecordAsync(),
            beforeCommand: () =>
            {
                if (_recordingHealth.CurrentRequestId is null)
                    _recordingHealth.BeginRequest(requestId, CaptureMode.Recording, RecordingHealthNow());
                _recordingHealth.SetStopIntent(requestId, intentional: true, RecordingHealthNow());
            },
            completed: succeeded =>
            {
                if (succeeded) _recordingHealth.EndRequest(requestId, intentional: true, RecordingHealthNow());
                else _recordingHealth.SetStopIntent(requestId, intentional: false, RecordingHealthNow());
                ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
            });
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

    private async Task RunObsCommandAsync(Func<ObsWebSocketClient, Task> command, Action? beforeCommand = null, Action<bool>? completed = null)
    {
        var client = _obsClient;
        if (_exiting || _obsCommandInProgress || client is null) return;
        _obsCommandInProgress = true;
        UpdateObsControls();
        try
        {
            beforeCommand?.Invoke();
            ApplyRecordingHealthSnapshot(_recordingHealth.Snapshot);
            await command(client);
            if (_exiting || !ReferenceEquals(_obsClient, client)) return;
            completed?.Invoke(true);
            while (_obsPollInProgress) await Task.Delay(25);
            await PollObsStatusAsync();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_obsClient, client))
            {
                completed?.Invoke(false);
                if (client.IsConnected) ShowObsError(ex.Message);
                else await HandleObsConnectionFailureAsync(ex);
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

    private async Task SaveClipAsync(bool fromHotkey, WardogsHighlightRun? highlight = null)
    {
        var client = _obsClient;
        var sessionId = highlight is null ? _obsReplaySessionId : highlight.SessionId;
        if (_exiting || _obsCommandInProgress) return;
        if (client?.IsConnected != true)
        {
            var message = _obsConnection is null ? "Connect to OBS on the Recording page before saving a clip." : "Wait for OBS to open before saving a clip.";
            ShowObsError(message);
            if (fromHotkey && _settings.ShowNotifications) _tray.ShowBalloonTip(3000, "OBS connection required", message, System.Windows.Forms.ToolTipIcon.Info);
            return;
        }
        if (ObsReplayState.Text == ReplayBufferUnavailable)
        {
            const string message = "Enable Replay Buffer in OBS to save a clip.";
            ShowObsError(message);
            if (fromHotkey && _settings.ShowNotifications) _tray.ShowBalloonTip(3000, ReplayBufferUnavailable, message, System.Windows.Forms.ToolTipIcon.Info);
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
            if (highlight is not null)
            {
                highlight.Saved++;
                AddActivity("WARDOGS highlight saved: " + path);
            }
            else if (_settings.ShowNotifications) _tray.ShowBalloonTip(4000, "Clip saved", path, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_obsClient, client))
            {
                if (highlight is not null)
                {
                    highlight.SaveError = ex.Message;
                    AddActivity("Couldn't save WARDOGS highlight: " + ex.Message);
                }
                if (client.IsConnected) ShowObsError(ex.Message);
                else await HandleObsConnectionFailureAsync(ex);
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
        var monitoring = _obsConnection is not null;
        var connectedVisibility = connected ? Visibility.Visible : Visibility.Collapsed;
        var setupVisibility = monitoring ? Visibility.Collapsed : Visibility.Visible;
        ObsConnectButton.Visibility = ObsPasswordLabel.Visibility = ObsWebSocketPasswordBox.Visibility =
            ObsRememberPasswordBox.Visibility = ObsConnectionOptions.Visibility = setupVisibility;
        ObsDisconnectButton.Visibility = monitoring ? Visibility.Visible : Visibility.Collapsed;
        ObsDisconnectButton.Content = connected ? "Disconnect" : "Cancel connection";
        ObsLiveDetails.Visibility = connectedVisibility;
        ObsSavedOutputs.Visibility = _lastObsRecordingPath is not null || _lastObsClipPath is not null ? Visibility.Visible : Visibility.Collapsed;
        ObsConnectButton.IsEnabled = !_exiting && !busy && !monitoring;
        ObsDisconnectButton.IsEnabled = !_exiting && monitoring && !busy;
        var state = ObsOutputState.Text;
        ObsStartButton.Visibility = state == "Stopped" ? Visibility.Visible : Visibility.Collapsed;
        ObsStopButton.Visibility = state is "Recording" or "Paused" ? Visibility.Visible : Visibility.Collapsed;
        ObsPauseButton.Visibility = state == "Recording" ? Visibility.Visible : Visibility.Collapsed;
        ObsResumeButton.Visibility = state == "Paused" ? Visibility.Visible : Visibility.Collapsed;
        ObsBookmarkActions.Visibility = ObsStopButton.Visibility;
        ObsBookmarkActions.IsEnabled = !busy;
        ObsStartButton.IsEnabled = connected && !busy && state == "Stopped";
        ObsStopButton.IsEnabled = connected && !busy && state is "Recording" or "Paused";
        ObsPauseButton.IsEnabled = connected && !busy && state == "Recording";
        ObsResumeButton.IsEnabled = connected && !busy && state == "Paused";
        ObsStartReplayButton.Visibility = ObsReplayState.Text == "Stopped" ? Visibility.Visible : Visibility.Collapsed;
        ObsStopReplayButton.Visibility = ObsSaveClipButton.Visibility = ObsReplayState.Text == "Running" ? Visibility.Visible : Visibility.Collapsed;
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
