using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Sessions;
using Microsoft.Win32;

namespace JamesTools;

public partial class MainWindow
{
    private void InitializeSessionsPanel()
    {
        SessionAttachmentKindBox.ItemsSource = Enum.GetValues<SessionAttachmentKind>();
        SessionAttachmentKindBox.SelectedItem = SessionAttachmentKind.Recording;
    }

    private void UpdateDetectedSessions(AutomationStatus status)
    {
        if (_sessionTracker is null || _sessionStore is null) return;
        try
        {
            var watching = _appliedSettings.AutomationEnabled;
            var observations = (status.ActiveGamePaths ?? [])
                .Select(path => _appliedSettings.Games.FirstOrDefault(g => string.Equals(
                    SessionLibraryStore.NormalizeExecutable(g.ExecutablePath), SessionLibraryStore.NormalizeExecutable(path), StringComparison.OrdinalIgnoreCase)))
                .Where(game => game is not null)
                .Select(game => new SessionGameObservation(game!.Name, game.ExecutablePath)).ToArray();
            var changed = _sessionTracker.Observe(DateTime.UtcNow, watching, observations);
            if (changed.Count > 0) RefreshSessionList();
            else if (SessionsList.SelectedItem is SessionListEntry selected &&
                     _sessionStore.Get(selected.Session.Id) is { Status: SessionStatus.Active } active)
                SessionDetailDuration.Text = $"Detected session time: {FormatSessionDuration(active.DetectedDuration)}";
            if (SessionsStatus is not null && string.IsNullOrEmpty(SessionsSearch.Text))
                SessionsStatus.Text = $"{_sessionStore.Snapshot().Count} session(s). Duration is detected session time.";
        }
        catch (Exception ex)
        {
            if (SessionsStatus is not null) SessionsStatus.Text = "Couldn't update session history: " + ex.Message;
        }
    }

    private void RefreshSessionList()
    {
        if (SessionsList is null) return;
        if (_sessionStore is null)
        {
            SessionsList.ItemsSource = Array.Empty<SessionListEntry>();
            if (SessionsStatus is not null) SessionsStatus.Text = "Session history is unavailable. The existing history file was preserved.";
            return;
        }
        var selectedId = (SessionsList.SelectedItem as SessionListEntry)?.Session.Id;
        var draftNotes = SessionNotes?.Text;
        var draftFavourite = SessionFavourite?.IsChecked;
        var query = SessionsSearch?.Text?.Trim() ?? "";
        var entries = _sessionStore.Snapshot()
            .Where(s => string.IsNullOrWhiteSpace(query) ||
                s.GameName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Notes.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.StartedUtc.ToLocalTime().ToString("g").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.StartedUtc.ToString("yyyy-MM-dd").Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.StartedUtc)
            .Select(s => new SessionListEntry(s,
                $"{s.StartedUtc.ToLocalTime():g} · {FormatSessionDuration(s.DetectedDuration)} · {s.Status}"))
            .ToArray();
        SessionsList.ItemsSource = entries;
        SessionsList.SelectedItem = entries.FirstOrDefault(e => e.Session.Id == selectedId);
        if (SessionsList.SelectedItem is SessionListEntry && selectedId is not null)
        {
            SessionNotes!.Text = draftNotes ?? "";
            SessionFavourite!.IsChecked = draftFavourite;
        }
        if (SessionsList.SelectedItem is null) ShowSessionDetails(null);
        if (SessionsStatus is not null && entries.Length == 0)
            SessionsStatus.Text = _sessionStore.Snapshot().Count == 0 ? "No sessions yet. Start watching to track configured games." : "No sessions match this search.";
    }

    private void SessionsSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshSessionList();

