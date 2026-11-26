using JamesTools.Core.Scanning;
using JamesTools.Core.Sessions;
internal static class WardogsReviewTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Work : IWardogsScanWork
    {
        public bool Cancel;
        public Func<CancellationToken, Task>? BeforeScan;
        public string DetectorVersion = "test";
        public WardogsBalances? Balances;
        public long? Reward;
        public async Task<WardogsScanResult> ScanAsync(SavedWardogsScan job, IProgress<WardogsProgress>? progress, CancellationToken token)
        {
            if (Cancel) throw new OperationCanceledException(token);
            if (BeforeScan is not null) await BeforeScan(token);
            return new WardogsScanResult(DetectorVersion, "synthetic", job.Preferences.Options ?? new(), 30,
                [new WardogsCandidate(1, WardogsEventKind.Kill, 12, "KILL fake", .8, .1, 2) { Reward = Reward }]) { Balances = Balances };
        }
    }
    public static async Task Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wardogs-review-test-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try {
            var source = Path.Combine(directory, "fake.mp4"); await File.WriteAllTextAsync(source, "fake source, never decoded");
            var work = new Work(); var store = new WardogsReviewStore(directory, work); await store.InitializeAsync();
            var owner = Guid.NewGuid(); var job = await store.EnqueueAsync(source, owner, new());
            await store.RunAsync(job.Id, null, default);
            var scan = (await store.SnapshotAsync()).Scans.Single();
            Check(scan.Status == ScanStatus.Completed && scan.SessionId == owner && scan.Fingerprint is not null, "completed pinned scan");
            using (await WardogsExporter.VerifiedSourceAsync(scan, default)) { }
            await store.ReviewAsync(scan.Id, [new(1, ReviewDecision.Keep, "chosen")], 3, 2, 1);
            var snap = await store.SnapshotAsync(); snap.Scans[0].Reviews[0] = new(1, ReviewDecision.Reject);
            Check((await store.SnapshotAsync()).Scans[0].Reviews[0].Decision == ReviewDecision.Keep, "deep snapshot");
            var restart = new WardogsReviewStore(directory, work); await restart.InitializeAsync();
            scan = (await restart.SnapshotAsync()).Scans.Single();
            Check(scan.Reviews.Single().Label == "chosen" && scan.Intervals().Single() == new HighlightInterval(9, 14), "decisions and intervals survive restart");
            var primary = Path.Combine(directory, "wardogs-reviews.json");
            var backup = primary + ".bak";
            var primaryStamp = File.GetLastWriteTimeUtc(primary); var backupBytes = await File.ReadAllBytesAsync(backup);
            await new WardogsReviewStore(directory, work).InitializeAsync();
            Check(File.GetLastWriteTimeUtc(primary) == primaryStamp && Enumerable.SequenceEqual(backupBytes, await File.ReadAllBytesAsync(backup)), "read-only open must not rotate backup");
            await restart.ReviewAsync(scan.Id, scan.Reviews, scan.Pre, scan.Post, scan.MergeGap);
            Check(File.GetLastWriteTimeUtc(primary) == primaryStamp, "unchanged review must not rewrite library");
            // An edit that preserves file size must still be noticed by its last-write time.
            File.SetLastWriteTimeUtc(source, scan.Fingerprint!.WriteUtc.AddSeconds(5));
            bool changed = false;
            try { using var verified = await WardogsExporter.VerifiedSourceAsync(scan, default); }
            catch (IOException) { changed = true; }
            Check(changed, "changed source timestamp disables playback");
            var pending = await restart.EnqueueAsync(source, null, new());
            await restart.InitializeAsync();
            Check((await restart.SnapshotAsync()).Scans.Single(s => s.Id == pending.Id).Status == ScanStatus.Queued, "shared store initialization leaves jobs intact");
            var interrupted = new WardogsReviewStore(directory, work); await interrupted.InitializeAsync();
            Check((await interrupted.SnapshotAsync()).Scans.Single(s => s.Id == pending.Id).Status == ScanStatus.Interrupted, "restart doesn't resume");
            var cancel = await interrupted.EnqueueAsync(source, null, new()); work.Cancel = true; await interrupted.RunAsync(cancel.Id, null, default);
            Check((await interrupted.SnapshotAsync()).Scans.Single(s => s.Id == cancel.Id) is { Status: ScanStatus.Cancelled, Result: null }, "cancel publishes no result");
            var queued = await interrupted.EnqueueAsync(source, null, new()); await interrupted.CancelPendingAsync();
            Check((await interrupted.SnapshotAsync()).Scans.Single(s => s.Id == queued.Id).Status == ScanStatus.Cancelled, "pending cancellation");
            bool rejected = false; try { await interrupted.ReviewAsync(scan.Id, [new(999, ReviewDecision.Keep)], 1, 1, 0); } catch (InvalidDataException) { rejected = true; } Check(rejected, "unknown candidate rejected");
            await File.WriteAllTextAsync(Path.Combine(directory, "wardogs-reviews.json"), "{corrupt");
            var recovery = new WardogsReviewStore(directory, work); await recovery.InitializeAsync();
            Check(recovery.RecoveryNotice.Length > 0 && Directory.GetFiles(directory, "*.corrupt-*").Length == 1, "backup recovery preserves corrupt file");
            // Valid JSON with invalid metadata must use the same recovery path as malformed JSON.
            await File.WriteAllTextAsync(primary, "{\"Version\":1,\"Preferences\":{},\"Scans\":[null]}");
            var nullRecovery = new WardogsReviewStore(directory, work); await nullRecovery.InitializeAsync();
            Check(nullRecovery.RecoveryNotice.Length > 0 && (await nullRecovery.SnapshotAsync()).Scans.Length > 0, "null record metadata recovers from backup");
            var preservedBackup = await File.ReadAllBytesAsync(backup);
            File.Delete(primary);
            var missingRecovery = new WardogsReviewStore(directory, work); await missingRecovery.InitializeAsync();
            Check(missingRecovery.RecoveryNotice.Length > 0 && (await missingRecovery.SnapshotAsync()).Scans.Length > 0 && Enumerable.SequenceEqual(preservedBackup, await File.ReadAllBytesAsync(backup)), "missing primary recovers without replacing backup");
            Check(!Directory.GetFiles(directory, "*.tmp-*").Any(), "store leaves no temporary payloads");
            File.Delete(Path.Combine(directory, "wardogs-reviews.json.bak")); await File.WriteAllTextAsync(Path.Combine(directory, "wardogs-reviews.json"), "{}");
            rejected = false; try { await new WardogsReviewStore(directory, work).InitializeAsync(); } catch (InvalidDataException) { rejected = true; } Check(rejected, "invalid metadata never reset");
            await CheckSessionMoney(directory);
            await CheckBackgroundQueue(directory);
            CheckRecoveryLinks(directory);
        } finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckBackgroundQueue(string directory)
    {
        string storage = Path.Combine(directory, "background");
        var work = new Work();
        var store = new WardogsReviewStore(storage, work);
        await store.InitializeAsync();
        var now = DateTime.UtcNow;
        SessionAttachment Attachment(string name, SessionAttachmentKind kind, int minutes)
        {
            string path = Path.Combine(directory, name + ".mp4");
            File.WriteAllText(path, "fixture");
            return new() { Path = path, Kind = kind, ObservedUtc = now.AddMinutes(minutes) };
        }
        var recording = Attachment("background-eligible", SessionAttachmentKind.Recording, -2);
        var locked = Attachment("still-being-written", SessionAttachmentKind.Recording, -1);
        var session = new GameSession { GameName = "WARDOGS", Id = Guid.NewGuid(), Status = SessionStatus.Completed,
            Attachments = [recording, locked, Attachment("background-clip", SessionAttachmentKind.Clip, 0)] };
        var other = session with { Id = Guid.NewGuid(), GameName = "Other", Attachments = [Attachment("other-game", SessionAttachmentKind.Recording, 0)] };
        var active = session with { Id = Guid.NewGuid(), Status = SessionStatus.Active, Attachments = [Attachment("active-game", SessionAttachmentKind.Recording, 0)] };
        GameSession[] sessions = [session, other, active];
        var legacy = await store.EnqueueAsync(recording.Path, session.Id, new());
        Check(await store.NextBackgroundScanAsync(sessions, "", default) is null, "manual scans have priority over background discovery");
        await store.RunAsync(legacy.Id, null, default);
        work.DetectorVersion = WardogsScanner.Version;
        using (var writer = new FileStream(locked.Path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var background = await store.NextBackgroundScanAsync(sessions, "", default);
            Check(background is { Background: true } && background.SessionId == session.Id && background.Source == recording.Path,
                "background skips unfinished files, clips, active sessions and other games, and upgrades older scans");
            var job = background!;
            using var cancellation = new CancellationTokenSource();
            work.BeforeScan = token => { cancellation.Cancel(); return Task.FromCanceled(token); };
            await store.RunAsync(job.Id, null, cancellation.Token);
            await store.CancelPendingAsync();
            var restarted = new WardogsReviewStore(storage, work);
            await restarted.InitializeAsync();
            var retried = await restarted.NextBackgroundScanAsync(sessions, "", default);
            Check(retried?.Id == job.Id && retried.Status == ScanStatus.Queued, "paused background scan persists and reuses the same ID on restart");
            work.BeforeScan = null;
            await restarted.RunAsync(job.Id, null, default);
            Check(await restarted.NextBackgroundScanAsync(sessions, "", default) is null, "completed background scans are never duplicated");
        }
    }

    private static void CheckRecoveryLinks(string directory)
    {
        string folder = Path.Combine(directory, "obs-archive");
        Directory.CreateDirectory(folder);
        var history = new SessionLibraryStore(Path.Combine(directory, "recovery-sessions"));
        var start = DateTime.UtcNow.AddDays(-2);
        var first = history.Create("WARDOGS", "wardogs.exe", start);
        history.End(first.Id, start.AddMinutes(12), SessionStatus.Completed);
        var other = history.Create("Another game", "another.exe", start.AddHours(1));
        history.End(other.Id, start.AddHours(1).AddMinutes(10), SessionStatus.Completed);
        var ambiguous = history.Create("WARDOGS", "wardogs.exe", start.AddHours(1));
        history.End(ambiguous.Id, start.AddHours(1).AddMinutes(10), SessionStatus.Completed);
        string FileAt(DateTime created, string prefix)
        {
            var path = Path.Combine(folder, prefix + created.ToLocalTime().ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ".mp4");
            File.WriteAllText(path, "recording");
            File.SetCreationTimeUtc(path, created);
            File.SetLastWriteTimeUtc(path, created.AddMinutes(8));
            return path;
        }
        var matching = FileAt(start.AddSeconds(-2), "");
        _ = FileAt(start.AddMinutes(1), "Replay ");
        _ = FileAt(start.AddHours(1).AddSeconds(1), "");
        var settings = new JamesTools.Core.Configuration.AppSettings { RecordingDirectory = folder };
        Check(WardogsRecordingLinks.Recover(history, settings, DateTime.UtcNow, default) == 1 && history.FindVideoSession(matching) == first.Id,
            "timestamp recovery links only the recording with a unique session, excluding replays and overlaps");
        Check(WardogsRecordingLinks.Recover(history, settings, DateTime.UtcNow, default) == 0 && history.Get(first.Id)?.Attachments.Count == 1,
            "timestamp recovery is idempotent");
    }

    private static async Task CheckSessionMoney(string directory)
    {
        var work = new Work();
        var store = new WardogsReviewStore(Path.Combine(directory, "money"), work);
        await store.InitializeAsync();
        var owner = Guid.NewGuid();
        var clock = DateTime.UtcNow;
        var attachments = new List<SessionAttachment>();
        async Task<Guid> Record(string name, long start, long end, long reward, SessionAttachmentKind kind, bool attach = true)
        {
            var path = Path.Combine(directory, name + ".mp4");
            await File.WriteAllTextAsync(path, "synthetic recording");
            if (attach) attachments.Add(new() { Path = path, Kind = kind, ObservedUtc = clock.AddMinutes(attachments.Count) });
            work.Balances = new(new(start, 1), new(end, 28));
            work.Reward = reward;
            var job = await store.EnqueueAsync(path, owner, new());
            await store.RunAsync(job.Id, null, default);
            return job.Id;
        }
        await Record("part1", 1000, 1100, 200, SessionAttachmentKind.Recording);
        var latestId = await Record("part1", 1000, 1100, 100, SessionAttachmentKind.Recording, attach: false);
        await Record("part2", 1100, 1200, 150, SessionAttachmentKind.Recording);
        await Record("clip", 9000, 9999, 999, SessionAttachmentKind.Clip);
        attachments.Add(new() { Path = Path.Combine(directory, "unscanned.mp4"), Kind = SessionAttachmentKind.Recording, ObservedUtc = clock.AddMinutes(10) });
        var summary = await store.SessionMoneyAsync(new() { Id = owner, Attachments = attachments });
        Check(summary is { Opening.Amount: 1000, Closing.Amount: 1200, DetectedRewards: 250, ScannedRecordings: 2, TotalRecordings: 3 },
            "session uses first and last recording balances, latest scans, reward totals, and excludes replay clips");
        Check(await store.FindCompletedScanIdAsync(attachments[0].Path, owner) == latestId
            && await store.FindCompletedScanIdAsync(attachments[0].Path, Guid.NewGuid()) is null,
            "open the latest completed scan belonging to the selected session");
    }
}
