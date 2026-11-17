namespace JamesTools.Core.Mcp;

internal static class SessionRecording
{
    internal static DiagnosticSession Append(DiagnosticSession session, MetricsSnapshot raw)
    {
        var sample = TelemetryData.Normalize(raw, profile: session.Profile);
        var stats = session.Statistics.ToDictionary(s => (s.Name, s.Unit));
        var metrics = new List<Metric>();
        foreach (var metric in sample.Metrics)
        {
            var key = (metric.Name, metric.Unit);
            if (!stats.TryGetValue(key, out var stat))
            {
                if (stats.Count >= TelemetryData.MaxStatistics) continue;
                stat = new(metric.Name, metric.Unit, 0, null, null, null);
            }
            stats[key] = TelemetryData.Add(stat, metric);
            metrics.Add(metric);
        }
        var retained = sample with { Metrics = metrics.ToArray() };
        var warnings = session.Warnings.ToList();
        if (session.Samples.Count == TelemetryData.MaxSamples) Warn(warnings, "Older snapshots were dropped at the 40-snapshot limit. Statistics still cover the entire recording.");
        if (metrics.Count != sample.Metrics.Count) Warn(warnings, "New metric identities were omitted at the 64-statistic limit.");
        return TelemetryData.BoundSession(session with
        {
            Samples = session.Samples.Append(retained).TakeLast(TelemetryData.MaxSamples).ToArray(), Statistics = stats.Values.ToArray(),
            Warnings = warnings.ToArray(), Findings = SessionFindings.Evaluate(retained, session.Findings)
        });
    }

    static void Warn(List<string> warnings, string warning)
    {
        if (!warnings.Contains(warning)) { if (warnings.Count == 16) warnings.RemoveAt(0); warnings.Add(warning); }
    }
}
