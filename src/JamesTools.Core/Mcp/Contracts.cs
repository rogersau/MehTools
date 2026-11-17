namespace JamesTools.Core.Mcp;

// Transport-independent, version 1 contracts. Null values never stand for zero.
public sealed record Availability(string Status, string? Reason = null);
public sealed record Metric(string Name, double? Value, string Unit, DateTimeOffset TimestampUtc, string Status, string Source, string? Reason = null);
public sealed record MetricsSnapshot(int SchemaVersion, DateTimeOffset TimestampUtc, IReadOnlyList<Metric> Metrics, IReadOnlyList<string> Warnings);
public sealed record InventoryComponent(string Category, string Name, IReadOnlyDictionary<string, string?> Properties, Availability Availability);
public sealed record SystemInventory(int SchemaVersion, DateTimeOffset TimestampUtc, IReadOnlyList<InventoryComponent> Components, IReadOnlyList<string> Warnings);
public sealed record Finding(string Component, string Severity, string Observation, string? Explanation, string Confidence, IReadOnlyList<string> Evidence, IReadOnlyList<string> NextChecks);
public sealed record DiagnosticResult(int SchemaVersion, DateTimeOffset TimestampUtc, string Category, string Status, IReadOnlyList<Finding> Findings, IReadOnlyList<Metric> Measurements, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors);
public sealed record HealthReport(int SchemaVersion, DateTimeOffset TimestampUtc, string Assessment, IReadOnlyList<Finding> Findings, IReadOnlyList<string> Limitations);
public sealed record GameComponent(string RelativePath, string Kind, string? Version, string Evidence, string RuntimeStatus);
public sealed record GameInspection(int SchemaVersion, DateTimeOffset TimestampUtc, string Status, string? Name, string? InstallationDirectory, IReadOnlyList<GameComponent> Components, IReadOnlyList<string> Warnings);
public sealed record EventLogFilter(DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, string? Source = null, string? Severity = null, int[]? EventIds = null, string? Category = null, int Limit = 50);
public sealed record DiagnosticEvent(DateTimeOffset TimestampUtc, string Source, int EventId, string Severity, string Category, string Summary);
public sealed record EventLogResult(int SchemaVersion, DateTimeOffset TimestampUtc, string Status, IReadOnlyList<DiagnosticEvent> Events, bool Truncated, IReadOnlyList<string> Warnings);
public sealed record DiagnosticCapabilities(int SchemaVersion, IReadOnlyList<string> Categories, IReadOnlyList<string> Profiles, IReadOnlyList<string> Limitations);

public interface IDiagnosticBackend : IDisposable
{
    Task<SystemInventory> GetSystemInfoAsync(CancellationToken cancellationToken);
    Task<MetricsSnapshot> SampleAsync(CancellationToken cancellationToken);
    Task<DiagnosticResult> RunDiagnosticsAsync(string category, CancellationToken cancellationToken);
    Task<GameInspection> InspectGameAsync(string game, CancellationToken cancellationToken);
    Task<EventLogResult> GetEventLogsAsync(EventLogFilter filter, CancellationToken cancellationToken);
    Task<HealthReport> GetHealthReportAsync(CancellationToken cancellationToken);
    DiagnosticCapabilities GetCapabilities();
}

public sealed record MetricStatistics(string Name, string Unit, int Count, double? Minimum, double? Maximum, double? Average);
public sealed record DiagnosticSession(int SchemaVersion, Guid Id, string Profile, DateTimeOffset StartedUtc, DateTimeOffset? EndedUtc, string Status, int DurationSeconds, int SamplingIntervalSeconds, IReadOnlyList<MetricsSnapshot> Samples, IReadOnlyList<MetricStatistics> Statistics, IReadOnlyList<DiagnosticEvent> NotableEvents, IReadOnlyList<Finding> Findings, IReadOnlyList<string> Warnings);
public sealed record HistoryQuery(DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, string[]? Metrics = null, int Limit = 100, bool ComparePrevious = false);
public sealed record HistoricalMetrics(int SchemaVersion, IReadOnlyList<MetricsSnapshot> Samples, IReadOnlyList<MetricStatistics> Statistics, IReadOnlyList<MetricStatistics> PreviousStatistics, bool Truncated, IReadOnlyList<string> Warnings);

public interface IDiagnosticSessions : IAsyncDisposable
{
    Task<MetricsSnapshot> GetLiveMetricsAsync(CancellationToken cancellationToken);
    Task<DiagnosticSession> StartAsync(int duration, string profile, int? samplingIntervalSeconds, CancellationToken cancellationToken);
    Task<DiagnosticSession> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<DiagnosticSession> StopAsync(Guid id, CancellationToken cancellationToken);
    Task<HistoricalMetrics> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiagnosticSession>> ListAsync(CancellationToken cancellationToken);
}
