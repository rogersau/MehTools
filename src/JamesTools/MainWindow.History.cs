using System.Windows;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Sessions;
namespace JamesTools;
public partial class MainWindow
{
    PerformanceHistoryWindow? _historyWindow;
    bool _historyOpening;
    private async void PerformanceHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_historyOpening) return;
        if (_historyWindow != null) { _historyWindow.Activate(); return; }
        if (SelectedGame(PerformanceGame) is not { } game) { PerformanceStatus.Text = "Select a configured game first."; return; }
        _historyOpening = true;
        try
        {
            var key = SessionLibraryStore.NormalizeExecutable(game.ExecutablePath);
            var title=game.Name;
            var sources = (_sessionStore?.Snapshot() ?? []).Where(s => SessionLibraryStore.NormalizeExecutable(s.ExecutableKey) == key)
                .SelectMany(s => s.Attachments.Where(a => a.Kind == SessionAttachmentKind.PerformanceReport).Select(a => (a.Path, Reference: "Session " + s.Id + " / " + s.GameName, Observed: a.ObservedUtc))).ToArray();
            var store=await System.Threading.Tasks.Task.Run(() => new PerformanceHistoryStore(_dataDirectory));
            if(_exiting)return;
            _historyWindow = new PerformanceHistoryWindow(store, key, title, sources, ShowReportTimeline) { Owner = this };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        catch (Exception ex) { PerformanceStatus.Text = "History unavailable: " + ex.Message; }
        finally { _historyOpening = false; }
    }
}
