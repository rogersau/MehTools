using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using JamesTools.Core.Platform;

namespace JamesTools.Core.Diagnostics;

/// <summary>A short-lived, best-effort Windows performance recording.</summary>
public sealed partial class PerformanceLogger : IAsyncDisposable
{
    private readonly DiagnosticOptions _options;
    private readonly DiagnosticRawWriter _raw;
    private readonly HardFaultTrace _hardFaults;
    private readonly ProcessActivity _processActivity = new();
    private ProcessActivityRate? _io;
    private readonly Queue<DiagnosticHardwareSample> _hardware = new();
    private readonly Dictionary<double, List<DiagnosticHardwareSample>> _eventHardware = new();
    private readonly Dictionary<string, Statistics> _extended = new();
    private readonly IReadOnlyDictionary<string, string> _topology = CpuTopology.Read();
    private IReadOnlyDictionary<string, string> _sensorStatus = new Dictionary<string, string>();
    private double? _previousCpuSeconds, _cpuWindowStartSeconds;
    private double _collectionTotalMs, _collectionMaxMs;
    public string? RawDataDirectory => _raw.DirectoryPath;
    public string? RawExportError => _raw.Error;
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
    private readonly Statistics _gamePrivate = new(), _commitUsed = new(), _commitLimit = new();
    private readonly Statistics _busiestCore = new(), _pagesInput = new(), _diskRead = new(), _diskWrite = new(), _diskLatency = new();
    private readonly Statistics _gpuClock = new(), _gpuPowerLimit = new();
    private readonly Dictionary<string, Statistics> _cores = new();
    private readonly Queue<DiagnosticRow> _timeline = new();
    private const int TimelineLimit = 3600;
    private int _timelineOmitted, _gpuAttempts, _gpuFailures, _processMatches;
    private string _gpuStatus = "Not queried yet.", _settingsContext = "Not provided.", _symptoms = "Not provided.";
    private string? _driverVersion;
    private bool _extendedGpuQuery = true;
    private string? _samplingError;
    private readonly string _powerPlanAtStart = ReadPowerPlan();
    private readonly Dictionary<int, (long Start, TimeSpan Cpu)> _previousProcesses = new();
    private (ulong Idle, ulong Kernel, ulong User)? _previousCpu;
    private TimeSpan? _previousProcessTime;
    private ulong _totalMemory;
    private double? _vramTotal;
    private string? _gpuName;
    private int _samples;
    private readonly List<(TimeSpan Offset, string Note, string Context)> _markers = new();
    private string _lastContext = "";
    private readonly FrameReportCollector? _frames;
    private readonly NetworkProbeCollector? _network;
    private readonly CancellationTokenRegistration _captureCancellation;
    private string? _frameStatus;

    private PerformanceLogger(string? executablePath, string? presentMonPath, string? frameUnavailableReason, NetworkProbeOptions? networkOptions = null, CancellationToken captureCancellation = default, DiagnosticOptions? options = null)
    {
        _options = options?.Copy() ?? new(); _options.Validate();
                // Validate before starting any owned sampler/process.
        if (networkOptions is not null) networkOptions=NetworkProbeOptions.Create(networkOptions.Enabled,string.Join("\n",networkOptions.Targets));
        _executablePath = string.IsNullOrWhiteSpace(executablePath) ? null : Path.GetFullPath(executablePath);
        _raw = new DiagnosticRawWriter();
        _hardFaults = new HardFaultTrace(_options.AdvancedHardFaultTracing, _elapsed, _raw.Fault);
        if (!string.IsNullOrWhiteSpace(presentMonPath) && !string.IsNullOrWhiteSpace(_executablePath))
        {
            try { _frames = new FrameReportCollector(presentMonPath, _executablePath, _elapsed, _options, _raw.Frame); _frameStatus = "PresentMon capture requested."; }
            catch (Exception ex) { _frameStatus = "PresentMon unavailable (" + ex.GetType().Name + ")."; }
        }
        else _frameStatus = _executablePath is null
            ? string.IsNullOrWhiteSpace(frameUnavailableReason) ? "No game selected; frame capture not requested." : "Frame capture unavailable: " + frameUnavailableReason
            : "PresentMon not found or not configured.";
        if (networkOptions is { Enabled:true }) _network = new NetworkProbeCollector(networkOptions, new SystemNetworkProbeBackend(), new StopwatchNetworkClock(_elapsed), _stop.Token);
        _sampling = Task.Run(SampleLoopAsync);
        _captureCancellation = captureCancellation.Register(() => _stop.Cancel());
    }

