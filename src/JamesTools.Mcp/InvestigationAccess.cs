using System.Text.Json;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Mcp;

namespace JamesTools.Mcp;

public sealed partial class DiagnosticAccess
{
    public Task<DiagnosticSession> StartInvestigation(int duration, StutterInvestigationOptions options, CancellationToken ct) => Run(token =>
    {
        options.Validate(duration);
        return (sessions as IStutterInvestigations ?? throw new NotSupportedException()).StartInvestigationAsync(duration, options, token);
    }, ct);

    async Task<DiagnosticSession> InvestigationSession(Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty) throw new ArgumentException();
        var session = await sessions.GetAsync(id, ct);
        if (session.InvestigationOptions is null) throw new NotSupportedException();
        return session;
    }

    static InvestigationReport Evidence(DiagnosticSession session) => session.Investigation ?? throw new KeyNotFoundException();

    public Task<DiagnosticSession> StopInvestigation(Guid id, CancellationToken ct) => Run(async token =>
    {
        _ = await InvestigationSession(id, token);
        return await sessions.StopAsync(id, token);
    }, ct);

    public Task<InvestigationReport> InvestigationReport(Guid id, CancellationToken ct) => Run(async token =>
        InvestigationRetention.Summary(Evidence(await InvestigationSession(id, token))), ct);

    public Task<InvestigationPage<InvestigationDevice>> InvestigationDevices(Guid id, int offset, int limit, CancellationToken ct) => Run(async token =>
    {
        PageBounds(offset, limit);
        var session = await InvestigationSession(id, token); var report = Evidence(session);
        return Page(session, report, report.Devices, offset, limit);
    }, ct);

    public Task<InvestigationPage<InvestigationEvent>> InvestigationEvents(Guid id, int offset, int limit, string? source, string? category, CancellationToken ct) => Run(async token =>
    {
        PageBounds(offset, limit);
        ExactFilter(source, 128); ExactFilter(category, 64);
        var session = await InvestigationSession(id, token); var report = Evidence(session);
        var events = report.Events.Where(e => (source is null || e.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) &&
            (category is null || e.Category.Equals(category, StringComparison.OrdinalIgnoreCase))).OrderBy(e => e.TimestampSeconds).ToArray();
        return Page(session, report, events, offset, limit);
    }, ct);

    public Task<InvestigationPage<FrameTelemetry>> InvestigationSpikes(Guid id, int offset, int limit, CancellationToken ct) => Run(async token =>
    {
        PageBounds(offset, limit);
        var session = await InvestigationSession(id, token); var report = Evidence(session);
        return Page(session, report, report.Spikes, offset, limit);
    }, ct);

    public Task<InvestigationPage<InvestigationCorrelation>> InvestigationCorrelations(Guid id, int offset, int limit, CancellationToken ct) => Run(async token =>
    {
        PageBounds(offset, limit);
        var session = await InvestigationSession(id, token); var report = Evidence(session);
        return Page(session, report, report.Correlations, offset, limit);
    }, ct);

    static void PageBounds(int offset, int limit)
    {
        if (offset is < 0 or > 30000 || limit is < 1 or > 200) throw new ArgumentException();
    }

    static void ExactFilter(string? value, int maximum)
    {
        // Source names are opaque in-memory equality filters, never XPath, paths or executable input.
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value != value.Trim() || value.Any(char.IsControl)))
            throw new ArgumentException();
    }

    static InvestigationPage<T> Page<T>(DiagnosticSession session, InvestigationReport report, IReadOnlyList<T> items, int offset, int limit)
    {
        var page = items.Skip(offset).Take(limit).ToArray();
        var result = new InvestigationPage<T>(1, session.Id, session.Status, offset, items.Count,
            offset + page.Length < items.Count ? offset + page.Length : null, page, report.Coverage, report.Collectors, report.Limitations);
        while (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > 96 * 1024 && page.Length > 1)
        {
            page = page.Take(page.Length / 2).ToArray();
            result = result with { Items = page, NextOffset = offset + page.Length };
        }
        return result;
    }

    public Task<InvestigationComparisonReport> CompareInvestigations(Guid beforeId, Guid afterId, string description, CancellationToken ct) => Run(async token =>
    {
        if (description is null || description.Length > 256 || description.Any(char.IsControl)) throw new ArgumentException();
        var a = await InvestigationSession(beforeId, token); var b = await InvestigationSession(afterId, token);
        if (a.Status == "recording" || b.Status == "recording") throw new ArgumentException();
        var before = Evidence(a); var after = Evidence(b);
        var result = InvestigationComparison.Compare(before, after, description);
        // Counts in correlation summaries precede MCP detail retention. Do not silently derive rates from truncated event pages.
        var ga = before.Correlations.ToDictionary(c => c.Group); var gb = after.Correlations.ToDictionary(c => c.Group);
        result = result with { EventRateChanges = ga.Keys.Union(gb.Keys).Select(g =>
        {
            var x = ga.GetValueOrDefault(g); var y = gb.GetValueOrDefault(g);
            return new InvestigationEventRateChange(g, x?.EventCount ?? 0, y?.EventCount ?? 0, x?.EventRatePerMinute,
                y?.EventRatePerMinute, x is null || y is null ? null : y.EventRatePerMinute - x.EventRatePerMinute);
        }).ToArray(), Limitations = result.Limitations.Take(48).Append("Event rates use retained group summaries before MCP detail reduction; missing groups remain unobserved. Comparison details are bounded to 220 KiB.").ToArray() };
        while (JsonSerializer.SerializeToUtf8Bytes(result, Json).Length > InvestigationRetention.MaximumBytes)
        {
            if (result.EventRateChanges.Count > 0) result = result with { EventRateChanges = result.EventRateChanges.Take(result.EventRateChanges.Count - 1).ToArray() };
            else if (result.CorrelationChanges.Count > 0) result = result with { CorrelationChanges = result.CorrelationChanges.Take(result.CorrelationChanges.Count - 1).ToArray() };
            else throw new InvalidDataException();
        }
        return result;
    }, ct);
}
