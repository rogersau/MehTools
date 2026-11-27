using System.IO;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using JamesTools;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Sessions;

internal static class SteamWardogsUiTests
{
    private const BindingFlags Members = BindingFlags.NonPublic | BindingFlags.Instance;

    internal static SteamWardogsWindow CreateWindow(Func<string?> discover, GameSession? session = null)
        => (SteamWardogsWindow)Activator.CreateInstance(typeof(SteamWardogsWindow),
            BindingFlags.NonPublic | BindingFlags.Instance, null, [(Func<string>)(() => ""), discover, null, session, null], null)!;

    internal static async Task Run(string data)
    {
        var root = Path.Combine(data, "steam");
        var timelines = Directory.CreateDirectory(Path.Combine(root, "timelines"));
        File.WriteAllText(Path.Combine(timelines.FullName, "timeline_186724020261124_082341.json"), """
            {"daterecorded":"1795508621","entries":[
                {"id":"1","type":"event","time":"812970","icon":"wd-kill","title":"Killed"},
                {"id":"2","type":"event","time":"825017","icon":"wd-assist","title":"Kill Assist"}
            ]}
            """);
        var steam = CreateWindow(() => root);
        Offscreen(steam);
        steam.Show();
        try
        {
            var table = (DataGrid)steam.FindName("EventsTable");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (table.Items.Count != 2) await Task.Delay(20, deadline.Token);
            ((ComboBox)steam.FindName("FilterBox")).SelectedIndex = 1;
            Check(table.Items.Count == 1, "Kills filter did not select only the Steam kill marker");
            Check(!((Button)steam.FindName("PreviewButton")).IsEnabled && !((Button)steam.FindName("ExportButton")).IsEnabled,
                "Steam review offered a clip for an event with no saved footage");
        }
        finally { steam.Close(); }
        await VerifyManualSelection(data);

        var settings = new SettingsStore(Path.Combine(data, "obs-capture-toggle"));
        settings.Save(new AppSettings
        {
            Games = [new() { Name = "WARDOGS", ExecutablePath = @"C:\Synthetic\wardogs.exe" }],
            ObsWebSocketPort = 52348, SaveClipHotkey = "", StutterMarkerHotkey = ""
        });
        var main = (MainWindow)Activator.CreateInstance(typeof(MainWindow), Members, null, [settings.DirectoryPath], null)!;
        Offscreen(main);
        main.Show();
        try
        {
            ((ListBox)main.FindName("Navigation")).SelectedIndex = 1;
            var grid = (DataGrid)main.FindName("GameGrid");
            grid.ScrollIntoView(grid.Items[0]);
            main.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var capture = Tree(grid).OfType<CheckBox>().Single(box => AutomationProperties.GetName(box) == "Automatic OBS capture");
            var draft = (AppSettings)main.DataContext;
            draft.Games[0].Name = "Unsaved draft name";
            Check(capture.IsChecked == true, "Capture checkbox was not bound to the existing rule");
            capture.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            capture.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(!draft.Games[0].Record && settings.Load()?.Games[0] is { Record: false, Name: "WARDOGS" },
                "Capture opt-out did not persist immediately, or saved an unrelated draft edit");
            Check(((AppSettings)typeof(MainWindow).GetField("_appliedSettings", Members)!.GetValue(main)!).Games[0].Record == false,
                "Capture opt-out did not change applied settings");
            await VerifySteamKd(main, data);
            await VerifyAutoHighlights(main, data, draft);
        }
        finally
        {
            await (Task)typeof(MainWindow).GetMethod("StopSteamHighlightsAsync", Members)!.Invoke(main, null)!;
            await ObsReconnectUiTests.CloseWindowAsync(main);
        }
        Console.WriteLine("PASS Steam events UI, immediate OBS opt-out, and opt-in Steam export after session completion");
    }

