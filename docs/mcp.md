# MehTools MCP

MehTools exposes Windows diagnostics through the official MCP C# SDK. The separate `MehTools.Mcp.exe` host reuses Core diagnostic services; the WPF app does not need to remain open. Stdio is the default transport. HTTP is an explicit, authenticated opt-in. Diagnostic reads do not change power plans, drivers, game files, recording settings, or launch arbitrary commands. Session start/stop writes bounded diagnostic history for the current user.

## Install and enable

Use Windows and the .NET 10 ASP.NET Core runtime for a framework-dependent MCP host. The desktop application additionally requires the .NET 10 Windows Desktop runtime; that runtime alone does not supply ASP.NET Core. The application publish includes `tools/mcp/MehTools.Mcp.exe`; keep its adjacent dependencies together. To build from source:

```powershell
dotnet build src/JamesTools.Mcp/JamesTools.Mcp.csproj -c Release
$mcp = (Resolve-Path src/JamesTools.Mcp/bin/Release/net10.0-windows/MehTools.Mcp.exe).Path
& $mcp --enable
& $mcp --status
```

Packaged installation example:

```powershell
$mcp = 'C:\Program Files\Meh Tools\tools\mcp\MehTools.Mcp.exe'
& $mcp --enable
& $mcp --status
```

Enablement is persisted, but does not start a host. An MCP client launches its own stdio host. Configuration defaults to `%LOCALAPPDATA%\JamesTools\Mcp\config.json`; diagnostic history belongs to the same Windows user's JamesTools app data. `--config <file>` chooses a configuration file only. It does not relocate the application data. `--disable` prevents future starts; stop any existing host separately. Restart after configuration changes. The detailed administrative/UI contract is [CONFIGURATION.md](../src/JamesTools.Mcp/CONFIGURATION.md).

## Connect Codex over stdio

The locally installed CLI's `codex mcp add --help` was checked alongside the [official OpenAI MCP configuration guide](https://developers.openai.com/codex/mcp/) on 2026-11-15. Codex supports local stdio commands and Streamable HTTP URLs, with configuration in `~/.codex/config.toml` or trusted project `.codex/config.toml`. See also the [official CLI command reference](https://learn.chatgpt.com/docs/developer-commands#codex-mcp).

```powershell
codex mcp add mehtools -- 'C:\Program Files\Meh Tools\tools\mcp\MehTools.Mcp.exe'
codex mcp list
```

Equivalent TOML (replace the installation path):

```toml
[mcp_servers.mehtools]
command = 'C:\Program Files\Meh Tools\tools\mcp\MehTools.Mcp.exe'
startup_timeout_sec = 30
tool_timeout_sec = 45
```

For a custom configuration, add `args = ['--config', 'C:\Users\you\MehTools-mcp.json']`. Do not put `--http` in a stdio launch command. Stdout is reserved for protocol traffic; logs and startup errors go to stderr. Administrative commands such as `--status` print JSON and exit, so do not use them as the client launch command.

## Secure opt-in HTTP

Create and enable a configuration, then edit its `http` section using the exact fields in [CONFIGURATION.md](../src/JamesTools.Mcp/CONFIGURATION.md). Defaults bind `127.0.0.1:17841`, route `/mcp`. Supply a cryptographically random bearer secret of at least 32 UTF-8 bytes through the configured environment variable. Set the same secret in the server process environment and the client environment; do not paste it into TOML or checked-in files.

```powershell
# MEHTOOLS_MCP_TOKEN must already be populated securely in this process.
& $mcp --http
```

Configure the client in another process with that environment variable available:

```powershell
codex mcp add mehtools-http --url http://127.0.0.1:17841/mcp --bearer-token-env-var MEHTOOLS_MCP_TOKEN
```

```toml
[mcp_servers.mehtools-http]
url = "http://127.0.0.1:17841/mcp"
bearer_token_env_var = "MEHTOOLS_MCP_TOKEN"
startup_timeout_sec = 30
tool_timeout_sec = 45
```

