using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using JamesTools.Core.Diagnostics;

namespace JamesTools.Core.Mcp;

/// <summary>Read-only local Windows diagnostics. Each native collector has at most one outstanding worker.</summary>
public sealed class WindowsDiagnosticBackend : IDiagnosticBackend
{
    private static readonly string[] Categories = ["cpu", "gpu", "memory", "storage", "network", "thermals", "drivers", "windows"];
    private static readonly string[] Profiles = ["general", "gaming", "cpu", "gpu", "storage"];
    private readonly object _gate = new();
    private readonly string _directory;
    private Task<SystemInventory>? _inventory;
    private Task<GameCatalog>? _catalog;
    private Task<MetricsSnapshot>? _sample;
    private Task<GameInspection>? _inspection;
    private Task<EventLogResult>? _events;
    private NvidiaTelemetry? _nvidia;
    private LiveHardwareSampler? _hardware;
    private DiagnosticCounters? _counters;
    private (long Received, long Sent, long Ticks, string Adapters)? _networkPrevious;
    private bool _disposed;

    public WindowsDiagnosticBackend(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        _directory = GameInspectionReader.NormalizeRoot(appDataDirectory)
            ?? throw new ArgumentException("App data must be an absolute local directory without traversal or alternate streams.", nameof(appDataDirectory));
    }

    public DiagnosticCapabilities GetCapabilities() => new(1, Categories.ToArray(), Profiles.ToArray(),
    [
        "Read-only observations; no stress tests, repairs, arbitrary commands, remote hosts or arbitrary file reads.",
        "CPU temperature/package power and GPU hotspot temperature are unsupported; NVML exposes only the first NVIDIA GPU.",
        "Inventory and discovered game roots are cached for this backend lifetime. Restart the backend to refresh.",
        "PDH and CPU/network rate counters require an earlier sample; null never means zero.",
        "Game scans inspect installed metadata and file existence only; runtime activation is unknown.",
        "Events use allowlisted System/Application providers and redacted summaries, at most 200 records across seven days.",
        "Native calls cannot always be interrupted. Caller waits are bounded and collectors never queue duplicate workers.",
        "Health reports express evidence and limitations; they do not calculate a health score or diagnose from a single sample."
    ]);

    public async Task<SystemInventory> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<SystemInventory> task;
        lock (_gate) { ThrowIfDisposed(); task = _inventory ??= Task.Run(WindowsInventory.Read); }
        var sampleTask = SampleAsync(cancellationToken);
        SystemInventory inventory;
        try { inventory = await task.WaitAsync(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { inventory = new(1, DateTimeOffset.UtcNow, [], ["unavailable: Windows inventory exceeded its response budget; the single cached collector remains outstanding."]); }
        return CombineInventory(inventory, await sampleTask.ConfigureAwait(false));
    }

    internal static SystemInventory CombineInventory(SystemInventory inventory, MetricsSnapshot snapshot)
    {
        var components = inventory.Components.ToList();
        var total = snapshot.Metrics.FirstOrDefault(m => m.Name == "memory.physical_total");
        var used = snapshot.Metrics.FirstOrDefault(m => m.Name == "memory.physical_used");
        var memoryAvailable = total is { Status: "available", Value: { } capacity } && used is { Status: "available", Value: { } consumed }
            && double.IsFinite(capacity) && double.IsFinite(consumed) && consumed >= 0 && capacity >= consumed;
        string? Number(double? value) => value?.ToString(CultureInfo.InvariantCulture);
        components.Add(new("memory", "Current physical memory", new Dictionary<string, string?>
        {
            ["totalMiB"] = memoryAvailable ? Number(total!.Value) : null,
            ["availableMiB"] = memoryAvailable ? Number(total!.Value - used!.Value) : null,
            ["usedMiB"] = memoryAvailable ? Number(used!.Value) : null,
            ["observedAtUtc"] = snapshot.TimestampUtc.ToString("O"),
            ["source"] = "GlobalMemoryStatusEx",
            ["scope"] = "dynamic; refreshed per inventory request"
        }, new(memoryAvailable ? "available" : total is { Status: not "available" } ? total.Status : used is { Status: not "available" } ? used.Status : "unavailable",
            memoryAvailable ? null : "Physical memory observation unavailable.")));
        components.Add(new("inventory", "Static inventory collection", new Dictionary<string, string?>
        {
            ["observedAtUtc"] = inventory.TimestampUtc.ToString("O"),
            ["scope"] = "static WMI and CPU capabilities; cached for backend lifetime",
            ["refresh"] = "Restart backend to refresh static components. Dynamic memory and monitoring have separate timestamps."
        }, new(inventory.Components.Count > 0 ? "available" : "unavailable")));
        string[] monitored = ["cpu.utilization", "cpu.frequency", "cpu.temperature", "cpu.package_power", "gpu.utilization", "gpu.temperature",
            "gpu.memory_temperature", "gpu.hotspot_temperature", "gpu.power", "memory.physical_total", "storage.transfer_latency", "network.receive"];
        foreach (var name in monitored)
        {
            var metric = snapshot.Metrics.FirstOrDefault(m => m.Name == name);
            components.Add(new("monitoring", name, new Dictionary<string, string?>
            {
                ["source"] = metric?.Source, ["unit"] = metric?.Unit,
                ["observedAtUtc"] = (metric?.TimestampUtc ?? snapshot.TimestampUtc).ToString("O"),
                ["scope"] = "dynamic; provider availability for this observation"
            }, new(metric?.Status ?? "unavailable", metric?.Reason ?? (metric is null ? "Collector returned no observation." : null))));
        }
        return new(1, DateTimeOffset.UtcNow, components, inventory.Warnings.Concat(snapshot.Warnings)
            .Append("Response time is current; static WMI components and dynamic memory/monitoring carry separate collection timestamps.").ToArray());
    }

    public async Task<MetricsSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<MetricsSnapshot> task;
        lock (_gate)
        {
            ThrowIfDisposed();
            task = _sample is { IsCompleted: false } ? _sample : _sample = Task.Run(SampleCore);
        }
        try { return await task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(1, DateTimeOffset.UtcNow, [], ["unavailable: Native sampling exceeded its response budget; no additional worker was started."]); }
    }