    private void SessionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowSessionDetails((SessionsList.SelectedItem as SessionListEntry)?.Session);
    }

    private void ShowSessionDetails(GameSession? session)
    {
        SessionDetailGame.Text = session?.GameName ?? "Select a session";
        SessionDetailWhen.Text = session is null ? "" : $"Started {session.StartedUtc.ToLocalTime():f}" +
            (session.EndedUtc is DateTime end ? $" · ended {end.ToLocalTime():f}" : "");
        SessionDetailDuration.Text = session is null ? "" : $"Detected session time: {FormatSessionDuration(session.DetectedDuration)}";
        SessionDetailStatus.Text = session?.Status.ToString() ?? "";
        SessionNotes.Text = session?.Notes ?? "";
        SessionFavourite.IsChecked = session?.Favourite ?? false;
        SessionAttachments.ItemsSource = session?.Attachments.OrderByDescending(a => a.ObservedUtc)
            .Select(a => new AttachmentListEntry(a)).ToArray() ?? [];
        UpdateSessionFileButtons();
        RefreshBookmarks();
        CompareSessionButton.IsEnabled = session is not null;
    }

    private void SaveSessionDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionStore is null || SessionsList.SelectedItem is not SessionListEntry entry) return;
        try
        {
            _sessionStore.UpdateDetails(entry.Session.Id, SessionNotes.Text, SessionFavourite.IsChecked == true);
            SessionsStatus.Text = "Session details saved.";
            RefreshSessionList();
        }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't save session details: " + ex.Message; }
    }

    private void SessionAttachments_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSessionFileButtons();

    private void UpdateSessionFileButtons()
    {
        var attachment = (SessionAttachments?.SelectedItem as AttachmentListEntry)?.Attachment;
        var exists = attachment is not null && File.Exists(attachment.Path);
        if (OpenSessionFileButton is not null) OpenSessionFileButton.IsEnabled = exists;
        if (ShowSessionFileButton is not null) ShowSessionFileButton.IsEnabled = exists;
        if (ViewSessionReportButton is not null) ViewSessionReportButton.IsEnabled = exists && attachment!.Kind == SessionAttachmentKind.PerformanceReport;
    }

    private void AttachSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionStore is null || SessionsList.SelectedItem is not SessionListEntry selected)
        {
            SessionsStatus.Text = "Select a session before attaching a file.";
            return;
        }
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Attach an existing recording, clip, or report", Filter = "All files (*.*)|*.*" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var kind = SessionAttachmentKindBox.SelectedItem is SessionAttachmentKind selectedKind
                ? selectedKind : SessionAttachmentKind.Recording;
            _sessionStore.AttachExplicitly(selected.Session.Id, kind, picker.FileName, DateTime.UtcNow);
            RefreshSessionList();
            SessionsStatus.Text = "File attached. The library stores a reference and leaves the file in place.";
        }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't attach file: " + ex.Message; }
    }

    private void OpenSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionAttachments.SelectedItem is not AttachmentListEntry entry) return;
        var id = (SessionsList.SelectedItem as SessionListEntry)?.Session.Id;
        if (entry.Attachment.Kind is SessionAttachmentKind.Recording or SessionAttachmentKind.Clip)
        {
            try { OpenMediaReview(entry.Attachment.Path, TimeSpan.Zero, id); }
            catch (Exception ex) { SessionsStatus.Text = "Couldn't open recording: " + ex.Message; }
        }
        else RunWithSelectedAttachment(path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
    }
    private void OpenMediaReview(string path, TimeSpan position, Guid? sessionId) => OpenMediaReview(path, position, sessionId, false);
    private void OpenMediaReview(string path, TimeSpan position, Guid? sessionId, bool lockSource, string? toolsFolder = null)
    {
        var guard = lockSource ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        try { var window = new RecordingPlaybackWindow(path, position, toolsFolder ?? _appliedSettings.MediaToolsFolder,
            target => { if (_sessionStore is null) throw new IOException("Clip published, but session history is unavailable; attach it manually later."); _sessionStore.AddAttachment(sessionId, "Unassigned clip", "", SessionAttachmentKind.Clip, target, DateTime.UtcNow); RefreshSessionList(); },
            folder => { var saved = _appliedSettings.Snapshot(); saved.MediaToolsFolder = folder; _store.Save(saved); _settings.MediaToolsFolder = folder; _appliedSettings.MediaToolsFolder = folder; }) { Owner = this }; window.Closed += (_, _) => guard?.Dispose(); window.Show(); } catch { guard?.Dispose(); throw; }
    }

    private void ShowSessionFile_Click(object sender, RoutedEventArgs e) => RunWithSelectedAttachment(path =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }));

    private void ViewSessionReport_Click(object sender, RoutedEventArgs e)
    {
        if (SessionAttachments.SelectedItem is AttachmentListEntry { Attachment.Kind: SessionAttachmentKind.PerformanceReport } entry)
            RunWithSelectedAttachment(path => ShowReportTimeline(File.ReadAllText(path), Path.GetFileName(path)));
    }

    private void RunWithSelectedAttachment(Action<string> action)
    {
        if (SessionAttachments.SelectedItem is not AttachmentListEntry entry) return;
        try
        {
            if (!File.Exists(entry.Attachment.Path)) throw new FileNotFoundException("This attachment is missing from its saved location.", entry.Attachment.Path);
            action(entry.Attachment.Path);
        }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't open attachment: " + ex.Message; }
    }

    private void CompareSession_Click(object sender, RoutedEventArgs e)
    {
        if (_sessionStore is null || SessionsList.SelectedItem is not SessionListEntry selected) return;
        try
        {
            var current = FindCurrentReport(selected.Session);
            var previous = _sessionStore.FindPreviousReport(selected.Session);
            var baseline = previous?.Attachments.Where(a => a.Kind == SessionAttachmentKind.PerformanceReport && File.Exists(a.Path))
                .OrderByDescending(a => a.ObservedUtc).FirstOrDefault();
            if (current is null || baseline is null)
            {
                SessionsStatus.Text = "No earlier same-game session has an available performance report to compare.";
                return;
            }
            var output = Path.Combine(_dataDirectory, $"Meh-Tools-comparison-{DateTime.Now:yyyy-MM-dd-HHmmss}.txt");
            File.WriteAllText(output, BuildComparison(File.ReadAllText(baseline.Path), File.ReadAllText(current.Path),
                Path.GetFileName(baseline.Path), Path.GetFileName(current.Path)), new System.Text.UTF8Encoding(false));
            SessionsStatus.Text = "Comparison saved: " + output;
            try { Process.Start(new ProcessStartInfo(output) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't compare reports: " + ex.Message; }
    }

    private SessionAttachment? FindCurrentReport(GameSession session) => session.Attachments
        .Where(a => a.Kind == SessionAttachmentKind.PerformanceReport && File.Exists(a.Path))
        .OrderByDescending(a => a.ObservedUtc).FirstOrDefault();

    private void AddSessionAttachment(Guid? id, SessionAttachmentKind kind, string path, string unassignedName)
    {
        if (_sessionStore is null) return;
        try
        {
            _sessionStore.AddAttachment(id, unassignedName, "", kind, path, DateTime.UtcNow);
            RefreshSessionList();
        }
        catch (Exception ex)
        {
            if (SessionsStatus is not null) SessionsStatus.Text = "Couldn't add file to session history: " + ex.Message;
        }
    }

    private static string FormatSessionDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}h {duration.Minutes}m" : $"{Math.Max(0, (int)duration.TotalMinutes)}m";

    private sealed record SessionListEntry(GameSession Session, string Summary)
    {
        public string GameName => (Session.Favourite ? "★ " : "") + Session.GameName;
    }

    private sealed record AttachmentListEntry(SessionAttachment Attachment)
    {
        public string Label => $"{Attachment.Kind} · {Attachment.Path}" + (File.Exists(Attachment.Path) ? "" : " · missing");
    }
}
