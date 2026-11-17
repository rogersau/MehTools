using System.Globalization;
using System.Text.Json;
using JamesTools.Core.Diagnostics;

namespace JamesTools.Core.Mcp;

internal static class SessionHistory
{
    // Read PerformanceHistoryStore's embedded ReportTimeline summaries, never its arbitrary
    // report paths. Averages have separate names so they cannot masquerade as live points.
    internal static MetricsSnapshot[] ReadLegacy(string directory, List<string> warnings)
    {
        var path = Path.Combine(directory, "performance-history.json");
        if (!File.Exists(path)) return [];
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("History index is a reparse point.");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (file.Length > 16 * 1024 * 1024) throw new InvalidDataException("History index exceeds 16 MiB.");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.GetProperty("Version").GetInt32() != 1 || root.GetProperty("Reports").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Unsupported history index version.");
            var result = new List<MetricsSnapshot>();
            foreach (var report in root.GetProperty("Reports").EnumerateArray().Take(10000))
            {
                if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("Summary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
                    !summary.TryGetProperty("SchemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version is not (2 or 3) ||
                    !summary.TryGetProperty("StartedUtc", out var started) || started.ValueKind != JsonValueKind.String ||
                    !DateTimeOffset.TryParseExact(started.GetString(), ["yyyy-MM-dd HH:mm:ss 'UTC'", "O"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) || time < DateTimeOffset.UtcNow - SessionStore.Retention) continue;
                var metrics = new List<Metric>();
                void Add(string field, string name, string unit)
                {
                    double? value = summary.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : null;
                    metrics.Add(new(name, value, unit, time, value is null ? "unavailable" : "available", "performance-history-summary", "Indexed report aggregate; the source report has not been reverified."));
                }
                Add("CpuAverage", "report.cpu_average", "%"); Add("GpuAverage", "report.gpu_average", "%"); Add("MemoryAverageMiB", "report.memory_average", "MiB");
                if (JsonSerializer.Deserialize<ReportMachineSummary>(summary.GetRawText()) is { } typed && PerformanceHistoryStore.FrameValid(typed))
                {
                    Add("FrameMsAverage", "report.frame_time_average", "ms");
                    Add("FrameP99Ms", "report.frame_time_p99", "ms");
                }
                result.Add(new(1, time, metrics, ["Historical report averages are aggregates, not instantaneous telemetry or verified workload comparisons."]));
            }
            warnings.Add("Read-only indexed report summaries are included as report.* aggregates. Source report paths and raw timelines are not opened or reverified.");
            if (result.Count > 1024) warnings.Add("Indexed report history was reduced to the latest 1024 summaries.");
            return result.OrderByDescending(s => s.TimestampUtc).Take(1024).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            warnings.Add("Indexed report history " + TelemetryData.Status(ex) + ": " + TelemetryData.SafeReason(ex));
            return [];
        }
    }
}
