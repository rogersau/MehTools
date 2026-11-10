using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace JamesTools.Core.Platform;

public sealed record RunningProcess(int Id, string Name, string? FullPath);

public static class ProcessSnapshot
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public static IReadOnlyList<RunningProcess> Capture()
    {
        var result = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var id = process.Id;
                    var name = process.ProcessName;
                    var path = GetImagePath(id);
                    if (!string.IsNullOrEmpty(path))
                        name = Path.GetFileName(path);
                    else if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        name += ".exe";

                    result.Add(new RunningProcess(id, name, path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // A process can exit between enumeration and inspection.
                }
            }
        }

        return result;
    }

    internal static string? GetImagePath(int processId)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
            return null;

        // Most paths are short. Grow only if Windows reports an insufficient buffer.
        const int errorInsufficientBuffer = 122;
        for (var capacity = 512; capacity <= 32768; capacity *= 2)
        {
            var buffer = new StringBuilder(capacity);
            var length = capacity;
            if (QueryFullProcessImageNameW(handle, 0, buffer, ref length))
                return buffer.ToString();
            if (Marshal.GetLastWin32Error() != errorInsufficientBuffer)
                return null;
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);
}
