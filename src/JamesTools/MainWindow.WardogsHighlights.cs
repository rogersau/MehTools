using System.IO;
using System.Windows;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Recording;
using JamesTools.Core.Scanning;

namespace JamesTools;

public partial class MainWindow
{
    private WardogsHighlightRun? _wardogsHighlights;
    private Task _wardogsHighlightsStop = Task.CompletedTask;

    private AutomationRecordingLifecycle? ActiveWardogsCapture =>
        _latestAutomationLifecycle is { RequestedMode: CaptureMode.WardogsHighlights, IsOwned: true, IntentionalStop: false } launch
            ? launch : null;

    private sealed record WardogsDetectionUpdate(string Status, LiveWardogsSnapshot Snapshot);

    private sealed class WardogsHighlightRun(Guid launchId, ObsWebSocketClient client, WardogsHighlights planner)
    {
        public Guid LaunchId { get; } = launchId;
        public ObsWebSocketClient Client { get; } = client;
        public WardogsHighlights Planner { get; } = planner;
        public CancellationTokenSource Cancel { get; } = new();
        public Task Worker { get; set; } = Task.CompletedTask;
        public WardogsDetectionUpdate? Update;
        public string? DetectionError;
        public string? SaveError { get; set; }
        public Guid? SessionId { get; set; }
        public int Saved { get; set; }
    }

    private void ShowWardogsHighlights(string? message)
    {
        WardogsHighlightsStatus.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        WardogsHighlightsStatus.Text = message is null ? "" : "WARDOGS highlights · " + message;
    }

    private async Task AdvanceWardogsHighlightsAsync(ObsWebSocketClient client, bool recording, bool replayActive)
    {
        var launch = ActiveWardogsCapture;
        if (launch is null || recording || !replayActive)
        {
            await StopWardogsHighlightsAsync();
            if (launch is not null && ActiveWardogsCapture?.LaunchId == launch.LaunchId)
                ShowWardogsHighlights(recording ? "Paused while OBS is recording." : "Waiting for the replay buffer…");
            return;
        }
        if (_wardogsHighlights is null)
        {
            if (!_wardogsHighlightsStop.IsCompleted) return;
            var windows = LiveTarget.Enumerate(launch.GameExecutable ?? "").Where(target => target.Pid == launch.GamePid).ToArray();
            var target = windows.Length == 1 ? windows[0] : windows.FirstOrDefault(window => window.Hwnd == Native.GetForegroundWindow());
            if (target is null)
            {
                ShowWardogsHighlights("Waiting for the WARDOGS game window.");
                return;
            }
            var config = _appliedSettings.LiveWardogs.Copy();
            if (!config.HighlightsValid)
            {
                ShowWardogsHighlights("Choose events and a post-roll in Live WARDOGS.");
                return;
            }
            var run = new WardogsHighlightRun(launch.LaunchId, client, new(config, launch.ReplaySeconds));
            _wardogsHighlights = run;
            run.Worker = Task.Run(() => DetectWardogsHighlightsAsync(run, target, config));
        }
        var current = _wardogsHighlights;
        if (current.LaunchId != launch.LaunchId || !ReferenceEquals(current.Client, client)) return;
        var update = Volatile.Read(ref current.Update);
        var now = RecordingHealthNow();
        if (!string.IsNullOrWhiteSpace(launch.GameRulePath))
            current.SessionId ??= _sessionTracker?.FindActive(launch.GameRulePath)?.Id;
        if (update is not null) current.Planner.Observe(update.Snapshot.Events, now);
        if (!_obsCommandInProgress && current.Planner.TakeDue(now))
        {
            current.SaveError = null;
            ShowWardogsHighlights("Saving highlight…");
            await SaveClipAsync(false, current);
            if (!ReferenceEquals(current, _wardogsHighlights)) return;
        }
        var detectionError = Volatile.Read(ref current.DetectionError);
        var status = current.SaveError is { } saveError ? "Clip save failed. " + saveError
            : current.Planner.Pending ? "Highlight detected. Waiting for follow-through…"
            : detectionError is not null ? "Detection stopped. " + detectionError
            : update?.Status ?? "Starting HUD detection…";
        ShowWardogsHighlights($"{status} · {current.Saved} saved");
    }

    private static async Task DetectWardogsHighlightsAsync(WardogsHighlightRun run, LiveTarget target, LiveWardogsSettings config)
    {
        var token = run.Cancel.Token;
        try
        {
            var capture = new WindowHudCapture(target, config);
            await LiveWardogsRun.RunAsync(capture,
                WindowsWardogsOcr.CreateAsync,
                snapshot => Volatile.Write(ref run.Update, new(capture.Status, snapshot)),
                new(config.Rate, config.Budget), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref run.DetectionError, ex.Message); }
    }

    private Task StopWardogsHighlightsAsync()
    {
        if (_wardogsHighlights is not { } run) return _wardogsHighlightsStop;
        _wardogsHighlights = null;
        run.Cancel.Cancel();
        _wardogsHighlightsStop = WaitForWardogsWorkerAsync(run);
        return _wardogsHighlightsStop;
    }

    private static async Task WaitForWardogsWorkerAsync(WardogsHighlightRun run)
    {
        try { await run.Worker; }
        finally { run.Cancel.Dispose(); }
    }
}
