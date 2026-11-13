using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using JamesTools.Core.Sessions;

namespace JamesTools.Core.Storage;

public sealed record StorageCapacity(long AvailableBytes, long TotalBytes, string QueriedPath);
public sealed record StorageCapacityResult(StorageCapacity? Capacity, string? Reason)
{
    public bool IsKnown => Capacity is not null;
}

public interface IStorageCapacityProvider
{
    StorageCapacityResult Query(string path);
}

/// <summary>Read-only capacity lookup. For a not-yet-created folder, queries its nearest existing parent.</summary>
public sealed class FileSystemStorageCapacityProvider : IStorageCapacityProvider
{
    public StorageCapacityResult Query(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return new(null, "The configured path is not an absolute folder path.");
            var existing = Path.GetFullPath(path);
            while (true)
            {
                try
                {
                    if ((File.GetAttributes(existing) & FileAttributes.Directory) == 0)
                        return new(null, "The configured path or one of its parents is a file.");
                    break;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || parent == existing)
                    return new(null, "No connected parent folder was found.");
                existing = parent;
            }
            // Query the actual directory, so mounted folders and network shares use their own volume.
            var queryPath = Path.EndsInDirectorySeparator(existing) ? existing : existing + Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(queryPath, out var available, out var total, out _))
                return new(null, new Win32Exception(Marshal.GetLastWin32Error()).Message);
            if (available > long.MaxValue || total > long.MaxValue)
                return new(null, "The reported capacity exceeds the supported range.");
            return new(new StorageCapacity((long)available, (long)total, existing), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(null, ex.Message);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong availableBytes, out ulong totalBytes, out ulong freeBytes);
}

public sealed record RecordingFolderPlan(string BaseDirectory, string PlannedDirectory, bool Organised);