    private static async Task VerifySteamKd(MainWindow main, string data)
    {
        string root = Path.Combine(data, "steam-kd");
        var timelines = Directory.CreateDirectory(Path.Combine(root, "timelines"));
        File.WriteAllText(Path.Combine(timelines.FullName, "timeline_186724020261124_082341.json"), """
            {"daterecorded":"1795508621","endtime":"30000","entries":[
                {"id":1,"type":"event","time":2500,"icon":"wd-kill"},
                {"id":2,"type":"event","time":10000,"icon":"wd-killed"},
                {"id":3,"type":"event","time":26000,"icon":"wd-killed"}
            ]}
            """);
        var start = DateTimeOffset.FromUnixTimeSeconds(1795508621).UtcDateTime;
        var applied = (AppSettings)typeof(MainWindow).GetField("_appliedSettings", Members)!.GetValue(main)!;
        var oldRoot = applied.SteamHighlights.RecordingFolder;
        applied.SteamHighlights.RecordingFolder = root;
        var show = typeof(MainWindow).GetMethod("ShowSessionSteamKdAsync", Members)!;
        var kd = (TextBlock)main.FindName("SessionSteamKd");
        try
        {
            await (Task)show.Invoke(main, [new GameSession { GameName = "WARDOGS", StartedUtc = start.AddSeconds(1),
                EndedUtc = start.AddSeconds(31), Status = SessionStatus.Interrupted }])!;
            Check(kd.Visibility == Visibility.Visible && kd.Text.Contains(0.5.ToString("0.00")) && kd.Text.Contains("1 kill · 2 death markers"),
                "Steam K/D was not displayed from the whole historical marker timeline");
            await (Task)show.Invoke(main, [null])!;
            Check(kd.Visibility == Visibility.Collapsed, "Steam K/D stayed visible when leaving the session");
        }
        finally { applied.SteamHighlights.RecordingFolder = oldRoot; }
    }

    private static async Task VerifyManualSelection(string data)
    {
        string root = Path.Combine(data, "steam-manual-review");
        const string timelineId = "timeline_186724020261124_082341";
        const string recordingId = "bg_1867240_20261124_082341";
        const uint unix = 1795508621;
        var start = DateTimeOffset.FromUnixTimeSeconds(unix);
        var timelines = Directory.CreateDirectory(Path.Combine(root, "timelines"));
        var recording = Directory.CreateDirectory(Path.Combine(root, "video", recordingId));
        File.WriteAllText(Path.Combine(timelines.FullName, timelineId + ".json"), """
            {"daterecorded":"1795508621","endtime":"30000","entries":[
                {"id":"1","type":"event","time":"2120","icon":"wd-kill","title":"Killed"},
                {"id":"2","type":"event","time":"15120","icon":"wd-assist","title":"Kill Assist"},
                {"id":"3","type":"event","time":"23120","icon":"wd-respawn","title":"Revived"}
            ]}
            """);
        byte[] span = [.. Bytes(1, Encoding.ASCII.GetBytes(recordingId)), .. Number(2, 0), .. Number(3, 30000), .. Number(10, 0)];
        byte[] row = [.. Bytes(1, Encoding.ASCII.GetBytes(timelineId)), .. Number(2, 1867240),
            .. Number(3, unix), .. Number(4, 30000), .. Bytes(5, span)];
        File.WriteAllBytes(Path.Combine(root, "gamerecording.pb"), Bytes(1, row));
        File.WriteAllText(Path.Combine(recording.FullName, "session.mpd"), """
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT30S">
                <Period start="PT0S"><AdaptationSet contentType="video"><Representation id="0">
                    <SegmentTemplate timescale="1000000" duration="3000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="1"/>
                </Representation></AdaptationSet></Period>
            </MPD>
            """);
        foreach (string file in new[] { "init-stream0.m4s", "chunk-stream0-00001.m4s", "chunk-stream0-00006.m4s" })
            File.WriteAllText(Path.Combine(recording.FullName, file), "synthetic coverage");

        var steam = CreateWindow(() => root, new GameSession
        {
            GameName = "WARDOGS", StartedUtc = start.UtcDateTime.AddSeconds(1), EndedUtc = start.UtcDateTime.AddSeconds(28),
            Status = SessionStatus.Interrupted
        });
        Offscreen(steam);
        steam.Show();
        try
        {
            T Ui<T>(string name) => (T)steam.FindName(name);
            void Click(string name) => Ui<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            string Count() => Ui<TextBlock>("SelectedEventCount").Text;
            var table = Ui<DataGrid>("EventsTable");
            var filter = Ui<ComboBox>("FilterBox");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (table.Items.Count != 3) await Task.Delay(20, deadline.Token);
            Check(Count().StartsWith("2 checked") && Ui<Button>("MontageButton").IsEnabled,
                "historical review did not select the two available events by default");
            filter.SelectedIndex = 1;
            Click("UseShownEventsButton");
            Check(Count().StartsWith("1 checked") && table.Items.Count == 1, "Use only shown did not pick just the filtered events");
            filter.SelectedIndex = 2;
            Click("IncludeShownEventsButton");
            Check(Count().StartsWith("2 checked"), "Include shown discarded selections from another filter");
            table.ScrollIntoView(table.Items[0]);
            steam.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var check = Tree(table).OfType<CheckBox>().Single(box =>
                AutomationProperties.GetName(box) == "Include event in highlights");
            Check(check.IsChecked == true, "selected event did not show as checked after filtering");
            check.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Check(Count().StartsWith("1 checked"), "individual checkbox did not change the export selection");
            filter.SelectedIndex = 0;
            Check(Count().StartsWith("1 checked"), "changing filter lost the user's selections");
            Click("ClearEventsButton");
            Check(Count().StartsWith("0 checked") && !Ui<Button>("MontageButton").IsEnabled, "empty selection offered a montage");
        }
        finally { steam.Close(); }
    }