Every HTTP request requires the bearer token. Host headers must match the exact allowlist; an empty host list permits the bind IP and `localhost` for loopback. Requests containing Origin are rejected unless the exact origin is configured. Allowlist entries cannot contain wildcards. Nonloopback listeners require a PFX certificate and HTTPS; the certificate password can come from a separate environment variable. A loopback listener can also sit behind an authenticated TLS proxy such as Tailscale Serve with appropriate tailnet ACLs. Preserve bearer authorization and allow only the exact proxy Host. Do not publish a plaintext bearer endpoint. Built-in HTTP authentication is a local bearer secret, not an OAuth authorization server.

## Tools and arguments

All successful tool outputs are structured JSON with an advertised output schema. Names and argument casing are exact. Inputs not shown below are rejected. Date/time arguments must be ISO 8601 UTC (`Z` or `+00:00`), and cannot be more than one minute in the future. The client can discover the actual schemas through `tools/list` and supported categories/profiles through `meh://capabilities`.

| Tool | Arguments | Successful result |
| --- | --- | --- |
| `get_system_info` | none | `SystemInventory`: hardware, Windows, drivers and per-source availability |
| `get_live_metrics` | none | `MetricsSnapshot`: current sensor values, timestamps, units and availability |
| `run_diagnostics` | required `category`: a capability category | `DiagnosticResult`: status, findings, evidence, measurements, warnings/errors |
| `get_historical_metrics` | optional `fromUtc`, `toUtc`, `metrics` (array), `limit` (default 100), `comparePrevious` (default false) | `HistoricalMetrics`: samples, current and previous statistics, truncation/warnings |
| `inspect_game` | required `game`: detected game name or exact catalog installation root | `GameInspection`: installed components and distinct runtime evidence |
| `get_event_logs` | optional `fromUtc`, `toUtc`, `source`, `severity`, `eventIds`, `category`, `limit` (default 50) | `EventLogResult`: bounded Windows event summaries and truncation |
| `get_health_report` | none | `HealthReport`: assessment, evidence-backed findings and limitations |
| `start_diagnostic_session` | required integer `duration` (seconds), `profile`; optional integer `samplingIntervalSeconds` | `DiagnosticSession`: ID and initial capture state; returns promptly |
| `get_diagnostic_session` | required `id`: nonempty UUID | `DiagnosticSession`: bounded snapshots and aggregate statistics |
| `stop_diagnostic_session` | required `id`: nonempty UUID | `DiagnosticSession`: final capture state; repeated stop is safe |
| `start_stutter_investigation` | required `duration` (1–300 seconds); optional `processId`, `deepInvestigation` (false), `deepUsbTracing` (false), `stutterThresholdMs` (33.333, range 1–10000) | Existing `DiagnosticSession` with explicit investigation options; capture starts in background |
| `stop_stutter_investigation` | required `id` | Same idempotent session stop; `stop_diagnostic_session` also stops investigations |
| `list_investigation_devices` | required `id`; optional `offset` (0), `limit` (50) | Paged captured devices, redacted identities and parent/hub/controller topology |
| `get_recent_device_events` | required `id`; optional `offset`, `limit`, exact `source` (≤128 characters), `category` (≤64) | Paged timestamp-ordered retained device/deep events with coverage and omissions |
| `get_performance_spikes` | required `id`; optional `offset`, `limit` | Paged slow frame intervals with original timing and stream identity |
| `get_stutter_correlations` | required `id`; optional `offset`, `limit` | Paged event/stall association summaries, background chance and confidence limitations |
| `get_investigation_report` | required `id` | Structured metrics, coverage, periodicity, collectors and correlation summaries; detail arrays use the paged tools |
| `compare_investigation_sessions` | required `beforeId`, `afterId`; optional `changeDescription` (≤256 characters) | Finished-session metric/rate deltas, group/correlation changes and comparison limitations |

