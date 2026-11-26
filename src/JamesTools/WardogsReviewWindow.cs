using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using DataFormats = System.Windows.DataFormats;
using Binding = System.Windows.Data.Binding;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using JamesTools.Core.Scanning;
namespace JamesTools;

public sealed class WardogsReviewWindow : Window
{
    private readonly WardogsReviewStore store;
    private readonly Action? scansChanged;
    private readonly Func<string, Guid?>? findSession;
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
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 80, Text = "Choose or drop recordings to start scanning.", Margin = new Thickness(3, 5, 3, 0) };
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock money = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 8, 3, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock intervals = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button stopScan = new() { Content = "Cancel scan" };
    private readonly Button openMoment = new() { Content = "Open moment" };
    private readonly Button export = new() { Content = "Export…" };
    private readonly Button stopExport = new() { Content = "Cancel export" };
    private readonly ActionMenu scanActions = new() { Content = "Scan actions" };
    private readonly ActionMenu bulkDecision = new() { Content = "Set decision for selection" };
    private readonly MenuItem cancelPending, rescan, removeScan;
    private readonly Expander timingOptions = new() { Header = "Highlight timing", Padding = new Thickness(0, 5, 0, 0) };
    private readonly TextBox tools = new(), fps = new(), decodeThreads = new(), crop = new(), pre = new(), post = new(), gap = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim additions = new(1);
    private CancellationTokenSource? current;
    private Guid? runningScan;
    private Task? pump, initialization;
    private bool ready, closing, allowClose, refreshing; private readonly TaskCompletionSource<bool> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SavedWardogsScan? selected;
    private int generation, selectionRequest, reviewRevision;
    public sealed class Row
    {
        public required WardogsCandidate Evidence { get; init; }
        public double Seconds => Evidence.Seconds;
        public string VideoTime => WardogsDisplay.VideoTime(Seconds);
        public string Kind => WardogsDisplay.EventName(Evidence.Kind);
        public string Text => Evidence.Text;
        public double? Confidence => Evidence.Confidence;
        public string Reward => Evidence.Reward is long reward ? WardogsDisplay.Cash(reward) : "—";
        public ReviewDecision Decision { get; set; }
        public string Label { get; set; } = "";
    }

    private sealed class VideoTimeColumn(Func<Row, Task> open) : DataGridColumn
    {
        protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
        {
            var text = new Run();
            text.SetBinding(Run.TextProperty, new Binding(nameof(Row.VideoTime)) { Mode = BindingMode.OneWay });
            var link = new Hyperlink(text) { ToolTip = "Open the video at this event" };
            link.SetResourceReference(Hyperlink.ForegroundProperty, "AccentBrush");
            link.Click += async (_, _) => { if (link.DataContext is Row row) await open(row); };
            return new TextBlock(link) { VerticalAlignment = VerticalAlignment.Center };
        }

        protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => GenerateElement(cell, dataItem);
    }
    public WardogsReviewWindow(string directory, string toolsFolder, Action<string, TimeSpan, Guid?, string> playback, Action<WardogsPublishedExport>? published = null)
        : this(new WardogsReviewStore(directory, new NativeWardogsScanWork(WindowsWardogsOcr.Create)), toolsFolder, playback, published, null) { }

    internal WardogsReviewWindow(WardogsReviewStore store, string toolsFolder, Action<string, TimeSpan, Guid?, string> playback,
        Action<WardogsPublishedExport>? published, Action? scansChanged, Func<string, Guid?>? findSession = null)
    {
        this.store = store;
        this.published = published;
        this.playback = playback;
        this.scansChanged = scansChanged;
        this.findSession = findSession;
        WindowTheme.Apply(this);
        Title = "WARDOGS recording review"; Width = 1150; Height = 850; MinWidth = 800; MinHeight = 650;
        var root = new Grid { Margin = new Thickness(16) }; Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 120 });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel(); root.Children.Add(top);
        var buttons = new WrapPanel(); top.Children.Add(buttons);
        Button AddButton(Panel panel, string label, Action action) { var b = new Button { Content = label, Margin = new Thickness(3) }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }; panel.Children.Add(b); return b; }
        AddButton(buttons, "Scan recordings…", () => { var p = new Microsoft.Win32.OpenFileDialog { Title = "Choose recordings to scan", Multiselect = true, Filter = "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v" }; if (p.ShowDialog(this) == true) _ = AddFilesAsync(p.FileNames, null); })
            .SetResourceReference(StyleProperty, "AccentButton");
        stopScan.Click += (_, _) => current?.Cancel(); buttons.Children.Add(stopScan);
        buttons.Children.Add(scanActions);
        rescan = scanActions.Add("Rescan selected", (_, _) => { if (selected is { } s) _ = AddFilesAsync([s.Source], s.SessionId); });
        cancelPending = scanActions.Add("Cancel queued manual scans", async (_, _) => await Safe(async () => { await store.CancelPendingAsync(); await Refresh(); }));
        removeScan = scanActions.Add("Remove saved scan", async (_, _) => await RemoveSelectedScan());
        top.Children.Add(status);
        top.Children.Add(money);
        status.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = status });
        var advanced = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var settingsPanel = new StackPanel();
        settingsPanel.Children.Add(new Expander { Header = "Tools and sampling", Content = advanced, Margin = new Thickness(0, 8, 0, 0) });
        var topScroll = new ScrollViewer { Content = settingsPanel, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        top.Children.Add(topScroll);
        void Field(Panel panel, string label, TextBox box, string value) { var line = new StackPanel { Margin = new Thickness(0, 4, 10, 5) }; line.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) }); box.Text = value; line.Children.Add(box); panel.Children.Add(line); }
        var defaultThreads = WardogsScanOptions.DefaultDecodeThreads.ToString(CultureInfo.InvariantCulture);
        Field(advanced, $"Video decode threads (1–{WardogsScanOptions.MaximumDecodeThreads})", decodeThreads, defaultThreads);
        System.Windows.Automation.AutomationProperties.SetName(decodeThreads, "Video decode threads");
        decodeThreads.ToolTip = "Higher values use more CPU. Changes apply to newly queued scans.";
        Field(advanced, "Custom FFmpeg folder (blank uses bundled tools)", tools, toolsFolder);
        var toolsBrowse = new WrapPanel(); advanced.Children.Add(toolsBrowse); AddButton(toolsBrowse, "Browse FFmpeg folder…", () => { var p = new Microsoft.Win32.OpenFolderDialog(); if (p.ShowDialog(this) == true) tools.Text = p.FolderName; });
        Field(advanced, "Samples per second (.1–10)", fps, "2"); Field(advanced, "Normalized crop: X,Y,Width,Height", crop, "0.65,0,0.35,0.35");
        var samplingButtons = new WrapPanel(); advanced.Children.Add(samplingButtons); AddButton(samplingButtons, "Reset sampling", () => { fps.Text = "2"; decodeThreads.Text = defaultThreads; crop.Text = "0.65,0,0.35,0.35"; });
        var sourcePanel = new StackPanel(); sourcePanel.Children.Add(new TextBlock { Text = "Saved scans", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) }); scans.Height = 80; sourcePanel.Children.Add(scans); sourcePanel.Children.Add(detail);
        var sourceScroll = new ScrollViewer { Content = sourcePanel, MaxHeight = 135, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 8) }; Grid.SetRow(sourceScroll, 1); root.Children.Add(sourceScroll);
        var bottom = new StackPanel(); var bottomScroll = new ScrollViewer { Content = bottom, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0) }; Grid.SetRow(bottomScroll, 3); root.Children.Add(bottomScroll);
        root.SizeChanged += (_, e) => { topScroll.MaxHeight = Math.Max(90, e.NewSize.Height * .18); sourceScroll.MaxHeight = Math.Max(90, e.NewSize.Height * .18); bottomScroll.MaxHeight = Math.Max(150, e.NewSize.Height * .31); };
        var review = new WrapPanel(); bottom.Children.Add(review);
        review.Children.Add(openMoment); review.Children.Add(export); review.Children.Add(stopExport); review.Children.Add(bulkDecision);
        openMoment.Click += async (_, _) => await OpenSelected();
        export.Click += (_, _) => { if (exportTask is null || exportTask.IsCompleted) exportTask = Export(); };
        stopExport.Click += (_, _) => exportCancel?.Cancel();
        foreach (var decision in Enum.GetValues<ReviewDecision>())
            bulkDecision.Add(decision.ToString(), async (_, _) =>
            {
                foreach (Row row in table.SelectedItems) row.Decision = decision;
                table.Items.Refresh();
                await SaveReview();
            });
        bottom.Children.Add(exportStatus);
        var preview = new DockPanel(); DockPanel.SetDock(thumbnail, Dock.Left); preview.Children.Add(thumbnail); preview.Children.Add(thumbnailStatus); bottom.Children.Add(preview);
        bottom.Children.Add(timingOptions);
        var timing = new StackPanel(); timingOptions.Content = timing;
        var timingFields = new WrapPanel(); timing.Children.Add(timingFields); pre.Width = post.Width = gap.Width = 130;
        Field(timingFields, "Before (seconds)", pre, "10"); Field(timingFields, "After (seconds)", post, "5"); Field(timingFields, "Merge gap (seconds)", gap, "0");
        timing.Children.Add(intervals);
        timing.Children.Add(new TextBlock { Text = "Highlight cuts follow nearby keyframes and retain all audio tracks.", TextWrapping = TextWrapping.Wrap });
        foreach (var box in new[] { pre, post, gap }) box.LostKeyboardFocus += async (_, _) => await SaveReview();
        table.Columns.Add(new VideoTimeColumn(OpenEvent) { Header = "Video time", IsReadOnly = true, SortMemberPath = nameof(Row.Seconds), Width = 102 });
        foreach (var (name, title, width) in new[] { ("Kind", "Event", 125), ("Text", "OCR text", 0), ("Reward", "Reward", 83), ("Confidence", "OCR confidence", 98) })
            table.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(name) { TargetNullValue = "—" }, IsReadOnly = true, Width = width == 0 ? new DataGridLength(2, DataGridLengthUnitType.Star) : width, MinWidth = width == 0 ? 150 : 55 });
        var decisionDisplay = new Style(typeof(System.Windows.Controls.ComboBox), (Style)FindResource(typeof(System.Windows.Controls.ComboBox)));
        decisionDisplay.Setters.Add(new Setter(IsHitTestVisibleProperty, false)); decisionDisplay.Setters.Add(new Setter(FocusableProperty, false));
        table.Columns.Add(new DataGridComboBoxColumn { Header = "Decision", Width = 125, ElementStyle = decisionDisplay, EditingElementStyle = (Style)FindResource(typeof(System.Windows.Controls.ComboBox)), ItemsSource = Enum.GetValues<ReviewDecision>(), SelectedItemBinding = new Binding("Decision") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        table.Columns.Add(new DataGridTextColumn { Header = "Label", MinWidth = 120, Width = new DataGridLength(1, DataGridLengthUnitType.Star), EditingElementStyle = (Style)FindResource(typeof(TextBox)), Binding = new Binding("Label") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } }); Grid.SetRow(table, 2); root.Children.Add(table);
        scans.SelectionChanged += (_, _) => { if (!refreshing) _ = SelectScan(); };
        table.SelectionChanged += (_, _) => { UpdateActions(); thumbnailCancel?.Cancel(); thumbnailTask = Thumbnail(); };
        table.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = SaveReview()));
        AllowDrop = true; Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _ = AddFilesAsync(paths, null); };
        Loaded += async (_, _) => { if (!closing) { initialization = Initialize(); await initialization; } };
        Closing += async (_, e) => { if (allowClose) return; e.Cancel = true; if (closing) return; if (await ShutdownAsync()) _ = Dispatcher.BeginInvoke(new Action(Close)); };
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool completed = selected?.Status == ScanStatus.Completed;
        stopScan.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
        rescan.IsEnabled = removeScan.IsEnabled = selected?.Status is ScanStatus.Completed or ScanStatus.Cancelled or ScanStatus.Failed or ScanStatus.Interrupted;
        cancelPending.IsEnabled = scans.Items.OfType<SavedWardogsScan>().Any(scan => !scan.Background && scan.Status == ScanStatus.Queued && scan.Id != runningScan);
        scanActions.Visibility = rescan.IsEnabled || cancelPending.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        openMoment.Visibility = completed && table.SelectedItems.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        bulkDecision.Visibility = completed && table.SelectedItems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        export.Visibility = completed && exportCancel is null && selected!.Reviews.Any(row => row.Decision == ReviewDecision.Keep) ? Visibility.Visible : Visibility.Collapsed;
        stopExport.Visibility = exportCancel is null ? Visibility.Collapsed : Visibility.Visible;
        timingOptions.Visibility = completed ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task RemoveSelectedScan() => Safe(async () =>
    {
        if (selected is not { } scan) return;
        await store.RemoveAsync(scan.Id);
        if (selected?.Id == scan.Id)
        {
            selected = null; generation++; table.ItemsSource = null; detail.Text = ""; intervals.Text = "";
        }
        await Refresh();
        scansChanged?.Invoke();
    });
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static double Number(TextBox box) => double.Parse(box.Text, CultureInfo.InvariantCulture);
    private static string EngineLabel(WardogsOcrEngine engine) => engine == WardogsOcrEngine.Windows ? "Windows OCR" : "Legacy OCR";
    private async Task Safe(Func<Task> action) { try { await action(); } catch (Exception ex) { status.Text = ex is OperationCanceledException ? "Operation cancelled." : ex.Message; } }
    private async Task Initialize()
    {
        try {
            await store.InitializeAsync(); var snap = await store.SnapshotAsync(); var p = snap.Preferences;
            if (closing) return;
            if (p.ToolsFolder.Length > 0 || p.Options is not null)
            {
                tools.Text = p.ToolsFolder;
                var o = p.Options ?? new();
                fps.Text = N(o.SampleFps);
                decodeThreads.Text = o.DecodeThreads.ToString(CultureInfo.InvariantCulture);
                crop.Text = string.Join(",", new[] { o.Crop.X, o.Crop.Y, o.Crop.Width, o.Crop.Height }.Select(N));
            }
            ready = true; await Refresh();
            if (store.RecoveryNotice.Length > 0) status.Text = store.RecoveryNotice;
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
            var inputs = paths.Take(21).Select(JamesTools.Core.Recording.MediaTools.LocalVideo).ToArray();
            if (inputs.Length == 0) return;
            if (!int.TryParse(decodeThreads.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int threads))
                throw new ArgumentException($"Enter a whole number from 1 to {WardogsScanOptions.MaximumDecodeThreads} for video decode threads.");
            var c = crop.Text.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray(); if (c.Length != 4) throw new ArgumentException("Enter four normalized crop values.");
            var o = new WardogsScanOptions { SampleFps = Number(fps), DecodeThreads = threads, Crop = new(c[0], c[1], c[2], c[3]) };
            o.Validate();
            var preferences = new ScanPreferences(tools.Text, o);
            _ = JamesTools.Core.Recording.MediaTools.Resolve(tools.Text, "ffmpeg");
            _ = JamesTools.Core.Recording.MediaTools.Resolve(tools.Text, "ffprobe");
            var snapshot = await store.SnapshotAsync();
            if (inputs.Length + snapshot.Scans.Count(s => s.Status is ScanStatus.Queued or ScanStatus.Running) > 20 || inputs.Length + snapshot.Scans.Length > 200) throw new InvalidOperationException("Queue limit 20; library limit 200. Cancel jobs or remove older scans first.");
            Guid? firstQueued = null;
            foreach (var path in inputs)
            {
                if (closing) break;
                var queued = await store.EnqueueAsync(path, sessionId ?? findSession?.Invoke(path), preferences);
                firstQueued ??= queued.Id;
            }
            await Refresh(selectScanId: current is null ? firstQueued : null);
            if (!closing && (pump is null || pump.IsCompleted)) pump = Pump();
            } finally { additions.Release(); }
        });
    }
    private async Task Pump()
    {
        await Safe(async () => { while (!closing) {
            var next = (await store.SnapshotAsync()).Scans.FirstOrDefault(s => !s.Background && s.Status == ScanStatus.Queued); if (next is null) break;
            using var active = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); current = active; runningScan = next.Id; UpdateActions();
            var fileName = Path.GetFileName(next.Source);
            status.Text = $"Preparing to scan {fileName}…";
            var progress = new Progress<WardogsProgress>(p =>
            {
                if (closing || !ReferenceEquals(current, active)) return;
                status.Text = p.EstimatedRemaining switch
                {
                    null => $"Scanning {fileName} · {p.Seconds:0.0} of {p.Duration:0.0} seconds · Estimating time remaining…",
                    { Ticks: 0 } => $"Finishing scan for {fileName}…",
                    TimeSpan remaining => $"Scanning {fileName} · {p.Seconds:0.0} of {p.Duration:0.0} seconds · About {FormatRemaining(remaining)} remaining"
                };
            });
            try { await store.RunAsync(next.Id, progress, active.Token); }
            finally { current = null; runningScan = null; UpdateActions(); }
            await Refresh(next.Id);
            scansChanged?.Invoke();
        } });
    }

    internal static string FormatRemaining(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return rounded.TotalHours >= 1
            ? $"{(long)rounded.TotalHours}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes}:{rounded.Seconds:00}";
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

    internal Task RefreshFromBackgroundAsync() => ready && !closing ? Safe(async () => await Refresh()) : Task.CompletedTask;

    internal Task ShowSavedScanAsync(Guid id) => Safe(async () =>
    {
        if (await initialized.Task.WaitAsync(lifetime.Token)) await Refresh(selectScanId: id);
    });

    private async Task Refresh(Guid? finishedScanId = null, Guid? selectScanId = null)
    {
        if (closing) return;
        int request = ++selectionRequest;
        if (!await SaveReview()) return;
        var snap = await store.SnapshotAsync();
        if (closing) return;
        if (finishedScanId is Guid id && snap.Scans.FirstOrDefault(s => s.Id == id) is { } finished)
        {
            var fileName = Path.GetFileName(finished.Source);
            status.Text = finished.Status switch
            {
                ScanStatus.Completed when finished.Result is { } result => $"Scan complete for {fileName} · {EngineLabel(result.OcrEngine)} · {result.Candidates.Count} candidate moments. See Saved scans.",
                ScanStatus.Failed => $"Couldn't scan {fileName}. {finished.Error}",
                ScanStatus.Cancelled => $"Scan cancelled for {fileName}.",
                _ => status.Text
            };
        }
        if (request != selectionRequest) return;
        var chosen = snap.Scans.FirstOrDefault(s => s.Id == (selectScanId ?? selected?.Id)) ?? snap.Scans.LastOrDefault();
        refreshing = true;
        try { scans.ItemsSource = snap.Scans.Reverse().ToArray(); scans.SelectedItem = chosen; }
        finally { refreshing = false; }
        table.IsEnabled = pre.IsEnabled = post.IsEnabled = gap.IsEnabled = true;
        if (chosen is not null) { if (chosen.Id != selected?.Id || selected.Status != ScanStatus.Completed) Show(chosen); }
        else { selected = null; generation++; table.ItemsSource = null; detail.Text = ""; intervals.Text = ""; money.Visibility = Visibility.Collapsed; }
        UpdateActions();
    }
    private void Show(SavedWardogsScan s)
    {
        selected = s; generation++;
        var engine = s.Result?.OcrEngine ?? WardogsOcrEngine.Windows;
        detail.Text = $"{s.Source}\nSession: {s.SessionId?.ToString() ?? "Unassigned"} · {s.Status}{(s.Background ? " (background)" : "")} · {s.Error}\n{EngineLabel(engine)} · {s.Preferences.Options?.SampleFps ?? 2} samples/sec";
        detail.ToolTip = $"{s.Fingerprint}\nCrop: {s.Preferences.Options?.Crop ?? new()}";
        bool hasMoney = s.Result is { } result && WardogsDisplay.ShowRecordingMoney(money, WardogsMoneySummary.ForRecording(result),
            balance => OpenAt(s, balance.Seconds));
        if (!hasMoney) money.Text = "No balance or cash rewards confirmed in this recording.";
        money.Visibility = hasMoney || s.Result?.DetectorVersion == WardogsScanner.Version ? Visibility.Visible : Visibility.Collapsed;
        var reviews = s.Reviews.ToDictionary(r => r.CandidateId);
        table.ItemsSource = s.Result?.Candidates.Select(c => { var r = reviews.GetValueOrDefault(c.Id); return new Row { Evidence = c, Decision = r?.Decision ?? ReviewDecision.Unreviewed, Label = r?.Label ?? "" }; }).ToArray() ?? [];
        pre.Text = N(s.Pre); post.Text = N(s.Post); gap.Text = N(s.MergeGap); Preview(s); UpdateActions();
    }
    private void Preview(SavedWardogsScan s) { intervals.Text = "Counts: " + string.Join(" · ", (s.Result?.Candidates ?? []).GroupBy(c => c.Kind).Select(g => $"{WardogsDisplay.EventName(g.Key)}: {g.Count()}")) + "\nKEEP intervals: " + string.Join("; ", s.Intervals().Take(100).Select(i => $"{i.Start:0.##}–{i.End:0.##}s")); }
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
            if (g == generation && version == reviewRevision && selected?.Id == s.Id) { selected = next; Preview(next); UpdateActions(); }
            return true;
        } catch (Exception ex) { status.Text = "Review changes were not saved: " + ex.Message; return false; }
    }
    private Task OpenSelected() => table.SelectedItem is Row row ? OpenEvent(row) : Task.CompletedTask;

    private Task OpenEvent(Row row) => selected is { Status: ScanStatus.Completed } scan ? OpenAt(scan, row.Seconds) : Task.CompletedTask;

    private Task OpenAt(SavedWardogsScan scan, double seconds) => Safe(async () =>
    {
        if (closing || selected?.Id != scan.Id) return;
        int g = generation;
        using var guard = await WardogsExporter.VerifiedSourceAsync(scan, lifetime.Token);
        if (closing || g != generation) return;
        playback(scan.Source, TimeSpan.FromSeconds(seconds), scan.SessionId, scan.Preferences.ToolsFolder);
        status.Text = $"Opened video at {WardogsDisplay.VideoTime(seconds)}.";
    });
    private async Task Export()
    {
        try {
            if (closing || selected is not { Status: ScanStatus.Completed }) return;
            // Capture visible decisions before the first await; later selection/edits cannot retarget this export.
            var snap = WardogsExporter.Capture(CaptureReview());
            await store.ReviewAsync(snap.Id, snap.Reviews, snap.Pre, snap.Post, snap.MergeGap);
            if (closing) return;
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Highlight video|*.mkv|Marker CSV|*.csv|Subtitle markers|*.srt", FileName = "wardogs-highlights" };
            if (picker.ShowDialog(this) != true || closing) return;
            var kind = Path.GetExtension(picker.FileName).ToLowerInvariant() switch
            {
                ".mkv" => WardogsExportKind.Montage,
                ".csv" => WardogsExportKind.Csv,
                ".srt" => WardogsExportKind.Srt,
                _ => throw new IOException("Choose a .mkv, .csv or .srt file.")
            };
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); exportCancel = cancel; UpdateActions();
            var progress = new Progress<string>(text => { if (!closing && ReferenceEquals(exportCancel, cancel)) exportStatus.Text = text; });
            var result = await Task.Run(() => new WardogsExporter(new(snap.Preferences.ToolsFolder)).ExportAsync(snap, picker.FileName, kind, progress, cancel.Token), cancel.Token);
            exportStatus.Text = "Published: " + result.Path + (result.VerifiedSeconds is { } seconds ? $" · requested {result.RequestedSeconds:0.###}s; verified {seconds:0.###}s · approximate keyframe cuts" : " · source timeline markers");
            if (result.CleanupWarning is not null) exportStatus.Text += " · " + result.CleanupWarning;
            // Shutdown awaits this task while the session store is still alive. Publication must retain its attachment.
            if (kind == WardogsExportKind.Montage) try { published?.Invoke(result); } catch (Exception ex) { exportStatus.Text += " · Published but attachment failed: " + ex.Message; }
        } catch (Exception ex) { if (!closing) exportStatus.Text = (ex is OperationCanceledException ? "Export cancelled." : "Export failed: " + ex.Message) + (ex.Data["CleanupWarning"] is string warning ? " " + warning : ""); }
        finally { exportCancel = null; UpdateActions(); }
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
                var guard = await WardogsExporter.VerifiedSourceAsync(scan, cancel.Token);
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
