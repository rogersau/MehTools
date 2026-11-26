using System.IO;
using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using JamesTools.Core.Sessions;

namespace JamesTools;

public partial class SteamWardogsWindow : Window
{
    private readonly Func<string> toolsFolder;
    private readonly Func<string?> discoverRecordingFolder;
    private readonly SteamHighlightSettings padding;
    private readonly Func<SteamWardogsTimeline, string, bool>? attachMontage;
    private readonly Dictionary<string, HashSet<long>> includedByTimeline = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private GameSession? preferredSession;
    private string? root;
    private bool busy;
    private bool closed;
    private bool shuttingDown;
    private TaskCompletionSource? idle;

    internal SteamWardogsWindow(Func<string> toolsFolder, Func<string?>? discoverRecordingFolder = null, SteamHighlightSettings? preferences = null,
        GameSession? session = null, Func<SteamWardogsTimeline, string, bool>? attachMontage = null)
    {
        this.toolsFolder = toolsFolder;
        this.discoverRecordingFolder = discoverRecordingFolder ?? SteamGameDetector.FindGameRecordingDirectory;
        padding = (preferences ?? new()).Copy();
        preferredSession = session;
        this.attachMontage = attachMontage;
        InitializeComponent();
        PaddingText.Text = $"Clips include about {padding.BeforeSeconds} seconds before and {padding.AfterSeconds} seconds after the event, depending on the footage Steam still has. Change timing in Recording settings.";
        Loaded += OnLoaded;
        Closed += (_, _) => { closed = true; lifetime.Cancel(); lifetime.Dispose(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await RefreshAsync(preferSession: preferredSession is not null);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshAsync(preferSession: preferredSession is not null && TimelineBox.SelectedItem is null);

    internal async Task ReviewSessionAsync(GameSession session)
    {
        if (busy || closed) return;
        preferredSession = session;
        await RefreshAsync(preferSession: true);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder shown in Steam → Settings → Game Recording" };
        if (Directory.Exists(root)) picker.InitialDirectory = root;
        if (picker.ShowDialog(this) != true) return;
        root = picker.FolderName;
        await RefreshAsync(preferSession: preferredSession is not null && TimelineBox.SelectedItem is null);
    }

    private Task RefreshAsync(bool preferSession = false) => RunAsync("Reading Steam timelines…", async token =>
    {
        var selectedId = (TimelineBox.SelectedItem as TimelineRow)?.Timeline.Id;
        root ??= await Task.Run(discoverRecordingFolder, token);
        if (closed) return;
        FolderLabel.Text = root ?? "Steam recording folder not detected";
        TimelineBox.ItemsSource = null;
        if (root is null)
        {
            SteamStatus.Text = "Choose the folder shown in Steam → Settings → Game Recording.";
            return;
        }
        string path = root;
        var catalog = await Task.Run(() => SteamWardogsLibrary.Read(path, token), token);
        if (closed) return;
        var rows = catalog.Timelines.Select(t => new TimelineRow(t)).ToArray();
        TimelineBox.ItemsSource = rows;
        if (preferSession && preferredSession is { } session)
        {
            var match = SteamWardogsLibrary.MatchHistoricalSession(session, catalog);
            TimelineBox.SelectedItem = rows.FirstOrDefault(row => row.Timeline.Id == match?.Id);
            if (match is null && rows.Length > 0)
                SteamStatus.Text = "No unique Steam timeline matched this session. You can choose a Steam session by date and time.";
        }
        else TimelineBox.SelectedItem = rows.FirstOrDefault(row => row.Timeline.Id == selectedId) ??
            rows.FirstOrDefault(row => row.Timeline.Events.Any(ev => ev.HasFootage)) ??
            rows.FirstOrDefault(row => row.Timeline.Events.Count > 0) ?? rows.FirstOrDefault();
        if (rows.Length == 0)
            SteamStatus.Text = "No WARDOGS recordings found. Enable Game Recording in Steam and refresh after playing.";
        if (catalog.UnreadableTimelines > 0)
            SteamStatus.Text += $" {catalog.UnreadableTimelines} timeline file(s) could not be read.";
    });

    private void Selection_Changed(object sender, SelectionChangedEventArgs e) => UpdateEventRows();

    private SteamWardogsTimeline? SelectedTimeline => (TimelineBox.SelectedItem as TimelineRow)?.Timeline;

    private HashSet<long> IncludedEvents(SteamWardogsTimeline timeline)
    {
        if (!includedByTimeline.TryGetValue(timeline.Id, out var included))
            includedByTimeline[timeline.Id] = included = timeline.Events.Where(ev => ev.HasFootage).Select(ev => ev.Id).ToHashSet();
        return included;
    }

    private void UpdateEventRows()
    {
        if (EventsTable is null || FilterBox is null || TimelineBox is null) return;
        long? currentId = (EventsTable.SelectedItem as EventRow)?.Event.Id;
        var timeline = SelectedTimeline;
        var included = timeline is null ? new HashSet<long>() : IncludedEvents(timeline);
        if (timeline is not null) included.IntersectWith(timeline.Events.Where(ev => ev.HasFootage).Select(ev => ev.Id));
        var rows = (timeline?.Events ?? []).Where(ev => FilterBox.SelectedIndex switch
        {
            1 => ev.Category == SteamWardogsEventCategory.Kills,
            2 => ev.Category == SteamWardogsEventCategory.Assists,
            3 => ev.Category == SteamWardogsEventCategory.Revives,
            4 => ev.Category == SteamWardogsEventCategory.Other,
            _ => true
        }).Select(ev => new EventRow(ev, included.Contains(ev.Id))).ToArray();
        EventsTable.ItemsSource = rows;
        EventsTable.SelectedItem = rows.FirstOrDefault(row => row.Event.Id == currentId) ?? rows.FirstOrDefault();
        UpdateControls();
        if (timeline is null) return;
        SteamStatus.Text = timeline.Events.Count == 0 ? "Steam saved no WARDOGS event markers for this session."
            : rows.Length == 0 ? "No events match this filter."
            : $"{rows.Length} event(s), {rows.Count(row => row.Event.HasFootage)} with matched footage.";
    }

    private void Event_Changed(object sender, SelectionChangedEventArgs e) => UpdateControls();

    private void EventIncluded_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTimeline is not { } timeline ||
            sender is not System.Windows.Controls.CheckBox { DataContext: EventRow { Event.HasFootage: true } row } check) return;
        var included = IncludedEvents(timeline);
        row.Included = check.IsChecked == true;
        if (row.Included) included.Add(row.Event.Id);
        else included.Remove(row.Event.Id);
        UpdateControls();
    }

    private void UseShownEvents_Click(object sender, RoutedEventArgs e) => ChangeSelection(clear: true, includeShown: true);
    private void IncludeShownEvents_Click(object sender, RoutedEventArgs e) => ChangeSelection(clear: false, includeShown: true);
    private void ClearEvents_Click(object sender, RoutedEventArgs e) => ChangeSelection(clear: true, includeShown: false);

    private void ChangeSelection(bool clear, bool includeShown)
    {
        if (SelectedTimeline is not { } timeline) return;
        var included = IncludedEvents(timeline);
        if (clear) included.Clear();
        if (includeShown)
            included.UnionWith(EventsTable.Items.OfType<EventRow>().Where(row => row.Event.HasFootage).Select(row => row.Event.Id));
        UpdateEventRows();
    }

    private async void Montage_Click(object sender, RoutedEventArgs e)
    {
        if (root is not { } recordingRoot || SelectedTimeline is not { EndedAt: not null } timeline) return;
        long[] ids = IncludedEvents(timeline).ToArray();
        if (ids.Length == 0) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save selected Steam events as one highlights video", Filter = "MP4 video (*.mp4)|*.mp4", DefaultExt = ".mp4",
            FileName = $"WARDOGS Steam highlights - {timeline.RecordedAt.LocalDateTime:yyyy-MM-dd HH-mm-ss}.mp4"
        };
        if (Directory.Exists(padding.OutputFolder)) picker.InitialDirectory = padding.OutputFolder;
        if (picker.ShowDialog(this) != true) return;
        string destination = picker.FileName;
        await RunAsync("Building a video from the checked Steam events…", async token =>
        {
            var exporter = new SteamWardogsClips(new MediaTools(toolsFolder()));
            var progress = new Progress<string>(message =>
            {
                if (!closed && busy && !shuttingDown) SteamStatus.Text = message;
            });
            var result = await Task.Run(() => exporter.ExportMontageAsync(recordingRoot, timeline, destination,
                padding.BeforeSeconds, padding.AfterSeconds, progress, selectedEventIds: ids, token: token), token);
            if (result.Path is null)
            {
                if (!closed) SteamStatus.Text = "No video saved. Steam no longer has footage for the selected events.";
                return;
            }
            string attachment;
            try { attachment = attachMontage?.Invoke(timeline, result.Path) == true ? " Added to the matching Meh Tools session." : ""; }
            catch (Exception ex) { attachment = " Couldn't attach it to Sessions. " + ex.Message; }
            if (!closed)
                SteamStatus.Text = $"Saved {result.IncludedEvents} event(s) to {result.Path}." +
                    (result.SkippedEvents > 0 ? $" Skipped {result.SkippedEvents} without footage." : "") + attachment;
        });
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mehtools-steam-preview-{Guid.NewGuid():N}.mp4");
        await MakeClipAsync(path, preview: true);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (TimelineBox.SelectedItem is not TimelineRow row || EventsTable.SelectedItem is not EventRow marker) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Steam highlight", Filter = "MP4 video (*.mp4)|*.mp4", DefaultExt = ".mp4",
            FileName = $"WARDOGS-Steam-{row.Timeline.RecordedAt.LocalDateTime:yyyyMMdd-HHmmss}-{marker.Event.Id}.mp4"
        };
        if (picker.ShowDialog(this) == true) await MakeClipAsync(picker.FileName, preview: false);
    }

    private Task MakeClipAsync(string path, bool preview)
    {
        if (root is not { } recordingRoot || TimelineBox.SelectedItem is not TimelineRow timeline ||
            EventsTable.SelectedItem is not EventRow { Event.HasFootage: true } selected) return Task.CompletedTask;
        return RunAsync(preview ? "Preparing the event preview…" : "Saving Steam highlight…", async token =>
        {
            bool previewOpened = false;
            try
            {
                string tools = toolsFolder();
                var exporter = new SteamWardogsClips(new MediaTools(tools));
                var clip = await Task.Run(() => exporter.ExportAsync(recordingRoot, timeline.Timeline.Id, selected.Event.Id, path,
                    padding.BeforeSeconds, padding.AfterSeconds, token), token);
                if (closed) return;
                if (preview)
                {
                    var player = new RecordingPlaybackWindow(clip.Path, clip.EventPosition, tools, null, null) { Owner = this };
                    player.Closed += (_, _) => DeletePreview(path);
                    player.Show();
                    previewOpened = true;
                    SteamStatus.Text = "Preview opened at the Steam event. No permanent clip saved.";
                }
                else SteamStatus.Text = $"Saved {clip.Media.Duration:0.#} seconds to {clip.Path}.";
            }
            finally { if (preview && !previewOpened) DeletePreview(path); }
        });
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (busy || closed || shuttingDown) return;
        busy = true;
        var operation = idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SteamStatus.Text = message;
        UpdateControls();
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) when (closed || shuttingDown) { }
        catch (Exception ex)
        {
            if (!closed) SteamStatus.Text = ex.Message;
        }
        finally
        {
            busy = false;
            if (!closed) UpdateControls();
            operation.TrySetResult();
        }
    }

    internal async Task ShutdownAsync()
    {
        if (closed || shuttingDown) return;
        shuttingDown = true;
        UpdateControls();
        lifetime.Cancel();
        if (idle is { } operation) await operation.Task;
        if (closed) return;
        foreach (var player in OwnedWindows.OfType<RecordingPlaybackWindow>().ToArray())
        {
            await player.ShutdownAsync();
            player.Close();
        }
        if (!closed) Close();
    }

    private void UpdateControls()
    {
        if (PreviewButton is null) return;
        var enabled = !busy && !shuttingDown;
        BrowseFolder.IsEnabled = RefreshButton.IsEnabled = TimelineBox.IsEnabled = FilterBox.IsEnabled = EventsTable.IsEnabled = enabled;
        PreviewButton.IsEnabled = ExportButton.IsEnabled = enabled && EventsTable.SelectedItem is EventRow { Event.HasFootage: true };
        var timeline = SelectedTimeline;
        int count = timeline is null ? 0 : IncludedEvents(timeline).Count;
        bool hasShown = EventsTable.Items.OfType<EventRow>().Any(row => row.Event.HasFootage);
        UseShownEventsButton.IsEnabled = IncludeShownEventsButton.IsEnabled = enabled && hasShown;
        ClearEventsButton.IsEnabled = enabled && count > 0;
        MontageButton.IsEnabled = enabled && count > 0 && timeline?.EndedAt is not null;
        SelectedEventCount.Text = timeline?.EndedAt is null && count > 0
            ? $"{count} checked · waiting for Steam to finalize" : $"{count} checked across all filters";
    }

    private static void DeletePreview(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record TimelineRow(SteamWardogsTimeline Timeline)
    {
        public string Label => $"{Timeline.RecordedAt.LocalDateTime:g} · {Timeline.Events.Count} event(s)";
    }

    private sealed class EventRow(SteamWardogsEvent gameEvent, bool included)
    {
        public SteamWardogsEvent Event { get; } = gameEvent;
        public bool Included { get; set; } = included;
        public string Time => $"{Event.TimelineMs / 3600000:00}:{Event.TimelineMs / 60000 % 60:00}:{Event.TimelineMs / 1000 % 60:00}.{Event.TimelineMs % 1000:000}";
        public string Coverage => Event.HasFootage ? "Available" : "Unavailable";
    }
}
