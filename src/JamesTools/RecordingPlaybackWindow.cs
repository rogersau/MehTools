using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
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
        Title = "Review / export clip"; Width = 900; Height = 760; MinWidth = 520; MinHeight = 540;
        var lifetime = new CancellationTokenSource(); CancellationTokenSource? operation = null; bool closed = false, opened = false;
        Task? exportTask = null, loadTask = null;
        var panel = new Grid { Margin = new Thickness(16) }; Content = panel;
        panel.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star), MinHeight = 140 });
        panel.RowDefinitions.Add(new() { Height = new GridLength(1.5, GridUnitType.Star), MinHeight = 230 });
        var controls = new StackPanel(); var controlScroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 0) }; Grid.SetRow(controlScroll, 1); panel.Children.Add(controlScroll);
        var status = new TextBlock { Text = "Opening media...", TextWrapping = TextWrapping.Wrap }; controls.Children.Add(status);
        var media = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual };
        panel.Children.Add(new Border { Name = "PlaybackPreview", Background = System.Windows.Media.Brushes.Black, Child = media });
        var thumbnail = new System.Windows.Controls.Image { Height = 90, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Visibility = Visibility.Collapsed }; controls.Children.Add(thumbnail);
        void Button(string label, Action action) { var b = new System.Windows.Controls.Button { Content = label, Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Left }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }; var bar = controls.Children.Count > 0 ? controls.Children[controls.Children.Count - 1] as WrapPanel : null; if (bar is null) { bar = new WrapPanel(); controls.Children.Add(bar); } bar.Children.Add(b); }
        System.Windows.Controls.TextBox Field(string label, string value) { controls.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 4), TextWrapping = TextWrapping.Wrap }); var t = new System.Windows.Controls.TextBox { Text = value }; controls.Children.Add(t); return t; }
        var seek = Field("Seek position (seconds)", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        Button("Play", () => { if (opened) { if (media.Clock is null) media.Play(); } });
        Button("Pause", () => { if (opened) media.Pause(); });
        Button("Seek", () => { if (opened && double.TryParse(seek.Text, CultureInfo.InvariantCulture, out var s) && s >= 0 && media.NaturalDuration.HasTimeSpan && s <= media.NaturalDuration.TimeSpan.TotalSeconds) media.Position = TimeSpan.FromSeconds(s); else status.Text = "Seek unavailable or invalid position. Manual export range remains available."; });
        var start = Field("Start (seconds)", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        var end = Field("End (seconds)", "");
        Button("Set start here", () => { if (opened) start.Text = media.Position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture); });
        Button("Set end here", () => { if (opened) end.Text = media.Position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture); });
        var destination = Field("New clip destination (.mkv)", Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-clip.mkv"));
        Button("Browse destination", () => { var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Matroska clip|*.mkv", OverwritePrompt = true }; if (picker.ShowDialog(this) == true) destination.Text = picker.FileName; });
        var folder = Field("Optional FFmpeg tools folder (blank uses PATH)", toolsFolder);
        Button("Browse tools folder", () => { var picker = new Microsoft.Win32.OpenFolderDialog(); if (picker.ShowDialog(this) == true) folder.Text = picker.FolderName; });
        Button("Save tools folder / clear for PATH", () => { try { if (folder.Text.Length > 0) { MediaTools.Resolve(folder.Text, "ffmpeg"); MediaTools.Resolve(folder.Text, "ffprobe"); } saveTools?.Invoke(folder.Text); status.Text = "Tool preference saved."; } catch (Exception ex) { status.Text = ex.Message; } });
        Button("Export approximate stream-copy trim", () => { if (!closing && (exportTask is null || exportTask.IsCompleted)) exportTask = Export(); });
        async Task Export() {
            if (operation is not null) return;
            operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try {
                status.Text = "Exporting and verifying... Cancel is available. Boundaries are keyframe limited.";
                var a = double.Parse(start.Text, CultureInfo.InvariantCulture); var b = double.Parse(end.Text, CultureInfo.InvariantCulture); var target = destination.Text;
                var result = await new MediaTools(folder.Text).ExportAsync(path, target, a, b, operation.Token);
                try { exported?.Invoke(target); }
                catch (Exception ex)
                {
                    if (!closed) status.Text = $"Clip published at {target}, but it couldn't be attached to session history: {ex.Message}. Attach this existing file manually.";
                    return;
                }
                if (!closed) status.Text = $"Published {target}. Requested {a:0.###}-{b:0.###} s; verified duration {result.Duration:0.###} s. Cuts are approximate.";
            } catch (Exception ex) { if (!closed) status.Text = ex is OperationCanceledException ? "Export cancelled." : ex.Message; }
            finally { operation.Dispose(); operation = null; }
        }
        Button("Cancel export", () => operation?.Cancel());
        Button("Copy timestamp", () => { try { System.Windows.Clipboard.SetText((opened ? media.Position : position).ToString("g")); } catch (Exception ex) { status.Text = "Couldn't copy timestamp: " + ex.Message; } });
        Button("Open file (no seek)", () => { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { status.Text = ex.Message; } });
        media.MediaOpened += (_, _) =>
        {
            try
            {
                opened = true;
                var validPosition = position >= TimeSpan.Zero && media.NaturalDuration.HasTimeSpan && position <= media.NaturalDuration.TimeSpan;
                media.Position = validPosition ? position : TimeSpan.Zero;
                media.Pause();
                if (!validPosition) status.Text = "Saved position is outside this video's available duration; playback starts at zero. Check the manual export range.";
            }
            catch (Exception ex) { opened = false; status.Text = "Couldn't seek this recording: " + ex.Message; }
        };
        media.MediaFailed += (_, e) => { opened = false; status.Text = "Playback codec unavailable. Manual range/export and external open remain available. " + e.ErrorException.Message; };
        Loaded += async (_, _) => { if (!closing) { loadTask = LoadMedia(); await loadTask; } };
        async Task LoadMedia() {
            try { media.Source = new Uri(path); media.Play(); } catch (Exception ex) { status.Text = ex.Message; }
            try {
                var tools = new MediaTools(folder.Text); var info = await tools.ProbeAsync(path, lifetime.Token);
                if (closed) return;
                end.Text = info.Duration.ToString("0.###", CultureInfo.InvariantCulture); status.Text = $"Duration {info.Duration:0.###} s; {info.AudioTracks} audio tracks. Export uses approximate keyframe boundaries.";
                var temp = await tools.ThumbnailAsync(path, lifetime.Token);
                try { if (!closed) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(temp); bitmap.EndInit(); thumbnail.Source = bitmap; thumbnail.Visibility = Visibility.Visible; } } finally { File.Delete(temp); }
            } catch (Exception ex) { if (!closed) status.Text = ex.Message + " Playback/external open may still work."; }
        }
        shutdown = async () => { closed = true; lifetime.Cancel(); operation?.Cancel(); if (exportTask is not null) await exportTask; if (loadTask is not null) await loadTask; };
        Closing += async (_, e) => {
            if (allowClose) return;
            e.Cancel = true; if (closing) return; closing = true;
            try { await ShutdownAsync(); allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
            catch (Exception ex) { closing = false; shutdownTask = null; status.Text = "Could not finish media cleanup: " + ex.Message; }
        };
        Closed += (_, _) => { closed = true; try { media.Stop(); media.Close(); } catch (Exception ex) { status.Text = ex.Message; } finally { lifetime.Dispose(); } };
    }
}
