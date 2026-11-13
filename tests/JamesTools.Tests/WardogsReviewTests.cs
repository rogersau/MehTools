using JamesTools.Core.Scanning;
internal static class WardogsReviewTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Work : IWardogsScanWork
    {
        public bool Cancel;
        private readonly NativeWardogsScanWork hashing = new();
        public Task<SourceFingerprint> FingerprintAsync(string path, CancellationToken token) => hashing.FingerprintAsync(path, token);
        public Task<WardogsScanResult> ScanAsync(SavedWardogsScan job, IProgress<WardogsProgress>? progress, CancellationToken token)
        {
            if (Cancel) throw new OperationCanceledException(token);
            return Task.FromResult(new WardogsScanResult("test", "synthetic", job.Preferences.Options ?? new(), 30, [new(1, WardogsEventKind.Kill, 12, "KILL fake", .8, .1, 2)]));
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
            Check(await store.SourceMatchesAsync(scan, default), "source verified");
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
            await File.WriteAllTextAsync(source, "different bytes"); Check(!await restart.SourceMatchesAsync(scan, default), "changed source disabled");
            var pending = await restart.EnqueueAsync(source, null, new());
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
        } finally { Directory.Delete(directory, true); }
    }
}