Event severity is `critical`, `error`, `warning`, `information`, or `verbose`. Event IDs are integers from 0 to 65535, with at most 32 per query. Event queries default to the last 24 hours. Both event/history ranges are limited to seven days; `fromUtc` must not exceed `toUtc`. Event limit is 1–200; history limit is 1–500. History accepts at most 32 exact metric names. Metric/provider/category arguments are validated, not executable expressions or queries. Game requests accept a discovered name (including punctuation) or the exact root of a discovered/configured installation. The backend rejects traversal, device/network paths, alternate data streams, reparse points and uncatalogued roots; there is no arbitrary scan-root access.

Diagnostic categories are `cpu`, `gpu`, `memory`, `storage`, `network`, `thermals`, `drivers`, and `windows`. Event categories are `cpu`, `gpu`, `memory`, `storage`, `network`, `drivers`, `windows`, plus `system`/`application` to choose one log. Omitted event severity selects critical/error/warning. Event provider names are a fixed allowlist appropriate to the selected category: WHEA/Kernel-Processor-Power, Display/NVIDIA/AMD display drivers, Resource-Exhaustion-Detector, disk/Ntfs/storahci/stornvme/StorPort, NDIS/TCPIP/DNS-Client, Kernel-PnP/DriverFrameworks-UserMode, and Kernel-Power/EventLog/WER/Application Error/Windows Error Reporting/.NET Runtime. Use exact Windows provider names (for example `Microsoft-Windows-WHEA-Logger`); free-form XPath and event payload extraction are unavailable. Summaries omit arbitrary event message data.

## Resources

| URI | Content |
| --- | --- |
| `meh://inventory` | Current `SystemInventory` |
| `meh://sensors` | Current `MetricsSnapshot` |
| `meh://capabilities` | `DiagnosticCapabilities`: categories, profiles, limitations |
| `meh://report/latest` | Last health report generated in this host; unavailable before the first report |
| `meh://sessions` | At most 100 recent session summaries; use the session tool for full samples |
| `meh://investigations` | Retained investigation session summaries, including explicit PID/deep options; detail is read by session ID |

Resources use `application/json` and the same backend, session store and limits as tools. URIs do not provide a file browser. The latest-report cache is per host; different stdio client processes can have different latest reports.

## Interpreting results

Versioned diagnostic records use `schemaVersion: 1`. Timestamps end in UTC offset zero. An absent measurement is JSON `null`; it is never a synthetic zero. Metric records carry `name`, nullable numeric `value`, explicit `unit`, `timestampUtc`, `status`, `source`, and an optional `reason`. Statistics carry `name`, `unit`, numeric `count`, and nullable `minimum`, `maximum`, `average`; an unsupported-only series has count zero and null statistics. Components have a separate availability status/reason. Optional strings, versions, installation directory, end time and explanations remain nullable when unknown.

The v1 output field reference below uses `?` for a nullable value and `[]` for an array. `V` means `schemaVersion`; `T` means `timestampUtc`. UUIDs and UTC timestamps are JSON strings. `properties` values are strings or null, rather than arbitrary nested JSON. The authoritative machine-readable schemas are advertised by the server.

