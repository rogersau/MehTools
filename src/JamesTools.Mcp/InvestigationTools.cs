using System.ComponentModel;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Mcp;
using ModelContextProtocol.Server;

namespace JamesTools.Mcp;

public sealed partial class DiagnosticTools
{
    [McpServerTool(Name = "start_stutter_investigation", UseStructuredContent = true, ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Explicitly capture a local stutter investigation for ANY running application PID, or system-only if omitted. Uses the diagnostic session service/store. Duration 1..300s (Deep: 30..300s). Deep tracing is opt-in, may require existing administrator rights, and never elevates. Multiple processes sharing the validated image path may be captured. Raw exports/ETL are disabled; only bounded session evidence is retained.")]
    public Task<DiagnosticSession> StartStutterInvestigation(int duration, CancellationToken cancellationToken,
        [Description("Accessible running application PID; omit for system-only hardware/device capture without frames. No executable/tool/output paths accepted.")] int? processId = null,
        [Description("Explicit opt-in to deeper local ETW tracing. Never requests elevation.")] bool deepInvestigation = false,
        [Description("Explicit USB ETW opt-in; requires deepInvestigation=true.")] bool deepUsbTracing = false,
        [Description("Slow-frame threshold 1..10000 milliseconds.")] double stutterThresholdMs = 33.333) =>
        access.StartInvestigation(duration, new(processId, deepInvestigation, deepUsbTracing, stutterThresholdMs), cancellationToken);

    [McpServerTool(Name = "stop_stutter_investigation", UseStructuredContent = true, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Stop an investigation using the existing diagnostic session lifecycle; idempotent. stop_diagnostic_session also works. Returns final retained evidence and summaries.")]
    public Task<DiagnosticSession> StopStutterInvestigation(Guid id, CancellationToken cancellationToken) => access.StopInvestigation(id, cancellationToken);

    [McpServerTool(Name = "list_investigation_devices", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Page the session's captured device inventory/topology, redacted identities, driver and parent/controller/hub links. Requires an explicit investigation session; no background collection occurs.")]
    public Task<InvestigationPage<InvestigationDevice>> ListInvestigationDevices(Guid id, CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        access.InvestigationDevices(id, offset, limit, cancellationToken);

    [McpServerTool(Name = "get_recent_device_events", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Page retained session device/deep events in timestamp order. Offset 0..30000, limit 1..200; source/category exact optional filters. Coverage and limitations describe omissions. Live pages can change until stop.")]
    public Task<InvestigationPage<InvestigationEvent>> GetRecentDeviceEvents(Guid id, CancellationToken cancellationToken,
        int offset = 0, int limit = 50, string? source = null, string? category = null) => access.InvestigationEvents(id, offset, limit, source, category, cancellationToken);

    [McpServerTool(Name = "get_performance_spikes", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Page retained slow frame intervals for a session, preserving original timestamps. Full-recording spike counts/metrics remain in get_investigation_report. Offset 0..30000, limit 1..200.")]
    public Task<InvestigationPage<FrameTelemetry>> GetPerformanceSpikes(Guid id, CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        access.InvestigationSpikes(id, offset, limit, cancellationToken);

    [McpServerTool(Name = "get_stutter_correlations", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Page event/stall association statistics, background chance, coverage, competing groups and limitations. Correlation is never causation. Match examples may be truncated. Offset 0..30000, limit 1..200.")]
    public Task<InvestigationPage<InvestigationCorrelation>> GetStutterCorrelations(Guid id, CancellationToken cancellationToken, int offset = 0, int limit = 50) =>
        access.InvestigationCorrelations(id, offset, limit, cancellationToken);

    [McpServerTool(Name = "get_investigation_report", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Read portable investigation metrics, periodicity, coverage, collector availability and correlation summaries. Events/spikes/devices use separate paged tools; get_diagnostic_session returns all retained report detail. Provisional while recording.")]
    public Task<InvestigationReport> GetInvestigationReport(Guid id, CancellationToken cancellationToken) => access.InvestigationReport(id, cancellationToken);

    [McpServerTool(Name = "compare_investigation_sessions", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description("Compare two finished retained investigation sessions by ID. Includes normalized frame/event rates, correlation changes and limitations; differences do not prove causation. No file paths or imported reports.")]
    public Task<InvestigationComparisonReport> CompareInvestigationSessions(Guid beforeId, Guid afterId, CancellationToken cancellationToken,
        [Description("Description of the deliberate change, at most 256 characters.")] string changeDescription = "") =>
        access.CompareInvestigations(beforeId, afterId, changeDescription, cancellationToken);
}