    private MetricsSnapshot SampleCore()
    {
        var now = DateTimeOffset.UtcNow;
        var metrics = new List<Metric>();
        var warnings = new List<string>();
        void Add(string name, double? value, string unit, string source, string? reason = null, string status = "unavailable")
        {
            if (value is { } number && !double.IsFinite(number)) value = null;
            metrics.Add(new(name, value, unit, now, value.HasValue ? "available" : status, source, value.HasValue ? null : reason ?? "No valid observation; rate counters may require warm-up."));
        }
        try
        {
            _nvidia ??= new NvidiaTelemetry();
            _hardware ??= new LiveHardwareSampler(_nvidia); // Shared NVML owner; initialized exactly once per backend.
            var hardware = _hardware.Sample();
            Add("cpu.utilization", hardware.Cpu, "%", "GetSystemTimes");
            var values = _nvidia.Sample();
            var fields = new (string Key, string Name, string Unit)[]
            {
                ("gpuPercent", "gpu.utilization", "%"), ("gpuPowerW", "gpu.power", "W"),
                ("gpuPowerLimitW", "gpu.power_limit", "W"), ("gpuConfiguredPowerLimitW", "gpu.configured_power_limit", "W"),
                ("gpuPowerPercentOfLimit", "gpu.power_percent_of_limit", "%"),
                ("gpuTemperatureC", "gpu.temperature", "°C"), ("gpuMemoryTemperatureC", "gpu.memory_temperature", "°C"),
                ("vramUsedMiB", "gpu.memory_used", "MiB"), ("vramCapacityMiB", "gpu.memory_total", "MiB"),
                ("gpuClockMHz", "gpu.clock", "MHz"), ("gpuMemoryClockMHz", "gpu.memory_clock", "MHz"),
                ("gpuFanPercent", "gpu.fan", "%"), ("gpuFanRpm", "gpu.fan_speed", "rpm"), ("gpuPState", "gpu.performance_state", "state"),
                ("gpuThermalLimited", "gpu.thermal_limited", "boolean"), ("gpuPowerLimited", "gpu.power_limited", "boolean")
            };
            foreach (var field in fields)
            {
                var detail = _nvidia.Status.GetValueOrDefault(field.Key);
                var status = detail?.EndsWith("status 4", StringComparison.Ordinal) == true ? "permission_denied" :
                    detail?.Contains("export absent", StringComparison.Ordinal) == true || detail?.EndsWith("status 3", StringComparison.Ordinal) == true ? "unsupported" : "unavailable";
                Add(field.Name, values.GetValueOrDefault(field.Key), field.Unit, "NVML GPU index 0",
                    _nvidia.Available ? detail ?? "NVML did not return this sensor; memory temperature is not a guaranteed junction sensor." : "NVIDIA NVML device unavailable.", status);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception)
        { warnings.Add("Hardware sampling: " + WindowsBackendSupport.Status(ex)); }
        Add("cpu.temperature", null, "°C", "Windows", "No supported CPU thermal provider configured.", "unsupported");
        Add("cpu.package_power", null, "W", "Windows", "No supported CPU package power provider configured.", "unsupported");
        Add("gpu.hotspot_temperature", null, "°C", "NVML", "NVML memory temperature is not GPU hotspot temperature.", "unsupported");
        try
        {
            _counters ??= new DiagnosticCounters();
            var counter = _counters.Sample();
            var frequencies = _counters.Frequencies.Values.OfType<double>().Where(double.IsFinite).ToArray();
            Add("cpu.frequency", frequencies.Length > 0 ? frequencies.Average() : null, "MHz", "PDH logical processor mean");
            // Aggregate metrics stay bounded independently of CPU count; static topology is in inventory.
            Add("memory.pages_input", counter.PagesInputPerSecond, "pages/s", "PDH");
            Add("storage.read", counter.DiskReadMiBPerSecond, "MiB/s", "PDH PhysicalDisk total");
            Add("storage.write", counter.DiskWriteMiBPerSecond, "MiB/s", "PDH PhysicalDisk total");
            Add("storage.transfer_latency", counter.DiskLatencyMs, "ms", "PDH PhysicalDisk total");
            var commit = DiagnosticCounters.ReadCommit();
            Add("memory.commit_used", commit.UsedMiB, "MiB", "GetPerformanceInfo");
            Add("memory.commit_limit", commit.LimitMiB, "MiB", "GetPerformanceInfo");
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            var valid = GlobalMemoryStatusEx(ref memory);
            Add("memory.physical_used", valid ? (memory.Total - memory.Available) / 1048576.0 : null, "MiB", "GlobalMemoryStatusEx");
            Add("memory.physical_total", valid ? memory.Total / 1048576.0 : null, "MiB", "GlobalMemoryStatusEx");
            Add("memory.physical_load", valid ? memory.Load : null, "%", "GlobalMemoryStatusEx");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        { warnings.Add("Windows counters: " + WindowsBackendSupport.Status(ex)); }
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Take(32))
            {
                var key = "storage.drive_" + drive.Name[0];
                try
                {
                    Add(key + ".free", drive.AvailableFreeSpace / 1073741824.0, "GiB", "GetDiskFreeSpaceEx");
                    Add(key + ".capacity", drive.TotalSize / 1073741824.0, "GiB", "GetDiskFreeSpaceEx");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { Add(key + ".free", null, "GiB", "GetDiskFreeSpaceEx", "Volume capacity unavailable.", WindowsBackendSupport.Status(ex)); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { warnings.Add("Volume capacity: " + WindowsBackendSupport.Status(ex)); }
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)).Take(128).ToArray();
            long received = 0, sent = 0;
            foreach (var adapter in adapters)
            {
                var statistics = adapter.GetIPStatistics();
                received = checked(received + statistics.BytesReceived); sent = checked(sent + statistics.BytesSent);
            }
            var ticks = Stopwatch.GetTimestamp();
            var ids = string.Join('|', adapters.Select(n => n.Id).Order(StringComparer.Ordinal)); // Internal only, never returned.
            double? rx = null, tx = null;
            if (_networkPrevious is { } previous && ids == previous.Adapters && received >= previous.Received && sent >= previous.Sent && ticks > previous.Ticks && adapters.Length > 0)
            {
                var seconds = (ticks - previous.Ticks) / (double)Stopwatch.Frequency;
                rx = (received - previous.Received) / seconds; tx = (sent - previous.Sent) / seconds;
            }
            _networkPrevious = (received, sent, ticks, ids);
            Add("network.receive", rx, "bytes/s", "NetworkInterface IP statistics total");
            Add("network.send", tx, "bytes/s", "NetworkInterface IP statistics total");
            Add("network.active_adapters", adapters.Length, "count", "NetworkInterface");
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or OverflowException)
        {
            _networkPrevious = null;
            Add("network.receive", null, "bytes/s", "NetworkInterface", "Adapter statistics unavailable.", WindowsBackendSupport.Status(ex));
            Add("network.send", null, "bytes/s", "NetworkInterface", "Adapter statistics unavailable.", WindowsBackendSupport.Status(ex));
        }
        warnings.Add("Instantaneous observations do not establish a bottleneck or hardware health. Network totals can include virtual adapters; no network probes were sent.");
        return new(1, now, Prioritize(metrics), warnings);
    }

    // Consumers may retain a bounded prefix. Keep observations from every category before optional detail.
    internal static IReadOnlyList<Metric> Prioritize(IReadOnlyList<Metric> metrics)
    {
        string[] primary = ["cpu.utilization", "gpu.utilization", "memory.physical_load", "storage.transfer_latency", "network.receive", "network.send",
            "gpu.temperature", "gpu.power", "gpu.memory_used", "gpu.thermal_limited", "memory.physical_used", "memory.physical_total",
            "memory.commit_used", "memory.commit_limit", "memory.pages_input", "storage.read", "storage.write", "cpu.frequency",
            "cpu.temperature", "cpu.package_power", "gpu.hotspot_temperature", "gpu.memory_temperature", "gpu.power_limited", "gpu.memory_total"];
        var order = primary.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        return metrics.OrderBy(m => order.GetValueOrDefault(m.Name, int.MaxValue)).ToArray();
    }

    public async Task<DiagnosticResult> RunDiagnosticsAsync(string category, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) ThrowIfDisposed();
        category = category?.ToLowerInvariant() ?? "";
        if (!Categories.Contains(category, StringComparer.Ordinal)) return new(1, DateTimeOffset.UtcNow, category, "unsupported", [], [], [], ["Unsupported diagnostic category."]);
        var snapshotTask = SampleAsync(cancellationToken);
        var inventoryTask = GetSystemInfoAsync(cancellationToken);
        await Task.WhenAll(snapshotTask, inventoryTask).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var inventory = await inventoryTask.ConfigureAwait(false);
        var measurements = snapshot.Metrics.Where(m => category == "thermals" ? m.Name.Contains("temperature", StringComparison.Ordinal) || m.Name == "gpu.thermal_limited" : m.Name.StartsWith(category + ".", StringComparison.Ordinal)).ToArray();
        var findings = Evaluate(snapshot).Where(f => category == "thermals" ? f.Component == "gpu" : f.Component == category).ToList();
        foreach (var component in inventory.Components.Where(c => c.Category == category).Take(256))
            findings.Add(new(category, "information", component.Name + ": " + component.Availability.Status,
                "Static inventory observation; no stress test or repair performed.", "observed",
                component.Properties.Where(p => p.Value is not null).Select(p => p.Key + "=" + p.Value).Take(20).ToArray(), []));
        var available = measurements.Any(m => m.Status == "available") || inventory.Components.Any(c => c.Category == category && c.Availability.Status == "available");
        var status = available ? measurements.Any(m => m.Status != "available") || inventory.Components.Any(c => c.Category == category && c.Availability.Status != "available") ? "partial" : "available" :
            measurements.Length > 0 && measurements.All(m => m.Status == "unsupported") ? "unsupported" :
            inventory.Components.FirstOrDefault(c => c.Category == category)?.Availability.Status ?? "unavailable";
        return new(1, DateTimeOffset.UtcNow, category, status, findings, measurements,
            snapshot.Warnings.Concat(inventory.Warnings).Append("Observed data only; driver versions are not checked against a remote latest-version catalog.").ToArray(), []);
    }