    /// <summary>Starts sampling immediately. The optional path selects matching processes and is never included in the report.</summary>
    public static Task<PerformanceLogger> StartAsync(string? executablePath = null, CancellationToken cancellationToken = default)
        => StartAsync(executablePath, null, cancellationToken);

    public static Task<PerformanceLogger> StartAsync(string? executablePath, string? presentMonPath, CancellationToken cancellationToken = default)
        => StartAsync(executablePath, presentMonPath, null, cancellationToken);

    public static Task<PerformanceLogger> StartAsync(string? executablePath, string? presentMonPath,
        string? frameUnavailableReason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PerformanceLogger(executablePath, presentMonPath, frameUnavailableReason, captureCancellation: cancellationToken));
    }

    public static Task<PerformanceLogger> StartAsync(string? executablePath, string? presentMonPath,
        string? frameUnavailableReason, NetworkProbeOptions networkOptions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PerformanceLogger(executablePath, presentMonPath, frameUnavailableReason, networkOptions, cancellationToken));
    }

    public static Task<PerformanceLogger> StartAsync(string? executablePath, string? presentMonPath,
        string? frameUnavailableReason, NetworkProbeOptions networkOptions, DiagnosticOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.Validate();
        return Task.FromResult(new PerformanceLogger(executablePath, presentMonPath, frameUnavailableReason, networkOptions, cancellationToken, options));
    }

    public void AddMarker(string? note = null)
    {
        lock (_gate)
        {
            var text = string.IsNullOrWhiteSpace(note) ? "Marker" : note.Trim();
            if (text.Length > 200) text = text[..200];
            if (_markers.Count < 10000 && _completion is null) _markers.Add((_elapsed.Elapsed, text.Replace("\r", " ").Replace("\n", " "), _lastContext));
        }
    }

    /// <summary>Player-provided context, editable until the recording stops. Settings are not inferred from hardware readings.</summary>
    public void SetContext(string? settings, string? symptoms)
    {
        static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "Not provided." : value.Trim()[..Math.Min(value.Trim().Length, 4000)];
        lock (_gate) { _settingsContext = Clean(settings); _symptoms = Clean(symptoms); }
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

    /// <summary>Sampling failure, if the collector ended before an explicit stop.</summary>
    public string? SamplingError => _samplingError ?? _frames?.CaptureError;

    private async Task<string> StopCoreAsync()
    {
        _elapsed.Stop();
        _hardFaults.EndAt(_elapsed.Elapsed.TotalSeconds);
        _frames?.CompleteCapture();
        _stop.Cancel();
        try { await _sampling.ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        if (_frames is not null)
        {
            try { await _frames.StopAsync().ConfigureAwait(false); } catch { }
            var remaining = _frames.TakeInterval();
            if (remaining.Count > 0) AddTimeline(DiagnosticRow.ForFrames(remaining));
        }
        await _hardFaults.DisposeAsync().ConfigureAwait(false);
        if (_network is not null) await _network.DisposeAsync().ConfigureAwait(false);
        var report = BuildReport();
        _raw.Finish(report, BuildMachineSummaryJson(_frames?.GetSummary(), _frames?.GetDiagnostics(), BuildInvestigations(_frames?.GetDiagnostics())), _markers.Select(m => new ReportMarker(m.Offset.TotalSeconds, m.Note)).ToArray());
        if (_raw.Error is { } rawError) report += "\nRaw export incomplete: " + rawError + ". Saved report summary remains available.\n";
        _frames?.Dispose();
        _captureCancellation.Dispose();
        _stop.Dispose();
        return report;
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            using var counters = new DiagnosticCounters();
            using var nvidia = new NvidiaTelemetry();
            var nextGpuSample = TimeSpan.Zero;
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                foreach (var stats in SampleStatistics()) stats.ClearCurrent();
                var offset = _elapsed.Elapsed.TotalSeconds;
                var collectionWatch = Stopwatch.StartNew();
                var values = new Dictionary<string, double?>();
                double? gpuStart = null, gpuEnd = null;
                SampleWindows();
                _hardFaults.SetTargets(_previousProcesses);
                var extra = counters.Sample(offset);
                foreach (var core in extra.Cores)
                {
                    if (!_cores.TryGetValue(core.Name, out var stats)) _cores[core.Name] = stats = new Statistics();
                    stats.Add(core.Percent);
                    values["cpuUtilizationPercent:" + core.Name] = core.Percent;
                }
                if (extra.Cores.Count > 0) _busiestCore.Add(extra.Cores.Max(c => c.Percent));
                _pagesInput.Add(extra.PagesInputPerSecond);
                _diskRead.Add(extra.DiskReadMiBPerSecond);
                _diskWrite.Add(extra.DiskWriteMiBPerSecond);
                _diskLatency.Add(extra.DiskLatencyMs);
                var commit = DiagnosticCounters.ReadCommit();
                _commitUsed.Add(commit.UsedMiB);
                _commitLimit.Add(commit.LimitMiB);
                var processGpu = counters.ProcessGpuMemory(_previousProcesses.Keys.ToHashSet());
                values["gameDedicatedGpuMiB"] = processGpu.DedicatedMiB;
                values["gameSharedGpuMiB"] = processGpu.SharedMiB;
                foreach (var frequency in counters.Frequencies) values["cpuFrequencyMHz:" + frequency.Key] = frequency.Value;
                _samples++;
                if (_elapsed.Elapsed >= nextGpuSample)
                {
                    nextGpuSample = _elapsed.Elapsed + TimeSpan.FromSeconds(_options.FastGpuPolling ? 1 : 6);
                    gpuStart = _elapsed.Elapsed.TotalSeconds;
                    if (nvidia.Available)
                    {
                        _gpuAttempts++;
                        var readings = nvidia.Sample();
                        foreach (var reading in readings) values[reading.Key] = reading.Value;
                        _gpuName = nvidia.Name; _driverVersion = nvidia.Driver;
                        _gpu.Add(readings.GetValueOrDefault("gpuPercent")); _vramUsed.Add(readings.GetValueOrDefault("vramUsedMiB"));
                        _gpuPower.Add(readings.GetValueOrDefault("gpuPowerW")); _gpuTemperature.Add(readings.GetValueOrDefault("gpuTemperatureC"));
                        _gpuClock.Add(readings.GetValueOrDefault("gpuClockMHz")); _gpuPowerLimit.Add(readings.GetValueOrDefault("gpuPowerLimitW"));
                        _vramTotal = readings.GetValueOrDefault("vramCapacityMiB");
                        _sensorStatus = new Dictionary<string, string>(nvidia.Status);
                        if (_gpu.Current is null) _gpuFailures++;
                        _gpuStatus = "Direct NVML; individual sensors validated at runtime.";
                    }
                    else if (!_options.FastGpuPolling) await SampleNvidiaAsync(_stop.Token).ConfigureAwait(false);
                    else _gpuStatus = "Fast sampling unavailable: NVML could not initialise. Use normal mode for nvidia-smi fallback.";
                    gpuEnd = _elapsed.Elapsed.TotalSeconds;
                }
                values["cpuPercent"] = _cpu.Current; values["busiestCpuPercent"] = _busiestCore.Current;
                values["ramAvailableMiB"] = _memoryAvailable.Current; values["ramUsedMiB"] = _memoryUsed.Current;
                values["commitUsedMiB"] = _commitUsed.Current; values["commitLimitMiB"] = _commitLimit.Current;
                values["gameCpuPercent"] = _gameCpu.Current; values["gameWorkingSetMiB"] = _gameMemory.Current;
                values["gamePrivateMiB"] = _gamePrivate.Current;
                values["processReadMiBps"] = _io?.ReadMiBps; values["processWriteMiBps"] = _io?.WriteMiBps;
                values["processReadOpsPerSecond"] = _io?.ReadOperationsPerSecond; values["processWriteOpsPerSecond"] = _io?.WriteOperationsPerSecond;
                values["processPageFaultsPerSecond"] = _io?.PageFaultsPerSecond;
                values["pagesInputPerSecond"] = _pagesInput.Current; values["diskReadMiBps"] = _diskRead.Current; values["diskWriteMiBps"] = _diskWrite.Current;
                values["diskLatencyMs"] = _diskLatency.Current;
                if (gpuStart is not null)
                {
                    values["gpuPercent"] = _gpu.Current; values["vramUsedMiB"] = _vramUsed.Current;
                    values["gpuPowerW"] = _gpuPower.Current; values["gpuTemperatureC"] = _gpuTemperature.Current;
                    values["gpuClockMHz"] = _gpuClock.Current; values["gpuPowerLimitW"] = _gpuPowerLimit.Current;
                }
                foreach (var value in values)
                {
                    if (!_extended.TryGetValue(value.Key, out var stats)) _extended[value.Key] = stats = new();
                    stats.Add(value.Value);
                }
                var sample = new DiagnosticHardwareSample(offset, _elapsed.Elapsed.TotalSeconds, counters.WindowStartSeconds ?? offset, _io?.StartSeconds, gpuStart, gpuEnd, values) { CpuWindowStartSeconds = _cpuWindowStartSeconds, ProcessWindowEndSeconds = _io?.EndSeconds };
                if (_hardware.Count == TimelineLimit) _hardware.Dequeue();
                _hardware.Enqueue(sample); _raw.Hardware(sample);
                var eventOffsets = _frames?.EventOffsets() ?? [];
                foreach (var expired in _eventHardware.Keys.Except(eventOffsets).ToArray()) _eventHardware.Remove(expired);
                foreach (var eventOffset in eventOffsets)
                {
                    if (!_eventHardware.TryGetValue(eventOffset, out var context))
                        _eventHardware[eventOffset] = context = _hardware.Where(h => h.CollectionEndSeconds >= eventOffset - 5 && h.CounterWindowStartSeconds <= eventOffset + 5).ToList();
                    else if (sample.CollectionEndSeconds >= eventOffset - 5 && sample.CounterWindowStartSeconds <= eventOffset + 5 && context.Count < 32) context.Add(sample);
                }
                collectionWatch.Stop(); _collectionTotalMs += collectionWatch.Elapsed.TotalMilliseconds;
                _collectionMaxMs = Math.Max(_collectionMaxMs, collectionWatch.Elapsed.TotalMilliseconds);
                var frames = _frames?.TakeInterval();
                AddTimeline(new DiagnosticRow(offset, _elapsed.Elapsed.TotalSeconds, _cpu.Current, _busiestCore.Current,
                    _memoryAvailable.Current, _memoryUsed.Current, _commitUsed.Current, _commitLimit.Current, _gameCpu.Current,
                    _gameMemory.Current, _gamePrivate.Current, _executablePath is null ? null : _processMatches, _gpu.Current, _vramUsed.Current,
                    _gpuPower.Current, _gpuTemperature.Current, _gpuClock.Current, _pagesInput.Current,
                    _diskRead.Current, _diskWrite.Current, _diskLatency.Current, frames));
                lock (_gate) _lastContext = string.Create(CultureInfo.InvariantCulture,
                    $"last sample at {offset:F1}s; CPU {_cpu.LastValueText("%")}; Mem used {_memoryUsed.LastValueText("MiB")}; GPU {_gpu.LastValueText("%")}");
                await Task.Delay(TimeSpan.FromSeconds(_options.FastGpuPolling ? 1 : 2), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _samplingError = "Sampling ended early (" + ex.GetType().Name + "). Measurements after the last timeline row are unavailable."; }
    }

    private IEnumerable<Statistics> SampleStatistics() => new[] { _cpu, _memoryUsed, _memoryAvailable, _gameCpu, _gameMemory,
        _gamePrivate, _commitUsed, _commitLimit, _busiestCore, _pagesInput, _diskRead, _diskWrite, _diskLatency,
        _gpu, _vramUsed, _gpuPower, _gpuTemperature, _gpuClock, _gpuPowerLimit };

    private void AddTimeline(DiagnosticRow row)
    {
        if (_timeline.Count == TimelineLimit) { _timeline.Dequeue(); _timelineOmitted++; }
        _timeline.Enqueue(row);
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
            _cpuWindowStartSeconds = _previousCpuSeconds;
            _previousCpuSeconds = _elapsed.Elapsed.TotalSeconds;
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
        _processMatches = 0;
        var now = _elapsed.Elapsed;
        var current = new Dictionary<int, (long Start, TimeSpan Cpu)>();
        double memory = 0, privateMemory = 0, cpuMilliseconds = 0;
        int matched = 0, comparable = 0;
        var activity = new List<ProcessActivityCounters>();
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
                    var workingSet = process.WorkingSet64 / 1048576.0;
                    var privateBytes = process.PrivateMemorySize64 / 1048576.0;
                    current[process.Id] = (start, cpu);
                    memory += workingSet;
                    privateMemory += privateBytes;
                    matched++;
                    try { activity.Add(ProcessActivity.Read(process, start, now.TotalSeconds)); }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { activity.Add(new(process.Id, start, now.TotalSeconds, null, null, null, null, null)); }
                    if (_previousProcesses.TryGetValue(process.Id, out var prior) && prior.Start == start && cpu >= prior.Cpu)
                    {
                        cpuMilliseconds += (cpu - prior.Cpu).TotalMilliseconds;
                        comparable++;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        _io = _processActivity.Update(activity, now.TotalSeconds);
        _processMatches = matched;
        if (matched > 0) { _gameMemory.Add(memory); _gamePrivate.Add(privateMemory); }
        if (comparable > 0 && _previousProcessTime is { } last && (now - last).TotalMilliseconds > 0)
            _gameCpu.Add(Math.Clamp(100 * cpuMilliseconds / (now - last).TotalMilliseconds / Environment.ProcessorCount, 0, 100));
        _previousProcesses.Clear();
        foreach (var entry in current) _previousProcesses.Add(entry.Key, entry.Value);
        _previousProcessTime = now;
    }

    private async Task SampleNvidiaAsync(CancellationToken cancellationToken)
    {
        _gpuAttempts++;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = NvidiaSmiPath(),
            Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total,power.draw,temperature.gpu" +
                (_extendedGpuQuery ? ",driver_version,clocks.current.graphics,power.limit" : "") + " --format=csv,noheader,nounits",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }};
        var started = false;
        try
        {
            if (!process.Start()) { _gpuFailures++; _gpuStatus = "nvidia-smi did not start."; return; }
            started = true;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _gpuFailures++;
                _gpuStatus = "nvidia-smi query failed; basic metrics will be retried.";
                _extendedGpuQuery = false;
                return;
            }
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var fields = line?.Split(',');
            if (fields is not { Length: >= 6 }) { _gpuFailures++; _gpuStatus = "Unrecognized nvidia-smi output."; return; }
            _gpuName = fields[0].Trim();
            AddNumber(fields[1], _gpu);
            AddNumber(fields[2], _vramUsed);
            if (TryNumber(fields[3], out var total)) _vramTotal = total;
            AddNumber(fields[4], _gpuPower);
            AddNumber(fields[5], _gpuTemperature);
            if (fields.Length >= 9)
            {
                _driverVersion = fields[6].Trim();
                AddNumber(fields[7], _gpuClock);
                AddNumber(fields[8], _gpuPowerLimit);
            }
            _gpuStatus = "Query succeeded; unsupported individual values remain unavailable.";
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _gpuFailures++;
            _gpuStatus = "nvidia-smi query timed out (2 seconds).";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { _gpuFailures++; _gpuStatus = "nvidia-smi unavailable (" + ex.GetType().Name + ")."; }
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

    private static string GetCpuName()
    {
        try { using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"); return key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "unavailable"; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return "unavailable"; }
    }
    private static void Append(StringBuilder text, string label, Statistics stats, string unit) => text.AppendLine($"- {label}: {stats.Format(unit)}");
    private static void AddNumber(string value, Statistics stats) { if (TryNumber(value, out var number)) stats.Add(number); }
    private static bool TryNumber(string value, out double number) => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number) && number >= 0;
    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    internal sealed class Statistics
    {
        private double _min = double.PositiveInfinity, _max = double.NegativeInfinity, _sum, _last;
        private int _count, _atOrAbove90;
        public int Count => _count;
        public double Min => _min;
        public double Max => _max;
        public double Average => _count == 0 ? 0 : _sum / _count;
        public double? Current { get; private set; }
        public double? First { get; private set; }
        public double? Last => _count == 0 ? null : _last;
        public void ClearCurrent() => Current = null;
        public void Add(double? value) { if (value is { } number) Add(number); }
        public void Add(double value)
        {
            if (!double.IsFinite(value) || value < 0) return;
            First ??= value;
            Current = value;
            _last = value; _min = Math.Min(_min, value); _max = Math.Max(_max, value); _sum += value; _count++; if (value >= 90) _atOrAbove90++;
        }
        public string Format(string unit) => _count == 0 ? "unavailable" : string.Create(CultureInfo.InvariantCulture, $"{_min:F1} / {Average:F1} / {_max:F1} {unit} ({_count} readings)");
        public string FractionAtOrAbove90() => _count == 0 ? "unavailable (0 valid readings)" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * _atOrAbove90 / _count:F1}% ({_atOrAbove90} of {_count} valid readings)");
        public string LastValueText(string unit) => Current is { } value ? string.Create(CultureInfo.InvariantCulture, $"{value:F1}{unit}") : "n/a";
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
