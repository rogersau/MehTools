using System.Text;
using JamesTools.Core.Configuration;
using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using JamesTools.Core.Sessions;

internal static class SteamWardogsTests
{
    private const string Timeline = "timeline_186724020261124_082341";
    private const string Recording = "bg_1867240_20261124_082607";
    private const string Later = "bg_1867240_20261124_082707";

    public static async Task Run()
    {
        using var temp = new TempDirectory();
        string timelines = Directory.CreateDirectory(Path.Combine(temp.Path, "timelines")).FullName;
        string timelinePath = Path.Combine(timelines, Timeline + ".json");
        string index = Path.Combine(temp.Path, "gamerecording.pb");
        var first = WriteRecording(temp.Path, Recording, first: 3, seconds: 9, fragments: [3, 4, 5]);
        string later = WriteRecording(temp.Path, Later, first: 1, seconds: 6, fragments: [1, 2]);
        File.WriteAllBytes(index, Message(1, Text(1, Timeline), Number(2, 1867240), Number(3, 1795508621), Number(4, 128000),
            Message(5, Text(1, Recording), Number(2, 106000), Number(3, 9000), Number(10, 100000)),
            Message(5, Text(1, Later), Number(2, 120000), Number(3, 6000), Number(10, 120000)),
            Number(42, 9)));
        File.WriteAllText(timelinePath, """
            {"daterecorded":"1795508621","endtime":"150000","entries":[
                {"id":"1","type":"event","time":"104000","icon":"wd-kill","title":"Killed"},
                {"id":"2","type":"event","time":"107250","icon":"wd-kill","title":"Killed","description":"AK74, 54 m"},
                {"id":"8","type":"event","time":"108750","icon":"wd-kill","title":"Killed"},
                {"id":3,"type":"event","time":115000,"icon":"wd-killed","title":"Killed by"},
                {"id":"4","type":"event","time":"123250","icon":"wd-assist","title":"Kill Assist"},
                {"id":"4","type":"event","time":"123250","icon":"wd-assist","title":"duplicate"},
                {"id":"5","type":"gamemode","time":"123251"},
                {"id":"6","type":"event","time":"123252","icon":"steam"},
                {"id":"7","type":"event","time":"126000","icon":"wd-respawn","title":"Revived"}
            ]}
            """);
        File.WriteAllText(Path.Combine(timelines, "timeline_186724020261125_082341.json"), "{");
        File.WriteAllText(Path.Combine(timelines, "timeline_22110020261124_082341.json"), "{}");
        var catalog = SteamWardogsLibrary.Read(temp.Path);
        var timeline = catalog.Timelines.Single();
        Check(catalog.UnreadableTimelines == 1 && timeline.Id == Timeline, "wrong game, timeline or malformed file handling");
        Check(timeline.Events.Select(e => (e.Id, e.HasFootage)).SequenceEqual(new[] { (1L, false), (2L, true), (8L, true), (3L, false), (4L, true), (7L, false) }),
            "wrong app/source, offsets, trim, gap, boundary, or duplicate event handling");
        Check(timeline.Events[1].Description == "AK74, 54 m", "marker detail was lost");
        VerifySettingsAndMatch(temp.Path, catalog, timeline);
        VerifyKillDeathMarkers(temp.Path);
        await VerifyMontage(temp.Path, timeline);

        // Fall back to the contiguous center fragment when neighboring audio has expired.
        File.Delete(Path.Combine(first, "chunk-stream1-00004.m4s"));
        var destination = Path.Combine(temp.Path, "export.mp4");
        var argsSeen = new List<string[]>();
        double firstPts = 6.017;
        var runner = new MediaTools("", (tool, arguments, _, _) =>
        {
            var parameters = arguments.ToArray(); argsSeen.Add(parameters);
            if (tool == "ffmpeg")
            {
                var staged = parameters.Select((p, i) => (p, i)).Where(t => t.p == "-i").Select(t => parameters[t.i+1]).ToArray();
                Check(staged.Select(File.ReadAllText).SequenceEqual(["init0|0-3|", "init1|1-3|"]), "wrong fragment or input track copied");
                File.WriteAllText(parameters[^1], "mock media");
                return Task.FromResult("");
            }
            return Task.FromResult(parameters.Contains("stream=start_time")
                ? FormattableString.Invariant($$"""{"streams":[{"start_time":"{{firstPts}}"}]}""")
                : """{"format":{"duration":"3.05"},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""");
        });
        var exporter = new SteamWardogsClips(runner);
        var clip = await exporter.ExportAsync(temp.Path, Timeline, 2, destination);
        Check(clip.EventPosition == TimeSpan.FromSeconds(1.25) && clip.Media.AudioTracks == 1 && File.Exists(destination),
            "clip did not use original timestamp or preserve audio");
        Check(argsSeen[1].Contains("-6") && argsSeen[1].Contains("1:a:0") && argsSeen[1].Contains("-copyts"), "remux did not keep streams aligned");
        Check(!Directory.EnumerateFiles(temp.Path, ".meh-steam-*").Any(), "temporary inputs were not cleaned up");
        await Reject<IOException>(() => exporter.ExportAsync(temp.Path, Timeline, 2, destination));
        firstPts = 0;
        await Reject<InvalidDataException>(() => exporter.ExportAsync(temp.Path, Timeline, 2, Path.Combine(temp.Path, "bad.mp4")));
        Check(!Directory.EnumerateFiles(temp.Path, ".meh-steam-*").Any(), "failed timestamp verification leaked files");
        File.Delete(Path.Combine(later, "chunk-stream0-00002.m4s"));
        await Reject<InvalidDataException>(() => exporter.ExportAsync(temp.Path, Timeline, 4, Path.Combine(temp.Path, "gone.mp4")));
        File.WriteAllBytes(index, [10, 127]);
        await Reject<InvalidDataException>(() => Task.FromResult(SteamWardogsLibrary.Read(temp.Path)));
    }

