using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using JamesTools;
using JamesTools.Core.Configuration;
using JamesTools.Core.ConfigurationSnapshots;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Scanning;

internal static class UiAudit
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static string? output;
    static int layouts;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static T Field<T>(object value, string field) => (T)value.GetType().GetField(field, Private)!.GetValue(value)!;
    static void Set(object value, string field, object data) => value.GetType().GetField(field, Private)!.SetValue(value, data);
    static IEnumerable<DependencyObject> Tree(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    static void Render(Window window, string label, double width, double height)
    {
        window.ApplyTemplate();
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        Check(window.Foreground is SolidColorBrush { Color.R: > 180 }, label + " has no shared foreground");
        foreach (var control in Tree(content).OfType<Control>().Where(c => c is DataGrid or ListBox or ComboBox or DataGridColumnHeader or TextBox or PasswordBox))
        {
            if (control.Background is SolidColorBrush bg && bg.Color.A > 0)
                Check(bg.Color.R < 140 || control.Name == "Navigation", $"{label}: {control.GetType().Name} {control.Name} has a light surface");
        }
        if (window is WardogsReviewWindow review) Check(Field<DataGrid>(review, "table").ActualHeight >= 120, "Review table is squeezed out");
        if (window is LiveWardogsWindow live) Check(Field<ListBox>(live, "events").ActualHeight >= 48, "Live event history is squeezed out");
        if (window is SteamWardogsWindow steam)
            Check(((DataGrid)steam.FindName("EventsTable")).ActualHeight >= 100, "Steam event table is squeezed out at " + width + "×" + height);
        if (window is RecordingPlaybackWindow) Check(Tree(content).OfType<Border>().Single(b => b.Name == "PlaybackPreview").ActualHeight >= 140, "Playback preview is squeezed out");
        if (window is GameProfilesEditorWindow profiles) Check(((ComboBox)profiles.FindName("CaptureModeBox")).SelectedItem != null && ((ComboBox)profiles.FindName("PowerPlanBox")).SelectedItem != null, "Inherited profile values are displayed as empty selections");
        if (output != null)
        {
            // Paint only this synthetic visual tree; never capture the desktop or user's recordings.
            var backdrop = new DrawingVisual(); using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            var image = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32); image.Render(backdrop); image.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(output, $"{label}-{(int)width}.png")); encoder.Save(file);
        }
        layouts++;
    }
    static async Task Close(Window window)
    {
        bool closed = false; window.Closed += (_, _) => closed = true;
        window.Close(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(closed, window.GetType().Name + " did not close");
    }
    public static async Task Run(string data, string[] args)
    {
        McpSettingsTests.Run(data);
        int render = Array.IndexOf(args, "--render");
        if (render >= 0) { output = Path.GetFullPath(args[render + 1]); Directory.CreateDirectory(output); }
        var game = new GameRule { Name = "Synthetic game", ExecutablePath = @"C:\Synthetic\game.exe" };
        var windows = new List<(string Name, Window Window)> {
            ("benchmarks", new BenchmarkWindow(new BenchmarkStore(data), game.ExecutablePath, game.Name, (_,_,_,_,_) => throw new Exception("Unexpected benchmark capture"), _ => { })),
            ("configuration", new ConfigurationSnapshotsWindow(new ConfigSnapshotStore(data), game, (_,_) => { })),
            ("mcp-settings", new McpSettingsWindow(data)),
            ("history", new PerformanceHistoryWindow(new PerformanceHistoryStore(data), game.ExecutablePath, game.Name, [], (_,_) => { })),
            ("profiles", new GameProfilesEditorWindow([new GameProfile { Name = "Balanced" }], [], [game])),
            ("stutter", new PerformanceTimelineWindow(new ParsedPerformanceReport(new(3, 30, "1000/meanFrameMs", 60, 16.7, 40, 65, 4096, 1, 60, null, "Synthetic scene", "Hitch"), [new(0, 1, 40, 65, 4096, 0, 1, 0, 1, 0, 1, "test", 60, 60, 20, 0)], []) {
                Investigations = [StutterInvestigator.Create([new FrameTelemetry(1, 2, "QPC", "7:A", 100, 80, 20, 10, 1, 11, 1, 12, 13, 0, "Independent Flip", "Application")],
                    [new DiagnosticHardwareSample(0, 2, 0, 0, null, null, new Dictionary<string, double?> { ["gpuPercent"] = null, ["processReadMiBps"] = 4 })], [], [new(1, "Noticed hitch")])]
            }, "Synthetic spike")),
            ("timeline", new PerformanceTimelineWindow(new ParsedPerformanceReport(new(2, 30, "Synthetic", 60, 16.7, 40, 65, 4096, 1, 60, null, "Synthetic scene", "Test marker"), [new(0, 1, 40, 65, 4096, 0, 1, 0, 1, 0, 1, "test", 60, 60, 20, 0)], [new(1, "Synthetic stutter")]), "Synthetic report")),
            ("playback", new RecordingPlaybackWindow(Path.Combine(data, "synthetic-not-opened.mkv"), TimeSpan.Zero)),
            ("live", new LiveWardogsWindow(new(), _ => { })),
            ("steam-wardogs", SteamWardogsUiTests.CreateWindow(() => null))
        };
        foreach (var entry in windows)
        {
            Render(entry.Window, entry.Name, entry.Window.Width - 32, entry.Window.Height - 40);
            Render(entry.Window, entry.Name, Math.Max(entry.Window.MinWidth, 470) - 32, Math.Max(entry.Window.MinHeight, 420) - 40);
            await Close(entry.Window);
        }
        var seek = new TaskCompletionSource<(string Source, TimeSpan Position)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var review = new WardogsReviewWindow(data, "", (source, position, _, _) => seek.TrySetResult((source, position)));
        Check(Field<Button>(review, "export").Visibility == Visibility.Collapsed && Field<Button>(review, "stopScan").Visibility == Visibility.Collapsed,
            "Empty review should hide export and cancel actions");
        var fixtureSource = Path.Combine(data, "timed review.mkv");
        Directory.CreateDirectory(data);
        File.WriteAllText(fixtureSource, "synthetic source");
        SourceFingerprint fingerprint;
        using (var file = File.OpenRead(fixtureSource)) fingerprint = SourceFingerprint.Read(file);
        var fixture = new SavedWardogsScan { Source = fixtureSource, Status = ScanStatus.Completed, Fingerprint = fingerprint,
            Result = new("synthetic", "UI fixture", new(), 200, [new(1, WardogsEventKind.Kill, 45.5, "KILL SAMPLE", .9, .1, 3), new(2, WardogsEventKind.Headshot, 90, "HEADSHOT KILL SAMPLE", .95, .2, 3)])
                { Balances = new(new(5000, 4.5), new(5900, 195)) },
            Reviews = [new(1, ReviewDecision.Keep, "")] };
        typeof(WardogsReviewWindow).GetMethod("Show", Private, [typeof(SavedWardogsScan)])!.Invoke(review, [fixture]);
        Check(Field<Button>(review, "export").Visibility == Visibility.Visible && Field<Button>(review, "stopExport").Visibility == Visibility.Collapsed,
            "Review with a kept moment should show one export action");
        Set(review, "refreshing", true); Field<ListBox>(review, "scans").ItemsSource = new[] { fixture }; Field<ListBox>(review, "scans").SelectedItem = fixture; Set(review, "refreshing", false);
        foreach (bool expanded in new[] { false, true })
        {
            Render(review, "review", 1118, 810);
            foreach (var options in Tree((DependencyObject)review.Content).OfType<Expander>()) options.IsExpanded = expanded;
            Render(review, expanded ? "review-expanded" : "review-small", 768, 610);
        }
        var table = Field<DataGrid>(review, "table");
        var timeCell = (TextBlock)table.Columns[0].GetCellContent(table.Items[0]);
        var timeLink = timeCell.Inlines.OfType<Hyperlink>().Single();
        Check(timeLink.Inlines.OfType<Run>().Single().Text == "0:00:45.5", "the event link displays source time with fractional seconds");
        timeLink.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Check(await seek.Task.WaitAsync(TimeSpan.FromSeconds(5)) == (fixtureSource, TimeSpan.FromSeconds(45.5)), "the event link opens the original file at its recorded time");
        seek = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var balances = Field<TextBlock>(review, "money").Inlines.OfType<Hyperlink>().ToArray();
        Check(balances.Length == 2 && balances[0].Inlines.OfType<Run>().Single().Text == "0:00:04.5", "both balance readings display linked timestamps");
        balances[1].RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
        Check(await seek.Task.WaitAsync(TimeSpan.FromSeconds(5)) == (fixtureSource, TimeSpan.FromSeconds(195)), "the last balance link opens the recording at its reading");
        var scrolls = Tree((DependencyObject)review.Content).OfType<ScrollViewer>().Where(s => s.Content is StackPanel && s.ScrollableHeight > 0).ToArray();
        Check(scrolls.Length >= 1, "Expanded review settings need a bounded scroll area");
        foreach (var scroll in scrolls) { scroll.ScrollToEnd(); scroll.UpdateLayout(); Check(scroll.VerticalOffset > 0, "Review controls cannot be reached by scrolling"); }
        // This fixture isn't in the store: clear it before closing so no synthetic metadata is saved.
        Set(review, "selected", null!); await Close(review);
        await CloseLifecycle(data);
        await Controls();
        AuditMainPages(game);
        Console.WriteLine($"PASS {layouts} synthetic window/page layouts, shared dark surfaces and minimum-size content");
    }
    static async Task CloseLifecycle(string data)
    {
        var review = new WardogsReviewWindow(data, "", (_,_,_,_) => { });
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(review, "pump", drain.Task);
        int closes = 0; review.Closed += (_,_) => closes++;
        review.Close(); review.Close();
        Check(closes == 0, "Review closed before pending work drained");
        drain.SetResult(); await Task.Delay(40);
        Check(closes == 1, "Review did not close exactly once after draining");

        var loaded = new WardogsReviewWindow(Path.Combine(data, "loaded"), "", (_,_,_,_) => { }) { ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
        loaded.Show();
        await Field<Task>(loaded, "initialization").WaitAsync(TimeSpan.FromSeconds(3));
        loaded.Close(); await Task.Delay(80);
        Check(!loaded.IsVisible && Field<bool>(loaded, "allowClose"), "Loaded review did not close cleanly");

        using var stop = new CancellationTokenSource();
        var live = new LiveWardogsWindow(new(), _=>{});
        Set(live, "cancel", stop); Set(live, "run", Task.Delay(Timeout.Infinite, stop.Token));
        closes = 0; live.Closed += (_,_) => closes++;
        live.Close(); live.Close(); await Task.Delay(40);
        Check(stop.IsCancellationRequested && closes == 1, "Live close must cancel and finish once");
        var benchmarkStore = new BenchmarkStore(Path.Combine(data, "cancel-benchmark"));
        var recipe = new BenchmarkRecipe(Guid.NewGuid(), @"C:\Synthetic\game.exe", "Cancellation fixture", "Synthetic", "Default", 0, 10, 1);
        benchmarkStore.Save(benchmarkStore.Data with { Recipes = [recipe] });
        var benchmark = new BenchmarkWindow(benchmarkStore, recipe.GameKey, "Synthetic", (_, run, _, _, token) => {
            var completion = new TaskCompletionSource<BenchmarkRun>();
            token.Register(() => completion.SetResult(run with { Status = "Incomplete", Reason = "Test close" })); return completion.Task;
        }, _ => { });
        var runButton = Field<Button>(benchmark, "runButton");
        Check(Equals(runButton.Content, "Start benchmark"), "Saved recipe should offer one start action");
        runButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var running = Field<Task>(benchmark, "currentRun");
        Check(Equals(runButton.Content, "Cancel run") && benchmark.Running, "Starting a benchmark should replace Start with Cancel");
        closes = 0; benchmark.Closed += (_,_) => closes++;
        benchmark.Close(); await running; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(closes == 1 && benchmarkStore.Data.Sets.Single().Runs.Single() is { Status: "Incomplete", Reason: "Test close" }, $"Benchmark close failed: closes={closes}; saved={benchmarkStore.Data.Sets.FirstOrDefault()?.Runs.FirstOrDefault()?.Status}; status={Field<TextBlock>(benchmark, "status").Text}");
        Console.WriteLine("PASS idle/loaded/pending review close, repeated close, live and benchmark cancellation");
    }
    public sealed class EditableRow { public string Label { get; set; } = "Before"; public string Decision { get; set; } = "Keep"; }
    static async Task Controls()
    {
        var item = new EditableRow();
        var combo = new ComboBox { ItemsSource = new[] { "Keep", "Reject" }, MinWidth = 180 };
        combo.SetBinding(ComboBox.SelectedItemProperty, new Binding(nameof(item.Decision)) { Source = item, Mode = BindingMode.TwoWay });
        var grid = new DataGrid { ItemsSource = new[] { item }, AutoGenerateColumns = false, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Label", Binding = new Binding(nameof(item.Label)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
        var stack = new StackPanel(); stack.Children.Add(combo); stack.Children.Add(grid);
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 50, LargeChange = 10 }; stack.Children.Add(slider);
        var actions = new ActionMenu { Content = "Test actions" }; stack.Children.Add(actions);
        bool invoked = false;
        var available = actions.Add("Available action", (_, _) => invoked = true);
        var unavailable = actions.Add("Unavailable action", (_, _) => { }); unavailable.IsEnabled = false;
        var window = new Window { Content = stack, Width = 420, Height = 260, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 }; WindowTheme.Apply(window); window.Show();
        combo.IsDropDownOpen = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        Check(popup.IsOpen && popup.Child is Border { Background: SolidColorBrush { Color.R: < 140 } }, "Dropdown popup has no dark surface");
        combo.SelectedIndex = 1; combo.IsDropDownOpen = false;
        Check(item.Decision == "Reject", "Dropdown selection stopped updating the bound value");
        grid.SelectedItem = item; grid.CurrentCell = new DataGridCellInfo(item, grid.Columns[0]); grid.Focus();
        Check(grid.BeginEdit(), "Data grid cannot begin editing");
        grid.UpdateLayout(); var editor = Tree(grid).OfType<TextBox>().First(); editor.Text = "After";
        Check(grid.CommitEdit(DataGridEditingUnit.Cell, true) && grid.CommitEdit(DataGridEditingUnit.Row, true) && item.Label == "After", "Data grid label edit did not commit");
        Slider.IncreaseLarge.Execute(null, slider); Check(slider.Value == 60, "Slider command stopped updating its value");
        Check(((Track)slider.Template.FindName("PART_Track", slider)).Value == slider.Value, "Slider thumb lost its value binding");
        var invokeActions = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(actions)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!;
        invokeActions.Invoke(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(actions.ContextMenu.IsOpen && unavailable.Visibility == Visibility.Collapsed, "Action menu should open and hide unavailable commands");
        var invokeItem = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.MenuItemAutomationPeer(available)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!;
        invokeItem.Invoke(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(invoked && !actions.ContextMenu.IsOpen, "Action menu should invoke its command and close");
        await Close(window);
        Console.WriteLine("PASS dropdown popup/selection, slider commands and data-grid edit/commit with shared templates");
    }
    static Window MarkupWindow(string name)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "JamesTools.sln"))) root = Directory.GetParent(root)?.FullName ?? throw new IOException("Repository not found");
        var xml = XDocument.Load(Path.Combine(root, "src", "JamesTools", name + ".xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        xml.Root!.Attribute(x + "Class")!.Remove();
        if (name == "MainWindow") { xml.Root.SetAttributeValue(XNamespace.Xmlns + "audit", "clr-namespace:JamesTools.UiChecks;assembly=JamesTools.UiTests"); xml.Root.Name = XNamespace.Get("clr-namespace:JamesTools.UiChecks;assembly=JamesTools.UiTests") + "MarkupHostWindow"; }
        xml.Root.SetAttributeValue(XNamespace.Xmlns + "ui", "clr-namespace:JamesTools;assembly=MehTools");
        foreach (var element in xml.Descendants().Where(el => el.Name.NamespaceName == "clr-namespace:JamesTools").ToArray())
            element.Name = XNamespace.Get("clr-namespace:JamesTools;assembly=MehTools") + element.Name.LocalName;
        foreach (var attribute in xml.Descendants().Attributes().Where(a => a.Name.NamespaceName == "clr-namespace:JamesTools").ToArray()) { attribute.Parent!.SetAttributeValue(XNamespace.Get("clr-namespace:JamesTools;assembly=MehTools") + attribute.Name.LocalName, attribute.Value); attribute.Remove(); }
        var events = new[] { typeof(Window), typeof(UIElement), typeof(FrameworkElement), typeof(Button), typeof(TextBox), typeof(Selector), typeof(DataGrid), typeof(CheckBox) }
            .SelectMany(t=>t.GetEvents()).Select(e=>e.Name).ToHashSet();
        foreach (var attribute in xml.Descendants().Attributes().Where(a => a.Name.NamespaceName.Length == 0 && (events.Contains(a.Name.LocalName) || a.Name.LocalName == "DataObject.Pasting")).ToArray()) attribute.Remove();
        return (Window)XamlReader.Parse(xml.ToString());
    }
    static void AuditMainPages(GameRule game)
    {
        // Parse markup without code-behind so this audit cannot start automation, hardware sampling or OBS.
        var main = MarkupWindow("MainWindow"); var settings = new AppSettings(); settings.Games.Add(game); main.DataContext = settings;
        var pages = (TabControl)main.FindName("Pages");
        foreach (TabItem page in pages.Items)
        {
            pages.SelectedItem = page;
            ((ListBox)main.FindName("Navigation")).SelectedIndex = pages.Items.IndexOf(page);
            ((TextBlock)main.FindName("PageTitle")).Text = page.Header.ToString();
            Render(main, "main-" + page.Header, 1148, 760);
            Render(main, "main-" + page.Header, 948, 650);
        }
        pages.SelectedIndex = 3; ((FrameworkElement)main.Content).UpdateLayout();
        var targeting = (ComboBox)main.FindName("VibranceTargetBox"); var display = (ComboBox)main.FindName("VibranceDisplayBox");
        var displayOptions = (WrapPanel)display.Parent;
        Check(targeting.Items.Count == 3 && display.Items.Count == 1, "Colour selectors do not resolve view-owned option lists");
        Check(displayOptions.Visibility == Visibility.Collapsed, "Display picker should be hidden for all displays");
        targeting.SelectedValue = VibranceTargetMode.SelectedDisplay; display.SelectedIndex = 0;
        Check(settings.VibranceTargetMode == VibranceTargetMode.SelectedDisplay && settings.VibranceSelectedDisplay == "synthetic", "Colour selections no longer update saved settings");
        Check(displayOptions.Visibility == Visibility.Visible, "Selecting a specific display should reveal the picker");
        Check(((Expander)main.FindName("ObsSetupOptions")).IsExpanded == false &&
              ((FrameworkElement)main.FindName("ObsLiveDetails")).Visibility == Visibility.Collapsed,
              "Recording should open with setup and disconnected controls tucked away");
        Check(((Button)main.FindName("StartPerformanceButton")).Visibility == Visibility.Visible &&
              ((Button)main.FindName("StopPerformanceButton")).Visibility == Visibility.Collapsed &&
              ((FrameworkElement)main.FindName("PerformanceReportActions")).Visibility == Visibility.Collapsed,
              "Idle performance view should offer Start and hide capture and report actions");
        pages.SelectedIndex = 5;
        ((FrameworkElement)main.Content).UpdateLayout();
        var games = (ComboBox)main.FindName("PerformanceGame");
        Check(games.Visibility == Visibility.Collapsed, "A single game should not require a game picker");
        settings.Games.Add(new GameRule { Name = "Other game", ExecutablePath = @"C:\Synthetic\other.exe" });
        Check(games.Visibility == Visibility.Visible, "Multiple games should reveal the game picker");
        Check(main.FindName("OverlayButton") is null && main.FindName("OverlayHotkeyBox") is null &&
            Tree((DependencyObject)main.Content).OfType<Button>().Any(b => Equals(b.Content, "Stutter Investigator…")),
            "Performance should retain Stutter Investigator without the live monitor controls");
        Console.WriteLine("PASS colour option bindings and two-way selection through main-window markup");
        main.Close();
    }
}
