# Stutter Investigator — implementation and validation

Validated on 16 November 2026 on Windows with .NET SDK 10.0.404 and 20 logical processors. This process was not elevated. GPT-6 Astra handled architecture/integration/review; GPT-6.1 Sol implementation and review delegates used high reasoning effort.

## Architecture and delivered behavior

The WPF/.NET 10 application, existing `PerformanceLogger`, PresentMon stream selection, hardware/process sampling, format-3 performance reports, and existing MCP session service/store remain the foundation. No packages, framework, kernel driver, game injection, cloud service or competing session manager were added. See [architecture audit](stutter-investigator-architecture.md).

- **Normal investigation:** select any accessible application or system-only capture; session-start device inventory; event-driven PnP/interface/configuration, audio/default endpoint, available driver event logs and relevant utility start/stop subscriptions; automatic deadline and early stop.
- **Deep investigation:** opt-in named ETW session, scheduling/ready/wait/execution pairs, CPU samples and available stack addresses, module offsets, DPC/ISR, disk/file/hard-fault and provider GPU/driver evidence; optional deeper USB providers; private circular ETL; watchdog and strict owner-aware recovery. Deep mode never requests elevation automatically.
- **Unified evidence:** shared QPC origin; source/receipt time, category/type, identities, duration, severity, uncertainty, raw reference and data quality. PresentMon's own timed termination bounds its child capture after parent failure.
- **Analysis:** dominant-stream frame statistics and lows, stall clustering, periodicity, one-to-one correlations, background exposure, phase-shift checks, competing explanations and explicit limitations. Notification delivery coincidences remain separate from precise occurrence matches. No automated finding claims a confirmed cause.
- **Desktop:** dedicated Investigator window on Performance, significant events during capture, timeline tracks and zoom, selectable stalls, local device labels, technical evidence, report import/export and controlled before/after comparison. Shared exports pseudonymize device IDs and omit local labels.
- **MCP:** eight additional tools and `meh://investigations`; validated running PID scope; same session lifecycle/store; paged evidence; reports and comparisons. Report retention is 220 KiB within the unchanged 300 KiB session / 32 MiB store limits. MCP disables raw exports/ETL and clearly reports detail omissions.

## Collector/API decisions

`CM_Register_Notification` avoids a hidden window and recurring inventory polling. SetupAPI/Configuration Manager provide inventory, driver metadata and hub/controller ancestry. Core Audio `IMMNotificationClient` handles endpoint/default changes. Enabled Event Log subscriptions and WMI extrinsic events add available driver/configuration/utility evidence; the collector never enables logs or changes permissions. Native callbacks copy bounded messages and update atomic counters; inventory resolution and publication occur on a worker.

Deep collection reuses the existing TraceEvent dependency. Absolute QPC aligns it with PresentMon. High-rate evidence is aggregated and bounded; aggregate durations are not treated as continuous activity. A paired interval is placed at its start, not shifted forward from its completion timestamp. Sampling enablement preserves the existing machine-wide sampling interval. Raw ETL is separate and private. Unresolved instruction pointers/module offsets remain unresolved.

## Automated and Windows results

| Check | Result |
| --- | --- |
| Solution Release build / C# type checking | Passed, zero warnings/errors |
| .NET analyzer lint (`dotnet format analyzers … --verify-no-changes`) | Passed |
| Core console suite | 65/65 test groups passed |
| WPF suite | Existing 39 synthetic layouts and interaction/close checks passed; Investigator pattern/selection/layout/idle-close checks passed |
| MCP backend suite | 14 groups passed |
| MCP session suite | 108 assertions passed |
| MCP protocol/security/investigation suite | 7/7 groups passed; 8/8 including the new real Windows investigation smoke |
| Real normal capture | 315 device identities; timed completion and cancellation passed; report reopened |
| Real PresentMon/WPF capture | 877 selected-stream frames, QPC raw data; 95.1% frame coverage in the final short run |
| Real MCP capture | Completed through existing service, 128 retained devices with explicit retention limits, first page 50; approximately 98 KiB persisted session; store reopen passed |
| Non-admin deep request | Explicit unavailable status; normal cleanup completed without elevation |
| Post-test cleanup | No PresentMon processes or Meh Tools ETW sessions remained; ETW session enumeration succeeded |

Deterministic fixtures cover USB arrival/removal, identity/topology, audio changes, QPC alignment, delayed/out-of-order/missing evidence, loss, scheduling pairs, module identity lifetimes, bounded queues, cancellation/failure cleanup, watchdog/recovery ownership, interruption, comparisons, report privacy/roundtrip, and MCP contracts/retention. Cases include eighteen spikes every 4.01 seconds with preceding USB events, unrelated random/dense USB events, CPU recurrence without spikes, unexplained stalls and competing candidates.

One final repeat, run concurrently with analyzers, hit an OBS WebSocket mock-disconnect failure in the pre-existing suite. All investigation groups passed in that run. The entire core suite was rerun by itself and passed 65/65; earlier full runs also passed. OBS WebSocket implementation/test code was not changed for this feature. The transient failure is recorded here rather than omitted from the validation history.

