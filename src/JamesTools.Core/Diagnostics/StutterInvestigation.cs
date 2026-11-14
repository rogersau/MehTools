namespace JamesTools.Core.Diagnostics;

public sealed record StutterInvestigation(double StartSeconds, double EndSeconds, FrameTelemetry Frame, FrameFinding Finding,
    IReadOnlyList<FrameTelemetry> Frames, IReadOnlyList<DiagnosticHardwareSample> Hardware,
    IReadOnlyList<HardFaultObservation> HardFaults, IReadOnlyList<ReportMarker> Markers)
{
    public int ContextFramesBeforeReportReduction { get; init; }
    public bool PlotFramesReduced => ContextFramesBeforeReportReduction > Frames.Count;
}

public static class StutterInvestigator
{
    public static StutterInvestigation Create(IReadOnlyList<FrameTelemetry> context, IEnumerable<DiagnosticHardwareSample> hardware,
        IEnumerable<HardFaultObservation> hardFaults, IEnumerable<ReportMarker> markers)
    {
        var frame = context[0]; // target is stored first; neighbours may be worse.
        var start = Math.Max(0, frame.OffsetSeconds - 5); var end = frame.OffsetSeconds + 5;
        var samples = hardware.Where(s => s.CollectionEndSeconds >= start && s.CounterWindowStartSeconds <= end).ToArray();
        var faults = hardFaults.Where(f => f.OffsetSeconds >= start && f.OffsetSeconds <= end).ToArray();
        var finding = FrameDiagnosis.Investigate(frame);
        var observations = new List<string>(); var causes = new List<string>();
        var precise = !frame.TimestampSource.StartsWith("CSV receipt", StringComparison.Ordinal);
        foreach (var s in samples.Where(s => precise && s.CounterWindowStartSeconds <= frame.IntervalEndSeconds && s.CollectionEndSeconds >= frame.IntervalStartSeconds))
        {
            if (s.Values.GetValueOrDefault("processReadMiBps") is > 0)
            { observations.Add($"Process-accounted reads in enclosing interval {s.ProcessWindowStartSeconds:F3}–{s.CollectionEndSeconds:F3} s."); causes.Add("Asset streaming is possible; accounted I/O includes cached/device operations and does not establish a physical disk stall."); }
            if (s.Values.GetValueOrDefault("pagesInputPerSecond") is > 0)
            { observations.Add($"System page-ins in enclosing interval {s.CounterWindowStartSeconds:F3}–{s.CollectionEndSeconds:F3} s."); causes.Add("Paging or mapped-file reads are possible; system page-ins alone do not identify the game."); }
        }
        if (precise && faults.Any(f => f.OffsetSeconds >= frame.IntervalStartSeconds - .1 && f.OffsetSeconds - f.DurationMs / 1000 <= frame.IntervalEndSeconds + .1))
        { observations.Add("Selected-process ETW hard faults overlapping or within 100 ms of the frame interval."); causes.Add("Hard-fault reads may contribute; overlap with the stalled game thread still requires scheduling evidence."); }
        var gpu = samples.Where(s => s.GpuCollectionStartSeconds is not null).MinBy(s => Math.Abs(s.GpuCollectionStartSeconds!.Value - frame.OffsetSeconds));
        if (precise && gpu is not null && gpu.Values.GetValueOrDefault("vramUsedMiB") is { } used && gpu.Values.GetValueOrDefault("vramCapacityMiB") is > 0 and var capacity && used / capacity > .9)
        { observations.Add($"Device-wide allocation was above 90% of physical capacity at {gpu.GpuCollectionStartSeconds:F3} s."); causes.Add("VRAM pressure is possible; physical capacity is not the game's WDDM budget and high allocation is not proof of a stall."); }
        if (observations.Count > 0) finding = finding with { Observation = finding.Observation + " " + string.Join(" ", observations), PossibleCause = finding.PossibleCause + " " + string.Join(" ", causes) };
        if (frame.TimestampSource.StartsWith("CSV receipt", StringComparison.Ordinal)) finding = finding with { ConfidenceAndLimitations = finding.ConfidenceAndLimitations + " Timestamp is CSV receipt time; buffering prevents precise hardware correlation." };
        var ordered = context.OrderBy(f => f.OffsetSeconds).ToArray();
        // Keep portable reports small; the raw CSV still contains all accepted frame records.
        var plot = ordered.Length <= 512 ? ordered : ordered.Chunk((int)Math.Ceiling(ordered.Length / 490d))
            .Select(chunk => chunk.MaxBy(f => f.FrameMs)!).Concat(ordered.OrderBy(f => Math.Abs(f.OffsetSeconds - frame.OffsetSeconds)).Take(9))
            .Append(ordered[0]).Append(ordered[^1]).Distinct().OrderBy(f => f.OffsetSeconds).ToArray();
        return new(start, end, frame, finding, plot, samples, faults,
            markers.Where(m => m.OffsetSeconds >= start && m.OffsetSeconds <= end).ToArray())
        { ContextFramesBeforeReportReduction = ordered.Length };
    }
}

