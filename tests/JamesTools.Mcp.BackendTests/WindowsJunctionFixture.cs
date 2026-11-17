using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Directory junctions do not require the symbolic-link privilege or an elevated test process.
internal static class WindowsJunctionFixture
{
    internal static void Create(string link, string target)
    {
        Directory.CreateDirectory(link);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + print.Length + 4];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(buffer.Length - 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 18 + substitute.Length);
        using var handle = CreateFileW(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
}
