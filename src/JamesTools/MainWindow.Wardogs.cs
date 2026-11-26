using System.Windows;
using JamesTools.Core.Scanning;
using JamesTools.Core.Sessions;
namespace JamesTools;
public partial class MainWindow
{
    private LiveWardogsWindow? _liveWardogsWindow;
    private void LiveWardogs_Click(object sender, RoutedEventArgs e)
    {
        if (_liveWardogsWindow is null) {
            _liveWardogsWindow = new LiveWardogsWindow(_appliedSettings.LiveWardogs, config => {
                var saved = _appliedSettings.Snapshot(); saved.LiveWardogs = config.Copy();
                _store.Save(saved);
                _appliedSettings.LiveWardogs = config.Copy();
                if (DataContext is JamesTools.Core.Configuration.AppSettings editing) editing.LiveWardogs = config.Copy();
            }) { Owner = this };
            _liveWardogsWindow.Closed += (_, _) => _liveWardogsWindow = null;
            _liveWardogsWindow.Show();
        } else _liveWardogsWindow.Activate();
    }
    private WardogsReviewWindow? _wardogsWindow;
    private WardogsReviewStore? _wardogsReviewStore;
    private int _wardogsMoneyRequest;

    private WardogsReviewStore WardogsStore() =>
        _wardogsReviewStore ??= new(_store.DirectoryPath, new NativeWardogsScanWork(WindowsWardogsOcr.Create));

    private async Task ShowSessionWardogsMoneyAsync(GameSession? session)
    {
        int request = ++_wardogsMoneyRequest;
        SessionWardogsMoney.Visibility = Visibility.Collapsed;
        if (session is null || !session.Attachments.Any(a => a.Kind == SessionAttachmentKind.Recording)) return;

        try
        {
            var summary = await WardogsStore().SessionMoneyAsync(session);
            if (request != _wardogsMoneyRequest || _exiting) return;
            var text = WardogsDisplay.Money(summary);
            if (text.Length == 0) return;
            SessionWardogsMoney.Text = $"WARDOGS money · {summary.ScannedRecordings} of {summary.TotalRecordings} recordings scanned\n{text}";
            SessionWardogsMoney.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or ArgumentException)
        {
            if (request != _wardogsMoneyRequest || _exiting) return;
            SessionWardogsMoney.Text = "Couldn't load WARDOGS scan totals. " + ex.Message;
            SessionWardogsMoney.Visibility = Visibility.Visible;
        }
    }
    private void Wardogs_Click(object sender, RoutedEventArgs e) => ShowWardogs();
    private async void ScanSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionAttachments.SelectedItem is not AttachmentListEntry a || a.Attachment.Kind is not (SessionAttachmentKind.Recording or SessionAttachmentKind.Clip)) return;
        var id = (SessionsList.SelectedItem as SessionListEntry)?.Session.Id;
        await ShowWardogs().AddFilesAsync([a.Attachment.Path], id);
    }

    private async void ReviewSessionScan_Click(object sender, RoutedEventArgs e)
    {
        if (SessionsList.SelectedItem is not SessionListEntry session || SessionAttachments.SelectedItem is not AttachmentListEntry file
            || file.Attachment.Kind is not (SessionAttachmentKind.Recording or SessionAttachmentKind.Clip)) return;
        try
        {
            var scanId = await WardogsStore().FindCompletedScanIdAsync(file.Attachment.Path, session.Session.Id);
            if (_exiting || (SessionsList.SelectedItem as SessionListEntry)?.Session.Id != session.Session.Id
                || (SessionAttachments.SelectedItem as AttachmentListEntry)?.Attachment != file.Attachment) return;
            if (scanId is Guid id) await ShowWardogs().ShowSavedScanAsync(id);
            else SessionsStatus.Text = "This file has no completed WARDOGS scan yet.";
        }
        catch (Exception ex) { SessionsStatus.Text = "Couldn't open the saved WARDOGS scan. " + ex.Message; }
    }

    private WardogsReviewWindow ShowWardogs()
    {
        if (_wardogsWindow is not null) { _wardogsWindow.Activate(); return _wardogsWindow; }
        _wardogsWindow = new WardogsReviewWindow(WardogsStore(), _appliedSettings.MediaToolsFolder,
            (path, position, session, folder) => OpenMediaReview(path, position, session, true, folder),
            result =>
            {
                if (_sessionStore is null) throw new System.IO.IOException("Session history unavailable; attach manually later.");
                _sessionStore.AddAttachment(result.SessionId, "Unassigned WARDOGS montage", "", SessionAttachmentKind.Clip, result.Path, DateTime.UtcNow);
                RefreshSessionList();
            },
            () => _ = ShowSessionWardogsMoneyAsync((SessionsList.SelectedItem as SessionListEntry)?.Session),
            path => _sessionStore?.FindVideoSession(path)) { Owner = this };
        _wardogsWindow.Closed += (_, _) => _wardogsWindow = null;
        _wardogsWindow.Show();
        return _wardogsWindow;
    }
}