/// <summary>Pure recording-folder planning. It never creates directories.</summary>
public static class RecordingFolderPlanner
{
    public static RecordingFolderPlan Plan(string baseDirectory, string? gameName, DateTime date, bool organise)
    {
        if (!organise) return new(baseDirectory ?? "", baseDirectory ?? "", false);
        if (string.IsNullOrWhiteSpace(baseDirectory) || !Path.IsPathFullyQualified(baseDirectory))
            throw new ArgumentException("The recording base folder must be an absolute path.", nameof(baseDirectory));
        var baseFullPath = Path.GetFullPath(baseDirectory);
        var baseRoot = Path.GetPathRoot(baseFullPath);
        var fullBase = string.Equals(baseFullPath, baseRoot, StringComparison.OrdinalIgnoreCase)
            ? baseFullPath : Path.TrimEndingDirectorySeparator(baseFullPath);
        var safeGame = SanitiseGameFolderName(gameName);
        var planned = Path.GetFullPath(Path.Combine(fullBase, safeGame, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var prefix = Path.EndsInDirectorySeparator(fullBase) ? fullBase : fullBase + Path.DirectorySeparatorChar;
        if (!planned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The planned recording folder would leave the configured base folder.", nameof(gameName));
        return new(fullBase, planned, true);
    }

    public static string SanitiseGameFolderName(string? gameName)
    {
        var value = string.IsNullOrWhiteSpace(gameName) ? "Game" : gameName.Trim();
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        value = new string(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (value.Length == 0 || value is "." or "..") value = "Game";
        var stem = value.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            value = "_" + value;
        return value;
    }
}

public sealed record CleanupPreviewEntry(string Path, int? AgeDays, long? SizeBytes, bool Candidate, string Reason, string? GameName);
public sealed record CleanupPreview(IReadOnlyList<CleanupPreviewEntry> Entries, long? PotentialReclaimableBytes, DateTime GeneratedUtc)
{
    public string ToText()
    {
        var lines = new List<string> { "Cleanup suggestions only. No files were changed. Preview can become stale; refresh to re-evaluate.", "Status\tAge days\tSize bytes\tReason\tPath" };
        lines.AddRange(Entries.Select(e => $"{(e.Candidate ? "Suggested" : "Protected/excluded")}\t{e.AgeDays?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}\t{e.SizeBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}\t{e.Reason}\t{e.Path}"));
        lines.Add($"Potential reclaimable bytes: {PotentialReclaimableBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}");
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Builds a read-only preview from saved session attachment references; never enumerates recording folders.</summary>
public static class CleanupPreviewBuilder
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".mov", ".flv", ".avi", ".ts", ".m4v", ".webm" };

    public static CleanupPreview Build(IEnumerable<GameSession> sessions, IEnumerable<string> recordingRoots,
        DateTime nowUtc, int? ageLimitDays, double? budgetGiB)
    {
        var roots = recordingRoots.Select(TryGetAbsolutePath).Where(p => p is not null).Select(p => Path.TrimEndingDirectorySeparator(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var sessionList = sessions.ToArray();
        var refs = new Dictionary<string, List<(GameSession Session, SessionAttachment Attachment)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessionList)
        foreach (var a in s.Attachments)
        {
            if (string.IsNullOrWhiteSpace(a.Path)) continue;
            string path;
            var absolutePath = TryGetAbsolutePath(a.Path);
            if (absolutePath is null) continue;
            path = absolutePath;
            if (!refs.TryGetValue(path, out var list)) refs[path] = list = [];
            list.Add((s, a));
        }

        var entries = new List<CleanupPreviewEntry>();
        var eligible = new List<CleanupPreviewEntry>();
        foreach (var (path, references) in refs)
        {
            if (references.Any(r => r.Attachment.Kind == SessionAttachmentKind.PerformanceReport)) continue;
            var owners = references.Where(r => r.Attachment.Kind == SessionAttachmentKind.Recording).ToArray();
            var gameName = owners.FirstOrDefault().Session?.GameName;
            string reason;
            bool safe = true;
            if (references.Any(r => r.Attachment.Kind == SessionAttachmentKind.Clip)) { reason = "Clip attachment is protected."; safe = false; }
            else if (references.Any(r => r.Session.Favourite)) { reason = "Referenced by a favourite session."; safe = false; }
            else if (references.Any(r => r.Session.Status != SessionStatus.Completed)) { reason = "Referenced by an active, interrupted, or otherwise incomplete session."; safe = false; }
            else if (owners.Length == 0 || references.Any(r => r.Attachment.Kind == SessionAttachmentKind.PerformanceReport)) { reason = "Only report or unsupported attachment references exist."; safe = false; }
            else if (!VideoExtensions.Contains(Path.GetExtension(path))) { reason = "File extension is not an allowed video type."; safe = false; }
            else if (!IsUnderAnyRoot(path, roots)) { reason = "File is outside configured recording folders."; safe = false; }
            else
            {
                try
                {
                    if (!File.Exists(path)) { reason = "File is missing."; safe = false; }
                    else if (HasReparsePoint(path)) { reason = "File or containing path is a reparse point or link."; safe = false; }
                    else reason = "Completed recording; no protection reference found.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { reason = "File metadata is unreadable: " + ex.Message; safe = false; }
            }
            long? size = null;
            int? ageDays = null;
            if (safe)
            {
                try { var info = new FileInfo(path); size = info.Length; ageDays = Math.Max(0, (int)Math.Floor((nowUtc - info.LastWriteTimeUtc).TotalDays)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { reason = "File metadata is unreadable: " + ex.Message; safe = false; size = null; }
            }
            var entry = new CleanupPreviewEntry(path, ageDays, size, false, reason, gameName);
            if (safe) eligible.Add(entry); else entries.Add(entry);
        }

        var suggested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ageLimitDays is int days && days >= 0)
            foreach (var entry in eligible.Where(e => e.AgeDays >= days)) suggested.Add(entry.Path);
        if (budgetGiB is > 0 && double.IsFinite(budgetGiB.Value))
        {
            var budget = budgetGiB.Value * 1024d * 1024 * 1024;
            var ordered = eligible.OrderByDescending(e => e.AgeDays).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            long total = 0;
            foreach (var entry in ordered) total = checked(total + entry.SizeBytes!.Value);
            foreach (var entry in ordered)
            {
                if (total <= budget) break;
                suggested.Add(entry.Path);
                total -= entry.SizeBytes!.Value;
            }
        }
        foreach (var e in eligible)
            entries.Add(suggested.Contains(e.Path) ? e with { Candidate = true, Reason = ageLimitDays is int d && e.AgeDays >= d ? $"Older than the {d}-day suggestion limit." : "Suggested by the storage budget." } : e with { Reason = "Within suggestion limits." });
        entries = entries.OrderByDescending(e => e.Candidate).ThenByDescending(e => e.AgeDays).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = entries.Where(e => e.Candidate).ToArray();
        long? reclaimable = candidates.Any(e => e.SizeBytes is null) ? null : candidates.Aggregate(0L, (sum, e) => checked(sum + e.SizeBytes!.Value));
        return new CleanupPreview(entries, reclaimable, nowUtc);
    }

    private static bool IsUnderAnyRoot(string path, IEnumerable<string> roots) => roots.Any(root =>
        IsUnderRoot(path, root));

    private static bool IsUnderRoot(string path, string root)
    {
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetAbsolutePath(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    private static bool HasReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
        var current = Path.GetDirectoryName(path);
        while (current is not null)
        {
            // A link above the configured root also makes lexical containment inconclusive.
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

}