| Record | Fields |
| --- | --- |
| `Availability` | `status`, `reason?` |
| `Metric` | `name`, `value?` (number), `unit`, `timestampUtc`, `status`, `source`, `reason?` |
| `MetricsSnapshot` | `V`, `T`, `metrics: Metric[]`, `warnings: string[]` |
| `InventoryComponent` | `category`, `name`, `properties: object<string,string?>`, `availability: Availability` |
| `SystemInventory` | `V`, `T`, `components: InventoryComponent[]`, `warnings: string[]` |
| `Finding` | `component`, `severity`, `observation`, `explanation?`, `confidence`, `evidence: string[]`, `nextChecks: string[]` |
| `DiagnosticResult` | `V`, `T`, `category`, `status`, `findings: Finding[]`, `measurements: Metric[]`, `warnings: string[]`, `errors: string[]` |
| `HealthReport` | `V`, `T`, `assessment`, `findings: Finding[]`, `limitations: string[]` |
| `GameComponent` | `relativePath`, `kind`, `version?`, `evidence`, `runtimeStatus` |
| `GameInspection` | `V`, `T`, `status`, `name?`, `installationDirectory?`, `components: GameComponent[]`, `warnings: string[]` |
| `DiagnosticEvent` | `timestampUtc`, `source`, `eventId` (integer), `severity`, `category`, `summary` |
| `EventLogResult` | `V`, `T`, `status`, `events: DiagnosticEvent[]`, `truncated` (boolean), `warnings: string[]` |
| `DiagnosticCapabilities` | `V`, `categories: string[]`, `profiles: string[]`, `limitations: string[]` |
| `MetricStatistics` | `name`, `unit`, `count` (integer), `minimum?`, `maximum?`, `average?` (numbers) |
| `DiagnosticSession` | `V`, `id` (UUID), `profile`, `startedUtc`, `endedUtc?`, `status`, `durationSeconds`, `samplingIntervalSeconds` (integers), `samples: MetricsSnapshot[]`, `statistics: MetricStatistics[]`, `notableEvents: DiagnosticEvent[]`, `findings: Finding[]`, `warnings: string[]`, additive `investigation?`, `investigationOptions?` |
| `InvestigationPage<T>` | `V`, `sessionId`, `status`, `offset`, `retainedCount`, `nextOffset?`, `items: T[]`, `coverage`, `collectors`, `limitations: string[]` |
| `HistoricalMetrics` | `V`, `samples: MetricsSnapshot[]`, `statistics: MetricStatistics[]`, `previousStatistics: MetricStatistics[]`, `truncated` (boolean), `warnings: string[]` |

Session lifecycle statuses are `recording`, `completed`, `stopped`, `failed`, and `interrupted`. These describe capture lifecycle separately from each sensor's availability status. Some backend operations can report `partial` when only part of a source was readable. Completed-session aggregates cover accepted samples over the capture; history aggregates cover matching retained history, not samples that retention has already discarded. Legacy app performance-history records can contribute when the session service receives the shared app-data root.

Units travel with each reading and statistic. Common units include `%`, `°C` for temperature, `W` for power, `MHz` for clocks, `MiB`/`bytes` for memory, `ms` for latency/frame time, `MiB/s`, `bytes/s`, `pages/s`, `rpm`, `state`, and `boolean`. Do not combine differently named or differently unitized series. Consult the returned unit rather than inferring it from a metric name. Collector-level failure sentinels can have an empty unit because no physical quantity was observed. Readings and availability use `available`, `unsupported`, `permission_denied`, `unavailable`, and `failure`; treat the explicit reason and null value as authoritative. MCP operation errors are bounded safe errors with `invalid_input`, `unsupported`, `permission_denied`, `unavailable`, `timeout`, `cancelled`, or `failure`. An unsupported sensor is a reported limitation, not evidence of healthy or faulty hardware.

Game DLL presence proves installation only. A DLSS/FSR/XeSS-related file does not establish runtime activation, selected quality mode, frame generation, or a performance gain. Findings distinguish observations, possible explanations, confidence, supporting evidence and next checks. Health assessments should not turn missing telemetry into a diagnosis.

## Capture profiles and workflow

Profiles are presets for diagnostic capture, not workload generators. Use the names advertised in `meh://capabilities`; the current session engine supports `general`, `gaming`, `cpu`, `gpu`, and `storage`. Duration is 1–3600 seconds and the optional sampling interval is 1–30 seconds. Omit the interval for profile defaults: general/storage use five seconds; gaming/cpu/gpu use one second. Profiles prioritize relevant metrics in the bounded capture window. Capture records existing telemetry without launching a game, stress test or arbitrary process. Frame-time data requires an explicitly scoped game capture; a generic MCP session cannot establish frame timing and reports that limitation.

Example initial investigation:

