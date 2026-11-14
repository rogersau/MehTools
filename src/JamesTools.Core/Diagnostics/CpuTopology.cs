using System.Runtime.InteropServices;

namespace JamesTools.Core.Diagnostics;

internal static class CpuTopology
{
    public static IReadOnlyDictionary<string, string> Read()
    {
        var result = new Dictionary<string, string>();
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out var size, IntPtr.Zero, 0);
        if (size is 0 or > 1048576) return result;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, size, out _, IntPtr.Zero, 0)) return result;
            var entries = new List<(string Id, byte Core, byte Efficiency)>();
            for (var offset = 0; offset + 32 <= size;)
            {
                var p = buffer + offset; var length = Marshal.ReadInt32(p);
                if (length < 32 || offset + length > size) break;
                if (Marshal.ReadInt32(p, 4) == 0)
                    entries.Add(($"{(ushort)Marshal.ReadInt16(p, 12)},{Marshal.ReadByte(p, 14)}", Marshal.ReadByte(p, 15), Marshal.ReadByte(p, 18)));
                offset += length;
            }
            var classes = entries.Select(e => e.Efficiency).Distinct().Order().ToArray();
            foreach (var e in entries) result[e.Id] = $"physical core {e.Core}, efficiency class {e.Efficiency}" +
                (classes.Length < 2 ? " (core type unavailable)" : e.Efficiency == classes[^1] ? " (P-class)" : e.Efficiency == classes[0] ? " (E-class)" : "");
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint size, out uint returnedLength, IntPtr process, uint flags);
}
