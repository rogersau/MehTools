using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using DataFormats = System.Windows.DataFormats;
using Binding = System.Windows.Data.Binding;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using JamesTools.Core.Scanning;
namespace JamesTools;

public sealed class WardogsReviewWindow : Window
{
    private readonly WardogsReviewStore store;
    private readonly Action<WardogsPublishedExport>? published;
    private CancellationTokenSource? exportCancel, thumbnailCancel;
    private Task? exportTask, thumbnailTask;
    private FileStream? previewGuard;
    private Guid? previewScan;
    private readonly System.Windows.Controls.Image thumbnail = new() { Height = 90, Width = 180, Stretch = System.Windows.Media.Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly TextBlock exportStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock thumbnailStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Action<string, TimeSpan, Guid?, string> playback;
    private readonly ListBox scans = new();
    private readonly DataGrid table = new() { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, SelectionMode = DataGridSelectionMode.Extended, EnableRowVirtualization = true, EnableColumnVirtualization = true };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock intervals = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox tools = new(), tess = new(), fps = new(), confidence = new(), crop = new(), pre = new(), post = new(), gap = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim additions = new(1);
    private CancellationTokenSource? current;
    private Task? pump, initialization;
    private bool ready, closing, allowClose, refreshing; private readonly TaskCompletionSource<bool> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SavedWardogsScan? selected;
    private int generation, selectionRequest, reviewRevision;
    public sealed class Row
    {
        public required WardogsCandidate Evidence { get; init; }
        public double Seconds => Evidence.Seconds;
        public string Kind => Evidence.Kind.ToString();
        public string Text => Evidence.Text;
        public double Confidence => Evidence.Confidence;
        public ReviewDecision Decision { get; set; }
        public string Label { get; set; } = "";
    }
    public WardogsReviewWindow(string directory, string toolsFolder, Action<string, TimeSpan, Guid?, string> playback, Action<WardogsPublishedExport>? published = null)
    {
        this.published = published; this.playback = playback; store = new(directory, new NativeWardogsScanWork());
        Title = "WARDOGS recording review"; Width = 1150; Height = 850; MinWidth = 800; MinHeight = 700;
        var root = new DockPanel { Margin = new Thickness(12), Background = System.Windows.SystemColors.WindowBrush }; Content = root;
        var top = new StackPanel(); var topScroll = new ScrollViewer { Content = top, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; DockPanel.SetDock(topScroll, Dock.Top); root.Children.Add(topScroll);
        top.Children.Add(new TextBlock { Text = "Review detected moments before keeping them. Detection accuracy needs calibration on gameplay. Files added here remain unassigned.", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel(); top.Children.Add(buttons);
        void Button(Panel panel, string label, Action action) { var b = new Button { Content = label, Margin = new Thickness(3) }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }; panel.Children.Add(b); }
        Button(buttons, "Add files…", () => { var p = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v" }; if (p.ShowDialog(this) == true) _ = AddFilesAsync(p.FileNames, null); });
        Button(buttons, "Cancel current", () => current?.Cancel());
        Button(buttons, "Remove saved scan", () => _ = Safe(async () => { if (selected is { } s) { await store.RemoveAsync(s.Id); if (selected?.Id == s.Id) { selected = null; generation++; table.ItemsSource = null; detail.Text = ""; intervals.Text = ""; } await Refresh(); } }));
        Button(buttons, "Cancel pending", () => _ = Safe(async () => { await store.CancelPendingAsync(); await Refresh(); }));
        Button(buttons, "Rescan selected", () => { if (selected is { } s) _ = AddFilesAsync([s.Source], s.SessionId); });
        var advanced = new StackPanel(); top.Children.Add(new Expander { Header = "Tools and advanced sampling (saved with each queued file)", Content = new ScrollViewer { Content = advanced, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        void Field(Panel panel, string label, TextBox box, string value) { var line = new DockPanel(); line.Children.Add(new TextBlock { Text = label, Width = 240 }); box.Text = value; line.Children.Add(box); panel.Children.Add(line); }
        Field(advanced, "FFmpeg folder (blank uses resolver)", tools, toolsFolder);
        Button(advanced, "Browse FFmpeg folder…", () => { var p = new Microsoft.Win32.OpenFolderDialog(); if (p.ShowDialog(this) == true) tools.Text = p.FolderName; });
        Field(advanced, "Tesseract executable (requires eng data)", tess, "");
        Button(advanced, "Browse Tesseract…", () => { var p = new Microsoft.Win32.OpenFileDialog { Filter = "Executable|*.exe" }; if (p.ShowDialog(this) == true) tess.Text = p.FileName; });
        Field(advanced, "Samples per second (.1–10)", fps, "2"); Field(advanced, "Minimum OCR confidence (0–100)", confidence, "35"); Field(advanced, "Normalized crop: X,Y,Width,Height", crop, "0.65,0,0.35,0.35");
        Button(advanced, "Reset sampling", () => { fps.Text = "2"; confidence.Text = "35"; crop.Text = "0.65,0,0.35,0.35"; });
        top.Children.Add(status); scans.Height = 115; top.Children.Add(scans); top.Children.Add(detail);
        var bottom = new StackPanel(); var bottomScroll = new ScrollViewer { Content = bottom, MaxHeight = 270, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; DockPanel.SetDock(bottomScroll, Dock.Bottom); root.Children.Add(bottomScroll);
        var exports = new WrapPanel(); bottom.Children.Add(exports);
        foreach (var kind in Enum.GetValues<WardogsExportKind>()) Button(exports, kind == WardogsExportKind.Montage ? "Export highlights (.mkv)…" : "Export " + kind.ToString().ToUpperInvariant() + "…", () => { if (exportTask is null || exportTask.IsCompleted) exportTask = Export(kind); });
        Button(exports, "Cancel export", () => exportCancel?.Cancel());
        Button(exports, "Clear export result", () => exportStatus.Text = "");
        bottom.Children.Add(exportStatus);
        bottom.Children.Add(new TextBlock { Text = "Highlight cuts are approximate and can include extra footage around keyframes. Every audio track is retained.", TextWrapping = TextWrapping.Wrap });
        var preview = new DockPanel(); DockPanel.SetDock(thumbnail, Dock.Left); preview.Children.Add(thumbnail); preview.Children.Add(thumbnailStatus); bottom.Children.Add(preview);
        var review = new WrapPanel(); bottom.Children.Add(review);
        foreach (var d in Enum.GetValues<ReviewDecision>()) Button(review, "Set selected " + d, () => { foreach (Row row in table.SelectedItems) row.Decision = d; table.Items.Refresh(); _ = SaveReview(); });
        Button(review, "Save decisions / labels / intervals", () => _ = SaveReview());
        Button(review, "Verify source and open selected timestamp", () => _ = OpenSelected());
        Field(bottom, "Pre seconds", pre, "10"); Field(bottom, "Post seconds", post, "5"); Field(bottom, "Merge gap seconds", gap, "0"); bottom.Children.Add(new ScrollViewer { Content = intervals, MaxHeight = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        foreach (var name in new[] { "Seconds", "Kind", "Text", "Confidence" }) table.Columns.Add(new DataGridTextColumn { Header = name == "Confidence" ? "OCR confidence" : name, Binding = new Binding(name), IsReadOnly = true, Width = name == "Text" ? new DataGridLength(2, DataGridLengthUnitType.Star) : name == "Confidence" ? 110 : 80, MinWidth = name == "Text" ? 160 : 65 });
        table.Columns.Add(new DataGridComboBoxColumn { Header = "Decision", Width = 110, ItemsSource = Enum.GetValues<ReviewDecision>(), SelectedItemBinding = new Binding("Decision") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        table.Columns.Add(new DataGridTextColumn { Header = "Label", MinWidth = 120, Width = new DataGridLength(1, DataGridLengthUnitType.Star), Binding = new Binding("Label") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } }); root.Children.Add(table);
        scans.SelectionChanged += (_, _) => { if (!refreshing) _ = SelectScan(); };
        table.SelectionChanged += (_, _) => { thumbnailCancel?.Cancel(); thumbnailTask = Thumbnail(); };
        table.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = SaveReview()));
        AllowDrop = true; Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddFilesAsync(paths, null); };
        Loaded += async (_, _) => { if (!closing) { initialization = Initialize(); await initialization; } };
        Closing += async (_, e) => { if (allowClose) return; e.Cancel = true; if (closing) return; if (await ShutdownAsync()) Close(); };
    }
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static double Number(TextBox box) => double.Parse(box.Text, CultureInfo.InvariantCulture);
    private async Task Safe(Func<Task> action) { try { await action(); } catch (Exception ex) { status.Text = ex is OperationCanceledException ? "Operation cancelled." : ex.Message; } }
    private async Task Initialize()
    {
        try {
            await store.InitializeAsync(); var snap = await store.SnapshotAsync(); var p = snap.Preferences;
            if (closing) return;
            if (p.Tesseract.Length > 0) { tools.Text = p.ToolsFolder; tess.Text = p.Tesseract; var o = p.Options ?? new(); fps.Text = N(o.SampleFps); confidence.Text = N(o.MinimumOcrConfidence); crop.Text = string.Join(",", new[] { o.Crop.X, o.Crop.Y, o.Crop.Width, o.Crop.Height }.Select(N)); }
            ready = true; await Refresh(); status.Text = store.RecoveryNotice;
        } catch (Exception ex) { status.Text = ex.Message; }
        finally { initialized.TrySetResult(ready); }
    }
    public async Task AddFilesAsync(IEnumerable<string> paths, Guid? sessionId)
    {
        await Safe(async () => {
            if (!ready) await initialized.Task.WaitAsync(lifetime.Token); if (!ready || closing) throw new InvalidOperationException("Scan library is not ready.");
            await additions.WaitAsync(lifetime.Token);
            try {
            if (closing || lifetime.IsCancellationRequested) return;
            var c = crop.Text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(); if (c.Length != 4) throw new ArgumentException("Enter four normalized crop values.");
            var o = new WardogsScanOptions { SampleFps = Number(fps), MinimumOcrConfidence = Number(confidence), Crop = new(c[0], c[1], c[2], c[3]) }; o.Validate();
            _ = new FfmpegTesseractFrames(JamesTools.Core.Recording.MediaTools.Resolve(tools.Text, "ffmpeg"), JamesTools.Core.Recording.MediaTools.Resolve(tools.Text, "ffprobe"), tess.Text);
            var inputs = paths.Take(21).Select(JamesTools.Core.Recording.MediaTools.LocalVideo).ToArray();
            var preferences = new ScanPreferences(tools.Text, tess.Text, o);
            var snapshot = await store.SnapshotAsync();
            if (inputs.Length + snapshot.Scans.Count(s => s.Status is ScanStatus.Queued or ScanStatus.Running) > 20 || inputs.Length + snapshot.Scans.Length > 200) throw new InvalidOperationException("Queue limit 20; library limit 200. Cancel jobs or remove older scans first.");
            foreach (var path in inputs) { if (closing) break; await store.EnqueueAsync(path, sessionId, preferences); }
            await Refresh(); if (!closing && (pump is null || pump.IsCompleted)) pump = Pump();
            } finally { additions.Release(); }
        });
    }
    private async Task Pump()
    {
        await Safe(async () => { while (!closing) {
            var next = (await store.SnapshotAsync()).Scans.FirstOrDefault(s => s.Status == ScanStatus.Queued); if (next is null) break;
            using var active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); current = active;
            status.Text = "Fingerprinting / scanning " + next.Source + " · pinned session " + (next.SessionId?.ToString() ?? "Unassigned");
            var progress = new Progress<WardogsProgress>(p => { if (!closing && ReferenceEquals(current, active)) status.Text = $"Scanning {Path.GetFileName(next.Source)}: {p.Seconds:0.0}/{p.Duration:0.0}s · {p.Samples} samples"; });
            try { await store.RunAsync(next.Id, progress, active.Token); }
            finally { current = null; }
            await Refresh();
        } });
    }
    private async Task SelectScan()
    {
        await Safe(async () => {
            if (closing || scans.SelectedItem is not SavedWardogsScan choice) return;
            int request = ++selectionRequest;
            table.IsEnabled = pre.IsEnabled = post.IsEnabled = gap.IsEnabled = false;
            try {
            if (!await SaveReview()) {
                if (request == selectionRequest) { refreshing = true; scans.SelectedItem = scans.Items.Cast<SavedWardogsScan>().FirstOrDefault(s => s.Id == selected?.Id); refreshing = false; }
                return;
            }
            var fresh = (await store.SnapshotAsync()).Scans.FirstOrDefault(s => s.Id == choice.Id);
            if (!closing && request == selectionRequest && fresh is not null) Show(fresh);
            } finally { if (request == selectionRequest) table.IsEnabled = pre.IsEnabled = post.IsEnabled = gap.IsEnabled = true; }
        });
    }
    private async Task Refresh()
    {
        if (closing) return;
        int request = ++selectionRequest;
        if (!await SaveReview()) return;
        var snap = await store.SnapshotAsync();
        if (closing || request != selectionRequest) return;
        var chosen = snap.Scans.FirstOrDefault(s => s.Id == selected?.Id) ?? snap.Scans.LastOrDefault();
        refreshing = true;
        try { scans.ItemsSource = snap.Scans.Reverse().ToArray(); scans.SelectedItem = chosen; }
        finally { refreshing = false; }
        table.IsEnabled = pre.IsEnabled = post.IsEnabled = gap.IsEnabled = true;
        if (chosen is not null) { if (chosen.Id != selected?.Id || selected.Status != ScanStatus.Completed) Show(chosen); }
        else { selected = null; generation++; table.ItemsSource = null; detail.Text = ""; intervals.Text = ""; }
    }
    private void Show(SavedWardogsScan s)
    {
        selected = s; generation++; detail.Text = $"{s.Source}\nSession: {s.SessionId?.ToString() ?? "Unassigned"} · {s.Status} · {s.Error}\n{s.Result?.DetectorVersion} · {s.Preferences.Options?.SampleFps ?? 2} samples/sec";
        detail.ToolTip = $"{s.Fingerprint}\nCrop: {s.Preferences.Options?.Crop ?? new()}";
        var reviews = s.Reviews.ToDictionary(r => r.CandidateId);
        table.ItemsSource = s.Result?.Candidates.Select(c => { var r = reviews.GetValueOrDefault(c.Id); return new Row { Evidence = c, Decision = r?.Decision ?? ReviewDecision.Unreviewed, Label = r?.Label ?? "" }; }).ToArray() ?? [];
        pre.Text = N(s.Pre); post.Text = N(s.Post); gap.Text = N(s.MergeGap); Preview(s);
    }
    private void Preview(SavedWardogsScan s) { intervals.Text = "Counts: " + string.Join(" · ", (s.Result?.Candidates ?? []).GroupBy(c => c.Kind).Select(g => $"{g.Key}: {g.Count()}")) + "\nKEEP intervals: " + string.Join("; ", s.Intervals().Take(100).Select(i => $"{i.Start:0.##}–{i.End:0.##}s")); }
    private SavedWardogsScan CaptureReview()
    {
        var s = selected ?? throw new InvalidOperationException("Choose a completed scan.");
        if (s.Status != ScanStatus.Completed) throw new InvalidOperationException("Choose a completed scan.");
        if (!table.CommitEdit(DataGridEditingUnit.Cell, true) || !table.CommitEdit(DataGridEditingUnit.Row, true)) throw new InvalidOperationException("Finish valid table edits first.");
        var rows = table.ItemsSource.Cast<Row>().Select(r => new CandidateReview(r.Evidence.Id, r.Decision, r.Label)).ToArray();
        return s with { Reviews = rows, Pre = Number(pre), Post = Number(post), MergeGap = Number(gap) };
    }
    private async Task<bool> SaveReview()
    {
        try {
            if (selected is not { Status: ScanStatus.Completed } s || allowClose) return true;
            var next = CaptureReview();
            int version = ++reviewRevision, g = generation;
            await store.ReviewAsync(s.Id, next.Reviews, next.Pre, next.Post, next.MergeGap);
            if (g == generation && version == reviewRevision && selected?.Id == s.Id) { selected = next; Preview(next); }
            return true;
        } catch (Exception ex) { status.Text = "Review changes were not saved: " + ex.Message; return false; }
    }
    private Task OpenSelected() => Safe(async () => { if (selected is not { Status: ScanStatus.Completed } s || table.SelectedItem is not Row row) return; int g = generation; status.Text = "Verifying source bytes…"; using var guard = await Task.Run(() => new FileStream(s.Source, FileMode.Open, FileAccess.Read, FileShare.Read), lifetime.Token); bool matches = await store.SourceMatchesAsync(s, lifetime.Token); if (closing || g != generation) return; if (!matches) { status.Text = "Source missing or changed. Playback disabled. Explicitly rescan to create new results."; return; } playback(s.Source, TimeSpan.FromSeconds(row.Seconds), s.SessionId, s.Preferences.ToolsFolder); status.Text = "Source verified; opened approximate timestamp."; });
    private async Task Export(WardogsExportKind kind)
    {
        try {
            if (closing || selected is not { Status: ScanStatus.Completed }) return;
            // Capture visible decisions before the first await; later selection/edits cannot retarget this export.
            var snap = WardogsExporter.Capture(CaptureReview());
            await store.ReviewAsync(snap.Id, snap.Reviews, snap.Pre, snap.Post, snap.MergeGap);
            if (closing) return;
            var ext = kind == WardogsExportKind.Montage ? "mkv" : kind == WardogsExportKind.Csv ? "csv" : "srt";
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = ext + "|*." + ext, DefaultExt = ext, FileName = "wardogs-highlights." + ext };
            if (picker.ShowDialog(this) != true || closing) return;
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); exportCancel = cancel;
            var progress = new Progress<string>(text => { if (!closing && ReferenceEquals(exportCancel, cancel)) exportStatus.Text = text; });
            var result = await Task.Run(() => new WardogsExporter(new(snap.Preferences.ToolsFolder)).ExportAsync(snap, picker.FileName, kind, progress, cancel.Token), cancel.Token);
            exportStatus.Text = "Published: " + result.Path + (result.VerifiedSeconds is { } seconds ? $" · requested {result.RequestedSeconds:0.###}s; verified {seconds:0.###}s · approximate keyframe cuts" : " · source timeline markers");
            if (result.CleanupWarning is not null) exportStatus.Text += " · " + result.CleanupWarning;
            // Shutdown awaits this task while the session store is still alive. Publication must retain its attachment.
            if (kind == WardogsExportKind.Montage) try { published?.Invoke(result); } catch (Exception ex) { exportStatus.Text += " · Published but attachment failed: " + ex.Message; }
        } catch (Exception ex) { if (!closing) exportStatus.Text = (ex is OperationCanceledException ? "Export cancelled." : "Export failed: " + ex.Message) + (ex.Data["CleanupWarning"] is string warning ? " " + warning : ""); }
        finally { exportCancel = null; }
    }
    private async Task Thumbnail()
    {
        var previous = thumbnailTask;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); thumbnailCancel = cancel;
        var scan = selected; var row = table.SelectedItem as Row; int g = generation;
        thumbnail.Source = null; thumbnail.Visibility = Visibility.Collapsed; thumbnailStatus.Text = "";
        string? path = null;
        try {
            if (previous is not null) await previous;
            cancel.Token.ThrowIfCancellationRequested();
            if (previewScan != scan?.Id) { if (previewGuard is not null) await previewGuard.DisposeAsync(); previewGuard = null; previewScan = null; }
            if (closing || scan is not { Status: ScanStatus.Completed } || row is null) return;
            if (previewGuard is null) {
                thumbnailStatus.Text = "Verifying source for preview…";
                var guard = await Task.Run(() => WardogsExporter.VerifiedSourceAsync(scan, cancel.Token), cancel.Token);
                if (closing || cancel.IsCancellationRequested || g != generation) { await guard.DisposeAsync(); return; }
                previewGuard = guard; previewScan = scan.Id;
            }
            path = await Task.Run(() => new JamesTools.Core.Recording.MediaTools(scan.Preferences.ToolsFolder).ThumbnailAsync(scan.Source, row.Seconds, cancel.Token), cancel.Token);
            if (closing || cancel.IsCancellationRequested || g != generation || !ReferenceEquals(table.SelectedItem, row)) return;
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(path); bitmap.EndInit(); bitmap.Freeze(); thumbnail.Source = bitmap; thumbnail.Visibility = Visibility.Visible;
            thumbnailStatus.Text = $"Approximate source {row.Seconds:0.###}s";
        } catch (Exception ex) { if (!closing && !cancel.IsCancellationRequested && g == generation) thumbnailStatus.Text = "Thumbnail unavailable; saved text remains available. " + ex.Message; }
        finally {
            try { if (path is not null && File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (!closing) thumbnailStatus.Text = "Thumbnail cleanup failed: " + ex.Message; }
            if (ReferenceEquals(thumbnailCancel, cancel)) thumbnailCancel = null;
        }
    }
    public async Task<bool> ShutdownAsync()
    {
        if (allowClose) return true; if (closing) return false;
        closing = true;
        try {
            if (!await SaveReview()) return false;
            lifetime.Cancel(); current?.Cancel(); exportCancel?.Cancel(); thumbnailCancel?.Cancel();
            if (exportTask is not null) await exportTask; if (thumbnailTask is not null) await thumbnailTask;
            if (previewGuard is not null) { await previewGuard.DisposeAsync(); previewGuard = null; previewScan = null; }
            await additions.WaitAsync();
            try { if (ready) await store.CancelPendingAsync(); }
            finally { additions.Release(); if (pump is not null) await pump; if (initialization is not null) await initialization; }
            allowClose = true; return true;
        } catch (Exception ex) { status.Text = "Could not finish saving the scan library: " + ex.Message; return false; }
        finally { if (!allowClose) closing = false; }
    }
}
