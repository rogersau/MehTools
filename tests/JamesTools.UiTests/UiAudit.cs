using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using JamesTools;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;

internal static class UiAudit
{
    static string? output;
    static int layouts;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
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
        if (window is SteamWardogsWindow steam)
            Check(((DataGrid)steam.FindName("EventsTable")).ActualHeight >= 100, "Steam event table is squeezed out at " + width + "×" + height);
        if (output != null)
        {
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
        int render = Array.IndexOf(args, "--render");
        if (render >= 0) { output = Path.GetFullPath(args[render + 1]); Directory.CreateDirectory(output); }
        var game = new GameRule { Name = "Synthetic game", ExecutablePath = @"C:\Synthetic\game.exe" };
        var windows = new List<(string Name, Window Window)> {
            ("stutter", new PerformanceTimelineWindow(new ParsedPerformanceReport(new(3, 30, "1000/meanFrameMs", 60, 16.7, 40, 65, 4096, 1, 60, null, "Synthetic scene", "Hitch"), [new(0, 1, 40, 65, 4096, 0, 1, 0, 1, 0, 1, "test", 60, 60, 20, 0)], []) {
                Investigations = [StutterInvestigator.Create([new FrameTelemetry(1, 2, "QPC", "7:A", 100, 80, 20, 10, 1, 11, 1, 12, 13, 0, "Independent Flip", "Application")],
                    [new DiagnosticHardwareSample(0, 2, 0, 0, null, null, new Dictionary<string, double?> { ["gpuPercent"] = null, ["processReadMiBps"] = 4 })], [], [new(1, "Noticed hitch")])]
            }, "Synthetic spike")),
            ("timeline", new PerformanceTimelineWindow(new ParsedPerformanceReport(new(2, 30, "Synthetic", 60, 16.7, 40, 65, 4096, 1, 60, null, "Synthetic scene", "Test marker"), [new(0, 1, 40, 65, 4096, 0, 1, 0, 1, 0, 1, "test", 60, 60, 20, 0)], [new(1, "Synthetic stutter")]), "Synthetic report")),
            ("steam-wardogs", SteamWardogsUiTests.CreateWindow(() => null))
        };
        foreach (var entry in windows)
        {
            Render(entry.Window, entry.Name, entry.Window.Width - 32, entry.Window.Height - 40);
            Render(entry.Window, entry.Name, Math.Max(entry.Window.MinWidth, 470) - 32, Math.Max(entry.Window.MinHeight, 420) - 40);
            await Close(entry.Window);
        }
        await Controls();
        AuditMainPages(game);
        Console.WriteLine($"PASS {layouts} synthetic window/page layouts, shared dark surfaces and minimum-size content");
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
        invokeItem.Invoke(); await Task.Delay(50); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Check(invoked && !actions.ContextMenu.IsOpen, $"Action menu should invoke its command and close (invoked={invoked}, open={actions.ContextMenu.IsOpen})");
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
        var main = MarkupWindow("MainWindow"); var settings = new AppSettings(); settings.Games.Add(game); main.DataContext = settings;
        var pages = (TabControl)main.FindName("Pages");
        foreach (TabItem page in pages.Items)
        {
            var index = pages.Items.IndexOf(page);
            pages.SelectedItem = page;
            var nav = (ListBox)main.FindName("Navigation");
            nav.SelectedIndex = index < nav.Items.Count ? index : -1;
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
        pages.SelectedIndex = 4;
        ((FrameworkElement)main.Content).UpdateLayout();
        var games = (ComboBox)main.FindName("PerformanceGame");
        Check(games.Visibility == Visibility.Collapsed, "A single game should not require a game picker");
        settings.Games.Add(new GameRule { Name = "Other game", ExecutablePath = @"C:\Synthetic\other.exe" });
        Check(games.Visibility == Visibility.Visible, "Multiple games should reveal the game picker");
        Check(Tree((DependencyObject)main.Content).OfType<Button>().Any(b => Equals(b.Content, "Stutter Investigator…")) &&
              Tree((DependencyObject)main.Content).OfType<Button>().Any(b => Equals(b.Content, "Copy for AI")),
            "Performance should retain Stutter Investigator and offer Copy for AI");
        Console.WriteLine("PASS colour option bindings and two-way selection through main-window markup");
        main.Close();
    }
}