1. Read `meh://capabilities` and call `get_system_info` and `get_live_metrics`.
2. Call `run_diagnostics` with `{"category":"cpu"}`. Inspect confidence and unavailable-source warnings.
3. Call `start_diagnostic_session` with `{"duration":60,"profile":"gaming","samplingIntervalSeconds":5}` before reproducing the issue manually.
4. Record its ID. Call `get_diagnostic_session` with `{"id":"<returned UUID>"}` during capture; call `stop_diagnostic_session` with the same ID to finish early.
5. Query `get_historical_metrics` with `{"limit":20,"comparePrevious":true}` and explicit UTC bounds when comparing equal windows.
6. Request `get_event_logs` with `{"severity":"error","limit":20}` around that UTC interval, then call `get_health_report`.

For an installation question, call `inspect_game` with `{"game":"<detected game name>"}` and examine component evidence/runtime status. An exact catalog root can disambiguate duplicate names. If the game is undiscovered, configure it through the app's normal library/settings flow; the MCP service does not accept an arbitrary scan root.

### Stutter investigations

Call `start_stutter_investigation` with `{"duration":60,"processId":1234}` for any accessible running application, including applications outside the game library. The PID is validated against Windows' accessible process image path before acceptance and again before capture startup. The executable is never launched. The shared logger selects processes with that validated image path, so multiple running instances can contribute frames. Omit `processId` for system hardware/device capture without application frame timing. Inaccessible, exited and network-path targets are rejected. No executable, PresentMon, command, import or output path is accepted by these tools. PresentMon uses the existing bundled locator, including the installed MCP host's sibling tool location.

Set `deepInvestigation:true` only when explicitly requesting deeper local ETW evidence; `deepUsbTracing:true` additionally requires deep mode. Normal duration is 1–300 seconds; Deep duration is 30–300 seconds, with early stopping available. Neither option requests elevation. Missing privileges/providers/PresentMon appear in collector availability, loss and limitations; unavailable evidence does not identify a healthy or faulty device. Network probes are disabled. MCP sets `DiagnosticOptions.WriteRawExports=false`: **no raw ETL or raw diagnostic export is retained**. Only bounded evidence in the normal session store survives.

Use the returned UUID with `get_diagnostic_session` during capture or `stop_diagnostic_session` / `stop_stutter_investigation` to finish early. Start acceptance is durable before collectors start. Initial report reads may be unavailable until the first snapshot; live analysis uses the latest 1000 frames/events and is explicitly provisional. Cancellation of a request after acceptance does not cancel the recording. One investigation per host is allowed, sharing the store's existing four-recording limit with ordinary sessions. Duration expiry, stop and host disposal all finalize the same recording once.

After stop, call `get_investigation_report` for summary statistics and use the device/event/spike/correlation tools for detail. Their `offset` is 0–30000 and `limit` is 1–200 (default 50). Follow `nextOffset` until null: the byte budget may return fewer items than requested. `retainedCount` describes the filtered retained detail, not the complete recording. Live cursors can shift; stop before paging when stable results matter. Correlations preserve aggregate event/stall/chance statistics before detail reduction; match examples are bounded. Coincidence is never causal proof.

Reports use a 220 KiB budget inside the unchanged 300 KiB session limit and 32 MiB store budget. At most 512 events, 256 spikes, 64 clusters, 128 devices, 16 hardware samples, 256 correlations (four match examples each), 256 event-periodicity groups and 32 collector statuses enter the report budget; detail can be reduced further to fit. Coverage counters and retained/input counts in limitations describe omissions. Summary metrics survive reduction; hardware/device details and event pages are not complete raw captures. Event/device strings pass through the portable report sanitizer. Session summaries omit report detail. Reports inherit the same twelve-session/seven-day retention, backup recovery and interrupted-owner semantics as ordinary sessions.

Compare two finished UUIDs with `compare_investigation_sessions`, describing a deliberate change in `changeDescription`. The shared comparison checks thresholds, warm-up, exposure and declared conditions. Event rate comparisons use retained group summaries before MCP event-detail reduction; missing groups remain unobserved. Comparison detail is bounded to 220 KiB and its limitations disclose any possible omission. Workload and scene controls are not inferred by MCP, and a before/after difference does not establish causation.

## Limits and troubleshooting

