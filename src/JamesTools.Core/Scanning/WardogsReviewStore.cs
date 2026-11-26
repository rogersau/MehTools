using System.Text.Json;
using JamesTools.Core.Sessions;

namespace JamesTools.Core.Scanning;

public enum ScanStatus { Queued, Running, Completed, Cancelled, Failed, Interrupted }
public enum ReviewDecision { Unreviewed, Keep, Reject }
public sealed record SourceFingerprint(long Length, DateTime WriteUtc)
{
    public static SourceFingerprint Read(FileStream file) => new(file.Length, File.GetLastWriteTimeUtc(file.SafeFileHandle));
}
public sealed record CandidateReview(int CandidateId, ReviewDecision Decision = ReviewDecision.Unreviewed, string Label = "");
public sealed record ScanPreferences(string ToolsFolder = "", WardogsScanOptions? Options = null);
public sealed record SavedWardogsScan
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Source { get; init; } = "";
    public Guid? SessionId { get; init; }
    public bool Background { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedUtc { get; init; }
    public ScanStatus Status { get; init; }
    public ScanPreferences Preferences { get; init; } = new();
    public SourceFingerprint? Fingerprint { get; init; }
    public WardogsScanResult? Result { get; init; }
    public CandidateReview[] Reviews { get; init; } = [];
    public string Error { get; init; } = "";
    public double Pre { get; init; } = 10;
    public double Post { get; init; } = 5;
    public double MergeGap { get; init; }
    public IReadOnlyList<HighlightInterval> Intervals() { if (Result is null) return []; var kept = Reviews.Where(r => r.Decision == ReviewDecision.Keep).Select(r => r.CandidateId).ToHashSet(); return WardogsIntervals.Plan(Result.Candidates.Where(c => kept.Contains(c.Id)), Result.Duration, Pre, Post, MergeGap); }
    public override string ToString() => $"{Status} · {Path.GetFileName(Source)} · {(SessionId is null ? "Unassigned" : SessionId)} · {CreatedUtc:g}";
}
public interface IWardogsScanWork
{
    Task<WardogsScanResult> ScanAsync(SavedWardogsScan job, IProgress<WardogsProgress>? progress, CancellationToken token);
}
public sealed class NativeWardogsScanWork(Func<IHudOcr> createOcr) : IWardogsScanWork
{
    public async Task<WardogsScanResult> ScanAsync(SavedWardogsScan job, IProgress<WardogsProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var preferences = job.Preferences;
        await using var ocr = createOcr();
        var ffmpeg = Recording.MediaTools.Resolve(preferences.ToolsFolder, "ffmpeg");
        var ffprobe = Recording.MediaTools.Resolve(preferences.ToolsFolder, "ffprobe");
        return await new WardogsScanner(new FfmpegWardogsFrames(ffmpeg, ffprobe, ocr)).ScanAsync(job.Source, preferences.Options, progress, token);
    }
}
// All persistence and jobs serialize through this module. Snapshots never expose retained arrays.
public sealed class WardogsReviewStore(string directory, IWardogsScanWork work)
{
    private sealed record Document(int Version, ScanPreferences Preferences, SavedWardogsScan[] Scans);
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim jobs = new(1);
    private bool initialized;
    private Document state = new(1, new(), []);
    private string FilePath => Path.Combine(directory, "wardogs-reviews.json");
    private static ScanPreferences Copy(ScanPreferences value) => value with { };
    private static SavedWardogsScan Copy(SavedWardogsScan value) => value with { Reviews = value.Reviews.ToArray(), Result = value.Result is { } r ? r with { Candidates = r.Candidates.ToArray() } : null };
    private static SavedWardogsScan[] Copy(SavedWardogsScan[] value) => value.Select(Copy).ToArray();
    private static Document Copy(Document value) => value with { Scans = Copy(value.Scans) };
    public string RecoveryNotice { get; private set; } = "";
    private static void Validate(Document d)
    {
        if (d.Version != 1 || d.Scans is null || d.Scans.Length > 200 || d.Preferences is null) throw new InvalidDataException("Unsupported or oversized scan library.");
        void Pref(ScanPreferences p) { if (p is null || p.ToolsFolder is null || p.ToolsFolder.Length > 4096) throw new InvalidDataException("Invalid tool preferences."); (p.Options ?? new()).Validate(); }
        Pref(d.Preferences);
        if (d.Scans.Any(s => s is null) || d.Scans.Select(s => s.Id).Distinct().Count() != d.Scans.Length) throw new InvalidDataException("Null or duplicate scan IDs.");
        foreach (var s in d.Scans)
        {
            Pref(s.Preferences);
            if (s.Id == Guid.Empty || s.SessionId == Guid.Empty || s.Source is null || s.Source.Length > 4096 || !Path.IsPathFullyQualified(s.Source) || Path.GetFullPath(s.Source).StartsWith(@"\\") || s.Source.Contains("://") || s.Source.AsSpan(2).Contains(':') || !Enum.IsDefined(s.Status) || s.Error is null || s.Error.Length > 2000 || s.CreatedUtc.Kind != DateTimeKind.Utc || s.Reviews is null || new[] { s.Pre, s.Post, s.MergeGap }.Any(v => !double.IsFinite(v) || v < 0 || v > 3600)) throw new InvalidDataException("Invalid scan metadata.");
            if (s.Background && s.SessionId is null) throw new InvalidDataException("Background scans require a session.");
            if (s.Fingerprint is { } f && (f.Length < 0 || f.WriteUtc.Kind != DateTimeKind.Utc)) throw new InvalidDataException("Invalid source file metadata.");
            if (s.FinishedUtc is { } finished && (finished.Kind != DateTimeKind.Utc || finished < s.CreatedUtc)) throw new InvalidDataException("Invalid scan timestamps.");
            if (s.Status != ScanStatus.Completed && (s.Result is not null || s.Reviews.Length != 0)) throw new InvalidDataException("Unfinished scan contains published results.");
            if (s.Status == ScanStatus.Completed && (s.Result is null || s.Fingerprint is null || s.FinishedUtc is null)) throw new InvalidDataException("Completed scan has no evidence.");
            if (s.Result is { } r)
            {
                if (r.Options is null || !Enum.IsDefined(r.OcrEngine)) throw new InvalidDataException("Invalid scan options or OCR engine.");
                r.Options.Validate();
                if (r.DetectorVersion is null || r.DetectorVersion.Length > 128 || r.Preset is null || r.Preset.Length > 256 || !double.IsFinite(r.Duration) || r.Duration <= 0 || r.Candidates is null || r.Candidates.Count > 100000 || r.Candidates.Any(c => c is null) || r.Candidates.Select(c => c.Id).Distinct().Count() != r.Candidates.Count) throw new InvalidDataException("Invalid scan result.");
                if (r.Balances is { } balances && !WardogsMoney.IsValid(balances, r.Duration)) throw new InvalidDataException("Invalid balance observations.");
                foreach (var c in r.Candidates) if (c.Id < 1 || !Enum.IsDefined(c.Kind) || !double.IsFinite(c.Seconds) || c.Seconds < 0 || c.Seconds > r.Duration || c.Text is null || c.Text.Length > 4096 || !c.HasValidConfidence(r.OcrEngine) || !double.IsFinite(c.RowY) || c.RowY < 0 || c.RowY > 1 || c.Observations < 1 || c.Reward is <= 0 or > WardogsMoney.MaximumAmount) throw new InvalidDataException("Invalid candidate.");
            }
            var candidateIds = s.Result?.Candidates.Select(c => c.Id).ToHashSet() ?? [];
            if (s.Reviews.Length > 100000 || s.Reviews.Any(r => r is null) || s.Reviews.Select(r => r.CandidateId).Distinct().Count() != s.Reviews.Length || s.Reviews.Any(r => !Enum.IsDefined(r.Decision) || r.Label is null || r.Label.Length > 256 || !candidateIds.Contains(r.CandidateId))) throw new InvalidDataException("Invalid review decisions.");
        }
    }
    private async Task Save(Document next)
    {
        var bytes = await Task.Run(() => { Validate(next); return JsonSerializer.SerializeToUtf8Bytes(next); }).ConfigureAwait(false);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Scan library exceeds 32 MB; remove older scans.");
        Directory.CreateDirectory(directory); var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try {
            await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
            if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak"); else File.Move(temp, FilePath);
            state = Copy(next);
        } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async Task InitializeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false); try {
            if (initialized) return;
            Task<Document> Read(string path) => Task.Run(async () => { if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Scan library exceeds 32 MB."); var d = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(path)) ?? throw new InvalidDataException("Empty scan library."); Validate(d); return d; });
            if (File.Exists(FilePath)) {
                try { state = await Read(FilePath); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) {
                    if (!File.Exists(FilePath + ".bak")) throw new InvalidDataException("Scan library is corrupt; original preserved.", ex);
                    state = await Read(FilePath + ".bak");
                    File.Copy(FilePath, FilePath + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    File.Delete(FilePath); RecoveryNotice = "Recovered scan library from backup; corrupt original preserved.";
                }
            } else if (File.Exists(FilePath + ".bak")) {
                state = await Read(FilePath + ".bak"); RecoveryNotice = "Recovered missing scan library from backup.";
            }
            if (RecoveryNotice.Length > 0 || state.Scans.Any(s => s.Status == ScanStatus.Running || !s.Background && s.Status == ScanStatus.Queued))
                await Save(state with { Scans = state.Scans.Select(s => s.Background
                    ? s.Status == ScanStatus.Running ? s with { Status = ScanStatus.Queued } : s
                    : s.Status is ScanStatus.Queued or ScanStatus.Running
                        ? s with { Status = ScanStatus.Interrupted, FinishedUtc = DateTime.UtcNow, Error = "Interrupted by application shutdown; explicitly rescan to retry." } : s).ToArray() });
            initialized = true;
        } finally { gate.Release(); }
    }

    public async Task<WardogsMoneySummary> SessionMoneyAsync(GameSession session)
    {
        await InitializeAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var recordings = session.Attachments.Where(a => a.Kind == SessionAttachmentKind.Recording)
                .OrderBy(a => a.ObservedUtc).DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            var bySource = CompletedScans(session.Id)
                .GroupBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(s => s.CreatedUtc).First().Result!, StringComparer.OrdinalIgnoreCase);
            var results = recordings.Select(a => bySource.GetValueOrDefault(a.Path)).OfType<WardogsScanResult>().ToArray();
            return WardogsMoneySummary.FromRecordings(results, recordings.Length);
        }
        finally { gate.Release(); }
    }