    public async Task<GameInspection> InspectGameAsync(string game, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<GameCatalog> catalog;
        lock (_gate) { ThrowIfDisposed(); catalog = _catalog ??= Task.Run(() => GameInspectionReader.Discover(_directory)); }
        GameCatalog discovered;
        try { discovered = await catalog.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(1, DateTimeOffset.UtcNow, "unavailable", null, null, [], ["Game discovery exceeded its response budget; one cached collector remains outstanding."]); }
        Task<GameInspection> task;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_inspection is { IsCompleted: false }) return new(1, DateTimeOffset.UtcNow, "unavailable", null, null, [], ["A game inspection is already outstanding."]);
            task = _inspection = Task.Run(() => GameInspectionReader.Inspect(game, discovered, cancellationToken));
        }
        try { return await task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(1, DateTimeOffset.UtcNow, "unavailable", null, null, [], ["Game scan exceeded its response budget; no additional scan was started."]); }
    }

    public async Task<EventLogResult> GetEventLogsAsync(EventLogFilter filter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) ThrowIfDisposed();
        try { WindowsEventLogs.Validate(filter, DateTimeOffset.UtcNow); }
        catch (ArgumentException ex) { return new(1, DateTimeOffset.UtcNow, "unsupported", [], false, [ex.Message]); }
        filter = filter with { EventIds = filter.EventIds?.ToArray() };
        Task<EventLogResult> task;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_events is { IsCompleted: false }) return new(1, DateTimeOffset.UtcNow, "unavailable", [], false, ["An event query is already outstanding."]);
            task = _events = Task.Run(() => WindowsEventLogs.Read(filter, cancellationToken));
        }
        try { return await task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return new(1, DateTimeOffset.UtcNow, "unavailable", [], true, ["Event query exceeded its response budget; no additional query was started."]); }
    }

    public async Task<HealthReport> GetHealthReportAsync(CancellationToken cancellationToken)
    {
        var snapshotTask = SampleAsync(cancellationToken);
        var eventsTask = GetEventLogsAsync(new(FromUtc: DateTimeOffset.UtcNow.AddHours(-24), Limit: 20), cancellationToken);
        await Task.WhenAll(snapshotTask, eventsTask).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var events = await eventsTask.ConfigureAwait(false);
        var findings = Evaluate(snapshot).ToList();
        foreach (var entry in events.Events.Where(e => e.Severity is "critical" or "error").Take(10))
            findings.Add(new("windows", "warning", entry.Summary, "An event is evidence of a reported condition, not proof of current failure or causation.", "observed",
                [$"{entry.TimestampUtc:O}; {entry.Source}; ID {entry.EventId}; {entry.Severity}"], ["Correlate the event time with the reported symptom."]));
        var limitations = snapshot.Warnings.Concat(events.Warnings).Concat(GetCapabilities().Limitations)
            .Append("Recent event query status: " + events.Status + ". Absence of findings does not establish a healthy system.")
            .Append("Existing performance-history reports are not silently treated as current health evidence; captured workload and age require explicit comparison.").ToArray();
        return new(1, DateTimeOffset.UtcNow, snapshot.Metrics.Any(m => m.Status == "available") ? "observations_only" : "insufficient_data", findings, limitations);
    }

    internal static IReadOnlyList<Finding> Evaluate(MetricsSnapshot snapshot)
    {
        var findings = new List<Finding>();
        foreach (var metric in snapshot.Metrics.Where(m => m.Status is "available" or "ok" or "success" && m.Value is { } v && double.IsFinite(v)))
        {
            var observation = metric.Name switch
            {
                "gpu.thermal_limited" when metric.Value == 1 => "NVML reported a thermal clock-limit flag at this sample.",
                "gpu.power_limited" when metric.Value == 1 => "NVML reported a power clock-limit flag; this can occur during normal operation.",
                "memory.physical_load" when metric.Value >= 90 => "Windows reported at least 90% physical memory load at this sample.",
                "storage.transfer_latency" when metric.Value >= 50 => "The system-wide disk transfer counter reported at least 50 ms at this sample.",
                _ => null
            };
            if (observation is not null) findings.Add(new(metric.Name.Split('.')[0], "warning", observation,
                "A single observation cannot establish a sustained bottleneck or hardware fault.", "observed",
                [$"{metric.TimestampUtc:O}; {metric.Name}={metric.Value?.ToString(CultureInfo.InvariantCulture)} {metric.Unit}; {metric.Source}"],
                ["Compare repeated observations during the affected workload."]));
        }
        return findings;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        Task? sampling;
        lock (_gate) { if (_disposed) return; _disposed = true; sampling = _sample; }
        // Never free native handles while a timed-out sample is still using them.
        if (sampling is { IsCompleted: false }) _ = sampling.ContinueWith(_ => DisposeSensors(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        else DisposeSensors();
    }

    private void DisposeSensors() { _counters?.Dispose(); _hardware?.Dispose(); _nvidia?.Dispose(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong Total, Available, Page, AvailablePage, Virtual, AvailableVirtual, Extended; }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
