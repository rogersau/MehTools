using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using JamesTools.Core.Sessions;

namespace JamesTools;

public partial class MainWindow
{
    private SteamWardogsWindow? steamWardogsWindow;
    private CancellationTokenSource? _steamKdLookup;
    private readonly Queue<(GameSession Session, AppSettings Settings)> _steamHighlightQueue = new();
    private CancellationTokenSource? _steamHighlightCancel;
    private Task? _steamHighlightWorker;
    private GameSession? _steamHighlightActiveSession;
    private static readonly TimeSpan SteamFinalizeWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SteamTimelinePoll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SteamTimelineTimeout = TimeSpan.FromMinutes(2);

    private async void SteamWardogs_Click(object sender, RoutedEventArgs e) => await ShowSteamWardogsAsync(null);

    private async void SessionSteamHighlights_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is SessionListEntry entry && SteamWardogsLibrary.IsHistoricalWardogsSession(entry.Session))
            await ShowSteamWardogsAsync(entry.Session);
    }

    private async Task ShowSteamWardogsAsync(GameSession? session)
    {
        if (steamWardogsWindow is { } existing)
        {
            existing.Activate();
            if (session is not null) await existing.ReviewSessionAsync(session);
            return;
        }
        var preferences = _appliedSettings.SteamHighlights.Copy();
        steamWardogsWindow = new(() => _appliedSettings.MediaToolsFolder,
            () => FindSteamRecordingFolder(preferences), preferences, session, AttachReviewedSteamMontage) { Owner = this };
        steamWardogsWindow.Closed += (_, _) => steamWardogsWindow = null;
        steamWardogsWindow.Show();
    }

    private bool AttachReviewedSteamMontage(SteamWardogsTimeline timeline, string destination)
    {
        if (_sessionStore is not { } store ||
            SteamWardogsLibrary.FindHistoricalSession(timeline, store.Snapshot()) is not { } session ||
            !store.AttachExplicitly(session.Id, SessionAttachmentKind.Clip, destination, DateTime.UtcNow)) return false;
        if (!_exiting) RefreshSessionList();
        return true;
    }

    private void BrowseSteamRecordingFolder_Click(object sender, RoutedEventArgs e) =>
        BrowseSteamFolder(SteamRecordingFolderBox, "Choose the folder shown in Steam → Settings → Game Recording");

    private void BrowseSteamHighlightsFolder_Click(object sender, RoutedEventArgs e) =>
        BrowseSteamFolder(SteamOutputFolderBox, "Choose where to save Steam highlights");

    private void BrowseSteamFolder(System.Windows.Controls.TextBox box, string title)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (Directory.Exists(box.Text)) picker.InitialDirectory = box.Text;
        if (picker.ShowDialog(this) == true) box.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, picker.FolderName);
    }

    private static string? FindSteamRecordingFolder(SteamHighlightSettings options) =>
        string.IsNullOrWhiteSpace(options.RecordingFolder) ? SteamGameDetector.FindGameRecordingDirectory() : options.RecordingFolder.Trim();

    private void RefreshGameSteamHighlights()
    {
        if (GameSteamHighlightsCard is null) return;
        var game = GameGrid.SelectedItem as GameRule;
        var supported = game is not null && SteamHighlightSettings.SupportsGame(game);
        GameSteamHighlightsCard.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        GameSteamAutoHighlights.IsChecked = supported && _settings.SteamHighlights.AutoExportFor(game!);
    }

    private void GameSteamAutoHighlights_Click(object sender, RoutedEventArgs e)
    {
        if (GameGrid.SelectedItem is GameRule game && SteamHighlightSettings.SupportsGame(game))
            game.SteamAutoHighlights = GameSteamAutoHighlights.IsChecked == true;
    }

    private void OpenSteamHighlightTiming_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = RecordingNavigationItem;
        SteamHighlightOptions.IsExpanded = true;
        SteamHighlightOptions.BringIntoView();
        SteamBeforeBox.Focus();
    }

    private async Task ShowSessionSteamKdAsync(GameSession? session)
    {
        _steamKdLookup?.Cancel();
        _steamKdLookup = null;
        SessionSteamKd.Visibility = Visibility.Collapsed;
        if (_exiting || session is null || !SteamWardogsLibrary.IsHistoricalWardogsSession(session)) return;

        var options = _appliedSettings.SteamHighlights.Copy();
        using var lookup = new CancellationTokenSource();
        _steamKdLookup = lookup;
        var token = lookup.Token;
        try
        {
            var estimate = await Task.Run(() =>
            {
                var root = FindSteamRecordingFolder(options);
                if (root is null) return null;
                return SteamWardogsLibrary.MatchHistoricalSession(session, SteamWardogsLibrary.ReadMarkers(root, token))?.KillDeaths();
            }, token);
            if (_exiting || token.IsCancellationRequested || estimate is null) return;
            var ratio = estimate.Ratio is double value ? value.ToString("0.00") : "—";
            SessionSteamKd.Text = $"Steam K/D: {ratio} · {estimate.Kills} {(estimate.Kills == 1 ? "kill" : "kills")} · {estimate.Deaths} {(estimate.Deaths == 1 ? "death marker" : "death markers")}";
            SessionSteamKd.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException)
        {
            // Session details remain usable if its Steam metadata has expired or is being replaced.
        }
        finally
        {
            if (ReferenceEquals(_steamKdLookup, lookup)) _steamKdLookup = null;
        }
    }

    private void ApplySteamHighlightSettings()
    {
        int queued = _steamHighlightQueue.Count;
        for (int i = 0; i < queued; i++)
        {
            var job = _steamHighlightQueue.Dequeue();
            if (ShouldAutoExportSteamHighlights(job.Session)) _steamHighlightQueue.Enqueue(job);
        }
        if (_steamHighlightActiveSession is { } active && !ShouldAutoExportSteamHighlights(active))
            _steamHighlightCancel?.Cancel();

        if (!HasAutomaticSteamHighlights)
            SteamHighlightStatus.Text = "Automatic Steam highlights are off.";
        else if (_steamHighlightWorker is not { IsCompleted: false } || _steamHighlightCancel?.IsCancellationRequested == true)
            SteamHighlightStatus.Text = "Automatic Steam highlights are on. Waiting for a finished WARDOGS session.";
    }

    private bool HasAutomaticSteamHighlights => _appliedSettings.Games.Any(_appliedSettings.SteamHighlights.AutoExportFor);

    private bool ShouldAutoExportSteamHighlights(GameSession session) =>
        SteamWardogsLibrary.IsCompletedWardogsSession(session) && _appliedSettings.Games.Any(game =>
            ExecutablePathRules.EqualsPath(game.ExecutablePath, session.ExecutableKey) && _appliedSettings.SteamHighlights.AutoExportFor(game));

    private void QueueCompletedSteamHighlights(IEnumerable<GameSession> changed)
    {
        if (_exiting || !HasAutomaticSteamHighlights) return;
        foreach (var session in changed.Where(ShouldAutoExportSteamHighlights))
            _steamHighlightQueue.Enqueue((session, _appliedSettings.Snapshot()));
        StartSteamHighlightWorker();
    }

    private void StartSteamHighlightWorker()
    {
        if (_exiting || !HasAutomaticSteamHighlights || _steamHighlightQueue.Count == 0 ||
            _steamHighlightWorker is { IsCompleted: false } || _sessionStore is null) return;
        _steamHighlightWorker = RunSteamHighlightQueueAsync(_sessionStore);
    }

    private async Task RunSteamHighlightQueueAsync(SessionLibraryStore store)
    {
        using var cancel = new CancellationTokenSource();
        _steamHighlightCancel = cancel;
        var token = cancel.Token;
        try
        {
            while (!token.IsCancellationRequested && _steamHighlightQueue.TryDequeue(out var job))
            {
                if (!ShouldAutoExportSteamHighlights(job.Session)) continue;
                _steamHighlightActiveSession = job.Session;
                try { await MakeSteamHighlightsAsync(store, job.Session, job.Settings, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested && !_exiting)
                        ReportSteamHighlightResult("Couldn't make Steam highlights. " + ex.Message);
                }
                finally { _steamHighlightActiveSession = null; }
            }
        }
        finally
        {
            _steamHighlightCancel = null;
            _steamHighlightWorker = null;
            StartSteamHighlightWorker();
        }
    }

    private async Task MakeSteamHighlightsAsync(SessionLibraryStore store, GameSession session, AppSettings settings, CancellationToken token)
    {
        SteamHighlightStatus.Text = "Waiting for Steam to finish saving the WARDOGS timeline…";
        await Task.Delay(SteamFinalizeWait, token);
        var options = settings.SteamHighlights;
        string? root = await Task.Run(() => FindSteamRecordingFolder(options), token);
        if (root is null || !Directory.Exists(root))
        {
            ReportSteamHighlightResult("Steam recording folder not found. Set it under Steam highlight timing and folders.");
            return;
        }

        SteamWardogsTimeline? timeline = null, previous = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < SteamTimelineTimeout)
        {
            token.ThrowIfCancellationRequested();
            SteamWardogsTimeline? candidate = null;
            try
            {
                candidate = await Task.Run(() => SteamWardogsLibrary.MatchCompletedSession(session, SteamWardogsLibrary.Read(root, token)), token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                // Steam can replace its index and JSON at different times after the process exits.
            }
            if (candidate is not null && previous is not null && candidate.Id == previous.Id &&
                candidate.EndedAt == previous.EndedAt && candidate.Events.SequenceEqual(previous.Events))
            {
                timeline = candidate;
                break;
            }
            previous = candidate;
            await Task.Delay(SteamTimelinePoll, token);
        }
        if (timeline is null)
        {
            ReportSteamHighlightResult("No unique finished Steam recording matched this WARDOGS session.");
            return;
        }

        string folder = options.OutputFolder.Trim();
        if (folder.Length == 0)
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos, Environment.SpecialFolderOption.DoNotVerify);
            if (videos.Length == 0) videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            folder = Path.Combine(videos, "Meh Tools", "Steam Highlights");
        }
        string destination = Path.Combine(folder, $"WARDOGS Steam highlights - {timeline.RecordedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss} - {session.Id:N}.mp4");
        bool exporting = true;
        var progress = new Progress<string>(message =>
        {
            if (exporting && !_exiting && !token.IsCancellationRequested) SteamHighlightStatus.Text = message;
        });
        SteamMontageResult result;
        try
        {
            var exporter = new SteamWardogsClips(new MediaTools(settings.MediaToolsFolder));
            result = await Task.Run(() => exporter.ExportMontageAsync(root, timeline, destination, options.BeforeSeconds, options.AfterSeconds, progress,
                categories: options.IncludedCategories, token: token), token);
        }
        finally { exporting = false; }
        if (result.Path is null)
        {
            ReportSteamHighlightResult(result.SkippedEvents > 0
                ? $"No highlights saved. Steam no longer has matched footage for {result.SkippedEvents} event(s)."
                : timeline.Events.Count == 0 ? "No highlights saved. This Steam recording had no WARDOGS events."
                : "No highlights saved. This Steam recording had none of your enabled event types.");
            return;
        }
        string count = $"{result.IncludedEvents} event(s) in {result.Windows} merged moment(s)." +
            (result.SkippedEvents > 0 ? $" {result.SkippedEvents} skipped because Steam footage was unavailable." : "");
        try
        {
            if (!store.AttachExplicitly(session.Id, SessionAttachmentKind.Clip, result.Path, DateTime.UtcNow))
                throw new IOException("The session is no longer in the library.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ReportSteamHighlightResult($"Saved Steam highlights to {result.Path}, but couldn't attach them. {ex.Message}");
            return;
        }
        if (!_exiting)
        {
            RefreshSessionList();
            ReportSteamHighlightResult("Steam highlights saved and added to Sessions. " + count);
            if (ShouldAutoExportSteamHighlights(session))
                ShowTrayNotification(new AutomationNotification("Steam highlights ready", count, false));
        }
    }

    private void ReportSteamHighlightResult(string message)
    {
        if (_exiting || !HasAutomaticSteamHighlights) return;
        SteamHighlightStatus.Text = message;
        AddActivity(message);
    }

    private async Task StopSteamHighlightsAsync()
    {
        _steamKdLookup?.Cancel();
        _steamHighlightQueue.Clear();
        _steamHighlightCancel?.Cancel();
        if (_steamHighlightWorker is { } worker) await worker;
    }
}
