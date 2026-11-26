using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("JamesTools.Mcp.SessionTests")]

namespace JamesTools.Core.Mcp;

internal static class TelemetryData
{
    internal const int MaxSamples = 40, MaxMetrics = 32, MaxStatistics = 64;
    internal const int SnapshotBytes = 6 * 1024, SessionBytes = 300 * 1024, LiveBytes = 96 * 1024;
    internal static readonly string[] Profiles = ["general", "gaming", "cpu", "gpu", "storage"];
    internal static string Text(string? text, int length = 256) => string.IsNullOrWhiteSpace(text) ? "unspecified" : text[..Math.Min(length, text.Length)];
    internal static bool Utc(DateTimeOffset time) => time != default && time.Offset == TimeSpan.Zero;
    internal static bool Key(string? name) => name is { Length: > 0 and <= 96 } && name.All(c => char.IsAsciiLetterOrDigit(c) || "_.,:/%-".Contains(c));
    internal static bool Good(string status) => status == "available";
    internal static string Status(Exception error) => error is UnauthorizedAccessException ? "permission_denied" : error is NotSupportedException ? "unsupported" : error is TimeoutException or OperationCanceledException ? "unavailable" : "failure";
    internal static string SafeReason(Exception error) => Status(error) switch
    {
        "permission_denied" => "The operation was denied by the operating system.",
        "unsupported" => "The collector does not support this operation.",
        "unavailable" => "The collector did not finish within its bounded operation lifetime.",
        _ => "The operation failed. Internal exception details are not exposed."
    };
    internal static MetricsSnapshot Failure(Exception error) => new(1, DateTimeOffset.UtcNow,
        [new("telemetry", null, "", DateTimeOffset.UtcNow, Status(error), "diagnostic-backend", SafeReason(error))], []);

    internal static MetricsSnapshot Normalize(MetricsSnapshot source, bool live = false, string profile = "general")
    {
        if (source is null || source.SchemaVersion != 1 || source.Metrics is null || source.Warnings is null)
            throw new InvalidDataException("Backend returned an invalid metrics snapshot.");
        var now = DateTimeOffset.UtcNow;
        var warnings = source.Warnings.Take(8).Select(x => Text(x)).ToList();
        var metrics = source.Metrics.Take(4096).Where(m => m is not null && Key(m.Name)).OrderBy(m => Priority(m.Name, profile)).Take(live ? 127 : MaxMetrics).Select(m =>
        {
            var status = m.Status?.ToLowerInvariant() switch
            {
                "available" => "available",
                "unsupported" => "unsupported",
                "permission_denied" => "permission_denied",
                "unavailable" => "unavailable",
                _ => "failure"
            };
            var value = Good(status) && m.Value is double v && double.IsFinite(v) ? m.Value : null;
            if (Good(status) && value is null) status = "unavailable";
            return m with { Value = value, Status = status, Unit = m.Unit is null ? "" : m.Unit[..Math.Min(24, m.Unit.Length)],
                Source = Text(m.Source, 96), Reason = m.Reason is null ? null : Text(m.Reason),
                TimestampUtc = m.TimestampUtc == default ? now : m.TimestampUtc.ToUniversalTime() };
        }).DistinctBy(m => (m.Name, m.Unit)).ToList();
        if (metrics.Count == 0) metrics.Add(new("telemetry", null, "", now, "unavailable", "diagnostic-backend", "The backend returned no supported telemetry observations."));
        // No process/game scope exists in v1 StartAsync. Never launch PresentMon here.
        metrics.RemoveAll(m => m.Name == "frame_time_ms");
        metrics.Add(new("frame_time_ms", null, "ms", source.TimestampUtc == default ? now : source.TimestampUtc.ToUniversalTime(), "unsupported", "diagnostic-sessions", "Frame timing requires an explicitly scoped game capture; PresentMon was not started."));
        if (source.Metrics.Count > metrics.Count - 1) warnings.Add("Metrics were bounded, deduplicated or rejected as invalid.");
        var snapshot = new MetricsSnapshot(1, source.TimestampUtc == default ? now : source.TimestampUtc.ToUniversalTime(), metrics.ToArray(), warnings.Take(8).ToArray());
        while (JsonSerializer.SerializeToUtf8Bytes(snapshot).Length > (live ? LiveBytes : SnapshotBytes) && metrics.Count > 1)
        {
            // Lowest-priority detail is at the end; keep the final frame-time limitation.
            metrics.RemoveAt(metrics.Count - 2);
            snapshot = snapshot with { Metrics = metrics.ToArray(), Warnings = [live ? "Live telemetry was reduced to its bounded response budget." : "Session telemetry was reduced to its 6 KiB snapshot budget."] };
        }
        return snapshot;
    }

