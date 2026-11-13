using JamesTools.Core.Platform;
using JamesTools.Core.Recording;
using System.Runtime.InteropServices;

namespace JamesTools.Core.Automation;

internal interface IAutomationPlatform : IDisposable
{
    IReadOnlyList<RunningProcess> CaptureProcesses();
    IReadOnlyList<LibraryGame> DiscoverLibraryGames(AppDetectionOptions options);
    IReadOnlyList<LibraryGame> FindRunningLibraryGames(IReadOnlyList<LibraryGame> catalog, IReadOnlyList<RunningProcess> processes);
    IReadOnlyList<VibranceDisplay> ReadVibrance();
    void SetVibrance(int percent);
    void SetVibrance(int percent, string deviceName) => throw new InvalidOperationException("Per-display vibrance is unavailable.");
    int? GetForegroundProcessId() => null;
    string? GetVisibleGameMonitorIdentity(int processId) => null;
    void ResetVibrance();
    Guid GetActivePowerPlan();
    void SetPowerPlan(Guid id);
    bool IsHdrActive();
    bool? GetHdrState() => IsHdrActive();
    int CloseApplications(IEnumerable<string> targets);
}

internal interface IRecordingController : IDisposable
{
    bool IsOwnedRecording { get; }
    RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath);
    Task<bool> StopOwnedAsync();
}

internal sealed class WindowsAutomationPlatform : IAutomationPlatform
{
    private NvidiaVibrance? _nvidia;
    public IReadOnlyList<RunningProcess> CaptureProcesses() => ProcessSnapshot.Capture();
    public IReadOnlyList<LibraryGame> DiscoverLibraryGames(AppDetectionOptions options) => LibraryGameDetector.Discover(options);
    public IReadOnlyList<LibraryGame> FindRunningLibraryGames(IReadOnlyList<LibraryGame> catalog, IReadOnlyList<RunningProcess> processes)
        => LibraryGameDetector.FindRunning(catalog, processes);
    public IReadOnlyList<VibranceDisplay> ReadVibrance() => (_nvidia ??= new NvidiaVibrance()).Read();
    public void SetVibrance(int percent) => (_nvidia ??= new NvidiaVibrance()).SetAll(percent);
    public void SetVibrance(int percent, string deviceName) => (_nvidia ??= new NvidiaVibrance()).SetDisplay(deviceName, percent);
    public int? GetForegroundProcessId() => WindowsGameWindow.GetForegroundProcessId();
    public string? GetVisibleGameMonitorIdentity(int processId) => WindowsGameWindow.GetVisibleMonitorIdentity(processId);
    public void ResetVibrance() { _nvidia?.Dispose(); _nvidia = null; }
    public Guid GetActivePowerPlan() => PowerPlans.GetActive();
    public void SetPowerPlan(Guid id) => PowerPlans.SetActive(id);
    public bool IsHdrActive() => HdrStatus.IsHdrActive();
    public bool? GetHdrState() => HdrStatus.TryIsHdrActive();
    public int CloseApplications(IEnumerable<string> targets) => AppCloser.Close(targets);
    public void Dispose() => ResetVibrance();
}

/// <summary>Read-only Win32 foreground/window-to-monitor discovery used by vibrance targeting.</summary>
internal static class WindowsGameWindow
{
    public static int? GetForegroundProcessId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0) return null;
        return unchecked((int)pid);
    }

    public static string? GetVisibleMonitorIdentity(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0) return null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid != (uint)processId) return true;
            var monitor = MonitorFromWindow(hwnd, 0x00000002); // MONITOR_DEFAULTTONEAREST
            if (monitor == IntPtr.Zero) return true;
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var name = info.Device.TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            return true;
        }, IntPtr.Zero);
        return names.Count == 1 ? names.Single() : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
}

internal sealed class WindowsRecordingController : IRecordingController
{
    private readonly ObsRecorder _recorder = new();
    public bool IsOwnedRecording => _recorder.IsOwnedRecording;
    public RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath) => _recorder.TryStart(settings, gamePid, gameExePath);
    public Task<bool> StopOwnedAsync() => _recorder.StopOwnedAsync();
    public void Dispose() => _recorder.Dispose();
}