    private static void VerifyKillDeathMarkers(string parent)
    {
        string root = Path.Combine(parent, "marker-only");
        string timelines = Directory.CreateDirectory(Path.Combine(root, "timelines")).FullName;
        File.WriteAllText(Path.Combine(timelines, Timeline + ".json"), """
            {"daterecorded":"1795508621","endtime":"150000","entries":[
              {"id":1,"time":10000,"type":"event","icon":"wd-kill"},
              {"id":1,"time":10000,"type":"event","icon":"wd-kill"},
              {"id":2,"time":20000,"type":"event","icon":"wd-killed"},
              {"id":3,"time":30000,"type":"event","icon":"wd-killed"},
              {"id":4,"time":40000,"type":"event","icon":"wd-assist"},
              {"id":5,"time":50000,"type":"event","icon":"wd-respawn"}
            ]}
            """);
        var catalog = SteamWardogsLibrary.ReadMarkers(root);
        var timeline = catalog.Timelines.Single();
        var session = new GameSession { GameName = "WARDOGS", StartedUtc = timeline.RecordedAt.UtcDateTime.AddSeconds(5),
            EndedUtc = timeline.RecordedAt.UtcDateTime.AddSeconds(149), Status = SessionStatus.Interrupted };
        Check(SteamWardogsLibrary.MatchHistoricalSession(session, catalog)?.KillDeaths() is { Kills: 1, Deaths: 2, Ratio: .5 } &&
            timeline.Events.All(e => !e.HasFootage) && SteamWardogsLibrary.Read(root).Timelines.Single().EndedAt is null,
            "Steam K/D needs only markers, must deduplicate IDs, and must not make the timeline eligible to export without the index");
        Check((timeline with { Events = timeline.Events.Where(e => e.Icon == "wd-kill").ToArray() }).KillDeaths()
                is { Kills: 1, Deaths: 0, Ratio: null } &&
            (timeline with { Events = timeline.Events.Where(e => e.Icon == "wd-killed").ToArray() }).KillDeaths()
                is { Kills: 0, Deaths: 2, Ratio: 0 } &&
            (timeline with { Events = timeline.Events.Where(e => e.Icon == "wd-assist").ToArray() }).KillDeaths() is null,
            "zero deaths, zero kills, or a timeline without K/D markers was shown incorrectly");
    }