    static int Priority(string name, string profile)
    {
        if (name == "telemetry") return -100;
        if (name.StartsWith("cpu.logical.", StringComparison.Ordinal)) return 1000;
        if (name.StartsWith("storage.drive_", StringComparison.Ordinal)) return 900;
        var essential = name switch
        {
            "cpu.utilization" => 0, "gpu.utilization" => 1,
            "gpu.thermal_limited" => 2, "gpu.power_limited" => 3,
            "gpu.temperature" => 4, "gpu.clock" => 5, "gpu.power" => 6,
            "gpu.memory_used" => 7, "gpu.memory_total" => 8,
            "memory.physical_used" => 9, "memory.physical_load" => 10, "memory.physical_total" => 11,
            "storage.read" => 12, "storage.write" => 13, "storage.transfer_latency" => 14,
            "network.receive" => 15, "network.send" => 16,
            "cpu.temperature" => 17, "cpu.package_power" => 18,
            _ => 200
        };
        var preferred = profile switch { "cpu" => "cpu.", "gpu" or "gaming" => "gpu.", "storage" => "storage.", _ => "" };
        return essential < 200 && preferred.Length > 0 && name.StartsWith(preferred, StringComparison.Ordinal) ? essential - 30 : essential;
    }

    internal static MetricStatistics[] Aggregate(IEnumerable<MetricsSnapshot> samples) => samples.SelectMany(s => s.Metrics)
        .GroupBy(m => (m.Name, m.Unit)).Take(MaxStatistics).Select(g =>
        {
            var result = new MetricStatistics(g.Key.Name, g.Key.Unit, 0, null, null, null);
            foreach (var metric in g) result = Add(result, metric);
            return result;
        }).ToArray();

    internal static MetricStatistics Add(MetricStatistics stat, Metric metric)
    {
        if (!Good(metric.Status) || metric.Value is not double value || !double.IsFinite(value)) return stat;
        var n = stat.Count + 1;
        // Weighted terms avoid overflowing (value - mean) for opposite finite extremes.
        var mean = stat.Count == 0 ? value : Math.Sign(stat.Average!.Value) == Math.Sign(value)
            ? stat.Average.Value + (value - stat.Average.Value) / n
            : stat.Average.Value * ((double)stat.Count / n) + value / n;
        return stat with { Count = n, Minimum = stat.Minimum is double min ? Math.Min(min, value) : value,
            Maximum = stat.Maximum is double max ? Math.Max(max, value) : value, Average = mean };
    }

    internal static DiagnosticSession Copy(DiagnosticSession session, bool summary = false) => session with
    {
        Investigation = summary || session.Investigation is null ? null : InvestigationRetention.Copy(session.Investigation),
        Samples = summary ? [] : session.Samples.Select(s => s with { Metrics = s.Metrics.ToArray(), Warnings = s.Warnings.ToArray() }).ToArray(),
        Statistics = session.Statistics.ToArray(), NotableEvents = summary ? [] : session.NotableEvents.ToArray(),
        Findings = summary ? [] : session.Findings.Select(f => f with { Evidence = f.Evidence.ToArray(), NextChecks = f.NextChecks.ToArray() }).ToArray(), Warnings = session.Warnings.ToArray()
    };

