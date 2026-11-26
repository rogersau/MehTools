using System.Globalization;
using System.Text;
using System.Text.Json;
using JamesTools.Core.Platform;

namespace JamesTools.Core.Diagnostics;

public sealed partial class PerformanceLogger
{
    private string BuildReport()
    {
        var frames = _frames?.GetSummary();
        var diagnostics = _frames?.GetDiagnostics();
        var investigations = BuildInvestigations(diagnostics);
        var text = new StringBuilder();
        text.AppendLine("# Performance recording");
        text.AppendLine("- Report format: 3");
        text.AppendLine($"- Started (UTC): {_started:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine($"- Duration: {_elapsed.Elapsed:c}; system samples: {_samples}; target system interval: {(_options.FastGpuPolling ? 1 : 2)} seconds plus query time; NVIDIA polling: {(_options.FastGpuPolling ? 1 : 6)} seconds; actual query windows recorded.");
        text.AppendLine($"- OS: {Environment.OSVersion.VersionString}; logical processors available to this app: {Environment.ProcessorCount}");
        text.AppendLine($"- CPU: {GetCpuName()}");
        text.AppendLine($"- Physical memory: {(_totalMemory > 0 ? F(_totalMemory / 1073741824.0) + " GiB" : "unavailable")}");
        text.AppendLine($"- NVIDIA GPU: {_gpuName ?? "unavailable or not detected"}");
        text.AppendLine($"- NVIDIA driver: {_driverVersion ?? "unavailable"}");
        if (_vramTotal is { } vram) text.AppendLine($"- GPU memory: {F(vram, "F0")} MiB");
        text.AppendLine($"- Power plan at start / end: {_powerPlanAtStart} / {ReadPowerPlan()}");
        text.AppendLine($"- Selected executable: {(_executablePath is null ? "None (system measurements only)." : Path.GetFileName(_executablePath))}");
        if (_investigationReport is not null) text.AppendLine().AppendLine(InvestigationReportWriter.ToText(_investigationReport));

        text.AppendLine().AppendLine("## Player context (self-reported)");
        lock (_gate)
        {
            text.AppendLine("### Game settings and test scene").AppendLine(_settingsContext);
            text.AppendLine().AppendLine("### Symptoms and target performance").AppendLine(_symptoms);
        }
        text.AppendLine().AppendLine("Settings are not detected automatically. If missing, ask for resolution/render scale, graphics preset/ray tracing, upscaling/frame generation, FPS cap/VSync/VRR, refresh rate, game version/scene, target FPS, and recording/background workload.");

        AppendDiagnostics(text, diagnostics, investigations);
        text.AppendLine().AppendLine("## Measurements");
        text.AppendLine("Values are minimum / average / maximum, followed by the number of valid readings. Averages are unweighted.");
        Append(text, "Total CPU", _cpu, "%");
        Append(text, "Busiest logical CPU at each sample (system-wide)", _busiestCore, "%");
        Append(text, "Physical memory used", _memoryUsed, "MiB");
        Append(text, "Physical memory available", _memoryAvailable, "MiB");
        Append(text, "System committed memory", _commitUsed, "MiB");
        Append(text, "System commit limit", _commitLimit, "MiB");
        Append(text, "System pages read from disk", _pagesInput, "pages/s");
        Append(text, "All physical disks read throughput", _diskRead, "MiB/s");
        Append(text, "All physical disks write throughput", _diskWrite, "MiB/s");
        Append(text, "All physical disks transfer latency", _diskLatency, "ms/transfer");
        Append(text, "GPU utilization", _gpu, "%");
        text.AppendLine($"- GPU utilization at or above 90%: {_gpu.FractionAtOrAbove90()} (fraction of valid readings, not elapsed time)");
        Append(text, "GPU memory used", _vramUsed, "MiB");
        Append(text, "GPU power", _gpuPower, "W");
        Append(text, "GPU configured power limit", _gpuPowerLimit, "W");
        Append(text, "GPU temperature", _gpuTemperature, "°C");
        Append(text, "GPU graphics clock", _gpuClock, "MHz");
        if (_executablePath != null)
        {
            Append(text, "Selected processes CPU (share of total system capacity)", _gameCpu, "%");
            Append(text, "Selected processes working set", _gameMemory, "MiB");
            Append(text, "Selected processes private bytes", _gamePrivate, "MiB");
            text.AppendLine($"- Working set, first / last / change: {Trend(_gameMemory)}");
            text.AppendLine($"- Private bytes, first / last / change: {Trend(_gamePrivate)}");
            text.AppendLine("- Memory changes can reflect loading/unloading or process restarts; growth alone does not prove a leak.");
        }
        text.AppendLine().AppendLine("## Logical CPU load (system-wide)");
        text.AppendLine("IDs are Windows processor group,logical CPU. They are hardware threads, not physical cores; no game-thread attribution is available.");
        if (_cores.Count == 0) text.AppendLine("- Unavailable (counters missing, no valid data, or recording too short).");
        foreach (var (id, stats) in _cores)
            text.AppendLine($"- CPU {id} ({_topology.GetValueOrDefault(id, "topology unavailable")}): {stats.Format("%")}; readings >=90%: {stats.FractionAtOrAbove90()}");

        text.AppendLine().AppendLine("## Frame pacing (PresentMon, best-effort)");
        text.AppendLine("- " + (frames?.Status ?? _frameStatus ?? "Unavailable."));
        if (frames?.Analysis is { } a)
        {
            text.AppendLine($"- Summary stream (PID:swapchain): {frames.Stream}; prefers instrumented application frames, then highest valid frame count, from {frames.StreamCount} streams. Other-stream frames excluded: {frames.OtherStreamFrames}; frames ignored beyond 16-stream limit: {frames.IgnoredFrames}.");
            text.AppendLine($"- FPS (from frame intervals): {F(frames.FpsMin)} / {F(frames.FpsAvg)} / {F(frames.FpsMax)} ({frames.Count} frames)");
            text.AppendLine("- Average FPS = 1000 / mean frame interval. Min/max FPS are reciprocals of single intervals, not sustained rates. Older format-1 reports averaged per-frame FPS and are not directly comparable.");
            text.AppendLine($"- Frame time: {F(frames.FrameMsMin, "F2")} / {F(frames.FrameMsAvg, "F2")} / {F(frames.FrameMsMax, "F2")} ms");
            text.AppendLine($"- Frame time P50 / P95 / P99 / P99.9: {F(a.P50Ms, "F2")} / {F(a.P95Ms, "F2")} / {F(a.P99Ms, "F2")} / {F(a.P999Ms, "F2")} ms");
            text.AppendLine($"- 1% / 0.1% percentile-equivalent FPS (1000 / P99 or P99.9): {F(Reciprocal(a.P99Ms))} / {F(Reciprocal(a.P999Ms))}. These are not averages of the slowest 1% or 0.1% of frames.");
            text.AppendLine($"- Frames >33.333 / >50 / >100 ms (overlapping counts): {a.Over33Ms} / {a.Over50Ms} / {a.Over100Ms} of {a.Count} frames.");
            text.AppendLine($"- Percentiles use nearest rank and 0.1 ms bucket upper edges. Frames >10000 ms: {a.OverflowCount}; percentiles in that overflow bucket are unavailable. Tail percentiles are unreliable in short captures.");
            if (frames.LatencyAvg is not null)
                text.AppendLine($"- Display latency (PresentMon), minimum / average: {F(frames.LatencyMin, "F2")} / {F(frames.LatencyAvg, "F2")} ms ({frames.LatencyCount} readings); not input-to-photon latency.");
            text.AppendLine("- Worst frames, recording offset / frame time (QPC where available; otherwise CSV receipt) (includes menus, loading, and alt-tab):");
            foreach (var worst in a.WorstFrames)
                text.AppendLine($"  - {F(worst.OffsetSeconds, "F3")} s / {F(worst.FrameMs, "F2")} ms");
        }

        lock (_gate)
        {
            text.AppendLine().AppendLine("## Markers");
            if (_markers.Count == 0) text.AppendLine("- None added.");
            foreach (var marker in _markers)
                text.AppendLine($"- {F(marker.Offset.TotalSeconds, "F3")} s — {marker.Note} [marker-json:{{\"offsetSeconds\":{F(marker.Offset.TotalSeconds, "F3")},\"note\":{System.Text.Json.JsonSerializer.Serialize(marker.Note)}}}] (latest sample: {marker.Context})");
        }
        text.AppendLine().AppendLine("## Capture quality and limitations");
        if (_samplingError is not null) text.AppendLine("- " + _samplingError);
        text.AppendLine($"- NVIDIA queries attempted / failed: {_gpuAttempts} / {_gpuFailures}. Last status: {_gpuStatus}");
        text.AppendLine($"- Valid system CPU / memory / busiest-CPU readings: {_cpu.Count} / {_memoryUsed.Count} / {_busiestCore.Count} of {_samples} system samples; selected process working-set readings: {_gameMemory.Count}.");
        if (frames is null || frames.Count == 0) text.AppendLine("- FPS, frame times, stutter and input latency are not measured. Frame capture was unavailable or returned no valid intervals.");
        text.AppendLine("- Logical CPU readings are interval averages across all applications. CPU effective clocks, CPU temperature, package power, CPU limit indicators and thread scheduling are unavailable without a supported sensor provider. Reported processor frequency is not an effective clock. GPU limit reasons are queried independently where supported. Short spikes may be missed; GPU power/temperature/clocks alone cannot prove throttling.");
        text.AppendLine("- GPU readings cover only the first NVIDIA GPU reported by NVML or nvidia-smi; other GPUs are not measured. GPU memory used is system-wide device allocation, not the game's VRAM budget or proof of VRAM exhaustion.");
        text.AppendLine("- Selected process measurements match accessible executable paths and exclude children with different executables. Working set includes shared resident pages; private bytes measure private allocation, including nonresident memory. Missing readings never mean zero.");
        text.AppendLine("- System commit is allocation backed by RAM/pagefiles, not pagefile usage. Pages Input/sec includes hard faults from mapped files, not just pagefiles; disk counters cover all applications and drives. They cannot attribute stalls to the game by themselves.");
        text.AppendLine("- PresentMon matches executable names, so identically named processes may be included. Summary and timeline prefer instrumented application-frame streams, then the most valid intervals in their scope and labels it. Neither selection guarantees the main game view. Capture may miss frames or include non-game presents; frame type is available only when PresentMon and driver/application instrumentation support it; input latency is not measured.");
        text.AppendLine("- QPC timestamps share the recording clock. Legacy fallback CSV receipt timestamps are approximate; buffering delays those frame offsets. The timeline pairs periodic hardware readings with frame windows, not exact per-frame hardware measurements. Correlation does not establish a cause.");
        text.AppendLine().AppendLine("## Prompt for analysis");
        text.AppendLine("Review the player context, capture quality, frame percentiles, worst frames, markers, memory trend and timeline. Explain evidence separately from hypotheses. Check whether slow frame windows coincide with high logical CPU load, GPU load, private memory/commit growth, hard paging or disk activity. Allow for loading, menus, alt-tab, FPS caps and frame generation. Do not assert a bottleneck, memory leak, thermal throttling or need for an upgrade from averages alone. Prioritize a few controlled repeatable tests with one setting changed at a time; explain what each result would confirm or disprove. Ask for missing game settings or measurements before recommending purchases.");
        text.AppendLine().AppendLine("## Machine summary (JSON)").AppendLine("```json").AppendLine(BuildMachineSummaryJson(frames, diagnostics, investigations)).AppendLine("```");
        AppendTimeline(text);
        if (_network is not null)
        {
            text.AppendLine().AppendLine(NetworkReports.Heading).AppendLine(NetworkReports.Explanation);
            text.AppendLine("```json").AppendLine(JsonSerializer.Serialize(_network.Snapshot())).AppendLine("```");
        }
        return text.ToString();
    }

    private string BuildMachineSummaryJson(FrameSummary? frames, FrameDiagnosticSummary? diagnostics, StutterInvestigation[] investigations)
    {
        static double? Avg(Statistics s) => s.Count == 0 ? null : Math.Round(s.Average, 3);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 3, selectedExecutable = _executablePath is null ? null : Path.GetFileName(_executablePath), logicalProcessors = _topology, durationSeconds = Math.Round(_elapsed.Elapsed.TotalSeconds, 3), systemSampleCount = _samples,
            cpuAvg = Avg(_cpu), busiestLogicalCpuAvg = Avg(_busiestCore), memUsedAvgMiB = Avg(_memoryUsed),
            commitUsedAvgMiB = Avg(_commitUsed), commitLimitAvgMiB = Avg(_commitLimit), pagesInputAvgPerSecond = Avg(_pagesInput),
            diskReadAvgMiBps = Avg(_diskRead), diskWriteAvgMiBps = Avg(_diskWrite), diskLatencyAvgMs = Avg(_diskLatency),
            gpuAvg = Avg(_gpu), gpuPowerAvgW = Avg(_gpuPower), gpuTempAvgC = Avg(_gpuTemperature), gpuClockAvgMHz = Avg(_gpuClock),
            gpuMemAvgMiB = Avg(_vramUsed), gameCpuAvg = Avg(_gameCpu), gameMemAvgMiB = Avg(_gameMemory), gamePrivateAvgMiB = Avg(_gamePrivate),
            driverVersion = _driverVersion, fpsMethod = "1000/meanFrameMs", fpsAvg = frames?.FpsAvg, frameMsAvg = frames?.FrameMsAvg,
            frameStream = frames?.Stream, frameStreamCount = frames?.StreamCount,
            displayLatencyAvgMs = frames?.LatencyAvg, frameCount = frames?.Count ?? 0,
            frameP50Ms = frames?.Analysis?.P50Ms, frameP95Ms = frames?.Analysis?.P95Ms,
            frameP99Ms = frames?.Analysis?.P99Ms, frameP999Ms = frames?.Analysis?.P999Ms,
            framesOver50Ms = frames?.Analysis?.Over50Ms, framesOver100Ms = frames?.Analysis?.Over100Ms,
            gpuTempMaxC = _gpuTemperature.Count > 0 ? (double?)_gpuTemperature.Max : null,
            framesOver33Ms = frames?.Analysis?.Over33Ms,
            pacing = diagnostics?.Pacing, frameMetrics = diagnostics?.Metrics,
            presentMonColumns = diagnostics?.PresentMonColumns, presentModes = diagnostics?.PresentModes, frameTypesAllStreams = diagnostics?.FrameTypes, frameTimeSources = diagnostics?.FrameTimeSources, conditions = _options.Conditions,
            warmupSeconds = _options.WarmupSeconds, stutterThresholdMs = _options.StutterThresholdMs,
            metrics = ExtendedMetrics(), investigations, stutterInvestigation = _investigationReport,
            markers = _markers.Select(m => new ReportMarker(m.Offset.TotalSeconds, m.Note)).ToArray(),
            hardware = _hardware.Select(h => h with { Values = h.Values.Where(v => !v.Key.StartsWith("cpuFrequencyMHz:") && !v.Key.StartsWith("cpuUtilizationPercent:")).ToDictionary(v => v.Key, v => v.Value) }).ToArray(), hardFaultStatus = _hardFaults.Status,
            processHardFaultCount = _hardFaults.Started && _gameMemory.Count > 0 ? (long?)_hardFaults.SelectedFaultCount : null,
            processHardFaultReadMiB = _hardFaults.Started && _gameMemory.Count > 0 ? _hardFaults.SelectedReadBytes / 1048576d : (double?)null,
            hardFaultEventsLost = _hardFaults.EventsLost, hardFaultEventsOmitted = _hardFaults.Omitted,
            hardFaultEventsUnattributed = _hardFaults.Unattributed,
            rawExportStatus = _raw.Error ?? "Available locally", rawRecordingId = Path.GetFileName(_raw.DirectoryPath),
            collectorAverageMs = _samples > 0 ? _collectionTotalMs / _samples : (double?)null, collectorMaximumMs = _collectionMaxMs,
            timelineRows = _timeline.Count, timelineRowsOmitted = _timelineOmitted
        });
    }

    private StutterInvestigation[] BuildInvestigations(FrameDiagnosticSummary? diagnostics) => diagnostics?.Events
        .Where(e => e.Count > 0).Select(e => StutterInvestigator.Create(e, _eventHardware.TryGetValue(e[0].OffsetSeconds, out var retained) ? retained.Concat(_hardware).Distinct() : _hardware, _hardFaults.Snapshot(),
            _markers.Select(m => new ReportMarker(m.Offset.TotalSeconds, m.Note)))).ToArray() ?? [];

    private DiagnosticMetric[] ExtendedMetrics()
    {
        foreach (var key in new[] { "gpuFanPercent", "gpuFanRpm", "gpuMemoryClockMHz", "gpuMemoryTemperatureC", "gpuPState", "gpuClockReasons", "gpuThermalLimited", "gpuPowerLimited", "gpuConfiguredClockLimited", "gpuIdle", "gpuPowerPercentOfLimit", "gpuHotspotC", "gpuCoreVoltageMv", "gpuVoltageReliabilityLimited", "gameGpuBudgetMiB", "gameGpuHeadroomMiB", "cpuPackageTemperatureC", "cpuPerCoreTemperatureC", "cpuEffectiveClockMHz", "cpuPackagePowerW", "cpuThermalLimited", "cpuPowerLimited" })
            _extended.TryAdd(key, new());
        return _extended.Select(pair =>
    {
        var (name, unit, source, scope) = DiagnosticMeasurements.Describe(pair.Key);
        var s = pair.Value;
        return new DiagnosticMetric(pair.Key, name, unit, source, scope,
            pair.Key.StartsWith("gpu") || pair.Key.StartsWith("vram") ? $"target {(_options.FastGpuPolling ? 1 : 6)} s; exact collection windows in hardware samples; NVML utilisation has its own device averaging window" :
            $"target {(_options.FastGpuPolling ? 1 : 2)} s; preceding counter interval in hardware samples", s.Count > 0 ? "Available" : _sensorStatus.GetValueOrDefault(pair.Key, "Unavailable / not measured"), s.Count,
            s.Count > 0 ? s.Min : null, s.Count > 0 ? s.Average : null, s.Count > 0 ? s.Max : null);
    }).ToArray();
    }

    private void AppendDiagnostics(StringBuilder text, FrameDiagnosticSummary? diagnostics, StutterInvestigation[] investigations)
    {
        text.AppendLine().AppendLine("## Overall performance and frame pacing");
        text.AppendLine($"- Warm-up exclusion: {_options.WarmupSeconds:F1} s (performance summaries only; raw frames and hardware are preserved). Hardware summaries include warm-up.");
        if (diagnostics?.Pacing is { } p)
        {
            text.AppendLine($"- Significant frames >{p.ThresholdMs:F3} ms: {p.SlowFrames}; {F(p.StuttersPerMinute)} per measured frame-minute; consecutive slow clusters: {p.ClusterCount}.");
            text.AppendLine($"- Frame-time population standard deviation: {F(p.StandardDeviationMs)} ms; frame-interval time below {_options.TargetFps:F1} FPS: {p.BelowTargetSeconds:F2} s. This is the sum of intervals exceeding the target period, not time from interpolated FPS.");
            foreach (var r in new[] { p.OneSecond, p.FiveSeconds })
                text.AppendLine($"- Rolling {r.WindowSeconds} s FPS min / average / max: {F(r.Minimum)} / {F(r.Average)} / {F(r.Maximum)} ({r.Windows} complete trailing windows, evaluated at frame completions; overlapping windows, no menu exclusions; rolling retention omissions: {p.RollingFramesOmitted}). Worst sustained {r.WindowSeconds} s FPS: {F(r.Minimum)}.");
            text.AppendLine("- Frame-time histogram: " + string.Join("; ", p.Histogram.Select(b => b.Key + ": " + b.Value)));
            foreach (var c in p.WorstClusters) text.AppendLine($"- Slow cluster {c.StartSeconds:F3}–{c.EndSeconds:F3} s: {c.Frames} consecutive frames, max {c.MaximumMs:F2} ms.");
        }
        else text.AppendLine("- Frame pacing: Unavailable.");
        text.AppendLine().AppendLine("## CPU/GPU bottleneck evidence");
        foreach (var metric in diagnostics?.Metrics ?? []) text.AppendLine($"- {metric.Name}: {F(metric.Average, "F2")} {metric.Unit}, {metric.Readings} readings; source PresentMon, per frame, selected PID/presentation stream.");
        text.AppendLine("- Present modes (selected stream): " + (diagnostics is null || diagnostics.PresentModes.Count == 0 ? "Unavailable" : string.Join("; ", diagnostics.PresentModes.Select(p => p.Key + ": " + p.Value))));
        text.AppendLine("- Frame types (separate streams, not combined FPS): " + (diagnostics is null || diagnostics.FrameTypes.Count == 0 ? "Unavailable" : string.Join("; ", diagnostics.FrameTypes.Select(p => p.Key + ": " + p.Value))));
        text.AppendLine("- CPU and GPU work overlap. Utilisation alone does not identify a bottleneck. Missing Busy/Wait values remain unavailable.");
        text.AppendLine().AppendLine("## Thermal, power, memory and process I/O detail");
        foreach (var metric in ExtendedMetrics().Where(m => m.Key.StartsWith("gpu") || m.Key.StartsWith("process") || m.Key.Contains("GpuMiB")))
            text.AppendLine($"- {metric.Name}: {F(metric.Minimum)} / {F(metric.Average)} / {F(metric.Maximum)} {metric.Unit} ({metric.Readings} readings; {metric.Availability}); {metric.Scope}; {metric.Source}; {metric.Sampling}.");
        text.AppendLine("- CPU package/per-core temperatures, effective clocks, package power and CPU thermal/power limits: Unavailable. No driver is installed. GPU hotspot/core voltage/voltage-reliability limit: Unavailable from this documented NVML path. Memory temperature is not assumed to be junction temperature.");
        text.AppendLine("- Game VRAM budget/headroom: Unavailable. External GPU Process Memory counters measure allocations across adapters, not a game's budget. This application's DXGI budget is never used as the game's. High allocation alone does not prove a bottleneck.");
        text.AppendLine("- NVML thermal and power flags are sampled evidence, not proof of a frame stall. Idle and configured clock limits can be normal GPU Boost behaviour. Voltage reliability is not inferred from NVML power or thermal flags.");
        text.AppendLine("- Process-accounted I/O can include cached, file, network and device I/O; mapped-file page-ins may not appear there. Process page faults combine soft and hard faults. System Pages Input/sec is not process attribution.");
        text.AppendLine($"- Advanced hard faults: {_hardFaults.Status} Lost events: {_hardFaults.EventsLost}; retained-event omissions: {_hardFaults.Omitted}; unknown PID: {_hardFaults.Unattributed}. File paths are never collected. Thread/process mapping and ETW losses limit coverage.");
        text.AppendLine().AppendLine("## Worst stutter investigations");
        if (investigations.Length == 0) text.AppendLine("- No measured frames exceeded the configured threshold, or frame capture was unavailable.");
        foreach (var e in investigations)
        {
            text.AppendLine($"### {e.Frame.OffsetSeconds:F3} s — {e.Frame.FrameMs:F2} ms");
            text.AppendLine("OBSERVATION: " + e.Finding.Observation);
            text.AppendLine("POSSIBLE CAUSE: " + e.Finding.PossibleCause);
            text.AppendLine("CONFIDENCE / LIMITATIONS: " + e.Finding.ConfidenceAndLimitations);
            text.AppendLine($"Context requested: {e.StartSeconds:F3}–{e.EndSeconds:F3} s; retained {e.ContextFramesBeforeReportReduction} context frames; report plot contains {e.Frames.Count} frame records (worst frame per group plus immediate neighbours when reduced), and {e.Hardware.Count} hardware samples. Raw exports preserve all accepted frames. Recording boundaries and bounded context may truncate this window.");
            text.AppendLine("Nearby frames (offset s / ms): " + string.Join(", ", e.Frames.OrderBy(f => Math.Abs(f.OffsetSeconds - e.Frame.OffsetSeconds)).Take(9).OrderBy(f => f.OffsetSeconds).Select(f => $"{f.OffsetSeconds:F3}/{f.FrameMs:F2}")));
            foreach (var h in e.Hardware)
            {
                string V(string key, string unit) => F(h.Values.GetValueOrDefault(key)) + " " + unit;
                text.AppendLine($"- Collection {h.CollectionStartSeconds:F3}–{h.CollectionEndSeconds:F3} s; PDH counters since {h.CounterWindowStartSeconds:F3} s; CPU since {F(h.CpuWindowStartSeconds, "F3")} s; process I/O {F(h.ProcessWindowStartSeconds, "F3")}–{F(h.ProcessWindowEndSeconds, "F3")} s; GPU query {F(h.GpuCollectionStartSeconds, "F3")}–{F(h.GpuCollectionEndSeconds, "F3")} s.");
                text.AppendLine($"  CPU {V("cpuPercent", "%")}; game CPU {V("gameCpuPercent", "%")}; RAM available {V("ramAvailableMiB", "MiB")}; commit {V("commitUsedMiB", "MiB")}.");
                text.AppendLine($"  GPU {V("gpuPercent", "%")}; temperature {V("gpuTemperatureC", "°C")}; graphics clock {V("gpuClockMHz", "MHz")}; power {V("gpuPowerW", "W")}; device VRAM {V("vramUsedMiB", "MiB")}; game allocation {V("gameDedicatedGpuMiB", "MiB")}; game budget Unavailable.");
                text.AppendLine($"  Process accounted reads/writes {V("processReadMiBps", "MiB/s")} / {V("processWriteMiBps", "MiB/s")}; process soft+hard faults {V("processPageFaultsPerSecond", "/s")}; system disk reads/writes {V("diskReadMiBps", "MiB/s")} / {V("diskWriteMiBps", "MiB/s")}; system page-ins {V("pagesInputPerSecond", "pages/s")}.");
            }
            foreach (var marker in e.Markers) text.AppendLine($"- Marker {marker.OffsetSeconds:F3} s: {marker.Note}");
        }
        text.AppendLine().AppendLine("## Recording conditions (self-reported)");
        foreach (var property in typeof(RecordingConditions).GetProperties())
            text.AppendLine($"- {property.Name}: {(property.GetValue(_options.Conditions) is string value && !string.IsNullOrWhiteSpace(value) ? value.Replace("\r", " ").Replace("\n", " ") : "Not provided")}");
        text.AppendLine().AppendLine("## Data quality and controlled follow-up tests");
        text.AppendLine($"- Collector wall time average / maximum: {F(_samples > 0 ? _collectionTotalMs / _samples : null)} / {_collectionMaxMs:F2} ms per sample; this excludes PresentMon/ETW consumer work and raw frame writes. Compare game frame pacing with monitoring disabled to measure total observer effect. Raw export status: {_raw.Error ?? (_options.WriteRawExports ? "Available locally" : "Disabled for this session")}.");
        text.AppendLine("- Repeat the same scene, route, duration and background workload. Compare render scale to test GPU rendering sensitivity; compare a repeated warm route with a cold route for streaming; check explicit ETW faults when access is available. Keep FPS cap, VSync, VRR and frame generation fixed. A lower GPU temperature does not prove higher FPS or prior throttling.");
        text.AppendLine("- Each metric's source, unit, scope, availability, target interval and reading coverage are included in machine-summary metrics; exact windows are in hardware. " +
            (_options.WriteRawExports ? "Raw frames.csv, hardware.jsonl, hard-faults.jsonl, markers.json and report.md are kept in the local Diagnostics recording folder." : "Raw files and ETL retention are disabled for this session."));
    }

    private void AppendTimeline(StringBuilder text)
    {
        text.AppendLine().AppendLine("## Sample timeline (CSV)");
        text.AppendLine($"Rows retained: {_timeline.Count}; earlier rows omitted: {_timelineOmitted} (limit {TimelineLimit}; whole-capture aggregates above include omitted rows). Times are seconds from recording start; decimal points are invariant. Blank means unavailable or not queried this cycle, including GPU columns between queries. Hardware counters average over their preceding sampling intervals; sample_start_s/sample_end_s bracket collection. Frame windows cover CSV received since the previous row; their offsets may be delayed by buffering. The busiest stream can change between rows.");
        text.AppendLine("```csv");
        text.AppendLine("sample_start_s,sample_end_s,cpu_pct,busiest_logical_cpu_pct,ram_available_mib,commit_used_mib,commit_limit_mib,game_cpu_pct,game_working_set_mib,game_private_mib,game_processes,gpu_pct,vram_used_mib,gpu_power_w,gpu_temp_c,gpu_clock_mhz,pages_input_per_s,disk_read_mib_per_s,disk_write_mib_per_s,disk_latency_ms,frame_window_start_s,frame_window_end_s,frame_stream,frame_count,fps,frame_max_ms,frames_over_50ms,ram_used_mib");
        foreach (var row in _timeline) text.AppendLine(row.ToCsv());
        text.AppendLine("```");
    }

    private static string F(double? value, string format = "F1") => value?.ToString(format, CultureInfo.InvariantCulture) ?? "unavailable";
    private static double? Reciprocal(double? value) => value > 0 ? 1000 / value : null;
    private static string Trend(Statistics stats) => stats.Count == 0 ? "unavailable" :
        $"{F(stats.First)} / {F(stats.Last)} / {F(stats.Last - stats.First)} MiB";
    private static string ReadPowerPlan()
    {
        try { return PowerPlans.GetActive().ToString(); }
        catch { return "unavailable"; }
    }
}

