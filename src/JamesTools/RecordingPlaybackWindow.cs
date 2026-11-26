using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using JamesTools.Core.Recording;

namespace JamesTools;

public sealed class RecordingPlaybackWindow : Window
{
    private Func<Task> shutdown = () => Task.CompletedTask;
    private Task? shutdownTask;
    private bool closing, allowClose;
    public Task ShutdownAsync() => shutdownTask ??= shutdown();
    public RecordingPlaybackWindow(string path, TimeSpan position) : this(path, position, "", null, null) { }

    public RecordingPlaybackWindow(string path, TimeSpan position, string toolsFolder, Action<string>? exported, Action<string>? saveTools)
    {
        WindowTheme.Apply(this);
        Title = Path.GetFileName(path);
        Width = 900; Height = 760; MinWidth = 520; MinHeight = 540;

        var lifetime = new CancellationTokenSource();
        CancellationTokenSource? operation = null;
        Task? exportTask = null, loadTask = null;
        bool closed = false, opened = false, playing = false, updatingPosition = false;

        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 140 });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = panel;
        var media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual };
        var thumbnail = new System.Windows.Controls.Image { Visibility = Visibility.Collapsed };
        var preview = new Grid(); preview.Children.Add(media); preview.Children.Add(thumbnail);
        panel.Children.Add(new Border { Name = "PlaybackPreview", Background = System.Windows.Media.Brushes.Black, Child = preview });

        var controls = new StackPanel();
        var scroll = new ScrollViewer { Content = controls, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(scroll, 1); panel.Children.Add(scroll);
        panel.SizeChanged += (_, e) => scroll.MaxHeight = Math.Max(125, Math.Min(320, e.NewSize.Height - 145));

        var status = new TextBlock { Text = "Opening recording…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var clock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var player = new DockPanel { Visibility = Visibility.Collapsed, LastChildFill = true };
        controls.Children.Add(player);
        var play = new Button { Content = "Play" };
        DockPanel.SetDock(clock, Dock.Right);
        player.Children.Add(play); player.Children.Add(clock);
        var seek = new Slider { Minimum = 0, LargeChange = 10, SmallChange = 1, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(seek, "Playback position in seconds");
        player.Children.Add(seek);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => UpdatePlayhead();

        var fallbackOpen = AddButton(controls, "Open original", OpenOriginal);
        fallbackOpen.Visibility = Visibility.Collapsed;
        controls.Children.Add(status);
        var trim = new StackPanel();
        controls.Children.Add(new Expander { Header = "Trim a clip", Content = trim, Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 9, 0, 0) });
        var start = TimeField(trim, "Start (seconds)", Seconds(position));
        var end = TimeField(trim, "End (seconds)", "");
        trim.Children.Add(new TextBlock { Text = "Cuts follow nearby keyframes and retain all audio tracks.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
        var export = new Button { Content = "Export clip…", HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        trim.Children.Add(export);
        export.SetResourceReference(StyleProperty, "AccentButton");
        var stopExport = AddButton(controls, "Cancel export", () => operation?.Cancel());
        stopExport.Visibility = Visibility.Collapsed;

        var folder = new TextBox { Text = toolsFolder };
        var options = new StackPanel();
        controls.Children.Add(new Expander { Header = "Playback and tool options", Content = options, Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 9, 0, 0) });
        var actions = new ActionMenu { Content = "Recording actions", HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        actions.Add("Copy timestamp", (_, _) => Perform(() => System.Windows.Clipboard.SetText((opened ? media.Position : position).ToString("g"))));
        actions.Add("Open original", (_, _) => Perform(OpenOriginal));
        options.Children.Add(actions);
        var exact = new TextBox { Text = Seconds(position), ToolTip = "Enter seconds and press Enter to seek" };
        options.Children.Add(new TextBlock { Text = "Jump to time (seconds, press Enter)", Margin = new Thickness(0, 6, 0, 4) });
        options.Children.Add(exact);
        exact.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            Perform(() =>
            {
                if (!opened || !double.TryParse(exact.Text, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || seconds > seek.Maximum)
                    throw new ArgumentException("Enter a position within this recording.");
                seek.Value = seconds;
            });
        };
        options.Children.Add(new TextBlock { Text = "FFmpeg folder override (blank uses bundled tools)", Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap });
        var toolRow = new DockPanel(); options.Children.Add(toolRow);
        var browseTools = AddButton(toolRow, "Browse…", () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog();
            if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName;
        });
        DockPanel.SetDock(browseTools, Dock.Right); toolRow.Children.Add(folder);
        if (saveTools is not null) AddButton(options, "Save tool preference", () =>
        {
            MediaTools.Resolve(folder.Text, "ffmpeg"); MediaTools.Resolve(folder.Text, "ffprobe");
            saveTools(folder.Text);
            status.Text = "Tool preference saved.";
        });
        export.Click += (_, _) => { if (!closing && (exportTask is null || exportTask.IsCompleted)) exportTask = Export(); };

        play.Click += (_, _) => Perform(() =>
        {
            if (!opened) return;
            if (playing) media.Pause();
            else { if (media.NaturalDuration.HasTimeSpan && media.Position >= media.NaturalDuration.TimeSpan) media.Position = TimeSpan.Zero; media.Play(); }
            SetPlaying(!playing);
        });
        seek.ValueChanged += (_, _) =>
        {
            if (!updatingPosition && opened) Perform(() => { media.Position = TimeSpan.FromSeconds(seek.Value); UpdatePlayhead(); });
        };
        media.MediaOpened += (_, _) => Perform(() =>
        {
            opened = true;
            player.Visibility = Visibility.Visible;
            fallbackOpen.Visibility = thumbnail.Visibility = Visibility.Collapsed;
            bool knownDuration = media.NaturalDuration.HasTimeSpan;
            seek.IsEnabled = exact.IsEnabled = knownDuration;
            seek.Maximum = knownDuration ? media.NaturalDuration.TimeSpan.TotalSeconds : 0;
            media.Position = knownDuration && position >= TimeSpan.Zero && position <= media.NaturalDuration.TimeSpan ? position : TimeSpan.Zero;
            media.Pause(); SetPlaying(false); UpdatePlayhead();
            if (end.Text.Length == 0 && knownDuration) end.Text = Seconds(media.NaturalDuration.TimeSpan);
            if (status.Text == "Opening recording…") status.Text = "";
        });
        media.MediaEnded += (_, _) => { SetPlaying(false); UpdatePlayhead(); };
        media.MediaFailed += (_, e) =>
        {
            opened = false; SetPlaying(false);
            player.Visibility = Visibility.Collapsed;
            fallbackOpen.Visibility = Visibility.Visible;
            if (thumbnail.Source is not null) thumbnail.Visibility = Visibility.Visible;
            status.Text = "Playback unavailable. " + e.ErrorException.Message;
        };

        Loaded += (_, _) => { if (!closing) loadTask = LoadMedia(); };
        shutdown = async () =>
        {
            closed = true; timer.Stop(); lifetime.Cancel(); operation?.Cancel();
            if (exportTask is not null) await exportTask;
            if (loadTask is not null) await loadTask;
        };
        Closing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            try { await ShutdownAsync(); allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
            catch (Exception ex) { closing = false; shutdownTask = null; status.Text = "Could not finish media cleanup: " + ex.Message; }
        };
        Closed += (_, _) => { closed = true; timer.Stop(); Perform(() => { media.Stop(); media.Close(); }); lifetime.Dispose(); };

        Button AddButton(Panel parent, string label, Action action)
        {
            var button = new Button { Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            button.Click += (_, _) => Perform(action);
            parent.Children.Add(button);
            return button;
        }

        TextBox TimeField(Panel parent, string label, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            parent.Children.Add(new TextBlock { Text = label }); parent.Children.Add(row);
            var field = new TextBox { Text = value };
            var usePlayhead = AddButton(row, "Use playhead", () => field.Text = Seconds(media.Position));
            usePlayhead.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = player });
            DockPanel.SetDock(usePlayhead, Dock.Right); row.Children.Add(field);
            return field;
        }

        void Perform(Action action)
        {
            try { action(); } catch (Exception ex) { status.Text = ex.Message; }
        }
        void OpenOriginal() => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        void SetPlaying(bool value)
        {
            playing = value; play.Content = value ? "Pause" : "Play";
            if (value) timer.Start(); else timer.Stop();
        }
        void UpdatePlayhead()
        {
            if (!opened) return;
            updatingPosition = true;
            try { if (!seek.IsMouseCaptureWithin) seek.Value = media.Position.TotalSeconds; }
            finally { updatingPosition = false; }
            clock.Text = media.Position.ToString("g");
        }

        async Task Export()
        {
            var picker = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Matroska clip|*.mkv", FileName = Path.GetFileNameWithoutExtension(path) + "-clip.mkv"
            };
            if (picker.ShowDialog(this) != true || closing) return;
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            export.IsEnabled = false; stopExport.Visibility = Visibility.Visible;
            try
            {
                status.Text = "Exporting and verifying…";
                var a = double.Parse(start.Text, CultureInfo.InvariantCulture);
                var b = double.Parse(end.Text, CultureInfo.InvariantCulture);
                var target = picker.FileName;
                var result = await new MediaTools(folder.Text).ExportAsync(path, target, a, b, operation.Token);
                try { exported?.Invoke(target); }
                catch (Exception ex) { if (!closed) status.Text = $"Clip saved at {target}. Couldn't attach it to the session. {ex.Message}"; return; }
                if (!closed) status.Text = $"Clip saved at {target} ({result.Duration:0.###} s).";
            }
            catch (Exception ex) { if (!closed) status.Text = ex is OperationCanceledException ? "Export cancelled." : ex.Message; }
            finally { operation.Dispose(); operation = null; export.IsEnabled = true; stopExport.Visibility = Visibility.Collapsed; }
        }

        async Task LoadMedia()
        {
            Perform(() => { media.Source = new Uri(path); media.Play(); });
            try
            {
                var tools = new MediaTools(folder.Text);
                var info = await tools.ProbeAsync(path, lifetime.Token);
                if (closed) return;
                if (end.Text.Length == 0) end.Text = info.Duration.ToString("0.###", CultureInfo.InvariantCulture);
                var temp = await tools.ThumbnailAsync(path, lifetime.Token);
                try
                {
                    if (closed) return;
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(temp); bitmap.EndInit(); thumbnail.Source = bitmap;
                    if (fallbackOpen.Visibility == Visibility.Visible) thumbnail.Visibility = Visibility.Visible;
                }
                finally { File.Delete(temp); }
            }
            catch (Exception ex) { if (!closed && !opened) status.Text = ex.Message; }
        }
    }

    private static string Seconds(TimeSpan position) => position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
