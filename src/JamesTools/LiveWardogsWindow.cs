using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Binding = System.Windows.Data.Binding;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using JamesTools.Core.Configuration;
using JamesTools.Core.Scanning;

namespace JamesTools;
public sealed class LiveWardogsWindow : Window
{
    private readonly LiveWardogsSettings settings;
    private readonly Action<LiveWardogsSettings> save;
    private readonly StackPanel fields = new();
    private readonly List<TextBox> inputs = [];
    private readonly ComboBox targets = new() { MinWidth = 250, Visibility = Visibility.Collapsed };
    private readonly TextBlock status = new() { Text = "Choose the game you want to watch.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock metrics = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox events = new();
    private readonly Button start = new() { Content = "Start" };
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? cancel;
    private Task? run;
    private WindowHudCapture? capture;
    private sealed record Update(int Generation, LiveWardogsSnapshot Snapshot);
    private Update? pending;
    private int generation;
    private bool closing;
    private bool closeRequested;
    public LiveWardogsWindow(LiveWardogsSettings initial, Action<LiveWardogsSettings> save)
    {
        this.save = save; settings = initial.Copy(); settings.Normalize();
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        WindowTheme.Apply(this);
        Title = "Live WARDOGS — opt-in"; Width = 760; Height = 750; MinWidth = 470; MinHeight = 420;
        var root = new Grid { Margin = new Thickness(16) }; Content = root;
        root.RowDefinitions.Add(new() { Height = new GridLength(2, GridUnitType.Star), MinHeight = 70 });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 48 });
        var top = new StackPanel { Margin = new Thickness(0, 8, 0, 8) }; Grid.SetRow(top, 1); root.Children.Add(top);
        fields.Children.Add(new TextBlock { Text = "Focus the game after starting to preview HUD notifications. To save clips automatically, choose the WARDOGS highlights capture mode and connect to OBS.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        var executable = new DockPanel(); fields.Children.Add(executable);
        var choose = new Button { Content = "Browse…" };
        DockPanel.SetDock(choose, Dock.Right); executable.Children.Add(choose);
        var targetPath = Input(nameof(settings.TargetPath)); executable.Children.Add(targetPath);
        System.Windows.Automation.AutomationProperties.SetName(targetPath, "Game executable");
        targetPath.ToolTip = "Game executable path";
        choose.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Executable|*.exe" }; if (d.ShowDialog(this) == true) { targetPath.Text = d.FileName; Refresh(); } };
        targetPath.LostKeyboardFocus += (_, _) => Refresh();
        fields.Children.Add(targets);
        targets.DropDownOpened += (_, _) => Refresh();
        System.Windows.Automation.AutomationProperties.SetName(targets, "Game window");

        var options = new StackPanel();
        fields.Children.Add(new Expander { Header = "Detection settings", Content = options, Padding = new Thickness(0, 8, 0, 0), Margin = new Thickness(0, 8, 0, 0) });
        Field(options, "Samples per second (.25–4)", nameof(settings.Rate)); Field(options, "Soft duty budget (.01–1)", nameof(settings.Budget));
        Field(options, "Normalized crop X", nameof(settings.X)); Field(options, "Normalized crop Y", nameof(settings.Y));
        Field(options, "Normalized crop width", nameof(settings.Width)); Field(options, "Normalized crop height", nameof(settings.Height));
        options.Children.Add(new TextBlock { Text = "Auto-saved events", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
        var choices = new WrapPanel();
        foreach (var (label, property) in new[] { ("Standard kills", nameof(settings.HighlightKills)), ("Headshots", nameof(settings.HighlightHeadshots)), ("Kill assists", nameof(settings.HighlightAssists)), ("Support events", nameof(settings.HighlightSupport)) })
        {
            var choice = new CheckBox { Content = label, Margin = new Thickness(0, 0, 12, 4) };
            choice.SetBinding(CheckBox.IsCheckedProperty, new Binding(property) { Source = settings, Mode = BindingMode.TwoWay });
            choices.Children.Add(choice);
        }
        options.Children.Add(choices);
        Field(options, "Seconds after the last event (3–10)", nameof(settings.HighlightPostSeconds));
        var saveButton = new Button { Content = "Save detection settings", HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        saveButton.Click += (_, _) =>
        {
            if (!ValidFields()) { status.Text = "Correct the highlighted fields, rate, budget or crop."; return; }
            if (!settings.HighlightsValid)
            {
                status.Text = "Select at least one highlight event and enter 3–10 seconds of follow-through.";
                return;
            }
            try { save(settings.Copy()); status.Text = "Detection settings saved."; }
            catch (Exception ex) { status.Text = "Could not save detection settings. " + ex.Message; }
        };
        options.Children.Add(saveButton);

        start.SetResourceReference(StyleProperty, "AccentButton");
        start.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        start.Click += async (_, _) =>
        {
            if (run is null) await StartAsync();
            else { start.IsEnabled = false; await StopAsync(); }
        };
        top.Children.Add(start); top.Children.Add(status); top.Children.Add(metrics);
        events.SetResourceReference(BackgroundProperty, "InputBrush"); events.SetResourceReference(ForegroundProperty, "TextBrush");
        Grid.SetRow(events, 2); root.Children.Add(events);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        Loaded += (_, _) => Refresh();
        Closing += OnClosing;
    }
    private TextBox Field(System.Windows.Controls.Panel panel, string label, string property)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        var box = Input(property);
        panel.Children.Add(box);
        return box;
    }
    private TextBox Input(string property)
    {
        var box = new TextBox { Margin = new Thickness(0, 2, 0, 5) };
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = settings, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true });
        inputs.Add(box);
        return box;
    }
    private bool ValidFields() => settings.IsValid && !inputs.Any(Validation.GetHasError);
    private void Refresh()
    {
        if (run is not null || closeRequested) return;
        var previous = targets.SelectedItem as LiveTarget;
        var windows = string.IsNullOrWhiteSpace(settings.TargetPath) ? [] : LiveTarget.Enumerate(settings.TargetPath);
        targets.ItemsSource = windows;
        targets.SelectedItem = windows.Length == 1 ? windows[0] : windows.FirstOrDefault(window =>
            previous is not null && window.Hwnd == previous.Hwnd && window.Pid == previous.Pid && window.Started == previous.Started);
        targets.Visibility = windows.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task StartAsync()
    {
        if (run != null || closeRequested) return;
        if (!ValidFields()) { status.Text = "Check the detection settings."; return; }
        Refresh();
        if (targets.SelectedItem is not LiveTarget target || !target.Valid() || !string.Equals(settings.TargetPath, target.Path, StringComparison.OrdinalIgnoreCase))
        {
            status.Text = targets.HasItems ? "Choose the game window to watch." : "Open the game and check its executable path.";
            return;
        }
        var config = settings.Copy(); cancel = new(); var token = cancel.Token; int gen = ++generation;
        fields.IsEnabled = false; start.Content = "Stop"; events.ItemsSource = null; metrics.Text = "Kills 0 • Headshots 0 • Assists 0 • Support 0"; status.Text = "Initializing…";
        run = Task.Run(async () =>
        {
            var source = new WindowHudCapture(target, config); capture = source;
            await LiveWardogsRun.RunAsync(source,
                WindowsWardogsOcr.CreateAsync,
                s => { if (Volatile.Read(ref generation) == gen) Interlocked.Exchange(ref pending, new Update(gen, s)); }, new(config.Rate, config.Budget), token);
        });
        try { await run; status.Text = "Stopped"; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status.Text = "Stopped"; }
        catch (Exception ex) { status.Text = "Error — stopped: " + ex.Message; }
        finally { capture = null; Interlocked.Exchange(ref pending, null); run = null; cancel.Dispose(); cancel = null; fields.IsEnabled = true; start.Content = "Start"; start.IsEnabled = true; }
    }
    private void Render()
    {
        var update = Interlocked.Exchange(ref pending, null); if (update == null || update.Generation != generation) return;
        var s = update.Snapshot;
        status.Text = (capture?.Status ?? "Stopping") + (s.Throttled ? " • throttled / soft pacing" : " • normal pacing");
        metrics.Text = $"Kills {s.Kills} • Headshots {s.Headshots} • Assists {s.Assists} • Support {s.SupportEvents}\nLast OCR {s.OcrMilliseconds:F1} ms • Processing {s.EndToEndMilliseconds:F1} ms\nSamples {s.SampleRate:F2}/s • OCR {s.OcrRate:F2}/s";
        events.ItemsSource = s.Events.Reverse().Select(e => $"{e.Timestamp:hh\\:mm\\:ss\\.fff}   {WardogsDisplay.EventName(e.Kind)}").ToArray();
    }
    public async Task StopAsync() { ++generation; cancel?.Cancel(); var task = run; if (task != null) { try { await task; } catch { /* StartAsync owns terminal status. */ } } }
    public async Task ShutdownAsync() { await StopAsync(); }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (run == null) { closeRequested = closing = true; timer.Stop(); return; }
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true; await StopAsync(); closing = true; timer.Stop();
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