    private static void VerifySettingsAndMatch(string root, SteamWardogsCatalog catalog, SteamWardogsTimeline timeline)
    {
        var preferences = new SettingsStore(root);
        Check(preferences.Deserialize("{}").SteamHighlights is
            { AutoExport: false, BeforeSeconds: 10, AfterSeconds: 10, IncludedCategories: SteamWardogsEventCategory.All },
            "Steam auto-export should default off with 10 seconds on both sides");
        var app = new AppSettings { SteamHighlights = new() { AutoExport = true, BeforeSeconds = 7, AfterSeconds = 22,
            RecordingFolder = @"D:\Steam", OutputFolder = @"D:\Highlights", IncludeOther = false } };
        var snapshot = app.Snapshot();
        app.SteamHighlights.AfterSeconds = 50;
        Check(preferences.Deserialize(preferences.Serialize(snapshot)).SteamHighlights is
            { AutoExport: true, BeforeSeconds: 7, AfterSeconds: 22, RecordingFolder: @"D:\Steam", OutputFolder: @"D:\Highlights", IncludeOther: false },
            "Steam settings did not round-trip as an independent snapshot");
        Check(!SteamHighlightSettings.ValidPadding(0, 0) && SteamHighlightSettings.ValidPadding(10, 0) &&
            !SteamHighlightSettings.ValidPadding(61, 10), "invalid Steam padding accepted or zero on one side rejected");
        var wardogs = new GameRule { Name = "My game", ExecutablePath = @"C:\Games\WardogsClient-Win64-Shipping.exe", Record = false };
        var otherGame = new GameRule { Name = "Other", ExecutablePath = @"C:\Games\other.exe", SteamAutoHighlights = true };
        var perGame = new AppSettings { Games = [wardogs, otherGame], SteamHighlights = new() { AutoExport = true } };
        Check(perGame.SteamHighlights.AutoExportFor(wardogs) && !perGame.SteamHighlights.AutoExportFor(otherGame),
            "the former global preference was not preserved for Wardogs or was applied to another game");
        wardogs.SteamAutoHighlights = false;
        Check(!perGame.SteamHighlights.AutoExportFor(wardogs), "explicit game opt-out did not override the old global preference");
        perGame.SteamHighlights.AutoExport = false;
        wardogs.SteamAutoHighlights = true;
        var copied = preferences.Deserialize(preferences.Serialize(perGame.Snapshot()));
        Check(copied.Games[0] is { SteamAutoHighlights: true, Record: false } && copied.SteamHighlights.AutoExportFor(copied.Games[0]),
            "per-game opt-in did not survive saving or depended on OBS");
        copied.SteamHighlights.IncludeKills = copied.SteamHighlights.IncludeAssists =
            copied.SteamHighlights.IncludeRevives = copied.SteamHighlights.IncludeOther = false;
        Check(SettingsValidation.Validate(copied).Any(m => m.Contains("one event type for automatic Steam highlights", StringComparison.Ordinal)),
            "per-game opt-in accepted automatic highlights without any event types");
        Check(timeline.EndedAt == timeline.RecordedAt.AddSeconds(150), "the Steam session end should include time after capture stopped");
        var session = new GameSession { GameName = "WARDOGS", StartedUtc = timeline.RecordedAt.UtcDateTime.AddSeconds(4),
            EndedUtc = timeline.EndedAt?.UtcDateTime.AddSeconds(2), Status = SessionStatus.Completed };
        Check(SteamWardogsLibrary.MatchCompletedSession(session, catalog)?.Id == Timeline, "close matching game session was not recognized");
        var interrupted = session with { Id = Guid.NewGuid(), Status = SessionStatus.Interrupted, EndedUtc = session.EndedUtc?.AddSeconds(-51) };
        Check(SteamWardogsLibrary.MatchCompletedSession(interrupted, catalog) is null &&
            SteamWardogsLibrary.MatchHistoricalSession(interrupted, catalog)?.Id == Timeline &&
            SteamWardogsLibrary.FindHistoricalSession(timeline, [interrupted])?.Id == interrupted.Id &&
            SteamWardogsLibrary.FindHistoricalSession(timeline, [session, interrupted]) is null,
            "historical export failed to match an interrupted session or attached to an ambiguous session");
        Check(SteamWardogsLibrary.MatchCompletedSession(session with { Status = SessionStatus.WatchingStopped }, catalog) is null &&
            SteamWardogsLibrary.MatchCompletedSession(session with { GameName = "Other game" }, catalog) is null &&
            SteamWardogsLibrary.MatchCompletedSession(session with { EndedUtc = session.EndedUtc?.AddMinutes(4) }, catalog) is null &&
            SteamWardogsLibrary.MatchCompletedSession(session, new([timeline with { EndedAt = null }], 0)) is null &&
            SteamWardogsLibrary.MatchCompletedSession(session, new([timeline, timeline with { Id = "other" }], 0)) is null,
            "automatic export matched an unfinished, unrelated, or ambiguous timeline");
    }