    private static async Task VerifyAutoHighlights(MainWindow main, string data, AppSettings draft)
    {
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Members)!.GetValue(main)!;
        T Ui<T>(string name) => (T)main.FindName(name);
        var applied = Field<AppSettings>("_appliedSettings");
        applied.AutomationEnabled = true;
        var tracker = Field<SessionTracker>("_sessionTracker");
        string exe = applied.Games[0].ExecutablePath;
        var observation = new SessionGameObservation("WARDOGS", exe);
        var idle = new AutomationStatus("None", "Off", "Off", "Off", "", "Watching", []);
        void GameExited() => typeof(MainWindow).GetMethod("UpdateDetectedSessions", Members)!.Invoke(main, [idle]);
        var completedWithoutOptIn = tracker.Observe(DateTime.UtcNow.AddMinutes(-15), true, [observation]).Single();
        GameExited();
        Check(Field<SessionLibraryStore>("_sessionStore").Get(completedWithoutOptIn.Id)?.Status == SessionStatus.Completed &&
            Field<Task?>("_steamHighlightWorker") is null, "turning automatic Steam highlights off still queued a completed session");

        Ui<ListBox>("Navigation").SelectedIndex = 1;
        var grid = Ui<DataGrid>("GameGrid");
        var wardogs = draft.Games[0];
        var other = new GameRule { Name = "Other game", ExecutablePath = @"C:\Synthetic\Other.exe" };
        var alternate = new GameRule { Name = "Alternate", ExecutablePath = @"C:\Synthetic\WardogsAlt.exe" };
        draft.Games.Add(other);
        draft.Games.Add(alternate);
        grid.SelectedItem = other;
        Check(Ui<Border>("GameSteamHighlightsCard").Visibility == Visibility.Collapsed, "non-Wardogs games should not show Steam highlights");
        grid.SelectedItem = wardogs;
        var toggle = Ui<CheckBox>("GameSteamAutoHighlights");
        Check(Ui<Border>("GameSteamHighlightsCard").Visibility == Visibility.Visible && toggle.IsChecked == false,
            "Wardogs should offer an off-by-default Steam highlights switch");
        toggle.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        grid.SelectedItem = alternate;
        Check(toggle.IsChecked == false, "enabling highlights for one game leaked into another row");
        grid.SelectedItem = wardogs;
        Check(toggle.IsChecked == true, "selecting another row lost the in-progress edit");
        Ui<Button>("OpenSteamHighlightTiming").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(Ui<ListBox>("Navigation").SelectedIndex == 2 && Ui<Expander>("SteamHighlightOptions").IsExpanded,
            "the Games link did not open Steam timing settings");
        Check(Ui<TextBox>("SteamBeforeBox").Text == "10" && Ui<TextBox>("SteamAfterBox").Text == "10", "Steam padding controls must show 10/10 by default");
        Ui<CheckBox>("SteamAutoKills").SetCurrentValue(ToggleButton.IsCheckedProperty, false);
        Ui<TextBox>("SteamBeforeBox").SetCurrentValue(TextBox.TextProperty, "8");
        Ui<TextBox>("SteamAfterBox").SetCurrentValue(TextBox.TextProperty, "12");
        var root = Directory.CreateDirectory(Path.Combine(data, "steam-auto-source"));
        Ui<TextBox>("SteamRecordingFolderBox").SetCurrentValue(TextBox.TextProperty, root.FullName);
        Check(wardogs.SteamAutoHighlights == true && draft.SteamHighlights is { AutoExport: false, BeforeSeconds: 8, AfterSeconds: 12, IncludeKills: false } &&
            draft.SteamHighlights.RecordingFolder == root.FullName, "Steam preferences UI did not update the settings draft");
        applied.SteamHighlights = draft.SteamHighlights.Copy();
        applied.Games[0] = wardogs.Copy();
        applied.Games.Add(alternate.Copy());
        tracker.Observe(DateTime.UtcNow.AddMinutes(-10), true, [new SessionGameObservation(alternate.Name, alternate.ExecutablePath)]);
        GameExited();
        Check(Field<Task?>("_steamHighlightWorker") is null, "exiting a different, unselected Steam rule queued highlights");
        var timelineDirectory = root.CreateSubdirectory("timelines");
        var steamStart = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeSeconds());
        var elapsed = (ulong)(DateTimeOffset.UtcNow - steamStart).TotalMilliseconds;
        string timelineId = "timeline_1867240" + steamStart.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string json = FormattableString.Invariant($$"""{"daterecorded":"{{steamStart.ToUnixTimeSeconds()}}","endtime":"{{elapsed}}"}""");
        File.WriteAllText(Path.Combine(timelineDirectory.FullName, timelineId + ".json"), json);
        byte[] row = [.. Bytes(1, Encoding.ASCII.GetBytes(timelineId)), .. Number(2, 1867240),
            .. Number(3, (ulong)steamStart.ToUnixTimeSeconds()), .. Number(4, elapsed)];
        File.WriteAllBytes(Path.Combine(root.FullName, "gamerecording.pb"), Bytes(1, row));
        var tracked = tracker.Observe(steamStart.UtcDateTime.AddSeconds(4), true, [observation]).Single();
        GameExited();
        Check(Field<Task?>("_steamHighlightWorker") is { IsCompleted: false }, "a completed opted-in Wardogs session did not queue a Steam export");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var status = Ui<TextBlock>("SteamHighlightStatus");
        while (!status.Text.Contains("no WARDOGS events", StringComparison.Ordinal))
            await Task.Delay(40, timeout.Token);
        Check(Field<SessionLibraryStore>("_sessionStore").Get(tracked.Id) is { Status: SessionStatus.Completed, Attachments.Count: 0 },
            "a session with no Steam events acquired an attachment");
    }

    private static byte[] Number(int field, ulong value) => [.. Varint((ulong)(field << 3)), .. Varint(value)];
    private static byte[] Bytes(int field, byte[] value) => [.. Varint((ulong)((field << 3) | 2)), .. Varint((ulong)value.Length), .. value];
    private static IEnumerable<byte> Varint(ulong value)
    {
        for (; value >= 128; value >>= 7) yield return (byte)(value | 128);
        yield return (byte)value;
    }

    private static IEnumerable<DependencyObject> Tree(DependencyObject element)
    {
        yield return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Tree(VisualTreeHelper.GetChild(element, index))) yield return child;
    }
    private static void Offscreen(Window window)
    {
        window.ShowActivated = window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = window.Top = -20000;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
