using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using JamesTools.Core.Platform;

namespace JamesTools.Core.Diagnostics;

/// <summary>A short-lived, best-effort Windows performance recording.</summary>
public sealed class PerformanceLogger : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly string? _executablePath;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Task _sampling;
    private Task<string>? _completion;
    private readonly Statistics _cpu = new(), _memoryUsed = new(), _memoryAvailable = new();
    private readonly Statistics _gpu = new(), _vramUsed = new(), _gpuPower = new(), _gpuTemperature = new();
    private readonly Statistics _gameCpu = new(), _gameMemory = new();
    private readonly Dictionary<int, (long Start, TimeSpan Cpu)> _previousProcesses = new();
    private (ulong Idle, ulong Kernel, ulong User)? _previousCpu;
    private TimeSpan? _previousProcessTime;
    private ulong _totalMemory;
    private double? _vramTotal;
    private string? _gpuName;
    private int _samples;

    private PerformanceLogger(string? executablePath)
    {
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFullPath(executablePath);
        _sampling = Task.Run(SampleLoopAsync);
    }

    /// <summary>Starts sampling immediately. The optional path selects matching processes and is never included in the report.</summary>
    public static Task<PerformanceLogger> StartAsync(string? executablePath = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PerformanceLogger(executablePath));
    }

    /// <summary>Stops sampling and returns a portable Markdown report. Repeated calls return the same report.</summary>
    public Task<string> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<string> completion;
        lock (_gate)
        {
            _completion ??= StopCoreAsync();
            completion = _completion;
        }
        return cancellationToken.CanBeCanceled ? completion.WaitAsync(cancellationToken) : completion;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task<string> StopCoreAsync()
    {
        _stop.Cancel();
        try { await _sampling.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        _elapsed.Stop();
        var report = BuildReport();
        _stop.Dispose();
        return report;
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            var nextGpuSample = TimeSpan.Zero;
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                SampleWindows();
                _samples++;
                if (_elapsed.Elapsed >= nextGpuSample)
                {
                    nextGpuSample = _elapsed.Elapsed + TimeSpan.FromSeconds(6);
                    await SampleNvidiaAsync(_stop.Token).ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void SampleWindows()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var current = (Idle: ToUInt64(idle), Kernel: ToUInt64(kernel), User: ToUInt64(user));
            if (_previousCpu is { } previous && current.Kernel >= previous.Kernel && current.User >= previous.User && current.Idle >= previous.Idle)
            {
                var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
                if (total > 0) _cpu.Add(Math.Clamp(100.0 * (1.0 - (double)(current.Idle - previous.Idle) / total), 0, 100));
            }
            _previousCpu = current;
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            _totalMemory = memory.TotalPhysical;
            _memoryAvailable.Add(memory.AvailablePhysical / 1048576.0);
            _memoryUsed.Add((memory.TotalPhysical - memory.AvailablePhysical) / 1048576.0);
        }
        if (_executablePath != null) SampleProcesses();
    }

    private void SampleProcesses()
    {
        var now = _elapsed.Elapsed;
        var current = new Dictionary<int, (long Start, TimeSpan Cpu)>();
        double memory = 0, cpuMilliseconds = 0;
        int matched = 0, comparable = 0;
        Process[] processes;
        try { processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_executablePath!)); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return; }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!string.Equals(ProcessSnapshot.GetImagePath(process.Id), _executablePath, StringComparison.OrdinalIgnoreCase)) continue;
                    var start = process.StartTime.ToUniversalTime().Ticks;
                    var cpu = process.TotalProcessorTime;
                    current[process.Id] = (start, cpu);
                    memory += process.WorkingSet64 / 1048576.0;
                    matched++;
                    if (_previousProcesses.TryGetValue(process.Id, out var prior) && prior.Start == start && cpu >= prior.Cpu)
                    {
                        cpuMilliseconds += (cpu - prior.Cpu).TotalMilliseconds;
                        comparable++;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        if (matched > 0) _gameMemory.Add(memory);
        if (comparable > 0 && _previousProcessTime is { } last && (now - last).TotalMilliseconds > 0)
            _gameCpu.Add(Math.Clamp(100 * cpuMilliseconds / (now - last).TotalMilliseconds / Environment.ProcessorCount, 0, 100));
        _previousProcesses.Clear();
        foreach (var entry in current) _previousProcesses.Add(entry.Key, entry.Value);
        _previousProcessTime = now;
    }

    private async Task SampleNvidiaAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = NvidiaSmiPath(),
            Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu --format=csv,noheader,nounits",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        var started = false;
        try
        {
            if (!process.Start()) return;
            started = true;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0) return;
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var fields = line?.Split(',');
            if (fields is not { Length: >= 6 }) return;
            _gpuName = fields[0].Trim();
            AddNumber(fields[1], _gpu);
            AddNumber(fields[2], _vramUsed);
            if (TryNumber(fields[3], out var total)) _vramTotal = total;
            AddNumber(fields[4], _gpuPower);
            AddNumber(fields[5], _gpuTemperature);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException) { }
        finally
        {
            if (started)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or Win32Exception) { }
            }
        }
    }

    private static string NvidiaSmiPath()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(system)) return system;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
    }

    private string BuildReport()
    {
        var text = new StringBuilder();
        text.AppendLine("# Performance recording");
        text.AppendLine($"- Started (UTC): {_started:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine($"- Duration: {_elapsed.Elapsed:c}; system samples: {_samples}; target system interval: 2 seconds plus GPU query time; NVIDIA polling: no more often than every 6 seconds.");
        text.AppendLine($"- OS: {Environment.OSVersion.VersionString}; logical processors: {Environment.ProcessorCount}");
        text.AppendLine($"- CPU: {GetCpuName()}");
        text.AppendLine($"- Physical memory: {(_totalMemory > 0 ? (_totalMemory / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture) + " GiB" : "unavailable")}");
        text.AppendLine($"- NVIDIA GPU: {_gpuName ?? "unavailable or not detected"}");
        if (_vramTotal is { } vram) text.AppendLine($"- GPU memory: {vram.ToString("F0", CultureInfo.InvariantCulture)} MiB");
        if (_executablePath != null) text.AppendLine($"- Selected executable: {Path.GetFileName(_executablePath)}");
        text.AppendLine().AppendLine("## Measurements");
        text.AppendLine("Values are minimum / average / maximum, followed by the number of readings.");
        Append(text, "Total CPU", _cpu, "%");
        Append(text, "Physical memory used", _memoryUsed, "MiB");
        Append(text, "Physical memory available", _memoryAvailable, "MiB");
        Append(text, "GPU utilization", _gpu, "%");
        text.AppendLine($"- GPU utilization at or above 90%: {_gpu.FractionAtOrAbove90()} (fraction of valid readings, not fraction of elapsed time)");
        Append(text, "GPU memory used", _vramUsed, "MiB");
        Append(text, "GPU power", _gpuPower, "W");
        Append(text, "GPU temperature", _gpuTemperature, "°C");
        if (_executablePath != null)
        {
            Append(text, "Selected processes CPU (share of total system capacity)", _gameCpu, "%");
            Append(text, "Selected processes working set", _gameMemory, "MiB");
        }
        text.AppendLine().AppendLine("## Limitations");
        text.AppendLine("- FPS, frame times, stutter and input latency are not measured. These summaries cannot establish the cause of a performance issue.");
        text.AppendLine("- CPU is system-wide, averaged across logical processors; per-core utilization, clock speeds, and throttling are not measured. Short spikes may be missed.");
        text.AppendLine("- GPU readings require NVIDIA's nvidia-smi and may be unsupported or unavailable. Only its first reported GPU is sampled; other GPU vendors are not measured.");
        text.AppendLine("- Process readings cover accessible running processes whose executable path matches the selection. They exclude child processes with different executables. Missing readings do not mean zero usage. Working set is resident memory, not total allocation.");
        text.AppendLine("- Sampling is periodic, values can be incomplete, and averages are unweighted averages of available readings. No raw timeline is included.");
        text.AppendLine().AppendLine("## Prompt for analysis");
        text.AppendLine("Please review this recording for possible performance constraints. Explain uncertainty and distinguish evidence from hypotheses. Suggest tests to confirm or disprove each hypothesis, then prioritize practical settings changes and possible hardware upgrades, with trade-offs and only where supported by the evidence. Ask for missing game settings or measurements. Do not assert a bottleneck from these summaries alone; FPS and frame times were not measured.");
        return text.ToString();
    }

    private static string GetCpuName()
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"); return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unavailable"; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return "unavailable"; }
    }
    private static void Append(StringBuilder text, string label, Statistics stats, string unit) => text.AppendLine($"- {label}: {stats.Format(unit)}");
    private static void AddNumber(string value, Statistics stats) { if (TryNumber(value, out var number)) stats.Add(number); }
    private static bool TryNumber(string value, out double number) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0;
    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    private sealed class Statistics
    {
        private double _min = double.PositiveInfinity, _max = double.NegativeInfinity, _sum;
        private int _count, _atOrAbove90;
        public void Add(double value) { _min = Math.Min(_min, value); _max = Math.Max(_max, value); _sum += value; _count++; if (value >= 90) _atOrAbove90++; }
        public string Format(string unit) => _count == 0 ? "unavailable" : string.Create(CultureInfo.InvariantCulture, $"{_min:F1} / {_sum / _count:F1} / {_max:F1} {unit} ({_count} readings)");
        public string FractionAtOrAbove90() => _count == 0 ? "unavailable (0 valid readings)" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * _atOrAbove90 / _count:F1}% ({_atOrAbove90} of {_count} valid readings)");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle, out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
