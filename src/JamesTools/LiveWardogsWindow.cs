using Button = System.Windows.Controls.Button;
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
    private readonly ComboBox targets = new() { MinWidth = 250 };
    private readonly TextBlock status = new() { Text = "Stopped — explicit Start required", TextWrapping = TextWrapping.Wrap };
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
        fields.Children.Add(new TextBlock { Text = "Detect your own HUD notifications. Choose the game and a window, then focus it after Start. No clips or bookmarks are created.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var scroll = new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; root.Children.Add(scroll);
        Field("Target executable (absolute path)", nameof(settings.TargetPath));
        var choose = new Button { Content = "Choose game executable…" }; choose.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Executable|*.exe" }; if (d.ShowDialog(this) == true) { settings.TargetPath = d.FileName; RefreshBindings(); Refresh(); } }; fields.Children.Add(choose);
        var refresh = new Button { Content = "Refresh matching windows (read only)" }; refresh.Click += (_, _) => Refresh(); fields.Children.Add(refresh); fields.Children.Add(targets);
        Field("Trusted Tesseract 5 native DLL (absolute path)", nameof(settings.NativeDll));
        var dll = new Button { Content = "Choose native DLL…" }; dll.Click += (_, _) => { var d = new Microsoft.Win32.OpenFileDialog { Filter = "Native DLL|*.dll" }; if (d.ShowDialog(this) == true) { settings.NativeDll = d.FileName; RefreshBindings(); } }; fields.Children.Add(dll);
        Field("English tessdata directory (contains eng.traineddata)", nameof(settings.Tessdata));
        var data = new Button { Content = "Choose tessdata folder…" }; data.Click += (_, _) => { var d = new Microsoft.Win32.OpenFolderDialog(); if (d.ShowDialog(this) == true) { settings.Tessdata = d.FolderName; RefreshBindings(); } }; fields.Children.Add(data);
        Field("Samples/second (.25–4)", nameof(settings.Rate)); Field("Soft duty budget (.01–1)", nameof(settings.Budget));
        Field("Normalized crop X", nameof(settings.X)); Field("Normalized crop Y", nameof(settings.Y)); Field("Normalized crop width", nameof(settings.Width)); Field("Normalized crop height", nameof(settings.Height));
        var buttons = new WrapPanel(); buttons.Children.Add(start); start.Click += async (_, _) => await StartAsync();
        var stop = new Button { Content = "Stop" }; stop.Click += async (_, _) => await StopAsync(); buttons.Children.Add(stop);
        var saveButton = new Button { Content = "Save configuration" }; saveButton.Click += (_, _) => { if (!ValidFields()) { status.Text = "Correct the highlighted fields, rate, budget or crop."; return; } try { save(settings.Copy()); if (run == null) status.Text = "Configuration saved. Start is always explicit."; } catch (Exception ex) { status.Text = "Could not save configuration: " + ex.Message; } }; buttons.Children.Add(saveButton); top.Children.Add(buttons); top.Children.Add(status); top.Children.Add(metrics);
        top.Children.Add(new TextBlock { Text = "Worker CPU is % of one core. Compare Performance monitor runs with detector on/off; no FPS impact estimate. History contains at most 256 events.", TextWrapping = TextWrapping.Wrap });
        events.SetResourceReference(BackgroundProperty, "InputBrush"); events.SetResourceReference(ForegroundProperty, "TextBrush");
        Grid.SetRow(events, 2); root.Children.Add(events);
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Render(), Dispatcher);
        Closing += OnClosing;
    }
    private void Field(string label, string property)
    {
        fields.Children.Add(new TextBlock { Text = label }); var box = new TextBox { Margin = new Thickness(0, 2, 0, 5) };
        box.SetBinding(TextBox.TextProperty, new Binding(property) { Source = settings, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged, ValidatesOnExceptions = true }); fields.Children.Add(box);
    }
    private void RefreshBindings() { foreach (var b in fields.Children.OfType<TextBox>()) b.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); }
    private bool ValidFields() => settings.IsValid && !fields.Children.OfType<TextBox>().Any(Validation.GetHasError);
    private void Refresh() { targets.ItemsSource = LiveTarget.Enumerate(settings.TargetPath); targets.SelectedIndex = 0; }
    private async Task StartAsync()
    {
        if (run != null || closeRequested) return;
        if (!ValidFields() || targets.SelectedItem is not LiveTarget target || !target.Valid() || !string.Equals(settings.TargetPath, target.Path, StringComparison.OrdinalIgnoreCase)) { status.Text = "Select a valid matching window and correct the highlighted fields, rate, budget and crop."; return; }
        if (!Path.IsPathFullyQualified(settings.NativeDll) || !File.Exists(settings.NativeDll) || !Path.IsPathFullyQualified(settings.Tessdata) || !File.Exists(Path.Combine(settings.Tessdata, "eng.traineddata"))) { status.Text = "Choose trusted absolute native DLL and English tessdata paths."; return; }
        var config = settings.Copy(); cancel = new(); var token = cancel.Token; int gen = ++generation;
        fields.IsEnabled = false; start.IsEnabled = false; events.ItemsSource = null; metrics.Text = "Kills 0 • Headshots 0 • Assists 0"; status.Text = "Initializing…";
        run = Task.Run(async () =>
        {
            var source = new WindowHudCapture(target, config); capture = source;
            await LiveWardogsRun.RunAsync(source,
                async ct => await PersistentHudOcr.StartAsync(Path.Combine(AppContext.BaseDirectory, "tools", "ocr", "JamesTools.OcrWorker.exe"), config.NativeDll, config.Tessdata, ct),
                s => { if (Volatile.Read(ref generation) == gen) Interlocked.Exchange(ref pending, new Update(gen, s)); }, new(config.Rate, config.Budget), token);
        });
        try { await run; status.Text = "Stopped"; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status.Text = "Stopped"; }
        catch (Exception ex) { status.Text = "Error — stopped: " + ex.Message; }
        finally { capture = null; Interlocked.Exchange(ref pending, null); run = null; cancel.Dispose(); cancel = null; fields.IsEnabled = true; start.IsEnabled = true; }
    }
    private void Render()
    {
        var update = Interlocked.Exchange(ref pending, null); if (update == null || update.Generation != generation) return;
        var s = update.Snapshot;
        status.Text = (capture?.Status ?? "Stopping") + (s.Throttled ? " • throttled / soft pacing" : " • normal pacing");
        metrics.Text = $"Kills {s.Kills} • Headshots {s.Headshots} • Assists {s.Assists}\nLast OCR {s.OcrMilliseconds:F1} ms • Processing {s.EndToEndMilliseconds:F1} ms (excludes capture age)\nSamples {s.SampleRate:F2}/s • OCR {s.OcrRate:F2}/s • Worker {s.WorkerCpuMilliseconds / Math.Max(.001, s.WorkerCpuElapsedSeconds) / 10:F1}% of one core";
        events.ItemsSource = s.Events.Reverse().Select(e => $"{e.Timestamp:hh\\:mm\\:ss\\.fff}   {e.Kind}").ToArray();
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
