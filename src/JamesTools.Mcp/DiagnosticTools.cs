using System.ComponentModel;
using JamesTools.Core.Mcp;
using ModelContextProtocol.Server;

namespace JamesTools.Mcp;

[McpServerToolType]
public sealed class DiagnosticTools(DiagnosticAccess access)
{
    [McpServerTool(Name = "get_system_info", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Read system hardware/software inventory with source availability and limitations.")]
    public Task<SystemInventory> GetSystemInfo(CancellationToken cancellationToken) => access.Inventory(cancellationToken);

    [McpServerTool(Name = "get_live_metrics", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Read a current sensor snapshot. Unavailable measurements remain null with explicit status.")]
    public Task<MetricsSnapshot> GetLiveMetrics(CancellationToken cancellationToken) => access.Sensors(cancellationToken);

    [McpServerTool(Name = "run_diagnostics", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Run an existing diagnostic category from meh://capabilities. No commands or file paths accepted.")]
    public Task<DiagnosticResult> RunDiagnostics([Description("Category from meh://capabilities, at most 64 characters.")] string category, CancellationToken cancellationToken) => access.Diagnose(category, cancellationToken);

    [McpServerTool(Name = "get_historical_metrics", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Query recorded metrics with statistics, optional prior-window comparison, and truncation indicators. Maximum range 7 days and 500 samples.")]
    public Task<HistoricalMetrics> GetHistoricalMetrics(CancellationToken cancellationToken,
        [Description("Inclusive UTC start (offset must be zero).")] DateTimeOffset? fromUtc = null,
        [Description("Inclusive UTC end (offset must be zero).")] DateTimeOffset? toUtc = null,
        [Description("Up to 32 exact metric names, each at most 128 characters.")] string[]? metrics = null,
        [Description("Maximum samples, 1 through 500.")] int limit = 100,
        [Description("Include statistics for the preceding equal time window.")] bool comparePrevious = false) =>
        access.History(new(fromUtc, toUtc, metrics, limit, comparePrevious), cancellationToken);

    [McpServerTool(Name = "inspect_game", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Inspect an installed game by detected name or exact detected installation root. The backend validates against installed games; arbitrary filesystem traversal is unavailable.")]
    public Task<GameInspection> InspectGame([Description("Detected game name (punctuation allowed) or exact detected installation root, at most 512 characters without control characters.")] string game, CancellationToken cancellationToken) => access.Game(game, cancellationToken);

    [McpServerTool(Name = "get_event_logs", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Read bounded Windows diagnostic event summaries. Defaults to 24 hours ending at toUtc (or now); maximum range 7 days and 200 events.")]
    public Task<EventLogResult> GetEventLogs(CancellationToken cancellationToken,
        [Description("Inclusive UTC start, offset zero.")] DateTimeOffset? fromUtc = null,
        [Description("Inclusive UTC end, offset zero.")] DateTimeOffset? toUtc = null,
        [Description("Optional event provider name, at most 128 characters.")] string? source = null,
        [Description("critical, error, warning, information, or verbose.")] string? severity = null,
        [Description("Up to 32 Windows event IDs from 0 through 65535.")] int[]? eventIds = null,
        [Description("Optional event category, at most 64 characters.")] string? category = null,
        [Description("Maximum events, 1 through 200.")] int limit = 50) =>
        access.Events(new(fromUtc, toUtc, source, severity, eventIds, category, limit), cancellationToken);

    [McpServerTool(Name = "get_health_report", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Create a health assessment using existing diagnostics, evidence, confidence, and limitations. Updates meh://report/latest.")]
    public Task<HealthReport> GetHealthReport(CancellationToken cancellationToken) => access.Report(cancellationToken);

    [McpServerTool(Name = "start_diagnostic_session", UseStructuredContent = true, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Start bounded background metric capture. Returns promptly; query or stop by its ID. Duration 1–3600 seconds, interval 1–30 seconds; the session service bounds retained data.")]
    public Task<DiagnosticSession> StartDiagnosticSession([Description("Capture duration in seconds, 1 through 3600.")] int duration,
        [Description("Profile from meh://capabilities.")] string profile, CancellationToken cancellationToken,
        [Description("Sampling interval 1–30 seconds; omit to use profile defaults (gaming 1 second, general 5 seconds).")] int? samplingIntervalSeconds = null) => access.Start(duration, profile, samplingIntervalSeconds, cancellationToken);

    [McpServerTool(Name = "get_diagnostic_session", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Read captured samples and statistics for a diagnostic session by UUID.")]
    public Task<DiagnosticSession> GetDiagnosticSession(Guid id, CancellationToken cancellationToken) => access.Session(id, false, cancellationToken);

    [McpServerTool(Name = "stop_diagnostic_session", UseStructuredContent = true, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stop capture for a diagnostic session by UUID and return its final captured evidence.")]
    public Task<DiagnosticSession> StopDiagnosticSession(Guid id, CancellationToken cancellationToken) => access.Session(id, true, cancellationToken);
}

[McpServerResourceType]
public sealed class DiagnosticResources(DiagnosticAccess access)
{
    [McpServerResource(UriTemplate = "meh://inventory", Name = "System inventory", MimeType = "application/json")]
    [Description("Current hardware/software inventory with explicit diagnostic-source availability.")]
    public async Task<string> Inventory(CancellationToken cancellationToken) => Serialize(await access.Inventory(cancellationToken));

    [McpServerResource(UriTemplate = "meh://sensors", Name = "Live sensor snapshot", MimeType = "application/json")]
    [Description("Current bounded sensor snapshot; missing readings have explicit statuses.")]
    public async Task<string> Sensors(CancellationToken cancellationToken) => Serialize(await access.Sensors(cancellationToken));

    [McpServerResource(UriTemplate = "meh://capabilities", Name = "Diagnostic capabilities", MimeType = "application/json")]
    [Description("Supported diagnostic categories, capture profiles, and backend limitations.")]
    public async Task<string> Capabilities(CancellationToken cancellationToken) => Serialize(await access.Capabilities(cancellationToken));

    [McpServerResource(UriTemplate = "meh://report/latest", Name = "Latest health report", MimeType = "application/json")]
    [Description("Last report produced by get_health_report in this host; unavailable until one is created.")]
    public async Task<string> LatestReport(CancellationToken cancellationToken) => Serialize(await access.LatestReport(cancellationToken));

    [McpServerResource(UriTemplate = "meh://sessions", Name = "Diagnostic sessions", MimeType = "application/json")]
    [Description("Latest 100 session summaries without raw samples/events/findings. Use get_diagnostic_session for details.")]
    public async Task<string> Sessions(CancellationToken cancellationToken) => Serialize(await access.Sessions(cancellationToken));

    private static string Serialize<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, DiagnosticAccess.Json);
}