    private static async Task VerifyMontage(string root, SteamWardogsTimeline timeline)
    {
        var plan = SteamWardogsClips.PlanMontage(timeline, 2, 1);
        Check(plan.IncludedEvents == 3 && plan.Windows.Select(w => (w.StartTimelineMs, w.First, w.Last)).SequenceEqual(
            new[] { (106000L, 3, 4), (120000L, 1, 2) }), "padding, cross-span ordering, or overlapping-event merge was wrong");
        foreach (bool changedLayout in new[] { false, true })
        {
            int partCount = 0, conversions = 0;
            var runner = new MediaTools("", (tool, args, _, _) =>
            {
                var a = args.ToArray();
                string name = Path.GetFileName(a[^1]);
                if (tool == "ffmpeg")
                {
                    if (a.Contains("-copyts")) partCount++;
                    else if (a.Contains("-vf")) { conversions++; Check(a.Contains("mpeg4"), "fallback encoder must be bundled"); }
                    else if (a.Contains("concat"))
                    {
                        var parts = File.ReadAllLines(a[Array.IndexOf(a, "-i") + 1]);
                        var prefix = changedLayout ? "normalized" : "part";
                        Check(parts.SequenceEqual([$"file '{prefix}-00000.mp4'", $"file '{prefix}-00001.mp4'"]), "wrong clip order or unsafe concat paths");
                    }
                    else throw new Exception("Unexpected media invocation.");
                    File.WriteAllText(a[^1], "mock media");
                    return Task.FromResult("");
                }
                if (a.Contains("stream=start_time"))
                {
                    bool first = File.ReadAllText(a[^1]).StartsWith("init0|0-3|", StringComparison.Ordinal);
                    return Task.FromResult(first ? """{"streams":[{"start_time":"6.017"}]}""" : """{"streams":[{"start_time":"0.02"}]}""");
                }
                if (a.Any(arg => arg.StartsWith("stream=codec_type,codec_name", StringComparison.Ordinal)))
                {
                    int width = changedLayout && name == "part-00001.mp4" ? 1280 : 1920;
                    return Task.FromResult($$"""{"streams":[{"codec_type":"video","width":{{width}}},{"codec_type":"audio"}]}""");
                }
                int duration = name == "highlights.mp4" ? 12 : 6;
                return Task.FromResult($$"""{"format":{"duration":{{duration}}},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""");
            });
            string destination = Path.Combine(root, changedLayout ? "mixed-layout.mp4" : "montage.mp4");
            var exporter = new SteamWardogsClips(runner);
            var result = await exporter.ExportMontageAsync(root, timeline, destination, 2, 1);
            Check(result is { IncludedEvents: 3, SkippedEvents: 3, Windows: 2, Media.Duration: 12 } &&
                File.Exists(destination) && partCount == 2 && conversions == (changedLayout ? 2 : 0),
                "montage did not report merged/skipped outcomes or verify layout fallback");
            Check(!Directory.EnumerateDirectories(root, ".meh-steam-*").Any(), "montage scratch files were not cleaned up");
            if (!changedLayout)
            {
                partCount = 0;
                var selected = await exporter.ExportMontageAsync(root, timeline, Path.Combine(root, "manual.mp4"), 2, 1,
                    selectedEventIds: [4, 4, 73]);
                Check(selected is { IncludedEvents: 1, SkippedEvents: 1, Windows: 1 } && partCount == 1,
                    "manual export did not use only the checked marker IDs or report a missing selection");
                partCount = 0;
                var filtered = await exporter.ExportMontageAsync(root, timeline, Path.Combine(root, "automatic-assists.mp4"), 2, 1,
                    categories: SteamWardogsEventCategory.Assists);
                Check(filtered is { IncludedEvents: 1, SkippedEvents: 0, Windows: 1 } && partCount == 1,
                    "automatic export did not use only opted-in event types");
            }
        }
    }

