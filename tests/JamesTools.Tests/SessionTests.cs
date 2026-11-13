using JamesTools.Core.Sessions;

internal static class SessionTests
{
    public static Task Run()
    {
        RepeatedObservationsAndMultipleGames();
        LateAndDuplicateAttachments();
        RecoveryAndMalformedStore();
        NotesAndFavourites();
        PreviousReportSelection();
        FailedWriteDoesNotPublishMutation();
        return Task.CompletedTask;
    }

    private static void RepeatedObservationsAndMultipleGames()
    {
        using var temp = new TempDirectory();
        var store = new SessionLibraryStore(temp.Path);
        var tracker = new SessionTracker(store);
        var start = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var first = new SessionGameObservation("Example", @"C:\Games\example.exe");
        var second = new SessionGameObservation("Other", @"C:\Games\other.exe");
        tracker.Observe(start, true, [first]);
        var firstId = tracker.ActiveSessions.Values.Single();
        tracker.Observe(start.AddSeconds(10), true, [first]);
        Check(tracker.ActiveSessions.Values.Single() == firstId, "repeated observations created another session");
        tracker.Observe(start.AddSeconds(20), true, [first, second]);
        Check(tracker.ActiveSessions.Count == 2, "simultaneous games were not tracked separately");
        tracker.Observe(start.AddSeconds(30), true, [second]);
        Check(store.Get(firstId)?.Status == SessionStatus.Completed, "game exit did not complete its session");
        tracker.Observe(start.AddSeconds(40), false, []);
        Check(store.Snapshot().Single(s => s.GameName == "Other").Status == SessionStatus.WatchingStopped,
            "stopping watching did not end active sessions with the right status");
    }

    private static void LateAndDuplicateAttachments()
    {
        using var temp = new TempDirectory();
        var store = new SessionLibraryStore(temp.Path);
        var now = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = store.Create("Example", SessionLibraryStore.NormalizeExecutable(@"C:\Games\example.exe"), now);
        store.End(session.Id, now.AddMinutes(2), SessionStatus.Completed);
        var file = Path.Combine(temp.Path, "late.mkv");
        File.WriteAllText(file, "media fixture");
        var attached = store.AddAttachment(session.Id, null, "", SessionAttachmentKind.Recording, file, now.AddMinutes(3));
        var duplicate = store.AddAttachment(null, "Unassigned recording", "", SessionAttachmentKind.Recording, file, now.AddMinutes(4));
        Check(attached == session.Id && duplicate == session.Id, "late or duplicate attachment lost its captured session identity");
        Check(store.Snapshot().Count == 1 && store.Get(session.Id)!.Attachments.Count == 1, "duplicate path was recorded twice");

        var unassigned = store.AddAttachment(null, "Unassigned clip", "", SessionAttachmentKind.Clip, file, now.AddMinutes(5));
        Check(unassigned == session.Id, "duplicate automatic confirmation was assigned twice");
        var other = store.Create("Other", "other-key", now.AddMinutes(6), SessionStatus.Completed);
        Check(store.AttachExplicitly(other.Id, SessionAttachmentKind.Clip, file, now.AddMinutes(7)) &&
              store.Get(other.Id)!.Attachments.Single().Kind == SessionAttachmentKind.Clip,
            "explicit file attachment could not select a session and attachment kind");

        var reopened = new SessionLibraryStore(temp.Path);
        Check(reopened.Get(session.Id)?.Attachments.Single().Path == Path.GetFullPath(file),
            "session history did not persist independently of settings");
    }

