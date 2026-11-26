using JamesTools.Core.Mcp;
using ModelContextProtocol;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace JamesTools.Mcp;

public sealed record SafeError(int SchemaVersion, DateTimeOffset TimestampUtc, string Status, string Reason);

/// <summary>Shares the real backend and capture store. Owns no diagnostic implementation.</summary>
public sealed partial class DiagnosticAccess(IDiagnosticBackend backend, IDiagnosticSessions sessions)
{
    private readonly SemaphoreSlim _operations = new(4, 4);
    private HealthReport? _latestReport;
    public const int MaximumPayloadBytes = 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNameCaseInsensitive = false
    };

    public Task<SystemInventory> Inventory(CancellationToken ct) => Run(backend.GetSystemInfoAsync, ct);
    public Task<MetricsSnapshot> Sensors(CancellationToken ct) => Run(sessions.GetLiveMetricsAsync, ct);
    public Task<DiagnosticCapabilities> Capabilities(CancellationToken ct) => Run(_ => Task.FromResult(backend.GetCapabilities()), ct);

    public Task<DiagnosticResult> Diagnose(string category, CancellationToken ct) => Run(async token =>
    {
        Identifier(category, 64);
        if (!backend.GetCapabilities().Categories.Contains(category, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException();
        return await backend.RunDiagnosticsAsync(category, token);
    }, ct);

    public Task<GameInspection> Game(string game, CancellationToken ct) => Run(token =>
    {
        if (string.IsNullOrWhiteSpace(game) || game.Length > 512 || game.Any(char.IsControl)) throw new ArgumentException();
        return backend.InspectGameAsync(game, token);
    }, ct);

    public Task<EventLogResult> Events(EventLogFilter filter, CancellationToken ct) => Run(token =>
    {
        Range(filter.FromUtc, filter.ToUtc, 7);
        if (filter.Limit is < 1 or > 200 || filter.EventIds is { Length: > 32 } || filter.EventIds?.Any(id => id is < 0 or > 65535) == true)
            throw new ArgumentException();
        if (filter.Source is not null) Identifier(filter.Source, 128, allowSpaces: true);
        if (filter.Category is not null) Identifier(filter.Category, 64, allowSpaces: true);
        if (filter.Severity is not null && !new[] { "critical", "error", "warning", "information", "verbose" }.Contains(filter.Severity, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException();
        var upper = filter.ToUtc ?? DateTimeOffset.UtcNow;
        return backend.GetEventLogsAsync(filter with { FromUtc = filter.FromUtc ?? upper.AddDays(-1), ToUtc = upper }, token);
    }, ct);

    public Task<HealthReport> Report(CancellationToken ct) => Run(async token =>
    {
        var report = await backend.GetHealthReportAsync(token);
        Volatile.Write(ref _latestReport, report);
        return report;
    }, ct);

    public Task<HealthReport> LatestReport(CancellationToken ct) => Run(_ => Task.FromResult(Volatile.Read(ref _latestReport) ?? throw new KeyNotFoundException()), ct);

    public Task<HistoricalMetrics> History(HistoryQuery query, CancellationToken ct) => Run(token =>
    {
        Range(query.FromUtc, query.ToUtc, 7);
        if (query.Limit is < 1 or > 500 || query.Metrics is { Length: > 32 }) throw new ArgumentException();
        foreach (var metric in query.Metrics ?? []) Identifier(metric, 128, allowSpaces: true);
        return sessions.GetHistoryAsync(query, token);
    }, ct);

    public Task<DiagnosticSession> Start(int duration, string profile, int? samplingIntervalSeconds, CancellationToken ct) => Run(token =>
    {
        Identifier(profile, 64);
        if (!backend.GetCapabilities().Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException();
        if (duration is < 1 or > 3600 || samplingIntervalSeconds is < 1 or > 30) throw new ArgumentException();
        return sessions.StartAsync(duration, profile, samplingIntervalSeconds, token);
    }, ct);

    public Task<DiagnosticSession> Session(Guid id, bool stop, CancellationToken ct) => Run(token =>
    {
        if (id == Guid.Empty) throw new ArgumentException();
        return stop ? sessions.StopAsync(id, token) : sessions.GetAsync(id, token);
    }, ct);

    public Task<IReadOnlyList<DiagnosticSession>> Sessions(CancellationToken ct) => Run<IReadOnlyList<DiagnosticSession>>(async token =>
    {
        var items = await sessions.ListAsync(token);
        return items.OrderByDescending(s => s.StartedUtc).Take(100).Select(s => s with
        {
            Samples = [], NotableEvents = [], Findings = [],
            Investigation = null,
            Warnings = s.Warnings.Concat(["Summary only; use get_diagnostic_session for captured details. List limited to latest 100 sessions."]).ToArray()
        }).ToArray();
    }, ct);

    private async Task<T> Run<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (!_operations.Wait(0)) throw Error("unavailable", "Operation capacity reached; retry later.");
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // Run synchronous backend setup off the transport thread. Keep the slot held until the
        // real operation ends, even if it ignores cancellation, so timed-out work cannot pile up.
        var task = Task.Run(() => operation(timeout.Token), CancellationToken.None);
        _ = task.ContinueWith(t => { _ = t.Exception; timeout.Dispose(); _operations.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            using var buffer = new BoundedBuffer(MaximumPayloadBytes / 3);
            JsonSerializer.Serialize(buffer, result, Json);
            return result;
        }
        catch (Exception ex) { throw ToSafeException(ex, ct); }
    }

    internal static McpException ToSafeException(Exception ex, CancellationToken ct = default) => ex switch
    {
        McpException m when RequestPolicy.IsSafeError(m.Message) => m,
        ArgumentException or JsonException or FormatException => Error("invalid_input", "Input is invalid or outside the documented bounds."),
        NotSupportedException => Error("unsupported", "This operation is unsupported by the diagnostic backend."),
        UnauthorizedAccessException or System.Security.SecurityException => Error("permission_denied", "Windows denied access to this diagnostic source."),
        KeyNotFoundException or FileNotFoundException => Error("unavailable", "The requested diagnostic data is unavailable."),
        TimeoutException => Error("timeout", "The operation exceeded its 30 second deadline."),
        OperationCanceledException => ct.IsCancellationRequested ? Error("cancelled", "The request was cancelled.") : Error("timeout", "The operation exceeded its 30 second deadline."),
        PayloadLimitException => Error("unavailable", "Response exceeds the size limit; request fewer records or a shorter session."),
        IOException => Error("unavailable", "The diagnostic source is temporarily unavailable."),
        _ => Error("failure", "The diagnostic operation failed.")
    };

    internal static McpException Error(string status, string reason) => new(JsonSerializer.Serialize(new SafeError(1, DateTimeOffset.UtcNow, status, reason), Json));

    private static void Identifier(string value, int maximum, bool allowSpaces = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() ||
            value.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '.') && !(allowSpaces && c == ' ')) || value.Contains(".."))
            throw new ArgumentException();
    }

    private static void Range(DateTimeOffset? from, DateTimeOffset? to, int days)
    {
        var upper = to ?? DateTimeOffset.UtcNow;
        var lower = from ?? upper.AddDays(-1);
        if (from?.Offset != null && from.Value.Offset != TimeSpan.Zero || to?.Offset != null && to.Value.Offset != TimeSpan.Zero || lower > upper || upper - lower > TimeSpan.FromDays(days) || upper > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ArgumentException();
    }
}
