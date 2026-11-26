using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Recording;
namespace JamesTools;
public partial class MainWindow
{
    private readonly BookmarkCaptureTracker _bookmarkCapture = new();
    private RecordingBookmarkStore? _bookmarkStore;
    private bool _bookmarkBusy;
    private long _bookmarkStatusVersion;
    private void ObsClient_RecordFileChanged(object? sender, ObsRecordFileChangedEventArgs e)
    {
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_exiting || !ReferenceEquals(sender, _obsClient)) return;
            _bookmarkCapture.FileChanged();
            BookmarkStatus.Text = "OBS split the recording. Moments remain unresolved; new bookmarks need a new unsplit recording.";
        });
    }
    private void InitializeBookmarks()
    {
        try { _bookmarkStore = new RecordingBookmarkStore(_dataDirectory); }
        catch (Exception ex) { BookmarkStatus.Text = ex.Message; }
        RefreshBookmarks();
    }
    private async void Bookmark_Click(object sender, RoutedEventArgs e) => await MarkBookmarkAsync(false);
    private async Task MarkBookmarkAsync(bool hotkey)
    {
        if (_exiting) return;
        void Report(string message)
        {
            BookmarkStatus.Text = message;
            if (_appliedSettings.ShowNotifications) _tray.ShowBalloonTip(2500, "Recording bookmark", message, System.Windows.Forms.ToolTipIcon.Info);
        }
        if (_bookmarkBusy || _obsCommandInProgress) { Report("OBS request in progress; try the bookmark again."); return; }
        var client = _obsClient;
        if (client?.IsConnected != true) { Report("Connect explicitly to OBS before marking a recording."); return; }
        if (_bookmarkStore is null) { Report("Bookmark metadata unavailable; existing file preserved."); return; }
        var generation = _bookmarkCapture.Generation;
        _bookmarkStatusVersion++; // Supersede a status poll that was requested before this fresh sample.
        var label = hotkey ? "Moment" : BookmarkLabel.Text;
        _bookmarkBusy = true;
        try
        {
            var status = await client.GetRecordStatusAsync();
            if (_exiting) return;
            if (!ReferenceEquals(client, _obsClient)) { Report("OBS connection changed; bookmark discarded."); return; }
            var bookmark = _bookmarkCapture.Mark(generation, status, label, DateTime.UtcNow);
            _bookmarkStore.Add(bookmark);
            RefreshBookmarks();
            Report($"Moment saved at {TimeSpan.FromMilliseconds(bookmark.OffsetMilliseconds):g} (approximate). Path pending OBS confirmation.");
        }
        catch (Exception ex) { if (!_exiting) Report("Bookmark not saved: " + ex.Message); }
        finally { _bookmarkBusy = false; }
    }
    private void ConfirmBookmarkPath(Guid? capture, string? path)
    {
        if (capture is null || string.IsNullOrWhiteSpace(path)) return;
        try { _bookmarkStore?.ConfirmPath(capture.Value, path); RefreshBookmarks(); }
        catch (Exception ex) { BookmarkStatus.Text = ex.Message; }
    }
    private void RefreshBookmarks()
    {
        if (BookmarkList is null) return;
        var selected = (BookmarkList.SelectedItem as BookmarkRow)?.Bookmark.Id;
        var draft = BookmarkEditLabel?.Text;
        var session = (SessionsList?.SelectedItem as SessionListEntry)?.Session.Id;
        var rows = (_bookmarkStore?.Snapshot() ?? []).Where(b => b.SessionId == session).Select(b => new BookmarkRow(b)).ToArray();
        SessionMomentsCard.Visibility = session is not null || rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarkList.ItemsSource = rows;
        BookmarkList.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoBookmarksText.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarkList.SelectedItem = rows.FirstOrDefault(row => row.Bookmark.Id == selected);
        if (BookmarkList.SelectedItem is not null && draft is not null) BookmarkEditLabel!.Text = draft;
    }
    private sealed record BookmarkRow(RecordingBookmark Bookmark)
    {
        public string Display => $"{TimeSpan.FromMilliseconds(Bookmark.OffsetMilliseconds):g} · {Bookmark.Label} · {Bookmark.ObservedUtc.ToLocalTime():g} · " +
            (Bookmark.RecordingPath is null ? "Unresolved path" : File.Exists(Bookmark.RecordingPath) ? Bookmark.RecordingPath : "Missing: " + Bookmark.RecordingPath);
    }
    private void BookmarkSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        var row = BookmarkList.SelectedItem as BookmarkRow;
        BookmarkActions.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is null) return;
        BookmarkEditLabel.Text = row.Bookmark.Label;
        OpenBookmarkButton.IsEnabled = File.Exists(row.Bookmark.RecordingPath);
    }
    private void EditBookmark_Click(object sender, RoutedEventArgs e) => MutateBookmark(false);
    private void RemoveBookmark_Click(object sender, RoutedEventArgs e) => MutateBookmark(true);
    private void MutateBookmark(bool remove)
    {
        if (BookmarkList.SelectedItem is not BookmarkRow row) return;
        try { if (remove) _bookmarkStore?.Remove(row.Bookmark.Id); else _bookmarkStore?.Edit(row.Bookmark.Id, BookmarkEditLabel.Text); RefreshBookmarks(); }
        catch (Exception ex) { SessionsStatus.Text = ex.Message; }
    }
    private void ShowUnassignedBookmarks_Click(object sender, RoutedEventArgs e) { SessionsList.SelectedItem = null; RefreshBookmarks(); }
    private void CopyBookmark_Click(object sender, RoutedEventArgs e)
    {
        try { if (BookmarkList.SelectedItem is BookmarkRow row) System.Windows.Clipboard.SetText(TimeSpan.FromMilliseconds(row.Bookmark.OffsetMilliseconds).ToString("g")); }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't copy timestamp: " + ex.Message; }
    }
    private void OpenBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkList.SelectedItem is not BookmarkRow row) return;
        var b = row.Bookmark;
        if (b.RecordingPath is null || !File.Exists(b.RecordingPath)) { SessionsStatus.Text = "Recording missing or unresolved. Copy timestamp remains available."; return; }
        try { OpenMediaReview(b.RecordingPath, TimeSpan.FromMilliseconds(b.OffsetMilliseconds), b.SessionId); }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't open recording: " + ex.Message; }
    }
}