public sealed record DiagnosticMetric(string Key, string Name, string Unit, string Source, string Scope, string Sampling,
    string Availability, int Readings, double? Minimum, double? Average, double? Maximum);

public static class DiagnosticMeasurements
{
    public static (string Name, string Unit, string Source, string Scope) Describe(string key)
    {
        if (key.StartsWith("cpuUtilizationPercent:", StringComparison.Ordinal)) return ($"CPU {key[22..]} utilisation", "%", "Windows Processor Information/% Processor Time", "system logical processor, all applications");
        if (key.StartsWith("cpuFrequencyMHz:", StringComparison.Ordinal)) return ($"CPU {key[16..]} reported frequency", "MHz", "Windows Processor Information/Processor Frequency (not effective clock)", "system logical processor");
        if (key is "cpuPackageTemperatureC" or "cpuPerCoreTemperatureC" or "cpuEffectiveClockMHz" or "cpuPackagePowerW" or "cpuThermalLimited" or "cpuPowerLimited")
            return (key, key.EndsWith("C") ? "°C" : key.EndsWith("MHz") ? "MHz" : key.EndsWith("W") ? "W" : "boolean", "Not measured: no supported privileged CPU sensor provider installed by Meh Tools", "system CPU package/cores");
        if (key is "gpuHotspotC" or "gpuCoreVoltageMv" or "gpuVoltageReliabilityLimited")
            return (key, key.EndsWith("C") ? "°C" : key.EndsWith("Mv") ? "mV" : "boolean", "Not measured: no documented sensor exposed by this NVML collector", "first NVIDIA device, all applications");
        if (key is "gameGpuBudgetMiB" or "gameGpuHeadroomMiB")
            return (key, "MiB", "Not measured: external game budget unavailable from selected PDH counters; own-process DXGI budget deliberately excluded", "selected executable processes");
        var unit = key.EndsWith("MiBps") ? "MiB/s" : key.EndsWith("MiB") ? "MiB" : key.EndsWith("PerSecond") ? "/s" : key.EndsWith("MHz") ? "MHz" : key.EndsWith("W") ? "W" : key.EndsWith("C") ? "°C" : key.EndsWith("Ms") ? "ms" : key.Contains("Percent") ? "%" : key.EndsWith("Rpm") ? "RPM" : key.EndsWith("Limited") || key == "gpuIdle" ? "boolean (0/1)" : key == "gpuClockReasons" ? "bitmask" : key == "gpuPState" ? "P-state ordinal" : "value";
        var source = key.StartsWith("gpu") || key.StartsWith("vram") ? "NVIDIA NVML (nvidia-smi fallback for basic sensors)" :
            key.StartsWith("process") ? "GetProcessIoCounters / GetProcessMemoryInfo" : key.StartsWith("game") ? "Windows process APIs / GPU Process Memory PDH counters" : "Windows system APIs / PDH";
        var scope = key.StartsWith("process") || key.StartsWith("game") ? "selected executable processes (accessible exact path matches)" :
            key.StartsWith("gpu") || key.StartsWith("vram") ? "first NVIDIA device, all applications" : "system-wide";
        var name = key switch
        {
            "processPageFaultsPerSecond" => "Process page faults (soft and hard combined)",
            "processReadMiBps" => "Process-accounted read throughput (not physical disk throughput)",
            "processWriteMiBps" => "Process-accounted write throughput (not physical disk throughput)",
            "gameDedicatedGpuMiB" => "Selected processes dedicated GPU allocation (all adapters)",
            "gameSharedGpuMiB" => "Selected processes shared GPU allocation (all adapters)",
            "vramUsedMiB" => "Device-wide dedicated VRAM allocation",
            "vramCapacityMiB" => "Physical VRAM capacity (not budget)",
            "gpuMemoryTemperatureC" => "GPU memory temperature (sensor meaning is device-dependent; not guaranteed junction)",
            "gpuFanRpm" => "GPU fan 0 intended speed",
            "gpuFanPercent" => "GPU intended fan speed",
            "gpuClockReasons" => "NVML clock event reason bitmask",
            _ => key
        };
        return (name, unit, source, scope);
    }
}
