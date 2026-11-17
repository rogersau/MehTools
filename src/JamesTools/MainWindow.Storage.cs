using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Storage;

namespace JamesTools;

public partial class MainWindow
{
    private bool _cleanupBusy;
    private CancellationTokenSource? _cleanupCancellation;
    private Task<RecordingCleanupResult>? _cleanupTask;

    private CleanupPreviewRow[] SelectedCleanupRows() => CleanupPreviewGrid.SelectedItems.OfType<CleanupPreviewRow>().ToArray();

    private void CleanupPreviewGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCleanupControls();

    private void UpdateCleanupControls()
    {
        if (CleanupSelectionSummary is null || DeleteRecordingsButton is null) return;
        var rows = SelectedCleanupRows();
        var protectedCount = rows.Count(r => !r.Entry.CanDelete);
        if (!_cleanupBusy)
            CleanupSelectionSummary.Text = rows.Length == 0
                ? "Select recordings in the table. Use Ctrl or Shift to select several files."
                : protectedCount > 0
                    ? $"{rows.Length} selected · {protectedCount} protected. Select only available or suggested recordings to delete."
                    : $"{rows.Length} selected · {FormatStorageBytes(rows.Sum(r => r.Entry.SizeBytes ?? 0))} to delete.";
        DeleteRecordingsButton.IsEnabled = !_cleanupBusy && rows.Length > 0 && protectedCount == 0 && _sessionStore is not null;
        SelectCleanupSuggestionsButton.IsEnabled = !_cleanupBusy && _cleanupPreview?.Entries.Any(e => e.Candidate) == true;
        ClearCleanupSelectionButton.IsEnabled = !_cleanupBusy && rows.Length > 0;
        RefreshCleanupButton.IsEnabled = !_cleanupBusy;
        CleanupPreviewGrid.IsEnabled = !_cleanupBusy;
        CancelCleanupButton.Visibility = _cleanupBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelCleanupButton.IsEnabled = _cleanupCancellation is { IsCancellationRequested: false };
    }

    private void SelectCleanupSuggestions_Click(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        CleanupPreviewGrid.SelectedItems.Clear();
        foreach (var row in CleanupPreviewGrid.Items.OfType<CleanupPreviewRow>().Where(r => r.Entry.Candidate && r.Entry.CanDelete))
            CleanupPreviewGrid.SelectedItems.Add(row);
    }

    private void ClearCleanupSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!_cleanupBusy) CleanupPreviewGrid.SelectedItems.Clear();
    }

    private void CancelCleanup_Click(object sender, RoutedEventArgs e)
    {
        _cleanupCancellation?.Cancel();
        CleanupSelectionSummary.Text = "Stopping after the current file…";
        UpdateCleanupControls();
    }

    private async void DeleteRecordings_Click(object sender, RoutedEventArgs e)
    {
        if (_exiting || _cleanupBusy || _sessionStore is null) return;
        var selected = SelectedCleanupRows().Select(r => r.Entry).ToArray();
        if (selected.Length == 0 || selected.Any(e => !e.CanDelete || e.FileStamp is null)) return;
        _cleanupBusy = true;
        ++_cleanupRefreshVersion;
        UpdateCleanupControls();
        try
        {
            var size = FormatStorageBytes(selected.Sum(e => e.SizeBytes ?? 0));
            var paths = string.Join("\n", selected.Take(8).Select(e => e.Path));
            if (selected.Length > 8) paths += $"\n… and {selected.Length - 8} more selected recording(s).";
            if (System.Windows.MessageBox.Show(this,
                $"Permanently delete {selected.Length} recording(s) ({size})?\n\n{paths}\n\n" +
                "This bypasses the Recycle Bin and cannot be undone. Session history and notes are kept; deleted files will be marked missing.\n\n" +
                "Stop recording and close players or exports first. Changed, protected or in-use files will be skipped.",
                "Delete selected recordings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

            CleanupOperationSummary.Text = "";
            CleanupOperationDetails.Text = "";
            CleanupOperationResults.Visibility = Visibility.Collapsed;
            _cleanupCancellation = new();
            var roots = RecordingRoots(_settings);
            var store = _sessionStore;
            var completed = 0;
            var progress = new Progress<RecordingCleanupFileResult>(_ =>
            {
                if (!_exiting && _cleanupBusy)
                    CleanupSelectionSummary.Text = $"Processed {++completed} of {selected.Length} recording(s)…";
            });
            CleanupSelectionSummary.Text = "Rechecking selected recordings…";
            UpdateCleanupControls();
            _cleanupTask = Task.Run(() => new RecordingCleanupService().DeleteSelected(selected, store.Snapshot,
                roots, _cleanupCancellation.Token, progress));
            var result = await _cleanupTask;
            if (_exiting) return;
            CleanupOperationSummary.Text = $"Deleted {result.DeletedCount} recording(s) ({FormatStorageBytes(result.DeletedBytes)}) · {result.SkippedCount} skipped." +
                (result.Cancelled ? $" Cancelled; {selected.Length - result.Files.Count} not processed." : "");
            CleanupOperationDetails.Text = string.Join(Environment.NewLine, result.Files.Select(f =>
                $"{(f.Deleted ? "DELETED" : "SKIPPED")}  {f.Path}{Environment.NewLine}{f.Message}"));
            CleanupOperationResults.Visibility = Visibility.Visible;
            AddActivity(CleanupOperationSummary.Text);
            await RefreshCleanupPreviewAsync();
            await RefreshStorageVisibilityAsync();
            RefreshSessionList();
            UpdateObsControls();
        }
        catch (Exception ex)
        {
            if (!_exiting) CleanupOperationSummary.Text = "Cleanup stopped: " + ex.Message + ". Refresh storage before continuing.";
        }
        finally
        {
            _cleanupTask = null;
            _cleanupCancellation?.Dispose();
            _cleanupCancellation = null;
            _cleanupBusy = false;
            if (!_exiting) UpdateCleanupControls();
        }
    }
}