Each operation has a 30-second deadline and the host permits at most four concurrent operations. Admission slots remain held until underlying work ends, including timed-out work. Requests are limited to 64 KiB and responses to 1 MiB. Oversized results return a bounded error rather than partial JSON. Oversized chunked HTTP uploads can be rejected by closing the connection before a response is delivered; retry only with a smaller request. HTTP permits at most eight admitted requests, returning 429 at capacity, and at most 32 connections. Stdio admits at most 32 frames per second with backpressure. The host also uses a smaller conservative typed-payload budget to allow for MCP encoding overhead. Configuration files are limited to 64 KiB. Live telemetry retains up to 128 prioritized metrics within 96 KiB. A session retains at most 40 snapshots of 6 KiB each, 64 aggregate series, and a 300 KiB record; history responses are also at most 300 KiB. At most four recordings and twelve retained sessions share a storage directory, with seven-day retention and a 32 MiB storage ceiling. Truncation and unavailable data are explicit. Session collection is bounded independently of requested duration: a maximum-duration capture does not return an ever-growing raw sample array. Persisted sessions are beneath `%LOCALAPPDATA%\JamesTools\mcp-sessions`.

If initialization fails, run `--status` and `--help` outside the MCP client, check enablement and stderr, confirm the executable and runtime exist, and restart the client after changing configuration. Do not debug by writing ordinary logs to stdio stdout. For HTTP 401, check that both processes have the same token and the server token meets the length requirement. For HTTP 403/400, inspect exact Host/Origin allowlists. For a bind failure, use another configured port or stop the existing listener. Never solve a sensor permission issue by disabling endpoint authentication. Driver-dependent sensors may remain unsupported; an administrator context does not guarantee sensor support. Empty history can mean no completed capture exists or a retention/query window excluded it. Use fewer records or a shorter query if the response budget is exceeded.

## Validation commands

The framework-free harness uses the official SDK client against real stdio and Streamable HTTP servers. Test-only backend injection uses the production registration seam; the production executable has no fixture switch.

```powershell
dotnet build JamesTools.sln -c Release
dotnet run --project tests/JamesTools.Tests -c Release --no-build
dotnet run --project tests/JamesTools.UiTests -c Release --no-build
dotnet run --project tests/JamesTools.Mcp.BackendTests -c Release --no-build -- --windows-smoke
dotnet run --project tests/JamesTools.Mcp.SessionTests -c Release --no-build
dotnet run --project tests/JamesTools.Mcp.Tests/JamesTools.Mcp.Tests.csproj -c Release
dotnet run --project tests/JamesTools.Mcp.Tests/JamesTools.Mcp.Tests.csproj -c Release --no-build -- --hardware
```

The first command exercises deterministic fixtures, protocol/schema validation, sessions, response bounds, HTTP security and local HTTPS with an ephemeral self-signed PFX pinned by the test client. It changes no system certificate trust. `--hardware` additionally launches the actual Windows diagnostic host for read-only inventory, telemetry, diagnostic and session checks. It writes diagnostic history but does not modify system configuration. The hardware log reports observed sensor availability; it does not claim that unobserved unsupported hardware paths passed. Remote nonloopback/Tailscale Serve deployment, tailnet ACLs and third-party client setup have not been exercised by this harness. See [mcp-architecture.md](mcp-architecture.md) for design boundaries and test coverage.

Validated on Windows with .NET SDK 10.0.404 on 2026-11-15:

```powershell
dotnet build tests/JamesTools.Mcp.Tests/JamesTools.Mcp.Tests.csproj -c Release --no-restore --verbosity quiet
dotnet run --project tests/JamesTools.Mcp.Tests/JamesTools.Mcp.Tests.csproj -c Release --no-build -- --hardware
```

The build completed with zero warnings/errors; all seven test groups passed (`contracts`, `sessions`, `stdio`, `http`, `https`, `configuration`, `hardware`). Both Windows live checks observed 35 available and five unsupported sensors. These counts describe that machine/run, not a required sensor inventory or a promise of support on another PC. The harness also accepts `--list` or `--only <group>` to isolate a regression. The HTTPS group tests local PFX/TLS behavior; no remote proxy or tailnet was configured.
