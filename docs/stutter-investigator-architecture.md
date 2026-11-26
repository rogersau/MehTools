# Stutter Investigator architecture audit

Meh Tools is a Windows .NET 10 application with a WPF desktop, a shared `JamesTools.Core` library and a separate ASP.NET/stdio MCP host. Tests are executable console suites, including WPF and MCP integration suites. CI builds the solution and runs each suite. The C# compiler and .NET analyzers check the application code.

## Existing infrastructure to retain

- `PerformanceLogger` owns manual performance recording, cancellation, system/process sampling, NVIDIA collection, PresentMon, hard faults and text/JSON/raw-CSV exports. It is the owner of investigation capture too.
- `FrameReportCollector` launches bundled PresentMon with a unique ETW session, filters process identities and separates swapchains. It requests `--qpc_time`; `FrameCaptureData` understands raw QPC ticks and QPC milliseconds. Independent relative timestamps remain receipt-time approximations. Existing `FrameAnalysis`, `FramePacing`, `FrameDiagnosis` and `StutterInvestigator` retain frame summaries and stall context.
- `DiagnosticCounters`, `ProcessActivity`, Windows process APIs and PDH collect system and selected-process load, I/O, memory and disk readings. Samples include collection and counter windows. `NvidiaTelemetry` uses NVML, with a limited nvidia-smi fallback. CPU temperature/effective clocks are explicitly unavailable without a supported provider. Normal hardware sampling is roughly every two seconds; NVIDIA roughly six seconds, with existing optional faster collection.
- `HardFaultTrace` already uses Microsoft TraceEvent and absolute QPC. Deep mode must avoid running a competing hard-fault kernel session.
- Format-3 reports combine readable text, structured JSON and bounded timeline CSV; `DiagnosticRawWriter` keeps fuller local data. `ReportTimeline`, `DiagnosticComparison` and performance history already support viewing and controlled comparisons. Add optional investigation data without breaking old report readers or existing CSV columns.
- `SessionTracker`/`SessionLibraryStore` associate game sessions and artifacts; these are not replaced. Rolling hardware capture remains a separate existing feature, not a source of new investigation polling.
- The WPF Performance page and timeline window provide navigation and plotting conventions. A focused Investigator window uses `PerformanceLogger`, rather than introducing another sampler or recording manager.
- The existing MCP `DiagnosticAccess`, `DiagnosticSessionService`, bounded `SessionStore` and tool registration own access, sessions and transport. Extend those paths with the same core investigation analysis; do not create another MCP server.

## Minimal additions

1. One explicit QPC clock passed through existing frame capture and new device/deep collectors. Every evidence item retains source, category, delivery time, timestamp basis, precision/availability and loss information.
2. An event-driven native Windows device/audio/event-log collector with a session-start inventory. Device identifiers are pseudonymized before portable export.
3. Optional, bounded TraceEvent collection with named-session ownership, cleanup/recovery and explicit privilege checks. No system-wide WPR cancellation, injection, driver installation or implicit elevation.
4. Bounded evidence retention and statistical analysis attached to the existing logger, preserving full-versus-retained coverage and refusing causal claims from coincidence.
5. A WPF investigation workflow, report sections and MCP tools using the same recording and analysis implementation.

The working tree contained user changes in packaging, bundled tools, OCR/media, UI tests and documentation before this task; those changes are retained.