    internal static DiagnosticSession BoundSession(DiagnosticSession session)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(session).Length <= SessionBytes) return session;
        session = session with { Warnings = session.Warnings.TakeLast(15).Append("Older snapshots or event details were omitted at the 300 KiB response limit. Full-recording statistics are unchanged.").ToArray() };
        while (JsonSerializer.SerializeToUtf8Bytes(session).Length > SessionBytes)
        {
            if (session.Samples.Count > 0) session = session with { Samples = session.Samples.Skip(1).ToArray() };
            else if (session.NotableEvents.Count > 0) session = session with { NotableEvents = session.NotableEvents.SkipLast(1).ToArray() };
            else if (session.Findings.Count > 0) session = session with { Findings = session.Findings.SkipLast(1).ToArray() };
            else throw new InvalidDataException("Session metadata exceeds the bounded response budget.");
        }
        return session;
    }

    internal static void Validate(DiagnosticSession s)
    {
        if (s is null || s.SchemaVersion != 1 || s.Id == Guid.Empty || !Profiles.Contains(s.Profile) || !Utc(s.StartedUtc) ||
            s.DurationSeconds is < 1 or > 3600 || s.SamplingIntervalSeconds is < 1 or > 30 ||
            s.Status is not ("recording" or "completed" or "stopped" or "failed" or "interrupted") ||
            (s.Status == "recording" ? s.EndedUtc is not null : s.EndedUtc is not { } end || !Utc(end) || end < s.StartedUtc) ||
            s.Samples is null || s.Samples.Count > MaxSamples || s.Statistics is null || s.Statistics.Count > MaxStatistics ||
            s.NotableEvents is null || s.NotableEvents.Count > 32 || s.Findings is null || s.Findings.Count > 8 ||
            s.Warnings is null || s.Warnings.Count > 16 || s.Warnings.Any(w => w is null || w.Length > 256))
            throw new InvalidDataException("Invalid persisted diagnostic session.");
        if (s.InvestigationOptions is { } options)
        {
            options.Validate(s.DurationSeconds);
            if (s.Investigation is { } report) InvestigationRetention.Validate(report);
        }
        else if (s.Investigation is not null) throw new InvalidDataException("Investigation report requires explicit session options.");
        foreach (var sample in s.Samples)
        {
            if (sample is null || sample.SchemaVersion != 1 || !Utc(sample.TimestampUtc) || sample.Metrics is null || sample.Metrics.Count > MaxMetrics + 1 ||
                sample.Warnings is null || sample.Warnings.Count > 8 || sample.Warnings.Any(w => w is null || w.Length > 256) ||
                JsonSerializer.SerializeToUtf8Bytes(sample).Length > SnapshotBytes)
                throw new InvalidDataException("Invalid persisted telemetry snapshot.");
            foreach (var m in sample.Metrics)
                if (m is null || !Key(m.Name) || m.Unit is null || m.Unit.Length > 24 || !Utc(m.TimestampUtc) ||
                    m.Source is null || m.Source.Length > 96 || m.Reason?.Length > 256 ||
                    m.Status is not ("available" or "unsupported" or "permission_denied" or "unavailable" or "failure") ||
                    (Good(m.Status) && m.Value is null) || (m.Value is double v && (!double.IsFinite(v) || !Good(m.Status))))
                    throw new InvalidDataException("Invalid persisted metric.");
        }
        foreach (var stat in s.Statistics)
            if (stat is null || !Key(stat.Name) || stat.Unit is null || stat.Unit.Length > 24 || stat.Count is < 0 or > 3601 ||
                (stat.Count == 0 ? stat.Minimum is not null || stat.Maximum is not null || stat.Average is not null :
                    stat.Minimum is not double a || stat.Maximum is not double b || stat.Average is not double c || !double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c) || a > b || c < a - Math.Abs(a) * 1e-12 || c > b + Math.Abs(b) * 1e-12))
                throw new InvalidDataException("Invalid persisted statistics.");
        if (s.Statistics.DistinctBy(x => (x.Name, x.Unit)).Count() != s.Statistics.Count) throw new InvalidDataException("Duplicate persisted statistics.");
        foreach (var finding in s.Findings)
            if (finding is null || finding.Component is null || finding.Component.Length > 96 || finding.Severity is null || finding.Severity.Length > 32 ||
                finding.Observation is null || finding.Observation.Length > 256 || finding.Explanation?.Length > 256 || finding.Confidence is null || finding.Confidence.Length > 32 ||
                finding.Evidence is null || finding.Evidence.Count > 4 || finding.NextChecks is null || finding.NextChecks.Count > 4 ||
                finding.Evidence.Concat(finding.NextChecks).Any(x => x is null || x.Length > 256))
                throw new InvalidDataException("Invalid persisted finding.");
        foreach (var e in s.NotableEvents)
            if (e is null || !Utc(e.TimestampUtc) || e.TimestampUtc < s.StartedUtc || e.TimestampUtc > s.EndedUtc || e.Source is null || e.Source.Length > 96 ||
                e.Summary is null || e.Summary.Length > 256 || e.Category is null || e.Category.Length > 96 || e.Severity is not ("warning" or "error" or "critical"))
                throw new InvalidDataException("Invalid persisted event.");
    }
}