Review identified and fixed three issues before final validation: independent periodic timers could produce overstated independent-trial confidence; native callbacks could acquire the inventory status lock; and failed event publication was missing from dropped-data accounting. Regressions now cover phase sweeps, callback status isolation and publication loss. A separate regression verifies that paired CPU execution durations begin at switch-in.

## Measured overhead and its limits

The normal-device benchmark ran in a separate process, including subscription startup, inventory and disposal:

- 328.125 ms process CPU over 10.404 seconds: **0.1577% of total machine CPU capacity**.
- Private-memory increase: **16.785 MiB**.
- No device transitions occurred; zero local dropped notifications were observed. Upstream notification loss is **unknown**, not proven zero.
- Actual device event-delivery latency and event-storm overhead were **not measured** in this run. Callback-only source occurrence time remains unknown.
- Deep ETW event loss and overhead were **not measured**, because this session lacked elevation.

A separate three-second full-logger smoke measured 421.875 ms process CPU and a 40.98 MiB private-memory increase, including initialization. These are short startup-heavy observations, not sustained-workload guarantees. Process CPU excludes external PresentMon/watchdog, Windows services and kernel tracing. Hardware collector wall time and native callback/ETW aggregation cost are reported separately in each recording, with the scope of each measurement stated.

## Known limitations and remaining validation

1. Elevated ETW delivery, native stack/scheduling correctness against WPA, USB reset/error schemas and abrupt-termination watchdog behavior still need an elevated Windows acceptance run. Implemented deterministic/native-layout/ownership tests do not substitute for this.
2. Physical USB unplug/replug and default-audio switching were not induced. Use a harmless peripheral and follow the [Windows procedure](stutter-investigator.md#validation-on-windows).
3. This account denied WMI process trace subscriptions. Some USB/driver channels were absent or disabled. Reports retain these availability limitations; the application does not change them.
4. Device/audio callback timestamps identify receipt, not reset occurrence. Event-log clocks may skew. Normal notifications alone cannot establish precise USB-to-frame causality.
5. Deep USB/GPU summaries retain provider event metadata/counts, not decoded transfer payloads or reconstructed GPU dependencies. CPU stacks have at most 32 retained addresses; automatic symbol downloads/function attribution are not implemented.
6. High-rate deep observations are aggregated; timing confidence is conservative. Session and report limits can omit detail. Shared labels intentionally hide user-editable device names/serials; local labels are available in the desktop technical view.
7. The original WARDOGS cause remains unproven. No WARDOGS-specific timing or device assumption is encoded. The new tool can test recurrence/coincidence, but this task did not capture that actual workload.

Follow-up priorities are elevated golden-trace validation, physical device/audio event validation, sustained-load/event-storm overhead measurement, richer safe USB event decoding/topology attribution, optional local symbol resolution, and randomized repeated before/after experiments that better distinguish coincident periodic sources from a shared mechanism.

## Files changed for this feature

New shared diagnostics files under `src/JamesTools.Core/Diagnostics`:

- `InvestigationEvidence.cs`, `InvestigationAnalysis.cs`, `InvestigationReport.cs`, `InvestigationComparison.cs`, `InvestigationExports.cs`.
- `InvestigationDevices.cs`, `InvestigationDevices.Windows.cs`.
- `InvestigationDeepTrace.cs`, `InvestigationDeepTraceEtw.cs`, `InvestigationDeepTraceOwnership.cs`.
- `PerformanceLogger.Investigation.cs`.

Existing core integration files: `DiagnosticOptions.cs`, `DiagnosticRawWriter.cs`, `FrameReportCollector.cs`, `HardFaultTrace.cs`, `PerformanceLogger.cs`, `PerformanceLogger.Report.cs`.

MCP core: new `InvestigationCapture.cs`, `InvestigationRetention.cs`; updated `Contracts.cs`, `DiagnosticSessionService.cs`, `TelemetryData.cs`. MCP host: new `InvestigationAccess.cs`, `InvestigationTools.cs`; updated `DiagnosticAccess.cs`, `DiagnosticTools.cs`, `McpHost.cs`, `ToolRegistration.cs`.

Desktop: new `StutterInvestigatorWindow.cs`, `InvestigationTimeline.cs`, `MainWindow.Investigation.cs`; updated `MainWindow.xaml`.

Tests: new core `InvestigationAnalysisTests.cs`, `InvestigationDeviceTests.cs`, `InvestigationDeepTraceTests.cs`, `InvestigationIntegrationTests.cs`; new WPF `InvestigationUiTests.cs`; new MCP `InvestigationTests.cs`, `InvestigationFixture.cs`, `InvestigationWindowsSmoke.cs`. Updated corresponding runners, WPF `DiagnosticIntegration.cs`, MCP `EndpointTests.cs`, `TlsTests.cs`, `TransportTests.cs`.

Documentation: `README.md`, `docs/mcp.md`, `docs/mcp-architecture.md`, plus the three `docs/stutter-investigator*.md` documents. Existing user changes in packaging, media/OCR, other UI files and documentation were preserved.