    public static async Task Smoke(string recordingRoot, string toolsFolder)
    {
        using var temp = new TempDirectory();
        var media = new MediaTools(toolsFolder);
        var catalog = SteamWardogsLibrary.Read(recordingRoot);
        var timeline = catalog.Timelines.First(t => t.Events.Any(e => e.HasFootage && e.Icon == "wd-kill"));
        var kill = timeline.Events.First(e => e.HasFootage && e.Icon == "wd-kill");
        var result = await new SteamWardogsClips(media).ExportAsync(recordingRoot, timeline.Id, kill.Id, Path.Combine(temp.Path, "smoke.mp4"));
        await media.Execute("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-i", result.Path, "-map", "0:v:0", "-map", "0:a?", "-f", "null", "-"],
            TimeSpan.FromMinutes(2), default);
        Check(kill.TimelineMs > 0 && result.EventPosition.TotalSeconds < result.Media.Duration, "event outside verified clip");
        Console.WriteLine($"PASS Steam read-only smoke, {catalog.Timelines.Count} timelines / {timeline.Events.Count} events in sampled timeline, " +
            $"decoded {result.Media.Duration:0.000}s clip with {result.Media.AudioTracks} audio track(s), marker {result.EventPosition.TotalSeconds:0.000}s into clip.");
        Console.WriteLine($"Steam folder discovery matches supplied path: {string.Equals(SteamGameDetector.FindGameRecordingDirectory(), Path.GetFullPath(recordingRoot), StringComparison.OrdinalIgnoreCase)}");
    }

    public static async Task MontageSmoke(string recordingRoot, string toolsFolder)
    {
        using var temp = new TempDirectory();
        var media = new MediaTools(toolsFolder);
        var catalog = SteamWardogsLibrary.Read(recordingRoot);
        var timeline = catalog.Timelines.First(t => t.EndedAt is not null && t.Events.Count(e => e.HasFootage) >= 5);
        var session = new GameSession { GameName = "WARDOGS", StartedUtc = timeline.RecordedAt.UtcDateTime.AddSeconds(4),
            EndedUtc = timeline.EndedAt?.UtcDateTime.AddSeconds(2), Status = SessionStatus.Completed };
        var matched = SteamWardogsLibrary.MatchCompletedSession(session, catalog);
        Check(matched?.Id == timeline.Id, "real timeline did not match completed session");
        var result = await new SteamWardogsClips(media).ExportMontageAsync(recordingRoot, matched!, Path.Combine(temp.Path, "smoke.mp4"));
        Check(result.Path is not null && result.Media is { VideoTracks: 1, AudioTracks: >= 1 } &&
            result.IncludedEvents + result.SkippedEvents == timeline.Events.Count && result.Windows < result.IncludedEvents &&
            result.Media.Duration < (timeline.EndedAt - timeline.RecordedAt)?.TotalSeconds / 2,
            "real montage lost clips, tracks or event counts, or included too much non-event footage");
        await media.Execute("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-i", result.Path!, "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-f", "null", "-"],
            TimeSpan.FromMinutes(3), default);
        Console.WriteLine($"PASS Steam montage smoke · {result.IncludedEvents} events, {result.SkippedEvents} skipped, {result.Windows} windows, {result.Media!.Duration:0.0}s, {result.Media.AudioTracks} audio track(s).");
    }

    public static async Task SelectionSmoke(string recordingRoot, string toolsFolder, string sessionsDirectory)
    {
        using var temp = new TempDirectory();
        var catalog = SteamWardogsLibrary.Read(recordingRoot);
        var timeline = catalog.Timelines.First(t => t.EndedAt is not null && t.Events.Count(e => e.HasFootage) >= 5);
        // The actual history file is copied so a test cannot write to the user's session library.
        File.Copy(Path.Combine(sessionsDirectory, "sessions.json"), Path.Combine(temp.Path, "sessions.json"));
        var sessions = new SessionLibraryStore(temp.Path);
        var session = SteamWardogsLibrary.FindHistoricalSession(timeline, sessions.Snapshot());
        Check(session is not null && SteamWardogsLibrary.MatchHistoricalSession(session, catalog)?.Id == timeline.Id,
            "real Steam footage did not match a unique historical Meh Tools session");
        var kd = SteamWardogsLibrary.MatchHistoricalSession(session!, SteamWardogsLibrary.ReadMarkers(recordingRoot))?.KillDeaths();
        Check(kd is not null && kd == timeline.KillDeaths(), "marker-only K/D did not match the full Steam timeline");
        Console.WriteLine($"PASS Steam K/D · {kd!.Kills} kills, {kd.Deaths} death markers, {kd.Ratio:0.00}.");
        var picks = timeline.Events.Where(e => e.Category == SteamWardogsEventCategory.Kills && e.HasFootage)
            .Take(3).Select(e => e.Id).ToArray();
        Check(picks.Length == 3, "expected three known Steam kill markers");
        var media = new MediaTools(toolsFolder);
        var result = await new SteamWardogsClips(media).ExportMontageAsync(recordingRoot, timeline, Path.Combine(temp.Path, "selected.mp4"), selectedEventIds: picks);
        Check(result is { Path: not null, IncludedEvents: 3, SkippedEvents: 0, Media.AudioTracks: >= 1 },
            "selected real Steam events were not exported with audio");
        await media.Execute("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-i", result.Path!, "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-f", "null", "-"],
            TimeSpan.FromMinutes(2), default);
        Check(sessions.AttachExplicitly(session!.Id, SessionAttachmentKind.Clip, result.Path!, DateTime.UtcNow) &&
            new SessionLibraryStore(temp.Path).Get(session.Id)!.Attachments.Any(a => a.Kind == SessionAttachmentKind.Clip && a.Path == result.Path),
            "verified historical highlight did not persist on the right session");
        Console.WriteLine($"PASS Steam selection smoke · matched {session.Status} session from {timeline.RecordedAt.LocalDateTime:g}, exported 3 of {timeline.Events.Count} events with audio.");
    }

    private static string WriteRecording(string root, string id, int first, int seconds, int[] fragments)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "video", id)).FullName;
        string Track(int i, string kind) => $$"""
            <AdaptationSet contentType="{{kind}}"><Representation id="{{i}}">
              <SegmentTemplate timescale="1000000" duration="3000000" initialization="init-stream$RepresentationID$.m4s" media="chunk-stream$RepresentationID$-$Number%05d$.m4s" startNumber="{{first}}"/>
            </Representation></AdaptationSet>
            """;
        File.WriteAllText(Path.Combine(directory, "session.mpd"), $$"""
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT{{seconds}}S">
            <Period start="PT0S">{{Track(0, "video")}}{{Track(1, "audio")}}</Period></MPD>
            """);
        for (var i = 0; i < 2; i++)
        {
            File.WriteAllText(Path.Combine(directory, $"init-stream{i}.m4s"), $"init{i}|");
            foreach (int fragment in fragments)
                File.WriteAllText(Path.Combine(directory, $"chunk-stream{i}-{fragment:D5}.m4s"), $"{i}-{fragment}|");
        }
        return directory;
    }

    private static byte[] Message(int field, params byte[][] fields) => Bytes(field, fields.SelectMany(x => x).ToArray());
    private static byte[] Text(int field, string text) => Bytes(field, Encoding.ASCII.GetBytes(text));
    private static byte[] Bytes(int field, byte[] value) => [.. Varint((ulong)((field << 3) | 2)), .. Varint((ulong)value.Length), .. value];
    private static byte[] Number(int field, ulong value) => [.. Varint((ulong)(field << 3)), .. Varint(value)];
    private static IEnumerable<byte> Varint(ulong v)
    {
        for (; v >= 128; v >>= 7) yield return (byte)(v | 128);
        yield return (byte)v;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    {
        try { await action(); throw new Exception("Expected " + typeof(T).Name); }
        catch (T) { }
    }
}