internal sealed record DiagnosticRow(double Start, double End, double? Cpu, double? BusiestCpu, double? AvailableMemory, double? MemoryUsed,
    double? CommitUsed, double? CommitLimit, double? GameCpu, double? GameMemory, double? GamePrivate, int? ProcessCount,
    double? Gpu, double? Vram, double? Power, double? Temperature, double? Clock, double? PagesInput,
    double? DiskRead, double? DiskWrite, double? DiskLatency, FrameInterval? Frames)
{
    public static DiagnosticRow ForFrames(FrameInterval window) => new(window.EndSeconds, window.EndSeconds,
        null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, window);

    public string ToCsv()
    {
        static string N(double? value) => value?.ToString("F3", CultureInfo.InvariantCulture) ?? "";
        return string.Join(",", new[] { N(Start), N(End), N(Cpu), N(BusiestCpu), N(AvailableMemory), N(CommitUsed), N(CommitLimit),
            N(GameCpu), N(GameMemory), N(GamePrivate), ProcessCount?.ToString(CultureInfo.InvariantCulture) ?? "", N(Gpu), N(Vram), N(Power),
            N(Temperature), N(Clock), N(PagesInput), N(DiskRead), N(DiskWrite), N(DiskLatency), N(Frames?.StartSeconds), N(Frames?.EndSeconds),
            Frames?.Stream ?? "", Frames?.Count.ToString(CultureInfo.InvariantCulture) ?? "", N(Frames?.Fps), N(Frames?.MaxMs), Frames?.Over50Ms.ToString(CultureInfo.InvariantCulture) ?? "", N(MemoryUsed) });
    }
}
