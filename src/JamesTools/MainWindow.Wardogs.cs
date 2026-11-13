using System.Windows;
using JamesTools.Core.Sessions;
namespace JamesTools;
public partial class MainWindow
{
    private WardogsReviewWindow? _wardogsWindow;
    private void Wardogs_Click(object sender, RoutedEventArgs e) => ShowWardogs();
    private async void ScanSessionFile_Click(object sender, RoutedEventArgs e)
    {
        if (SessionAttachments.SelectedItem is not AttachmentListEntry a || a.Attachment.Kind is not (SessionAttachmentKind.Recording or SessionAttachmentKind.Clip)) return;
        var id = (SessionsList.SelectedItem as SessionListEntry)?.Session.Id;
        ShowWardogs();
        await _wardogsWindow!.AddFilesAsync([a.Attachment.Path], id);
    }
    private void ShowWardogs()
    {
        if (_wardogsWindow is null) { _wardogsWindow = new WardogsReviewWindow(_store.DirectoryPath, _appliedSettings.MediaToolsFolder, (path, position, session, folder) => OpenMediaReview(path, position, session, true, folder), result => { if (_sessionStore is null) throw new System.IO.IOException("Session history unavailable; attach manually later."); _sessionStore.AddAttachment(result.SessionId, "Unassigned WARDOGS montage", "", SessionAttachmentKind.Clip, result.Path, DateTime.UtcNow); RefreshSessionList(); }) { Owner = this }; _wardogsWindow.Closed += (_, _) => _wardogsWindow = null; _wardogsWindow.Show(); }
        else _wardogsWindow.Activate();
    }
}
