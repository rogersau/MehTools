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
        var text = new StringBuilder();
        text.AppendLine("# Performance recording");
        text.AppendLine("- Report format: 2");
        text.AppendLine($"- Started (UTC): {_started:yyyy-MM-dd HH:mm:ss} UTC");
        text.AppendLine($"- Duration: {_elapsed.Elapsed:c}; system samples: {_samples}; target system interval: 2 seconds plus query time; NVIDIA polling: no more often than every 6 seconds.");
        text.AppendLine($"- OS: {Environment.OSVersion.VersionString}; logical processors available to this app: {Environment.ProcessorCount}");
        text.AppendLine($"- CPU: {GetCpuName()}");
        text.AppendLine($"- Physical memory: {(_totalMemory > 0 ? F(_totalMemory / 1073741824.0) + " GiB" : "unavailable")}");
        text.AppendLine($"- NVIDIA GPU: {_gpuName ?? "unavailable or not detected"}");
        text.AppendLine($"- NVIDIA driver: {_driverVersion ?? "unavailable"}");
        if (_vramTotal is { } vram) text.AppendLine($"- GPU memory: {F(vram, "F0")} MiB");
        text.AppendLine($"- Power plan at start / end: {_powerPlanAtStart} / {ReadPowerPlan()}");
        text.AppendLine($"- Selected executable: {(_executablePath is null ? "None (system measurements only)." : Path.GetFileName(_executablePath))}");

        text.AppendLine().AppendLine("## Player context (self-reported)");
        lock (_gate)
        {
            text.AppendLine("### Game settings and test scene").AppendLine(_settingsContext);
            text.AppendLine().AppendLine("### Symptoms and target performance").AppendLine(_symptoms);
        }
        text.AppendLine().AppendLine("Settings are not detected automatically. If missing, ask for resolution/render scale, graphics preset/ray tracing, upscaling/frame generation, FPS cap/VSync/VRR, refresh rate, game version/scene, target FPS, and recording/background workload.");

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
            text.AppendLine($"- CPU {id}: {stats.Format("%")}; readings >=90%: {stats.FractionAtOrAbove90()}");

        text.AppendLine().AppendLine("## Frame pacing (PresentMon, best-effort)");
        text.AppendLine("- " + (frames?.Status ?? _frameStatus ?? "Unavailable."));
        if (frames?.Analysis is { } a)
        {
            text.AppendLine($"- Summary stream (PID:swapchain): {frames.Stream}; selected by highest valid frame count, from {frames.StreamCount} streams. Other-stream frames excluded: {frames.OtherStreamFrames}; frames ignored beyond 16-stream limit: {frames.IgnoredFrames}.");
            text.AppendLine($"- FPS (from frame intervals): {F(frames.FpsMin)} / {F(frames.FpsAvg)} / {F(frames.FpsMax)} ({frames.Count} frames)");
            text.AppendLine("- Average FPS = 1000 / mean frame interval. Min/max FPS are reciprocals of single intervals, not sustained rates. Older format-1 reports averaged per-frame FPS and are not directly comparable.");
            text.AppendLine($"- Frame time: {F(frames.FrameMsMin, "F2")} / {F(frames.FrameMsAvg, "F2")} / {F(frames.FrameMsMax, "F2")} ms");
            text.AppendLine($"- Frame time P50 / P95 / P99 / P99.9: {F(a.P50Ms, "F2")} / {F(a.P95Ms, "F2")} / {F(a.P99Ms, "F2")} / {F(a.P999Ms, "F2")} ms");
            text.AppendLine($"- 1% / 0.1% percentile-equivalent FPS (1000 / P99 or P99.9): {F(Reciprocal(a.P99Ms))} / {F(Reciprocal(a.P999Ms))}. These are not averages of the slowest 1% or 0.1% of frames.");
            text.AppendLine($"- Frames >33.333 / >50 / >100 ms (overlapping counts): {a.Over33Ms} / {a.Over50Ms} / {a.Over100Ms} of {a.Count} frames.");
            text.AppendLine($"- Percentiles use nearest rank and 0.1 ms bucket upper edges. Frames >10000 ms: {a.OverflowCount}; percentiles in that overflow bucket are unavailable. Tail percentiles are unreliable in short captures.");
            if (frames.LatencyAvg is not null)
                text.AppendLine($"- Display latency (PresentMon), minimum / average: {F(frames.LatencyMin, "F2")} / {F(frames.LatencyAvg, "F2")} ms ({frames.LatencyCount} readings); not input-to-photon latency.");
            text.AppendLine("- Worst frames, approximate CSV receipt offset / frame time (includes menus, loading, and alt-tab):");
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
        text.AppendLine("- Logical CPU readings are interval averages across all applications. CPU clocks, CPU temperature, thread scheduling and CPU/GPU throttling reasons are not measured. Short spikes may be missed; GPU power/temperature/clocks alone cannot prove throttling.");
        text.AppendLine("- GPU readings cover only the first NVIDIA GPU reported by nvidia-smi; other GPUs are not measured. GPU memory used is system-wide device allocation, not the game's VRAM budget or proof of VRAM exhaustion.");
        text.AppendLine("- Selected process measurements match accessible executable paths and exclude children with different executables. Working set includes shared resident pages; private bytes measure private allocation, including nonresident memory. Missing readings never mean zero.");
        text.AppendLine("- System commit is allocation backed by RAM/pagefiles, not pagefile usage. Pages Input/sec includes hard faults from mapped files, not just pagefiles; disk counters cover all applications and drives. They cannot attribute stalls to the game by themselves.");
        text.AppendLine("- PresentMon matches executable names, so identically named processes may be included. Summary uses the stream with most valid intervals; timeline uses the busiest stream in each window and labels it. Neither selection guarantees the main game view. Capture may miss frames or include non-game presents; generated/displayed frame distinctions and input latency are not measured.");
        text.AppendLine("- CSV receipt times are approximate; buffering delays frame offsets. The timeline pairs periodic hardware readings with frame windows, not exact per-frame hardware measurements. Correlation does not establish a cause.");
        text.AppendLine().AppendLine("## Prompt for analysis");
        text.AppendLine("Review the player context, capture quality, frame percentiles, worst frames, markers, memory trend and timeline. Explain evidence separately from hypotheses. Check whether slow frame windows coincide with high logical CPU load, GPU load, private memory/commit growth, hard paging or disk activity. Allow for loading, menus, alt-tab, FPS caps and frame generation. Do not assert a bottleneck, memory leak, thermal throttling or need for an upgrade from averages alone. Prioritize a few controlled repeatable tests with one setting changed at a time; explain what each result would confirm or disprove. Ask for missing game settings or measurements before recommending purchases.");
        text.AppendLine().AppendLine("## Machine summary (JSON)").AppendLine("```json").AppendLine(BuildMachineSummaryJson(frames)).AppendLine("```");
        AppendTimeline(text);
        if (_network is not null)
        {
            text.AppendLine().AppendLine(NetworkReports.Heading).AppendLine(NetworkReports.Explanation);
            text.AppendLine("```json").AppendLine(JsonSerializer.Serialize(_network.Snapshot())).AppendLine("```");
        }
        return text.ToString();
    }

    private string BuildMachineSummaryJson(FrameSummary? frames)
    {
        static double? Avg(Statistics s) => s.Count == 0 ? null : Math.Round(s.Average, 3);
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 2, durationSeconds = Math.Round(_elapsed.Elapsed.TotalSeconds, 3), systemSampleCount = _samples,
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
            timelineRows = _timeline.Count, timelineRowsOmitted = _timelineOmitted
        });
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
