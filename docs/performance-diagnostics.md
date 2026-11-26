# Performance diagnostics (report format 3)

## Using it

1. Open **Performance**, choose the running game if you have several configured, and expand **Capture settings**. The fields are optional. Enter the scene, resolution, rendering settings, caps/sync/refresh, game version, ambient conditions and background workload you know. Settings are self-reported, never inferred. Save settings to retain recent values.
2. Leave normal sampling enabled initially: system/process counters about every two seconds, GPU about every six seconds. Enable **Faster GPU and system sampling** for roughly one-second sampling when investigating a hitch. Actual collection windows are saved.
3. Choose a stutter threshold (default strictly greater than 33.333 ms), target FPS (default 60), and optional warm-up exclusion. Counts above 33.333, 50 and 100 ms are also preserved. Warm-up excludes **frame performance summaries**, while raw frames and hardware remain intact. Hardware averages include warm-up.
4. Start a log and play. Use **Add marker** with a note, or the existing configurable **Stutter marker** shortcut (default Ctrl+Alt+F9). This uses RegisterHotKey, with no keyboard hook or injection. A marker timestamps when Meh Tools receives it; it does not identify the exact stalled frame.
5. Stop and save. **View report timeline** now includes **Worst stutters**. Select one of the ten worst qualifying frames to inspect approximately five seconds before and after it. Frame time, CPU Busy and GPU Busy use per-frame timestamps; hardware sample ticks align with a table showing their real sampling windows. The report includes observations, possible causes and confidence/limitations.
6. Open **Retest and compare** to compare saved reports, choose a baseline, or open performance history. Keep the scene, route, duration, cap, frame generation, sync and background tasks fixed when testing a change.

Normal recording never requests elevation. **Advanced ETW hard faults** is explicitly opt-in and uses existing elevated access only. Without that access it reports Unavailable and all other diagnostics continue. Meh Tools does not install a driver, elevate itself or change a protected setting. It does not collect affected file names, even in advanced mode.

## What is collected

| Area | Source and scope | Interpretation |
|---|---|---|
| CPU load, RAM, commit, storage | Existing Windows APIs/PDH, system-wide; logical processors retain group,index identities | Total load does not reveal game-thread saturation. Commit is not pagefile usage. Pages Input/sec includes mapped-file page-ins. |
| Process CPU, working set/private allocation | Exact accessible executable path matches; separate PID/start-time baselines | Different executable children are excluded. Allocation growth does not prove a memory leak. |
| Process read/write bytes and operations | GetProcessIoCounters, interval deltas for matching process lifetimes | Accounted I/O may include file, network/device and cached I/O. It is not physical disk throughput or a complete account of mapped-file reads. |
| Process page faults | GetProcessMemoryInfo PageFaultCount, interval deltas | Soft and hard faults combined. Counter reset, exit, access failure or missing baseline yields unavailable, not zero. |
| Advanced process hard faults | Real-time ETW MemoryHardFaults plus thread/process mapping, QPC alignment, read byte count and elapsed fault time | Only faults mapping to a verified selected process lifetime are retained. Unknown attribution and ETW losses are reported. Not proof that a critical game thread stalled. No file names or ETL files are written. |
| Frame telemetry | PresentMon CSV, separate PID/lifetime/swapchain/instrumented-frame-type streams | CPU Busy/Wait, GPU Busy/Wait/duration/latency, render-present/display latency, Present API duration, present mode and frame type where present. CPU/GPU work overlaps; measurements are not additive. |
| GPU sensors | Documented NVML exports loaded from the existing NVIDIA driver; first NVIDIA device | GPU utilisation, temperature, graphics/memory clocks, memory allocation/capacity, draw, configured/enforced power limits, draw relative to enforced limit, intended fan percentage/RPM (fan 0), P-state, clock-event mask, thermal/power/configured-clock/idle flags. Unsupported exports or return values remain null independently. |
| GPU memory temperature | NVML field 82, runtime support checked | Device-dependent memory temperature; not assumed to be a junction sensor. Actual local smoke test returned NVML NOT_SUPPORTED (3). |
| Selected process GPU memory | Windows GPU Process Memory Dedicated Usage/Shared Usage PDH instances matched by PID, summed across adapters | Allocation counters, not exclusive ownership, residency, a budget or proof of pressure. Shared allocations may be counted in multiple processes. Device-wide NVML allocation and these process counters are different scopes. |
| CPU frequency and core classes | Processor Information/Processor Frequency PDH, GetSystemCpuSetInformation | Reported frequency is **not effective clock**. Relative efficiency classes identify P/E classes only when Windows exposes distinct classes. Core indexes are group-relative. |

