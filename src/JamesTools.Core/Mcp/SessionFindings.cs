using System.Globalization;

namespace JamesTools.Core.Mcp;

internal static class SessionFindings
{
    internal static Finding[] Evaluate(MetricsSnapshot sample, IReadOnlyList<Finding> previous) =>
        WindowsDiagnosticBackend.Evaluate(sample).Concat(previous).DistinctBy(f => (f.Component, f.Observation)).Take(8).Select(f => f with
        {
            Component = TelemetryData.Text(f.Component, 96), Severity = TelemetryData.Text(f.Severity, 32), Observation = TelemetryData.Text(f.Observation),
            Explanation = f.Explanation is null ? null : TelemetryData.Text(f.Explanation), Confidence = TelemetryData.Text(f.Confidence, 32),
            Evidence = f.Evidence.Take(3).Select(x => TelemetryData.Text(x)).ToArray(), NextChecks = f.NextChecks.Take(4).Select(x => TelemetryData.Text(x)).ToArray()
        }).ToArray();

    internal static Finding[] WithAggregates(IReadOnlyList<Finding> findings, IReadOnlyList<MetricStatistics> statistics) => findings.Select(f =>
    {
        var evidence = f.Evidence.Take(3).ToList();
        var stat = statistics.FirstOrDefault(s => s.Count > 0 && f.Evidence.Any(e => e.Contains(s.Name + "=", StringComparison.Ordinal)));
        if (stat is not null) evidence.Add(TelemetryData.Text(string.Create(CultureInfo.InvariantCulture,
            $"Full accepted recording: {stat.Name}; n={stat.Count}; min={stat.Minimum:G6}; max={stat.Maximum:G6}; mean={stat.Average:G6} {stat.Unit}. No causality is inferred.")));
        return f with { Evidence = evidence.ToArray() };
    }).ToArray();
}
