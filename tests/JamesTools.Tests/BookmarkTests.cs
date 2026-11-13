using JamesTools.Core.Recording;
using JamesTools.Core.Configuration;
internal static class BookmarkTests
{
    private static void Check(bool value) { if (!value) throw new Exception("Bookmark assertion failed"); }
    private static void Refuses(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected refusal"); }
    public static Task Run()
    {
        CaptureEventsKeepMediaIdentity();
        var tracker = new BookmarkCaptureTracker();
        ObsRecordStatus Status(long duration, bool paused = false, bool active = true, long bytes = 100) => new(active, paused, duration, bytes);
        Refuses(() => tracker.Mark(tracker.Generation, Status(0, active: false), "", DateTime.UtcNow));
        var owner = Guid.NewGuid(); tracker.Observe(Status(12000), owner);
        var generation = tracker.Generation;
        var a = tracker.Mark(generation, Status(13000), "First", DateTime.UtcNow);
        var b = tracker.Mark(generation, Status(13000, true), "Paused", DateTime.UtcNow);
        var c = tracker.Mark(generation, Status(14000), "Resumed", DateTime.UtcNow);
        Check(a.CaptureId == b.CaptureId && b.OffsetMilliseconds == 13000 && c.OffsetMilliseconds == 14000 && a.SessionId == owner);
        tracker.BreakContinuity(); tracker.Observe(Status(90000), null);
        Refuses(() => tracker.Mark(generation, Status(15000), "late", DateTime.UtcNow));
        var external = tracker.Mark(tracker.Generation, Status(91000), "", DateTime.UtcNow);
        Check(external.SessionId is null && external.OffsetMilliseconds == 91000 && external.CaptureId != a.CaptureId);
        tracker.Observe(Status(1, bytes: 1), null); Check(tracker.CaptureId != external.CaptureId);
        Refuses(() => tracker.Mark(tracker.Generation, Status(0, bytes: 0), "reset", DateTime.UtcNow));
        var root = Path.Combine(Path.GetTempPath(), "MehBookmarks-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            var store = new RecordingBookmarkStore(root); store.Add(a); store.Add(b); store.Add(c); store.Add(external);
            var path = Path.Combine(root, "missing.mkv");
            store.ConfirmPath(a.CaptureId, path); store.ConfirmPath(a.CaptureId, path);
            Check(store.Snapshot().Count(x => x.RecordingPath == path) == 3 && store.Snapshot().Last().RecordingPath is null);
            var copy = store.Snapshot(); copy[0] = copy[0] with { Label = "mutated" }; Check(store.Snapshot()[0].Label == "First");
            store.Edit(a.Id, "Edited"); store.Remove(b.Id);
            var loaded = new RecordingBookmarkStore(root); Check(loaded.Snapshot().Length == 3 && loaded.Snapshot()[0].Label == "Edited");
            try { loaded.Add(loaded.Snapshot()[0]); throw new Exception("Duplicate accepted"); } catch (InvalidDataException) { }
            Check(new RecordingBookmarkStore(root).Snapshot().Length == 3);
            var file = Path.Combine(root, "bookmarks.json"); File.Delete(file); Directory.CreateDirectory(file);
            try { loaded.Edit(a.Id, "failed"); throw new Exception("Expected save failure"); } catch (IOException) { }
            Check(loaded.Snapshot()[0].Label == "Edited"); Directory.Delete(file); File.WriteAllText(file, "corrupt");
            try { _ = new RecordingBookmarkStore(root); throw new Exception("Expected corruption error"); } catch (InvalidDataException) { }
            Check(File.ReadAllText(file) == "corrupt");
        }
        finally { Directory.Delete(root, true); }
        var settings = new AppSettings { BookmarkHotkey = "Ctrl+Alt+F8" };
        var snapshot = settings.Snapshot(); settings.BookmarkHotkey = "";
        var serializer = new SettingsStore("unused"); Check(serializer.Deserialize(serializer.Serialize(snapshot)).BookmarkHotkey == "Ctrl+Alt+F8");
        settings.BookmarkHotkey = "Ctrl+Alt+F10";
        Check(SettingsValidation.Validate(settings).Any(e => e.Contains("Bookmark") && e.Contains("different")));
        settings.BookmarkHotkey = "F8"; Check(SettingsValidation.Validate(settings).Any(e => e.Contains("Bookmark hotkey is invalid")));
        settings.BookmarkHotkey = ""; Check(!SettingsValidation.Validate(settings).Any(e => e.Contains("Bookmark")));
        return Task.CompletedTask;
    }

    private static void CaptureEventsKeepMediaIdentity()
    {
        var tracker = new BookmarkCaptureTracker();
        var owner = Guid.NewGuid();
        ObsRecordStateChangedEventArgs State(string name, bool active = false, bool paused = false) => new(active, paused, "OBS_WEBSOCKET_OUTPUT_" + name, null);
        tracker.ObserveState(State("STARTING"));
        tracker.ObserveState(State("STARTED", true));
        tracker.Observe(new(true, false, 12000, 100), owner);
        var capture = tracker.CaptureId;
        tracker.ObserveState(State("PAUSED", false, true)); // Raw OBS event flag differs from GetRecordStatus.
        var moment = tracker.Mark(tracker.Generation, new(true, true, 12000, 100), "Paused", DateTime.UtcNow);
        Check(moment.CaptureId == capture && moment.SessionId == owner);
        tracker.ObserveState(State("RESUMED", true));
        tracker.Observe(new(true, false, 13000, 110), null); // Original owner remains pinned after game ends.
        Check(tracker.SessionId == owner);
        tracker.ObserveState(State("STOPPING"));
        tracker.ObserveState(State("STOPPING"));
        tracker.Observe(new(true, false, 14000, 120), null);
        Check(tracker.CaptureId is null);
        Check(tracker.ObserveState(State("STOPPED")) == capture);
        Check(tracker.ObserveState(State("STOPPED")) is null);

        tracker.ObserveState(State("STARTING"));
        tracker.Observe(new(true, false, 1000, 10), owner);
        Check(tracker.CaptureId != capture);
        tracker.FileChanged();
        tracker.Observe(new(true, false, 2000, 20), owner);
        Refuses(() => tracker.Mark(tracker.Generation, new(true, false, 3000, 30), "split", DateTime.UtcNow));
        tracker.ObserveState(State("STOPPING"));
        Check(tracker.ObserveState(State("STOPPED")) is null);

        tracker.BreakContinuity();
        tracker.Observe(new(true, false, 90000, 1000), owner); // Reconnect in mid-recording: media file offset is unproven.
        var reconnect = tracker.Mark(tracker.Generation, new(true, false, 91000, 1100), "Reconnect", DateTime.UtcNow);
        Check(reconnect.OffsetMilliseconds == 91000);
        tracker.ObserveState(State("STOPPING"));
        Check(tracker.ObserveState(State("STOPPED")) is null);
    }
}