Every hardware metric has a unit, source, scope, target sampling interval, availability and valid reading count in the machine summary. Actual collection and preceding PDH, CPU and process-I/O counter windows are preserved separately in hardware samples; failed PDH polls clear frequency/GPU-memory values and the next successful interval retains its earlier successful baseline. A GPU utilisation query may itself represent a device-defined averaging interval. Hardware readings are never interpolated onto individual frames.

NVML remains a best-effort read-only driver interface. Normal mode keeps the existing nvidia-smi basic-sensor fallback if NVML cannot initialise. Fast mode does not launch nvidia-smi every second when NVML is unavailable. Other collection continues.

## Deliberately unavailable measurements

* **CPU package/per-core temperature, effective clocks, package power and thermal/power limits:** the repository has no installed, supported privileged CPU sensor provider. Standard load/frequency counters cannot substitute for these sensors. No driver is installed and ACPI thermal-zone temperatures are not mislabelled as CPU package temperature.
* **GPU hotspot, core voltage and voltage/reliability limiting:** not exposed by this documented NVML collector. These are reported Not measured; thermal/power flags are not used to infer voltage limits. This is a collector limitation, not a claim that the hardware lacks those sensors. No private NVAPI ordinals or driver register access are used.
* **Memory junction temperature:** generic NVML memory temperature is queried, but its junction meaning is not guaranteed. The local device rejected the field as unsupported.
* **External game's WDDM budget/headroom:** selected PDH counters expose allocation but not that process's budget. IDXGIAdapter3::QueryVideoMemoryInfo reports the calling process's budget. Meh Tools never labels its own budget as the game's. Near physical VRAM capacity is reported as a weak pressure clue, with the scope stated explicitly.
* **Files associated with hard faults:** intentionally not traced. Reliable file attribution needs additional file-name/object event tracking and privacy choices. This implementation retains no sensitive file paths.
* **Definitive game-thread/scheduler cause:** no context-switch, stack, injection or engine profiler collection is added. Disk coincidence, high utilisation, high VRAM allocation, temperature change or Busy ratios alone cannot prove causation.

## Timing, statistics and retention

The actual PresentMon executable's help is inspected for optional frame-type/QPC flags. CSV column names determine metric availability; both CPUBusy/GPUBusy and MsCPUBusy/MsGPUBusy families are understood. QPC ticks and QPC milliseconds share the recording's monotonic clock. Original PresentMon timestamps, column names and CSV receipt times are also exported. A relative timestamp without a trustworthy shared origin falls back to explicitly approximate receipt timing; it is not falsely aligned to the recording. The render/present-to-display field in legacy CSV is not equated with modern CPU-start-to-display latency.

MsBetweenPresents describes the interval **before** its timestamp; FrameTime/MsBetweenAppStart describe the CPU frame starting at the timestamp. The normalized raw export records the field used. Instrumented application-frame streams are preferred for summaries, then the stream with the most valid intervals. Generated streams remain separate in raw data. Without instrumentation, frame type is unknown. Exact executable identity checks may reject buffered frames if the target has already exited; rejected/over-limit frames are counted.