    private static void RecoveryAndMalformedStore()
    {
        using var temp = new TempDirectory();
        var store = new SessionLibraryStore(temp.Path);
        var tracker = new SessionTracker(store);
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        tracker.Observe(start, true, [new SessionGameObservation("Example", @"C:\Games\example.exe")]);
        tracker.Observe(start.AddMinutes(5), true, [new SessionGameObservation("Example", @"C:\Games\example.exe")]);
        var id = tracker.ActiveSessions.Values.Single();
        var recovered = new SessionLibraryStore(temp.Path).Get(id)!;
        Check(recovered.Status == SessionStatus.Interrupted && recovered.EndedUtc == start.AddMinutes(5),
            "active session recovery invented or lost detected time");

        var corruptDir = Path.Combine(temp.Path, "corrupt");
        Directory.CreateDirectory(corruptDir);
        var corruptPath = Path.Combine(corruptDir, "sessions.json");
        const string malformed = "{ this is deliberately malformed";
        File.WriteAllText(corruptPath, malformed);
        try { _ = new SessionLibraryStore(corruptDir); throw new Exception("malformed history was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(corruptPath) == malformed, "malformed history was overwritten");
        File.WriteAllText(corruptPath, "{\"FormatVersion\":99,\"Sessions\":[]}");
        var unsupported = File.ReadAllText(corruptPath);
        try { _ = new SessionLibraryStore(corruptDir); throw new Exception("unsupported history version was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(corruptPath) == unsupported, "unsupported history version was overwritten");
    }

    private static void NotesAndFavourites()
    {
        using var temp = new TempDirectory();
        var store = new SessionLibraryStore(temp.Path);
        var session = store.Create("Example", "key", DateTime.UtcNow, SessionStatus.Completed);
        Check(store.UpdateDetails(session.Id, "Stuttered after loading", true), "session details could not be updated");
        var loaded = new SessionLibraryStore(temp.Path).Get(session.Id)!;
        Check(loaded.Notes == "Stuttered after loading" && loaded.Favourite, "notes or favourite did not persist");
    }

    private static void PreviousReportSelection()
    {
        using var temp = new TempDirectory();
        var store = new SessionLibraryStore(temp.Path);
        var key = SessionLibraryStore.NormalizeExecutable(@"C:\Games\example.exe");
        var oldPath = Path.Combine(temp.Path, "old.txt");
        var currentPath = Path.Combine(temp.Path, "current.txt");
        File.WriteAllText(oldPath, "old report");
        File.WriteAllText(currentPath, "current report");
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var older = store.Create("Example", key, start, SessionStatus.Completed);
        store.AddAttachment(older.Id, null, key, SessionAttachmentKind.PerformanceReport, oldPath, start);
        var newerPrevious = store.Create("Example", key, start.AddDays(1), SessionStatus.Completed);
        store.AddAttachment(newerPrevious.Id, null, key, SessionAttachmentKind.PerformanceReport, currentPath, start.AddDays(1));
        var current = store.Create("Example", key, start.AddDays(2), SessionStatus.Completed);
        Check(store.FindPreviousReport(current)?.Id == newerPrevious.Id, "same-game previous report was not selected by recency");
        File.Delete(currentPath);
        Check(store.FindPreviousReport(current)?.Id == older.Id, "missing report was selected instead of the available prior report");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void FailedWriteDoesNotPublishMutation()
    {
        using var temp = new TempDirectory();
        var store = new SessionLibraryStore(temp.Path);
        var tracker = new SessionTracker(store);
        var now = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var first = new SessionGameObservation("First", @"C:\Games\first.exe");
        var second = new SessionGameObservation("Second", @"C:\Games\second.exe");
        tracker.Observe(now, true, [first]);
        // Holding the destination open without delete sharing prevents atomic
        // replacement on Windows without altering the original history.
        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                tracker.Observe(now.AddSeconds(1), true, [first, second]);
                throw new Exception("history save unexpectedly succeeded while locked");
            }
            catch (IOException) { }
            Check(store.Snapshot().Count == 1, "failed write published an unsaved session");
            Check(tracker.ActiveSessions.Count == 1, "failed write changed tracked sessions");
        }
        tracker.Observe(now.AddSeconds(2), true, [first, second]);
        Check(store.Snapshot().Count == 2 && tracker.ActiveSessions.Count == 2,
            "retry after write failure duplicated a session");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "JamesTools-Sessions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
