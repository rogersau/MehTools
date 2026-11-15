using System.Globalization;
using System.Security;
using System.Runtime.InteropServices;

namespace JamesTools.Core.Mcp;

internal static class WindowsBackendSupport
{
    internal static string Status(Exception ex) => ex switch
    {
        UnauthorizedAccessException or SecurityException => "permission_denied",
        System.ComponentModel.Win32Exception win32 when win32.NativeErrorCode == 5 => "permission_denied",
        COMException com when com.HResult == unchecked((int)0x80070005) => "permission_denied",
        System.Management.ManagementException management when management.ErrorCode == System.Management.ManagementStatus.AccessDenied => "permission_denied",
        System.Management.ManagementException management when management.ErrorCode == System.Management.ManagementStatus.Timedout => "unavailable",
        TimeoutException => "unavailable",
        PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException => "unsupported",
        IOException => "unavailable",
        _ => "failure"
    };

    // Never return exception messages, device identifiers, or arbitrary provider text to clients.
    internal static string Text(string? value, int length = 160)
    {
        if (value is null) return "";
        return new string(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
    }

    internal static string Number(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    internal static byte[] ReadBounded(string path, int maximum)
    {
        GameInspectionReader.CheckAncestors(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > maximum) throw new InvalidDataException("Metadata exceeds size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