    public async Task<Guid?> FindCompletedScanIdAsync(string source, Guid sessionId)
    {
        await InitializeAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return CompletedScans(sessionId).Where(scan => string.Equals(scan.Source, source, StringComparison.OrdinalIgnoreCase))
                .MaxBy(scan => scan.CreatedUtc)?.Id;
        }
        finally { gate.Release(); }
    }

    private IEnumerable<SavedWardogsScan> CompletedScans(Guid sessionId) => state.Scans.Where(scan => scan.Status == ScanStatus.Completed
        && (scan.SessionId is null || scan.SessionId == sessionId));

    public async Task<(ScanPreferences Preferences, SavedWardogsScan[] Scans)> SnapshotAsync() { await gate.WaitAsync(); try { return (Copy(state.Preferences), Copy(state.Scans)); } finally { gate.Release(); } }

    public async Task<SavedWardogsScan?> NextBackgroundScanAsync(IReadOnlyList<GameSession> sessions, string defaultToolsFolder, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (state.Scans.Any(s => !s.Background && s.Status is ScanStatus.Running or ScanStatus.Queued)) return null;
            var recordings = sessions.Where(session => session.Status != SessionStatus.Active && WardogsRecordingLinks.IsWardogs(session))
                .SelectMany(session => session.Attachments.Where(a => a.Kind == SessionAttachmentKind.Recording)
                    .Select(a => (Session: session.Id, Attachment: a)))
                .OrderByDescending(item => item.Attachment.ObservedUtc);
            var bySource = state.Scans.ToLookup(scan => scan.Source, StringComparer.OrdinalIgnoreCase);
            foreach (var (session, attachment) in recordings)
            {
                token.ThrowIfCancellationRequested();
                var attempts = bySource[attachment.Path].Where(scan => scan.SessionId is null || scan.SessionId == session).ToArray();
                if (attempts.Any(scan => scan.Status == ScanStatus.Completed && scan.Result?.DetectorVersion == WardogsScanner.Version)) continue;
                var pending = attempts.FirstOrDefault(scan => scan.Background && scan.Status == ScanStatus.Queued);
                if (pending is null && attempts.Any(scan => scan.Background)) continue;
                if (!IsAvailableRecording(attachment.Path)) continue;
                if (pending is not null) return Copy(pending);
                if (state.Scans.Length >= 200 || state.Scans.Count(s => s.Status is ScanStatus.Running or ScanStatus.Queued) >= 20)
                    throw new InvalidOperationException("The scan library or queue is full. Remove older scans in WARDOGS review to continue.");
                var job = new SavedWardogsScan { Source = attachment.Path, SessionId = session, Background = true,
                    Preferences = state.Preferences.Options is null ? new(defaultToolsFolder) : Copy(state.Preferences) };
                await Save(state with { Scans = state.Scans.Append(job).ToArray() }).ConfigureAwait(false);
                return Copy(job);
            }
            return null;
        }
        finally { gate.Release(); }
    }

    internal static bool IsAvailableRecording(string path)
    {
        try
        {
            using var file = new FileStream(Recording.MediaTools.LocalVideo(path), FileMode.Open, FileAccess.Read, FileShare.Read);
            return file.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }
    public async Task<SavedWardogsScan> EnqueueAsync(string source, Guid? sessionId, ScanPreferences preferences)
    {
        source = Recording.MediaTools.LocalVideo(source);
        await gate.WaitAsync(); try {
            if (state.Scans.Count(s => s.Status is ScanStatus.Queued or ScanStatus.Running) >= 20) throw new InvalidOperationException("Queue limit is 20 files.");
            var s = new SavedWardogsScan { Source = source, SessionId = sessionId, Preferences = Copy(preferences) };
            var scans = state.Scans.Where(scan => !(scan.Background && scan.Status == ScanStatus.Queued && scan.SessionId == sessionId
                && string.Equals(scan.Source, source, StringComparison.OrdinalIgnoreCase)));
            await Save(state with { Preferences = Copy(preferences), Scans = scans.Append(s).ToArray() }); return Copy(s);
        } finally { gate.Release(); }
    }
    private async Task Update(Guid id, Func<SavedWardogsScan, SavedWardogsScan> change) { await gate.WaitAsync(); try { if (!state.Scans.Any(s => s.Id == id)) throw new InvalidOperationException("Scan is no longer in the library."); await Save(state with { Scans = state.Scans.Select(s => s.Id == id ? change(s) : s).ToArray() }); } finally { gate.Release(); } }
    public async Task RemoveAsync(Guid id) { await gate.WaitAsync(); try { if (state.Scans.Any(s => s.Id == id && s.Status is ScanStatus.Queued or ScanStatus.Running)) throw new InvalidOperationException("Cancel and finish the job before removing it."); await Save(state with { Scans = state.Scans.Where(s => s.Id != id).ToArray() }); } finally { gate.Release(); } }
    public Task CancelPendingAsync() => CancelQueued();
    private async Task CancelQueued() { await gate.WaitAsync(); try { if (!state.Scans.Any(s => !s.Background && s.Status == ScanStatus.Queued)) return; await Save(state with { Scans = state.Scans.Select(s => !s.Background && s.Status == ScanStatus.Queued ? s with { Status = ScanStatus.Cancelled, FinishedUtc = DateTime.UtcNow } : s).ToArray() }); } finally { gate.Release(); } }
    public async Task ReviewAsync(Guid id, CandidateReview[] reviews, double pre, double post, double gap)
    {
        var snapshot = reviews.ToArray();
        await gate.WaitAsync(); try {
            var s = state.Scans.Single(s => s.Id == id);
            if (s.Status != ScanStatus.Completed) throw new InvalidOperationException("Only completed scans can be reviewed.");
            if (s.Reviews.SequenceEqual(snapshot) && s.Pre == pre && s.Post == post && s.MergeGap == gap) return;
            await Save(state with { Scans = state.Scans.Select(x => x.Id == id ? x with { Reviews = snapshot, Pre = pre, Post = post, MergeGap = gap } : x).ToArray() });
        } finally { gate.Release(); }
    }
    public async Task RunAsync(Guid id, IProgress<WardogsProgress>? progress, CancellationToken token)
    {
        await jobs.WaitAsync(token); try {
            SavedWardogsScan s;
            await gate.WaitAsync(token);
            try {
                var queued = state.Scans.FirstOrDefault(s => s.Id == id && s.Status == ScanStatus.Queued);
                if (queued is null) return;
                s = queued;
                await Save(state with { Scans = state.Scans.Select(x => x.Id == id ? x with { Status = ScanStatus.Running } : x).ToArray() });
            } finally { gate.Release(); }
            try {
                var result = await Task.Run(async () => {
                    using var guard = new FileStream(s.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var fingerprint = SourceFingerprint.Read(guard);
                    var found = await work.ScanAsync(s, progress, token); token.ThrowIfCancellationRequested(); return (fingerprint, found);
                }, token);
                await Update(id, x => x with { Status = ScanStatus.Completed, Fingerprint = result.fingerprint, Result = result.found, FinishedUtc = DateTime.UtcNow });
            } catch (OperationCanceledException) when (s.Background && token.IsCancellationRequested) { await Update(id, x => x with { Status = ScanStatus.Queued }); }
            catch (OperationCanceledException) { await Update(id, x => x with { Status = ScanStatus.Cancelled, FinishedUtc = DateTime.UtcNow }); }
            catch (Exception ex) { await Update(id, x => x with { Status = ScanStatus.Failed, FinishedUtc = DateTime.UtcNow, Error = ex.Message[..Math.Min(2000, ex.Message.Length)] }); }
        } finally { jobs.Release(); }
    }
}