* Average FPS = 1000 / arithmetic mean frame interval.
* P50/P95/P99/P99.9 retain the existing nearest-rank 0.1 ms histogram upper-edge method. Overflow above 10 seconds makes affected percentiles unavailable.
* Percentile-equivalent FPS = 1000/P99 or 1000/P99.9. These are not slowest-frame-subset averages.
* Rolling FPS counts completed intervals in trailing one/five wall-clock seconds, evaluated at frame completions. Only complete windows are summarized. Windows overlap and their sample average is not time-weighted. No claim is made about unobserved windows during a capture gap. A single-frame reciprocal is not presented as sustained minimum FPS.
* Variability is population standard deviation of frame intervals. Below-target time is the sum of intervals greater than 1000/targetFPS, not time derived from interpolated FPS.
* Significant stutters are qualifying **frames**; cluster count separately groups consecutive slow frames. Rates divide by measured frame-interval exposure. Comparison also labels fixed-threshold counts per full recording minute, which may differ after warm-up/capture gaps.
* Up to 16 presentation streams, ten worst events per stream, 4,096 neighbouring frame records per retained event, and 20,000 frames per rolling window are kept. Hardware timeline retains 3,600 samples; hardware context for retained worst events is retained separately (up to 32 samples per event). Portable reports reduce large event contexts to at most 512 actual frame records (worst in each group plus immediate neighbours and endpoints), with original retained counts stated; raw CSV preserves all accepted frames. Per-logical-processor frequency and utilisation samples remain in hardware.jsonl, with their aggregates in the report metric catalog, rather than duplicating all logical-processor columns into the report hardware array. Frame summaries cover the whole measured capture. Application-owned retained data is bounded; the optional TraceEvent library also maintains kernel thread/process mapping state.
* Up to 10,000 markers and 10,000 attributed ETW events are retained in memory. ETW counts/read volumes still aggregate beyond that event retention limit, and omitted events are counted. Raw ETW events remain in their local export. Context can be truncated by recording boundaries, very high frame rates, event retention limits, sparse samples or late CSV delivery. Reports state this limitation.

## Local files and format compatibility

