using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.Tracing.Session;

namespace JamesTools.Core.Diagnostics;

internal static partial class InvestigationDeepTraceOwnership
{
    // Versioned namespace with exact owner identity. No prefix-only/global ETW cancellation.
    [GeneratedRegex(@"\AMehToolsDeep-v1-([1-9][0-9]{0,9})-([1-9][0-9]{0,18})-([0-9a-f]{32})\z", RegexOptions.CultureInvariant)]
    private static partial Regex OwnedName();
    internal static string NewName(int pid, long startTicks) => string.Create(CultureInfo.InvariantCulture,
        $"MehToolsDeep-v1-{pid}-{startTicks}-{Guid.NewGuid():N}");
    internal static bool TryOwner(string name, out int pid, out long startTicks)
    {
        var match = OwnedName().Match(name);
        pid = 0; startTicks = 0;
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out pid) &&
            long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out startTicks) && startTicks <= DateTime.MaxValue.Ticks;
    }

    internal static IReadOnlyList<InvestigationCollectorStatus> Recover()
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
            return [new("Deep ETW recovery", "unavailable", "Recovery requires existing elevated permissions; no elevation requested.")];
        try { return Recover(TraceEventSession.GetActiveSessionNames(), OwnerStart, name => InvestigationDeepTraceNative.Stop(name)); }
        catch (Exception ex) { return [new("Deep ETW recovery", "unavailable", "Session enumeration failed (" + ex.GetType().Name + ").")]; }
    }

    // null means provably absent; exceptions (access denied, etc.) mean unknown and must never authorize stopping.
    internal static long? OwnerStart(int pid)
    {
        try { using var owner = Process.GetProcessById(pid); return owner.StartTime.ToUniversalTime().Ticks; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
    internal static IReadOnlyList<InvestigationCollectorStatus> Recover(IEnumerable<string> names, Func<int, long?> ownerStart, Action<string> stop)
    {
        int recovered = 0, live = 0, unknown = 0, failed = 0;
        foreach (var name in names)
        {
            if (!TryOwner(name, out int pid, out long ticks)) continue;
            try
            {
                var current = ownerStart(pid);
                if (current == ticks) { live++; continue; }
                // Exact name carries a GUID and original process start time; PID reuse cannot claim ownership.
                try { stop(name); recovered++; } catch { failed++; }
            }
            catch { unknown++; }
        }
        return [new("Deep ETW recovery", failed + unknown > 0 ? "incomplete" : "complete",
            $"Stopped orphan sessions: {recovered}; live owners preserved: {live}; unknown owners preserved: {unknown}; stop failures: {failed}.")];
    }

    internal static IInvestigationDeepTraceWatchdog Arm(string name, int seconds)
    {
        if (!TryOwner(name, out int pid, out long ticks)) throw new ArgumentException("Invalid owned session name.", nameof(name));
        string suffix = Guid.NewGuid().ToString("N");
        string readyName = @"Local\MehToolsDeepReady-" + suffix, cancelName = @"Local\MehToolsDeepCancel-" + suffix;
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        var cancel = new EventWaitHandle(false, EventResetMode.ManualReset, cancelName);
        Process? helper = null;
        try
        {
            // The helper opens a process handle before its handshake, pins the owner identity and has its own deadline.
            // It survives abrupt termination of the app; it never executes WPR or touches any other session.
            string script = WatchdogScript(name, pid, ticks, seconds, readyName, cancelName);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
            helper = Process.Start(info) ?? throw new InvalidOperationException("Watchdog did not start.");
            if (!ready.WaitOne(TimeSpan.FromSeconds(8)) || helper.HasExited)
                throw new InvalidOperationException("Watchdog handshake failed; ETW was not enabled.");
            return new Watchdog(helper, cancel);
        }
        catch
        {
            cancel.Set(); cancel.Dispose();
            if (helper is not null) { try { if (!helper.HasExited) helper.Kill(); } catch (Win32Exception) { } finally { helper.Dispose(); } }
            throw;
        }
    }

    internal static string WatchdogScript(string name, int pid, long ticks, int seconds, string readyName, string cancelName) => $$"""
        $ErrorActionPreference = 'Stop'
        $ownerProcess = [Diagnostics.Process]::GetProcessById({{pid}})
        if ($ownerProcess.StartTime.ToUniversalTime().Ticks -ne {{ticks}}) { exit 2 }
        $ownerHandle = $ownerProcess.Handle
        $logmanFile = [IO.Path]::Combine($env:SystemRoot, 'System32\logman.exe')
        if (-not [IO.File]::Exists($logmanFile)) { exit 3 }
        $readySignal = [Threading.EventWaitHandle]::OpenExisting('{{readyName}}')
        $cancelSignal = [Threading.EventWaitHandle]::OpenExisting('{{cancelName}}')
        $watch = [Diagnostics.Stopwatch]::StartNew()
        [void]$readySignal.Set()
        while (-not $cancelSignal.WaitOne(250)) {
            if ($ownerProcess.HasExited -or $watch.Elapsed.TotalSeconds -ge {{seconds + 15}}) { break }
        }
        if (-not $cancelSignal.WaitOne(0)) {
            # Retry across an in-flight startup syscall or transient stop failure after owner death.
            for ($attempt = 0; $attempt -lt 6; $attempt++) {
                try {
                    $stopInfo = New-Object Diagnostics.ProcessStartInfo
                    $stopInfo.FileName = $logmanFile
                    $stopInfo.Arguments = 'stop {{name}} -ets'
                    $stopInfo.UseShellExecute = $false
                    $stopInfo.CreateNoWindow = $true
                    $stopInfo.RedirectStandardOutput = $true
                    $stopInfo.RedirectStandardError = $true
                    $stopInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
                    $stopProcess = [Diagnostics.Process]::Start($stopInfo)
                    $discardOutput = $stopProcess.StandardOutput.ReadToEndAsync()
                    $discardError = $stopProcess.StandardError.ReadToEndAsync()
                    if (-not $stopProcess.WaitForExit(5000)) { $stopProcess.Kill() }
                } catch { } finally { if ($null -ne $stopProcess) { $stopProcess.Dispose(); $stopProcess = $null } }
                [Threading.Thread]::Sleep(500)
            }
        }
        $ownerProcess.Dispose()
        $readySignal.Dispose()
        $cancelSignal.Dispose()
        """;

    private sealed class Watchdog(Process helper, EventWaitHandle cancel) : IInvestigationDeepTraceWatchdog
    {
        private int _disposed;
        public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !helper.HasExited;
        public void Detach()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Leave helper armed but release all of this process's handles.
            cancel.Dispose(); helper.Dispose();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                cancel.Set();
                if (!helper.WaitForExit(1500)) helper.Kill();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            finally { cancel.Dispose(); helper.Dispose(); }
        }
    }
}
