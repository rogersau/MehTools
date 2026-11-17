using JamesTools.Core.Sessions;
using JamesTools.Core.Storage;

internal static class StorageCleanupTests
{
    private static readonly DateTime Now = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    public static Task Run()
    {
        UsageCountsProtectedMediaAndSharedPathsOnce();
        ExplicitSelectionDeduplicatesAndContinuesAfterFailures();
        ChangesAndNewProtectionsPreventDeletion();
        UnsafePathsAndReadOnlyFilesAreProtected();
        CancellationPreservesRemainingFiles();
        NativeDeletionUsesTheVerifiedFile();
        return Task.CompletedTask;
    }

    private static void UsageCountsProtectedMediaAndSharedPathsOnce()
    {
        using var temp = new TempDirectory();
        var old = Video(temp.Path, "old.mkv", 32, 40);
        var recent = Video(temp.Path, "recent.mkv", 64, 1);
        var favourite = Video(temp.Path, "favourite.mkv", 8);
        var clip = Video(temp.Path, "clip.mkv", 16);
        var active = Video(temp.Path, "active.mkv", 4);
        var missing = Path.Combine(temp.Path, "missing.mkv");
        var sessions = new[] { Session(old), Session(old), Session(recent), Session(favourite, favourite: true),
            Session(clip, kind: SessionAttachmentKind.Clip), Session(active, SessionStatus.Active), Session(missing) };
        var preview = CleanupPreviewBuilder.Build(sessions, [temp.Path], Now, 30, null);
        Check(preview.Entries.Count == 6 && preview.KnownMediaBytes == 124 && preview.UnknownSizeCount == 1,
            "usage must include protected recordings/clips, deduplicate shared files and distinguish unknown sizes");
        Check(preview.DeletableBytes == 96 && preview.PotentialReclaimableBytes == 32,
            "available bytes and suggestion bytes must remain separate");
        Check(preview.Entries.Single(e => e.Path == recent) is { CanDelete: true, Candidate: false },
            "manual cleanup must work without age or budget suggestions");
        Check(preview.Entries.Single(e => e.Path == old) is { CanDelete: true, FileStamp: not null },
            "available recordings must carry their original file metadata");
    }

    private static void ExplicitSelectionDeduplicatesAndContinuesAfterFailures()
    {
        using var temp = new TempDirectory();
        var locked = Video(temp.Path, "locked.mkv", 10);
        var selected = Video(temp.Path, "selected.mkv", 20);
        var untouched = Video(temp.Path, "untouched.mkv", 30);
        var sessions = new[] { Session(locked), Session(selected), Session(untouched) };
        var files = new TestFiles { FailPath = locked };
        var preview = CleanupPreviewBuilder.Build(sessions, [temp.Path], Now, null, null, files);
        var first = preview.Entries.Single(e => e.Path == locked);
        var second = preview.Entries.Single(e => e.Path == selected);
        var result = new RecordingCleanupService(files).DeleteSelected([first, second, second], () => sessions, [temp.Path]);
        Check(result is { DeletedCount: 1, DeletedBytes: 20, SkippedCount: 1, Cancelled: false },
            "partial cleanup must report actual deletions once and continue past a locked file");
        Check(!File.Exists(selected) && File.Exists(locked) && File.Exists(untouched), "cleanup affected an unselected or unavailable file");
        Check(sessions[1].Attachments.Single().Path == selected, "cleanup must preserve session history references");
    }

