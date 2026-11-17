using JamesTools.Core.Sessions;

namespace JamesTools.Core.Storage;

public sealed record RecordingCleanupFileResult(string Path, bool Deleted, long DeletedBytes, string Message);
public sealed record RecordingCleanupResult(IReadOnlyList<RecordingCleanupFileResult> Files, bool Cancelled)
{
    public int DeletedCount => Files.Count(f => f.Deleted);
    public long DeletedBytes => Files.Sum(f => f.DeletedBytes);
    public int SkippedCount => Files.Count(f => !f.Deleted);
}

/// <summary>Acts only on explicit preview selections, rechecking current references and the original file before each deletion.</summary>
public sealed class RecordingCleanupService(IRecordingFileAccess? fileAccess = null)
{
    private readonly IRecordingFileAccess files = fileAccess ?? new RecordingFileAccess();

    public RecordingCleanupResult DeleteSelected(IEnumerable<CleanupPreviewEntry> selection,
        Func<IReadOnlyList<GameSession>> readSessions, IReadOnlyList<string> recordingRoots,
        CancellationToken cancellationToken = default, IProgress<RecordingCleanupFileResult>? progress = null)
    {
        var results = new List<RecordingCleanupFileResult>();
        foreach (var entry in selection.DistinctBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested) return new(results, true);
            RecordingCleanupFileResult result;
            try
            {
                if (!entry.CanDelete || entry.FileStamp is null)
                    throw new IOException("This preview entry is protected. Refresh and select an available recording.");
                // Inspect only this path, while retaining every shared session/attachment protection.
                var sessions = readSessions().Select(s => s with
                {
                    Attachments = s.Attachments.Where(a => string.Equals(CleanupPreviewBuilder.TryGetAbsolutePath(a.Path),
                        entry.Path, StringComparison.OrdinalIgnoreCase)).ToList()
                }).Where(s => s.Attachments.Count > 0);
                var current = CleanupPreviewBuilder.Build(sessions, recordingRoots, DateTime.UtcNow, null, null, files)
                    .Entries.SingleOrDefault(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
                if (current is null || !current.CanDelete)
                    throw new IOException(current?.Reason ?? "The recording is no longer referenced by session history.");
                if (current.FileStamp != entry.FileStamp)
                    throw new IOException("Recording changed since the preview. Refresh before deleting it.");
                if (cancellationToken.IsCancellationRequested) return new(results, true);
                files.Delete(entry.Path, entry.FileStamp);
                result = new(entry.Path, true, entry.FileStamp.SizeBytes, "Deleted permanently.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                result = new(entry.Path, false, 0, ex.Message);
            }
            results.Add(result);
            progress?.Report(result);
        }
        return new(results, cancellationToken.IsCancellationRequested);
    }
}
