using System;
using System.Runtime.InteropServices;
using System.Text;

namespace JamesTools.Core.Recording;

internal static class ObsWindows
{
    // OBS window-helpers.c escapes '#' first and ':' second, then joins the
    // title, class and executable name with colons.
    internal static string EncodeWindow(GameWindow window) =>
        $"{EncodePart(window.Title)}:{EncodePart(window.ClassName)}:{EncodePart(window.ExecutableName)}";

    internal static string? GetConfiguredExecutable(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts = value.Split(':');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0) return null;
        return parts[2].Replace("#3A", ":", StringComparison.Ordinal)
            .Replace("#22", "#", StringComparison.Ordinal);
    }

    private static string EncodePart(string part) => part
        .Replace("#", "#22", StringComparison.Ordinal)
        .Replace(":", "#3A", StringComparison.Ordinal);

    internal static GameWindow? FindVisibleWindow(int processId, string executableName)
    {
        if (!OperatingSystem.IsWindows()) return null;

        GameWindow? best = null;
        long bestArea = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)processId || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            var style = GetWindowStyle(hwnd, -16 /* GWL_STYLE */);
            var extendedStyle = GetWindowStyle(hwnd, -20 /* GWL_EXSTYLE */);
            if ((style & 0x40000000L) != 0 || (extendedStyle & 0x80L) != 0) return true;
            if (!GetClientRect(hwnd, out var rect)) return true;
            var width = (long)rect.Right - rect.Left;
            var height = (long)rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || width * height <= bestArea) return true;

            var length = GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var title = new StringBuilder(length + 1);
            var className = new StringBuilder(256);
            if (GetWindowText(hwnd, title, title.Capacity) <= 0 || GetClassName(hwnd, className, className.Capacity) <= 0)
                return true;

            best = new GameWindow(processId, title.ToString(), className.ToString(), executableName);
            bestArea = width * height;
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static long GetWindowStyle(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index).ToInt64() : GetWindowLong32(window, index);

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int valueSize);
}
