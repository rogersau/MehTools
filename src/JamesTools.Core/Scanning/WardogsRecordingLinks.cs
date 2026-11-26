using System.Globalization;
using JamesTools.Core.Configuration;
using JamesTools.Core.Sessions;
using JamesTools.Core.Storage;

namespace JamesTools.Core.Scanning;

public static class WardogsRecordingLinks
{
    internal static bool IsWardogs(GameSession session) => IsWardogs(session.GameName, session.ExecutableKey);

    internal static bool IsWardogs(string? name, string? executable) =>
        string.Equals(name?.Trim(), "WARDOGS", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileNameWithoutExtension(executable)?.StartsWith("Wardogs", StringComparison.OrdinalIgnoreCase) == true;

    public static int Recover(SessionLibraryStore store, AppSettings settings, DateTime nowUtc, CancellationToken token)
    {
        var sessions = store.Snapshot();
        var wardogs = sessions.Where(s => s.EndedUtc.HasValue && IsWardogs(s)).ToArray();
        if (wardogs.Length == 0) return 0;
        var linked = sessions.SelectMany(s => s.Attachments).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in wardogs)
        {
            var rule = settings.Games.FirstOrDefault(game => string.Equals(SessionLibraryStore.NormalizeExecutable(game.ExecutablePath), session.ExecutableKey, StringComparison.OrdinalIgnoreCase));
            var root = rule is null ? settings.RecordingDirectory : EffectiveGameOptionsResolver.Resolve(settings, rule).RecordingDirectory;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith(@"\\")) continue;
            roots.Add(root);
            roots.Add(RecordingFolderPlanner.Plan(root, session.GameName, session.StartedUtc.ToLocalTime(), organise: true).PlannedDirectory);
        }
        var pollAllowance = TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 2, 30) + 2);
        var closeAllowance = TimeSpan.FromSeconds(Math.Clamp(settings.RecordingStopDelaySeconds, 0, 120) + 20);
        int count = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                token.ThrowIfCancellationRequested();
                if (linked.Contains(file.FullName) || !IsStandardRecordingName(file)) continue;
                var started = file.CreationTimeUtc;
                var written = file.LastWriteTimeUtc;
                if (written - started < TimeSpan.FromSeconds(15) || nowUtc - written < TimeSpan.FromSeconds(20)) continue;
                var owners = wardogs.Where(session => started >= session.StartedUtc - pollAllowance && started < session.EndedUtc
                    && written <= session.EndedUtc + (session.Status == SessionStatus.Interrupted ? TimeSpan.FromMinutes(5) : closeAllowance)).Take(2).ToArray();
                if (owners.Length != 1) continue;
                var owner = owners[0];
                if (sessions.Any(s => s.Id != owner.Id && s.EndedUtc is { } end
                    && Overlaps(started, written, s.StartedUtc, end, TimeSpan.FromSeconds(20)))) continue;
                if (!WardogsReviewStore.IsAvailableRecording(file.FullName)) continue;
                store.AddAttachment(owner.Id, null, owner.ExecutableKey, SessionAttachmentKind.Recording, file.FullName, started);
                linked.Add(file.FullName);
                count++;
            }
        }
        return count;
    }

    private static bool IsStandardRecordingName(FileInfo file)
    {
        string stem = Path.GetFileNameWithoutExtension(file.Name);
        var created = file.CreationTime;
        const string pattern = "yyyy-MM-dd HH-mm-ss";
        return string.Equals(stem, created.ToString(pattern, CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || string.Equals(stem, created.AddSeconds(-1).ToString(pattern, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool Overlaps(DateTime firstStart, DateTime firstEnd, DateTime secondStart, DateTime secondEnd, TimeSpan threshold)
        => (firstEnd < secondEnd ? firstEnd : secondEnd) - (firstStart > secondStart ? firstStart : secondStart) > threshold;
}