    private static void ChangesAndNewProtectionsPreventDeletion()
    {
        using var temp = new TempDirectory();
        var path = Video(temp.Path, "recording.mkv", 20);
        var session = Session(path);
        IReadOnlyList<GameSession> sessions = [session];
        var files = new TestFiles();
        var entry = CleanupPreviewBuilder.Build(sessions, [temp.Path], Now, null, null, files).Entries.Single();
        var cleanup = new RecordingCleanupService(files);
        RecordingCleanupResult Delete() => cleanup.DeleteSelected([entry], () => sessions, [temp.Path]);

        sessions = [session with { Favourite = true }];
        Check(Delete().SkippedCount == 1, "a newly saved favourite was deleted");
        sessions = [session, Session(path, kind: SessionAttachmentKind.Clip)];
        Check(Delete().SkippedCount == 1, "a new shared clip reference was ignored");
        sessions = [session with { Status = SessionStatus.Active }];
        Check(Delete().SkippedCount == 1, "a new active-session reference was ignored");
        sessions = [];
        Check(Delete().SkippedCount == 1, "an unreferenced file was deleted");
        sessions = [session];
        Check(cleanup.DeleteSelected([entry], () => sessions, []).SkippedCount == 1, "a removed recording root was ignored");
        Check(cleanup.DeleteSelected([entry with { CanDelete = false }], () => sessions, [temp.Path]).SkippedCount == 1,
            "a protected selection was accepted");
        files.Identity = "replacement";
        Check(Delete().SkippedCount == 1, "a replacement with matching size/times was deleted");
        files.Identity = "original";
        File.SetLastWriteTimeUtc(path, Now.AddDays(-2));
        Check(Delete().SkippedCount == 1, "a same-size recording with a changed write time was deleted");
        File.WriteAllBytes(path, new byte[21]);
        Check(Delete().SkippedCount == 1, "a resized recording was deleted");
        File.Delete(path);
        Check(Delete().SkippedCount == 1 && files.DeleteAttempts == 0, "a missing or stale recording reached the deleter");

        // A session change during an earlier deletion must protect a later item in the same batch.
        var first = Video(temp.Path, "first.mkv", 2);
        var later = Video(temp.Path, "later.mkv", 3);
        sessions = [Session(first), Session(later)];
        var preview = CleanupPreviewBuilder.Build(sessions, [temp.Path], Now, null, null, files);
        files.AfterDelete = () => sessions = sessions.Select(s => s.Attachments.Any(a => a.Path == later) ? s with { Favourite = true } : s).ToArray();
        var batch = cleanup.DeleteSelected([preview.Entries.Single(e => e.Path == first), preview.Entries.Single(e => e.Path == later)], () => sessions, [temp.Path]);
        Check(batch.DeletedCount == 1 && batch.SkippedCount == 1 && File.Exists(later), "session protections were only checked once for the batch");
    }

