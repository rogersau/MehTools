using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace JamesTools.Core.Diagnostics;

/// <summary>Lightweight system-wide CPU and first NVIDIA GPU utilization sampler.</summary>
public sealed class LiveHardwareSampler : IDisposable
{
    private (ulong Idle, ulong Kernel, ulong User)? _previous;
    private readonly NvidiaTelemetry _nvidia;
    private readonly bool _ownsNvidia;

    public LiveHardwareSampler() { _nvidia = new(); _ownsNvidia = true; }
    internal LiveHardwareSampler(NvidiaTelemetry nvidia) => _nvidia = nvidia;

    public (double? Cpu, double? Gpu) Sample()
    {
        double? cpu = null, gpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var now = (Idle: ToUInt64(idle), Kernel: ToUInt64(kernel), User: ToUInt64(user));
            if (_previous is { } previous && now.Idle >= previous.Idle && now.Kernel >= previous.Kernel && now.User >= previous.User)
            {
                var total = (now.Kernel - previous.Kernel) + (now.User - previous.User);
                if (total > 0) cpu = Math.Clamp(100 * (1 - (double)(now.Idle - previous.Idle) / total), 0, 100);
            }
            _previous = now;
        }
        gpu = _nvidia.SampleUtilization();
        return (cpu, gpu);
    }

    public void Dispose() { if (_ownsNvidia) _nvidia.Dispose(); }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle, out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);

}

/// <summary>Reads a private PresentMon CSV stream without retaining a frame history.</summary>
public sealed class LiveFrameSampler : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Aggregate> _frames = new();
    private readonly Process _process;
    private readonly Task _reader;
    private readonly string _executable, _session;
    private bool _disposed;
    private string _status = "Waiting for game frames…";

    public LiveFrameSampler(string presentMonPath, string gamePath)
    {
        _executable = presentMonPath;
        _session = "MehTools-" + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo { FileName = presentMonPath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--process_name", Path.GetFileName(gamePath), "--output_stdout", "--session_name", _session, "--no_track_input" }) start.ArgumentList.Add(arg);
        _process = Process.Start(start) ?? throw new InvalidOperationException("PresentMon could not be started.");
        _reader = Task.Run(ReadAsync);
    }

    private async Task ReadAsync()
    {
        var errors = _process.StandardError.ReadToEndAsync();
        try
        {
            string[]? header = null;
            int frame = -1, latency = -1, chain = -1, pid = -1;
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                var fields = ParseCsv(line);
                if (header is null)
                {
                    var names = fields.Select(s => s.Trim()).ToArray();
                    frame = Array.FindIndex(names, s => s is "FrameTime" or "MsBetweenAppStart" or "MsBetweenPresents");
                    latency = Array.FindIndex(names, s => s is "DisplayLatency" or "MsDisplayLatency");
                    chain = Array.IndexOf(names, "SwapChainAddress");
                    pid = Array.IndexOf(names, "ProcessID");
                    if (frame < 0) continue;
                    header = names;
                    continue;
                }
                if (frame >= fields.Length || !Number(fields[frame], out var milliseconds) || milliseconds <= 0) continue;
                double? display = latency >= 0 && latency < fields.Length && Number(fields[latency], out var value) && value >= 0 ? value : null;
                var key = (pid >= 0 && pid < fields.Length ? fields[pid] : "") + ":" + (chain >= 0 && chain < fields.Length ? fields[chain] : "");
                lock (_gate)
                {
                    if (!_frames.TryGetValue(key, out var aggregate))
                    {
                        if (_frames.Count >= 128) continue;
                        _frames[key] = aggregate = new Aggregate();
                    }
                    aggregate.Count++; aggregate.Total += milliseconds;
                    if (display is { } measured) { aggregate.LatencyTotal += measured; aggregate.LatencyCount++; }
                }
            }
            await _process.WaitForExitAsync().ConfigureAwait(false);
            var error = (await errors.ConfigureAwait(false)).Trim();
            lock (_gate) _status = _process.ExitCode == 0 ? "PresentMon stopped." : "PresentMon stopped: " + (error.Length > 180 ? error[..180] : error);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            lock (_gate) _status = "Frame capture stopped: " + ex.Message;
        }
    }

    public (double? Fps, double? Latency, string Status) Sample()
    {
        lock (_gate)
        {
            var best = _frames.Values.MaxBy(x => x.Count);
            _frames.Clear();
            return best is null ? (null, null, _status) : (1000 * best.Count / best.Total, best.LatencyCount > 0 ? best.LatencyTotal / best.LatencyCount : null, "Live game frames");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = CloseAsync();
    }

    private async Task CloseAsync()
    {
        // Stop only our uniquely named ETW session before falling back to killing the process.
        try
        {
            var stop = new ProcessStartInfo { FileName = _executable, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--terminate_existing_session", "--session_name", _session }) stop.ArgumentList.Add(arg);
            using var stopper = Process.Start(stop);
            if (stopper is not null)
            {
                try { await stopper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { try { stopper.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { } }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        try
        {
            if (!_process.HasExited)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { _process.Kill(entireProcessTree: true); }
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
        try { await _reader.ConfigureAwait(false); }
        finally { _process.Dispose(); }
    }

    private static bool Number(string value, out double number) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
    internal static string[] ParseCsv(string line)
    {
        var fields = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        return fields.ToArray();
    }
    private sealed class Aggregate { public int Count, LatencyCount; public double Total, LatencyTotal; }
}
