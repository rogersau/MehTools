using System.Globalization;
using System.ComponentModel;
using System.Runtime.InteropServices;

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