    private static void UnsafePathsAndReadOnlyFilesAreProtected()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "recordings"); Directory.CreateDirectory(root);
        var outside = Video(temp.Path, "outside.mkv", 12);
        var readOnly = Video(root, "read-only.mkv", 10);
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        try
        {
            var files = new TestFiles { MultipleLinks = true };
            var hardLinked = Video(root, "shared.mkv", 14);
            var preview = CleanupPreviewBuilder.Build([Session(outside), Session(readOnly), Session(hardLinked)], [root], Now, 1, null, files);
            Check(preview.Entries.All(e => !e.CanDelete), "outside, read-only or multiply linked files were available for deletion");
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }

        var link = Path.Combine(root, "linked.mkv");
        var directoryLink = Path.Combine(root, "linked-folder");
        try
        {
            File.CreateSymbolicLink(link, outside);
            Directory.CreateSymbolicLink(directoryLink, temp.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        { Console.WriteLine("SKIP storage symlink fixture: " + ex.Message); return; }
        var links = CleanupPreviewBuilder.Build([Session(link), Session(Path.Combine(directoryLink, "outside.mkv"))], [root], Now, 1, null);
        Check(links.Entries.All(e => !e.CanDelete && e.SizeBytes is null), "a file or ancestor link escaped recording-folder containment");
        Check(File.Exists(outside), "inspecting a link affected its target");
    }

    private static void CancellationPreservesRemainingFiles()
    {
        using var temp = new TempDirectory();
        var first = Video(temp.Path, "first.mkv", 5);
        var second = Video(temp.Path, "second.mkv", 6);
        var sessions = new[] { Session(first), Session(second) };
        using var cancellation = new CancellationTokenSource();
        var files = new TestFiles { AfterDelete = cancellation.Cancel };
        var entries = CleanupPreviewBuilder.Build(sessions, [temp.Path], Now, null, null, files).Entries;
        var cleanup = new RecordingCleanupService(files);
        cancellation.Cancel();
        Check(cleanup.DeleteSelected(entries, () => sessions, [temp.Path], cancellation.Token) is { DeletedCount: 0, Cancelled: true },
            "an already-cancelled cleanup deleted a recording");
        using var during = new CancellationTokenSource();
        files.AfterDelete = during.Cancel;
        var result = cleanup.DeleteSelected(entries, () => sessions, [temp.Path], during.Token);
        Check(result is { DeletedCount: 1, Cancelled: true } && entries.Count(e => File.Exists(e.Path)) == 1,
            "cancellation must preserve unprocessed files and report completed work");
    }

    private static void NativeDeletionUsesTheVerifiedFile()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP native recording deletion checks: Windows required."); return; }
        using var temp = new TempDirectory();
        var path = Video(temp.Path, "native.mkv", 30);
        var sessions = new[] { Session(path) };
        var files = new RecordingFileAccess();
        var entry = CleanupPreviewBuilder.Build(sessions, [temp.Path], Now, null, null).Entries.Single();
        Check(entry.CanDelete && entry.FileStamp?.Identity is not null, "native preview did not capture the file identity");
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            var locked = new RecordingCleanupService().DeleteSelected([entry], () => sessions, [temp.Path]);
            Check(locked.SkippedCount == 1 && File.Exists(path), "a recording open for writing was deleted");
        }
        File.SetLastWriteTimeUtc(path, entry.FileStamp!.LastWriteUtc);
        var stale = files.Inspect(path) with { Identity = "different-file" };
        RejectNativeDelete(stale, "different file identity");
        File.SetLastWriteTimeUtc(path, entry.FileStamp.LastWriteUtc.AddMinutes(1));
        RejectNativeDelete(entry.FileStamp, "changed write timestamp");
        File.SetLastWriteTimeUtc(path, entry.FileStamp.LastWriteUtc);
        File.SetCreationTimeUtc(path, entry.FileStamp.CreatedUtc.AddMinutes(-1));
        RejectNativeDelete(entry.FileStamp, "changed creation timestamp");
        File.SetCreationTimeUtc(path, entry.FileStamp.CreatedUtc);

        // Keep the old file alive so NTFS cannot reuse its identity for the replacement.
        var original = Path.Combine(temp.Path, "original.mkv");
        File.Move(path, original);
        File.WriteAllBytes(path, new byte[entry.FileStamp.SizeBytes]);
        File.SetCreationTimeUtc(path, entry.FileStamp.CreatedUtc);
        File.SetLastWriteTimeUtc(path, entry.FileStamp.LastWriteUtc);
        var replacement = files.Inspect(path);
        Check(replacement.SizeBytes == entry.FileStamp.SizeBytes && replacement.CreatedUtc == entry.FileStamp.CreatedUtc &&
            replacement.LastWriteUtc == entry.FileStamp.LastWriteUtc && replacement.Identity != entry.FileStamp.Identity,
            "native replacement fixture must differ only in file identity");
        RejectNativeDelete(entry.FileStamp, "replacement with matching size and timestamps");
        var replaced = new RecordingCleanupService().DeleteSelected([entry], () => sessions, [temp.Path]);
        Check(replaced is { DeletedCount: 0, SkippedCount: 1 } && File.Exists(path), "cleanup accepted a replacement identity");
        File.Delete(path);
        File.Move(original, path);

        var result = new RecordingCleanupService().DeleteSelected([entry], () => sessions, [temp.Path]);
        Check(result is { DeletedCount: 1, DeletedBytes: 30, SkippedCount: 0 } && !File.Exists(path),
            "native handle deletion failed for an unchanged, closed recording: " + string.Join("; ", result.Files.Select(f => f.Message)));
        Console.WriteLine("PASS native recording deletion: unchanged file, writer lock, identity, replacement and timestamps");

        void RejectNativeDelete(RecordingFileStamp expected, string scenario)
        {
            try { files.Delete(path, expected); throw new Exception("Native deletion accepted " + scenario); }
            catch (IOException) { Check(File.Exists(path), "native stale-file rejection removed the recording: " + scenario); }
        }
    }

    private static string Video(string root, string name, int size, int ageDays = 3)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays));
        return path;
    }

    private static GameSession Session(string path, SessionStatus status = SessionStatus.Completed,
        bool favourite = false, SessionAttachmentKind kind = SessionAttachmentKind.Recording) => new()
    {
        Id = Guid.NewGuid(), GameName = "Synthetic game", Status = status, Favourite = favourite,
        Attachments = [new() { Kind = kind, Path = path, ObservedUtc = Now }]
    };

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class TestFiles : IRecordingFileAccess
    {
        private readonly RecordingFileAccess actual = new();
        public string Identity { get; set; } = "original";
        public string? FailPath { get; init; }
        public bool MultipleLinks { get; init; }
        public int DeleteAttempts { get; private set; }
        public Action? AfterDelete { get; set; }
        public RecordingFileStamp Inspect(string path) => actual.Inspect(path) with { Identity = Identity, LinkCount = MultipleLinks ? 2u : 1u };
        public void Delete(string path, RecordingFileStamp expected)
        {
            DeleteAttempts++;
            if (path == FailPath) throw new IOException("Synthetic recording is in use.");
            Check(Inspect(path) == expected, "cleanup did not pass the original file stamp to deletion");
            File.Delete(path);
            AfterDelete?.Invoke();
        }
    }
}
