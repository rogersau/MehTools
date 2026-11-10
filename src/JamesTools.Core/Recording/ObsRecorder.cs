using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace JamesTools.Core.Recording;

/// <summary>
/// Starts one OBS process owned by this instance. It never attaches to or closes
/// an OBS process started by someone else. A live process is not proof that OBS
/// successfully started its encoder or captured a game frame.
/// </summary>
public sealed class ObsRecorder : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    private readonly object _gate = new();
    private Process? _ownedProcess;
    private int _ownedPid;
    private DateTime _ownedStartTimeUtc;
    private string? _ownedPath;
    private bool _stopping;
    private bool _disposed;

    public bool IsOwnedRecording
    {
        get
        {
            lock (_gate)
            {
                ClearExitedProcess();
                return !_disposed && _ownedProcess is not null;
            }
        }
    }

    // OBS has used both obs64.exe and obs32.exe. Treat inaccessible processes
    // conservatively as running; it is unsafe to edit a possibly active config.
    public bool IsAnyObsRunning => AnyObsRunning();

    public RecorderStartResult TryStart(ObsSettings settings, int gamePid, string gameExePath)
    {
        lock (_gate)
        {
            if (_disposed) return Failed("The OBS recorder has been disposed.");
            ClearExitedProcess();
            if (_ownedProcess is not null || _stopping)
                return new RecorderStartResult(RecorderStartStatus.AlreadyRunning, "OBS is already running for this session.");
            if (AnyObsRunning())
                return new RecorderStartResult(RecorderStartStatus.AlreadyRunning, "OBS is already running. Close it before starting a managed recording.");

            ObsFileTransaction? transaction = null;
            Process? launched = null;
            try
            {
                ArgumentNullException.ThrowIfNull(settings);
                if (!OperatingSystem.IsWindows()) return Failed("OBS recording is available on Windows only.");
                if (gamePid <= 0 || string.IsNullOrWhiteSpace(gameExePath)) return Failed("Select a running game first.");
                var obsPath = FullPath(settings.ExecutablePath, "OBS executable");
                var obsName = Path.GetFileName(obsPath);
                if (!obsName.Equals("obs64.exe", StringComparison.OrdinalIgnoreCase) &&
                    !obsName.Equals("obs32.exe", StringComparison.OrdinalIgnoreCase))
                    return Failed("Select an OBS Studio executable named obs64.exe or obs32.exe.");
                if (!File.Exists(obsPath)) return Failed("The selected OBS executable does not exist.");
                var workingDirectory = Path.GetDirectoryName(obsPath) ?? throw new InvalidDataException("The OBS executable folder is invalid.");
                var configRoot = FullPath(settings.ConfigRoot, "OBS configuration folder");
                ValidateConfigRoot(configRoot, workingDirectory, settings.Portable);
                var recordingDirectory = FullPath(settings.RecordingDirectory, "recording folder");
                CheckRecordingDirectory(recordingDirectory);
                var gamePath = FullPath(gameExePath, "game executable");
                var gameStart = GetProcessIdentity(gamePid, gamePath);

                var preparation = ObsConfiguration.Prepare(configRoot, recordingDirectory, gamePid, gamePath, ObsWindows.FindVisibleWindow);
                if (preparation.WaitingForWindow)
                    return new RecorderStartResult(RecorderStartStatus.WaitingForWindow, preparation.Message);
                transaction = preparation.Transaction ?? throw new InvalidDataException("OBS configuration could not be prepared.");
                if (AnyObsRunning())
                    return new RecorderStartResult(RecorderStartStatus.AlreadyRunning, "OBS started while the recording was being prepared.");

                transaction.Apply(AnyObsRunning);
                VerifyProcessIdentity(gamePid, gamePath, gameStart);
                if (AnyObsRunning()) throw new IOException("OBS started while the recording was being prepared.");
                transaction.VerifyStable();

                var startInfo = new ProcessStartInfo
                {
                    FileName = obsPath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Minimized
                };
                if (settings.Portable) startInfo.ArgumentList.Add("--portable");
                startInfo.ArgumentList.Add("--disable-updater");
                startInfo.ArgumentList.Add("--startrecording");
                launched = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start OBS.");
                int pid = launched.Id;
                var started = GetProcessIdentity(launched.SafeHandle, obsPath);
                if (!IsLive(launched) || GetProcessIdentity(pid, obsPath) != started)
                    throw new InvalidOperationException("The OBS process identity could not be verified.");
                _ownedProcess = launched;
                _ownedPid = pid;
                _ownedStartTimeUtc = started;
                _ownedPath = obsPath;
                launched = null;

                // Catch immediate startup crashes. Later exits are detected when
                // IsOwnedRecording is queried or a stop is requested.
                if (_ownedProcess.WaitForExit(1000))
                {
                    var exitCode = _ownedProcess.ExitCode;
                    ClearExitedProcess();
                    throw new InvalidOperationException($"OBS exited during startup (exit code {exitCode}).");
                }
                return new RecorderStartResult(RecorderStartStatus.Started,
                    "OBS launched with recording requested. Check OBS if capture or encoding fails.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                InvalidOperationException or Win32Exception or System.Text.Json.JsonException or NotSupportedException)
            {
                bool untrackedRunning = launched is not null && IsLive(launched);
                launched?.Dispose();
                ClearExitedProcess();
                if (_ownedProcess is not null || untrackedRunning)
                    return Failed($"{ex.Message} OBS may still be running; check it before starting again.");
                if (transaction is not null && !transaction.TryRollback(AnyObsRunning, out var rollbackError))
                    return Failed($"{ex.Message} Automatic rollback could not be completed: {rollbackError}");
                return Failed(ex.Message);
            }
        }
    }

    /// <summary>Requests a normal OBS close and waits up to 20 seconds. Never force-kills OBS.</summary>
    public Task<bool> StopOwnedAsync()
    {
        lock (_gate)
        {
            if (_disposed || _stopping) return Task.FromResult(false);
            ClearExitedProcess();
            if (_ownedProcess is null) return Task.FromResult(true);
            _stopping = true;
            var process = _ownedProcess;
            var pid = _ownedPid;
            var started = _ownedStartTimeUtc;
            var path = _ownedPath!;
            return StopCoreAsync(process, pid, started, path);
        }
    }

    private async Task<bool> StopCoreAsync(Process process, int pid, DateTime started, string path)
    {
        // Yield before touching _gate again, even if the process has exited.
        await Task.Yield();
        bool stopped = false;
        try
        {
            var elapsed = Stopwatch.StartNew();
            bool closeSent = false;
            while (elapsed.Elapsed < StopTimeout)
            {
                if (!IsLive(process)) { stopped = true; break; }
                // Recheck both PID creation time and executable before every close
                // request. A recycled PID must never receive our request.
                if (!HasSameIdentity(pid, path, started)) break;
                process.Refresh();
                if (!closeSent) closeSent = process.CloseMainWindow();
                if (closeSent)
                {
                    var remaining = StopTimeout - elapsed.Elapsed;
                    if (remaining > TimeSpan.Zero)
                    {
                        var wait = process.WaitForExitAsync();
                        stopped = await Task.WhenAny(wait, Task.Delay(remaining)).ConfigureAwait(false) == wait;
                    }
                    break;
                }
                await Task.Delay(250).ConfigureAwait(false);
            }
            if (!stopped && !IsLive(process)) stopped = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            stopped = !IsLive(process);
        }
        finally
        {
            lock (_gate)
            {
                _stopping = false;
                if ((stopped || _disposed) && ReferenceEquals(process, _ownedProcess))
                {
                    _ownedProcess = null;
                    _ownedPath = null;
                    process.Dispose();
                }
            }
        }
        return stopped;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // Disposing this adapter never closes a recording. A stop already in
            // progress owns the handle until its graceful wait has completed.
            if (!_stopping)
            {
                _ownedProcess?.Dispose();
                _ownedProcess = null;
                _ownedPath = null;
            }
        }
    }

    private void ClearExitedProcess()
    {
        if (_ownedProcess is null || _stopping || IsLive(_ownedProcess)) return;
        _ownedProcess.Dispose();
        _ownedProcess = null;
        _ownedPath = null;
    }

    private static bool AnyObsRunning()
    {
        foreach (string name in new[] { "obs64", "obs32" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { return true; }
            try { if (processes.Length != 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private static bool IsLive(Process process)
    {
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
    }

    private static DateTime GetProcessIdentity(int pid, string expectedPath)
    {
        using var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect the selected process.");
        return GetProcessIdentity(handle, expectedPath);
    }

    private static DateTime GetProcessIdentity(SafeProcessHandle handle, string expectedPath)
    {
        var name = new StringBuilder(32768);
        int length = name.Capacity;
        if (!QueryFullProcessImageName(handle, 0, name, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the selected process executable.");
        var actual = name.ToString();
        if (string.IsNullOrWhiteSpace(actual) || !SamePath(actual, expectedPath))
            throw new InvalidOperationException($"The running process executable '{actual}' does not match '{expectedPath}'.");
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify the selected process creation time.");
        var fileTime = ((long)(uint)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime;
        return DateTime.FromFileTimeUtc(fileTime);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle handle, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle,
        out System.Runtime.InteropServices.ComTypes.FILETIME creation,
        out System.Runtime.InteropServices.ComTypes.FILETIME exit,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
        out System.Runtime.InteropServices.ComTypes.FILETIME user);

    private static void VerifyProcessIdentity(int pid, string path, DateTime start)
    {
        if (!HasSameIdentity(pid, path, start))
            throw new InvalidOperationException("The selected game process exited or changed while OBS was being prepared.");
    }

    private static bool HasSameIdentity(int pid, string path, DateTime start)
    {
        try { return GetProcessIdentity(pid, path) == start; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return false; }
    }

    private static string FullPath(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException($"Choose an absolute path for the {description}.");
        return Path.GetFullPath(value);
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CheckRecordingDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The recording folder does not exist.");
        var probe = Path.Combine(path, ".jamestools-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    private static void ValidateConfigRoot(string requestedRoot, string workingDirectory, bool portable)
    {
        var installRoot = Directory.GetParent(workingDirectory)?.Parent?.FullName
            ?? throw new InvalidDataException("The OBS executable is not inside the expected bin folder.");
        string[] portableMarkers = { "portable_mode", "portable_mode.txt", "obs_portable_mode", "obs_portable_mode.txt" };
        if (!portable && portableMarkers.Any(marker => File.Exists(Path.Combine(installRoot, marker))))
            throw new InvalidDataException("This OBS installation has a portable-mode marker. Enable portable mode for this installation.");
        var baseDir = portable ? Path.Combine(installRoot, "config") : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(baseDir)) throw new InvalidDataException("The Windows application data folder is unavailable.");
        var defaultRoot = Path.Combine(baseDir, "obs-studio");
        var configRoot = defaultRoot;
        var profilesRoot = defaultRoot;
        var scenesRoot = defaultRoot;

        // OBS 31+ may redirect these three locations independently. A single
        // ConfigRoot can safely describe them only when they resolve together.
        var globalIni = Path.Combine(defaultRoot, "global.ini");
        if (!portable && File.Exists(globalIni))
        {
            var global = IniDocument.Read(globalIni);
            configRoot = RelocatedRoot(global.GetValue("Locations", "Configuration"), defaultRoot, workingDirectory);
            profilesRoot = RelocatedRoot(global.GetValue("Locations", "Profiles"), defaultRoot, workingDirectory);
            scenesRoot = RelocatedRoot(global.GetValue("Locations", "SceneCollections"), defaultRoot, workingDirectory);
        }
        if (!SamePath(requestedRoot, configRoot) || !SamePath(requestedRoot, profilesRoot) || !SamePath(requestedRoot, scenesRoot))
            throw new InvalidDataException("The selected configuration folder does not match the profile, scenes, and user configuration that this OBS installation will load.");
        if (!Directory.Exists(requestedRoot)) throw new DirectoryNotFoundException("The selected OBS configuration folder does not exist.");
    }

    private static string RelocatedRoot(string? location, string defaultRoot, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(location)) return defaultRoot;
        var full = Path.GetFullPath(location, workingDirectory);
        return Directory.Exists(full) ? Path.Combine(full, "obs-studio") : defaultRoot;
    }

    private static RecorderStartResult Failed(string message) => new(RecorderStartStatus.Failed, message);
}
