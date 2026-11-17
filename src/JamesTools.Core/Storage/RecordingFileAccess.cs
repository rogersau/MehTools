using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JamesTools.Core.Storage;

public sealed record RecordingFileStamp(long SizeBytes, DateTime LastWriteUtc, DateTime CreatedUtc,
    FileAttributes Attributes, string? Identity = null, uint LinkCount = 1);

public interface IRecordingFileAccess
{
    RecordingFileStamp Inspect(string path);
    void Delete(string path, RecordingFileStamp expected);
}

/// <summary>Reads metadata without reading video contents; deletes only an unchanged, exclusively held Windows file.</summary>
public sealed class RecordingFileAccess : IRecordingFileAccess
{
    public RecordingFileStamp Inspect(string path)
    {
        path = ValidatePath(path);
        EnsureSafePath(path);
        if (OperatingSystem.IsWindows())
        {
            using var handle = Open(path, 0x80 /* FILE_READ_ATTRIBUTES */, 7 /* share read/write/delete */);
            return InspectHandle(handle, path);
        }
        var info = new FileInfo(path);
        return new(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc, info.Attributes);
    }

    public void Delete(string path, RecordingFileStamp expected)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Recording deletion requires Windows.");
        path = ValidatePath(path);
        EnsureSafePath(path);
        // Exclusive access refuses recordings in use and prevents writes/renames between validation and deletion.
        using var handle = Open(path, 0x80010000 /* GENERIC_READ | DELETE */, 0);
        var current = InspectHandle(handle, path);
        if (current != expected) throw new IOException("Recording changed since the preview. Refresh before deleting it.");
        if ((current.Attributes & FileAttributes.ReadOnly) != 0 || current.LinkCount != 1)
            throw new IOException("Read-only recordings and files with multiple hard links are protected.");
        EnsureSafePath(path);
        // FileDispositionInfo deletes this verified handle on close, without reopening a replaceable path.
        // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle
        byte deleteFile = 1;
        if (!SetFileInformationByHandle(handle, 4 /* FileDispositionInfo */, ref deleteFile, 1))
            throw NativeError("Couldn't delete recording");
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("An absolute local recording path is required.");
        if (OperatingSystem.IsWindows() && (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
            path[2] is not ('\\' or '/') || path.IndexOf(':', 2) >= 0 ||
            path[3..].Split('\\', '/').Any(p => p is "." or ".." || p.EndsWith('.') || p.EndsWith(' '))))
            throw new IOException("Network, device, alternate-stream and ambiguous recording paths are protected.");
        return Path.GetFullPath(path);
    }

    private static void EnsureSafePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Recording or containing folder is a reparse point or link.");
            if (current == path && (attributes & FileAttributes.Directory) != 0)
                throw new IOException("The recording path is a directory.");
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint sharing)
    {
        var handle = CreateFileW(path, access, sharing, 0, 3 /* OPEN_EXISTING */, 0x00200000 /* OPEN_REPARSE_POINT */, 0);
        if (handle.IsInvalid)
        {
            var error = NativeError("Couldn't open recording (it may be in use or unavailable)");
            handle.Dispose();
            throw error;
        }
        return handle;
    }

    private static RecordingFileStamp InspectHandle(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeError("Couldn't inspect recording");
        if ((info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Links and directories are protected.");
        var finalPath = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0) throw NativeError("Couldn't verify recording location");
        if (length >= finalPath.Capacity || !string.Equals(finalPath.ToString(), @"\\?\" + path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The recording location changed or resolves through an unsupported path.");
        return new(checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)),
            FileTime(info.LastWrite), FileTime(info.Creation), info.Attributes,
            $"{info.VolumeSerial:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}", info.Links);
    }

    private static DateTime FileTime(FILETIME time) => DateTime.FromFileTimeUtc(
        ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime);

    private static IOException NativeError(string message) => new(message + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public FileAttributes Attributes;
        public FILETIME Creation, LastAccess, LastWrite;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