Each explicit recording automatically creates `%LOCALAPPDATA%\JamesTools\Diagnostics\<timestamp-id>\` containing:

* `frames.csv`: buffered normalized per-frame records, including warm-up and available frame diagnostics.
* `hardware.jsonl`: timestamped samples and nullable hardware/process metrics.
* `hard-faults.jsonl`: attributed fault records when tracing is enabled and accessible.
* `markers.json`, `summary.json`, `report.md`: written/flushed at stop.

The recording status shows the raw folder. **Stop and save** still offers the existing portable text/Markdown report. Raw files remain local and are not automatically uploaded, analysed remotely, deleted or rotated. A crash may lose the last buffered writes; already-flushed CSV/JSONL remains inspectable. I/O failure disables raw output and reports an incomplete export while in-memory summaries continue. Close the recording before copying files. Delete old recording folders yourself when no longer needed.

Format 3 preserves format-2 CSV timeline columns and flat summary keys, and adds typed conditions, pacing, frame metric availability, hardware samples, investigations and quality fields. JSON missing numeric values are null; CSV missing cells are blank. Hardware/frame count zero is legitimate only for a measured count. Format-2 timeline/history/benchmark readers continue working. Older human report averages can be compared conservatively; missing legacy fields remain unavailable. Unknown report versions and truncated timelines fail cleanly.

## Validation and overhead

Final validation: Release build with .NET analyzers succeeded with zero warnings/errors; 60/60 console test groups passed; WPF regression checks and 37 synthetic layouts passed; whitespace validation for new diagnostics and git diff checks passed; the package audit reported no known vulnerable dependencies. `AdvancedDiagnosticTests` supplies versioned CSV/JSON, missing-sensor and transient-NVML failures, process restarts, timestamp buffering, rolling FPS, clusters, raw export, unavailable-privilege and comparison fixtures. WPF tests include a format-3 stutter investigation alongside a legacy timeline.

Run:

```powershell
dotnet build JamesTools.sln --configuration Release -p:RunAnalyzersDuringBuild=true -p:EnableNETAnalyzers=true
dotnet run --project tests/JamesTools.Tests --configuration Release --no-build
dotnet run --project tests/JamesTools.UiTests --configuration Release --no-build
dotnet run --project tests/JamesTools.UiTests --configuration Release --no-build -- --presentmon-integration
dotnet run --project tests/JamesTools.Tests --configuration Release --no-build -- --diagnostic-benchmark
dotnet list src/JamesTools.Core/JamesTools.Core.csproj package --vulnerable --include-transitive
```

One local synthetic run parsed and buffered-exported 100,000 frames in 453.9 ms (4.54 µs/frame, about 3,462 allocated bytes/frame and 103.5 raw bytes/frame). At 100 FPS that fixture implies roughly 37 MB of frame CSV per hour. This does not measure game interference or real PresentMon/ETW/driver overhead. A separate five-second ordinary Windows recording measured about 10.8 ms average collector wall time per two-second sample, excluding frame-reader/ETW work. Values vary with system load. NVML replaces repeated nvidia-smi launches; raw files use 64 KiB buffers. Faster mode doubles system-counter polling and increases GPU sampling roughly sixfold. Advanced mode adds an 8 MiB ETW buffer and a real-time consumer thread; it is off by default. Microsoft TraceEvent 4.0.0 is the only new package (managed library about 3.3 MB plus dependencies), used for documented event decoding/session cleanup rather than hand-coded kernel payloads.

Remaining manual checks: record real Wardogs gameplay with the installed PresentMon/driver; check QPC offsets and stream/frame-type selection against the source CSV, especially with frame generation; repeat with monitoring off/default/fast to measure observer effect; compare process I/O/GPU-memory counters with Windows counters; test accessible advanced ETW with a controlled mapped-file workload and verify session cleanup after cancellation. The optional WPF/PresentMon live integration check was attempted: PresentMon stopped cleanly but returned no usable target frames, so it reported SKIP and did not validate real frame timing. No matching MehTools tracing session remained in the post-run session query. No gaming workload, generated-frame integration, elevated kernel ETW trace, CPU privileged sensors, long-duration soak or driver-loss scenario was verified on hardware in this change. Actual ordinary Windows CPU/memory/process/NVML sampling and missing memory-temperature support were exercised.

## Official references used

* [Intel PresentMon console fields and flags](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md), plus the bundled executable's `--help`.
* [NVIDIA NVML device queries](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html) and [NVIDIA's published NVML header](https://github.com/NVIDIA/go-nvml/blob/main/pkg/nvml/nvml.h). Fan percentage/RPM APIs describe intended operating speeds; sensor exposure is checked by return code.
* [GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters), [PROCESS_MEMORY_COUNTERS](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters), [system CPU sets](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-system_cpu_set_information).
* [ETW hard-fault payload and thread attribution](https://learn.microsoft.com/en-us/windows/win32/etw/pagefault-hardfault), [system trace sessions](https://learn.microsoft.com/en-us/windows/win32/etw/configuring-and-starting-a-systemtraceprovider-session), [Microsoft TraceEvent guide](https://github.com/microsoft/perfview/blob/main/documentation/TraceEvent/TraceEventProgrammersGuide.md).
* [DXGI QueryVideoMemoryInfo scope](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_4/nf-dxgi1_4-idxgiadapter3-queryvideomemoryinfo).

## Changed files

Collection and analysis:

* `src/JamesTools.Core/Diagnostics/FrameReportCollector.cs`, `FrameDiagnostics.cs`, `StutterInvestigation.cs`
* `src/JamesTools.Core/Diagnostics/PerformanceLogger.cs`, `PerformanceLogger.Report.cs`, `DiagnosticRawWriter.cs`
* `src/JamesTools.Core/Diagnostics/NvidiaTelemetry.cs`, `LivePerformance.cs`, `ProcessActivity.cs`, `HardFaultTrace.cs`
* `src/JamesTools.Core/Diagnostics/DiagnosticCounters.cs`, `CpuTopology.cs`, `DiagnosticOptions.cs`, `DiagnosticComparison.cs`
* `src/JamesTools.Core/Diagnostics/ReportTimeline.cs`, `PerformanceHistory.cs`, `BenchmarkRecipes.cs`
* `src/JamesTools.Core/JamesTools.Core.csproj` (Microsoft TraceEvent dependency)

Settings and presentation:

* `src/JamesTools.Core/Configuration/AppSettings.cs`, `SettingsStore.cs`, `SettingsValidation.cs`
* `src/JamesTools/MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.Performance.cs`, `MainWindow.Benchmarks.cs`
* `src/JamesTools/PerformanceTimelineWindow.xaml`, `PerformanceTimelineWindow.xaml.cs`

Validation and documentation:

* `tests/JamesTools.Tests/AdvancedDiagnosticTests.cs`, `Program.cs`, `ReportTimelineTests.cs`
* `tests/JamesTools.UiTests/UiAudit.cs`, `Program.cs`, `DiagnosticIntegration.cs`
* `README.md`, `docs/performance-diagnostics.md`
