# MCP configuration and desktop integration contract (v1)

The packaged executable is `tools/mcp/MehTools.Mcp.exe`. All paths below are for the current Windows user. Default app data is `%LOCALAPPDATA%\JamesTools`.

## Configuration

Default file: `%LOCALAPPDATA%\JamesTools\Mcp\config.json`. `--config <absolute-or-relative-path>` overrides only the configuration file, not application data or diagnostic storage. The canonical config path is limited to 2048 characters so its status artifact remains bounded. `inspect_game` may identify an exact installation root already validated against the detected game catalog; no MCP operation grants arbitrary filesystem access or accepts commands.

JSON uses these exact, case-sensitive camelCase fields. Unknown fields fail validation. The UI may atomically write this file or invoke `--enable` / `--disable`. Omitted fields take the defaults shown:

```json
{
  "schemaVersion": 1,
  "enabled": false,
  "http": {
    "address": "127.0.0.1",
    "port": 17841,
    "tokenEnvironmentVariable": "MEHTOOLS_MCP_TOKEN",
    "certificatePath": null,
    "certificatePasswordEnvironmentVariable": null,
    "allowedHosts": [],
    "allowedOrigins": []
  }
}
```

`address` must be an IP literal without a scope ID. `port` must be 1024–65535. Loopback is the default. Nonloopback binding requires an absolute certificate file path (PFX) and HTTPS. HTTP mode always requires a bearer secret, read only from `tokenEnvironmentVariable`, of 32–4096 visible ASCII bytes. Secrets are never stored in config or status. Certificate passwords, if needed, come only from the named environment variable. Hosts must be exact DNS names or IP literals, without ports, schemes, or wildcards. Empty `allowedHosts` means the bind address plus `localhost` for loopback. Origins must be exact HTTP(S) origins with no path, query, fragment, or credentials; empty means reject every request containing Origin. Never configure a foreign browser origin unless intentionally authorizing it. Nonloopback origins require HTTPS.

All configuration changes require host restart. `--disable` prevents future startup; the desktop should stop any process it launched to stop an already running host. Enabled does not autostart anything. The desktop/client must launch the host explicitly. Stdio is default and opens no listener; `--http` explicitly selects HTTP at `/mcp`. Do not pass `--http` to an MCP client's stdio command.

## Administrative CLI

- `--help`: human-readable help on stdout, exit 0.
- `--enable` / `--disable`: atomically preserve and update config, JSON administrative result on stdout, exit 0. Cannot combine with startup or status options except `--config`.
- `--status [--config path]`: one JSON object on stdout, no listener and no backend creation, exit 0 if configuration is valid. Does not need a bearer token. Disabled configuration is a normal status.
- Normal startup: stdout exclusively MCP protocol (stdio); logs and startup failures go to stderr. Invalid configuration or startup failures exit nonzero. Disabled startup exits nonzero.

The exact status object is:

```json
{
  "schemaVersion": 1,
  "configPath": "C:\\Users\\example\\AppData\\Local\\JamesTools\\Mcp\\config.json",
  "configurationExists": true,
  "enabled": true,
  "restartRequiredForChanges": true,
  "hosts": [
    {
      "processId": 1234,
      "processStartedUtc": "2026-11-15T01:00:00+00:00",
      "startedUtc": "2026-11-15T01:00:01+00:00",
      "transport": "stdio",
      "endpoint": null
    }
  ],
  "warnings": []
}
```

`hosts` includes only hosts for the selected canonical config path, with a live matching PID/process-start timestamp and a held status-file lease. Empty hosts means no host was verified; configuration alone never implies running. Multiple stdio clients may create multiple hosts. Runtime artifacts are bounded small JSON files beneath `%LOCALAPPDATA%\JamesTools\Mcp\runtime`, protected to the current user and SYSTEM. No tokens or diagnostic data go there. Hosts remove their own artifact on clean shutdown. Startup prunes recognized stale artifacts only after confirming no creator lease is held. It preserves held/live and unrecognized files and refuses startup if the directory remains at the 256-artifact cap. Pruning and creation share a cross-process lock. Stale files are ignored by status. Access/verification failures are reported in `warnings`, not as verified hosts. HTTP endpoint reflects the actual bind scheme/address/port. A loopback HTTP endpoint may be published only through an authenticated TLS proxy (for example Tailscale Serve with tailnet ACLs), retaining bearer authorization and configuring the proxy's exact Host; never expose plaintext bearer traffic publicly.

## Test integration

`McpHost.AddDiagnostics(IServiceCollection, IDiagnosticBackend, IDiagnosticSessions)` registers the production tools/resources with explicitly supplied services. `McpHost.BuildStdioHost(HostApplicationBuilder, Stream input, Stream output)` builds the real bounded stdio host after registration. Neither method needs Windows diagnostic construction. These are injection seams for tests, not runtime fixture switches. The caller owns/disposes injected backend and session services.

The ten tool names and all successful result payloads use the frozen Core v1 contracts. Runtime validation enforces bounded inputs. Every tool advertises an SDK-generated output schema and returns typed structured content. Execution failures are safe MCP tool errors with statuses `invalid_input`, `unsupported`, `permission_denied`, `unavailable`, `timeout`, `cancelled`, or `failure`; exception messages, stack traces, tokens, and arbitrary file paths are not exposed. Resources return JSON and use the same backend/session services.

Tool registration explicitly preserves null fields and requires JSON numbers for numeric arguments (numeric strings are rejected). The generated inventory schema permits null dictionary values, matching `InventoryComponent.Properties`. Nullable measurements are never replaced with zeros or placeholders.

## Operational bounds

Both transports bound incoming requests to 64 KiB and outgoing protocol messages to 1 MiB. Individual successful typed payloads have a conservative one-third-MiB budget to leave room for MCP text plus structured content. Oversized results return a safe `unavailable` tool error instead of truncated or invalid JSON. Tools have a 30-second deadline and four outstanding backend-operation slots; a timed-out backend retains its slot until it actually finishes. Dispatch is capped at eight requests. Stdio validates whole newline-delimited frames before parsing, applies 32-frames/second backpressure, and times out incomplete frames after 15 seconds. Stdout writes have a 10-second deadline. HTTP limits headers to 16 KiB/32 headers, connections to 32, concurrent requests to eight, and each authenticated request to 40 seconds. Legacy SSE and subscriptions are disabled; HTTP is stateless.

Sessions accept duration 1–3600 seconds and optional sampling interval 1–30 seconds. Omitted intervals reach Core unchanged, preserving gaming/cpu/gpu 1-second and general/storage 5-second defaults. Core bounds retained samples and aggregate data. Both history and event ranges are at most seven days; event queries default to 24 hours ending at the supplied `toUtc` or now. History limits are 1–500 samples, events 1–200, metric filters up to 32 names, event IDs up to 32. Explicit timestamps must use UTC (zero offset). `meh://sessions` is capped to 100 summaries. `meh://report/latest` is the last health report generated in this host, and is unavailable until one is created. Core session storage receives the app-data root so its `mcp-sessions` directory and legacy `performance-history.json` index remain shared with the app.

The shipped `CONFIGURATION.md` is copied alongside the executable in build and publish output.
