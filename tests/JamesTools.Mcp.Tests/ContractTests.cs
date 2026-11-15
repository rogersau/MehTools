using System.Text.Json;
using JamesTools.Core.Mcp;

namespace JamesTools.Mcp.Tests;

internal static class ContractTests
{
    internal static async Task RunAsync()
    {
        using var backend = new FixtureBackend();
        var inventory = await backend.GetSystemInfoAsync(default);
        Check.That(inventory.SchemaVersion == 1, "Inventory schema version.");
        Check.Utc(inventory.TimestampUtc);
        var metrics = await backend.SampleAsync(default);
        var restored = JsonSerializer.Deserialize<MetricsSnapshot>(JsonSerializer.Serialize(metrics, Check.Json), Check.Json)!;
        Check.That(restored.Metrics.Count == 6, "Metric status fixture lost fields.");
        foreach (var metric in restored.Metrics)
        {
            Check.Utc(metric.TimestampUtc);
            Check.That(!string.IsNullOrWhiteSpace(metric.Unit), "Units must be explicit.");
            if (metric.Status != "available") Check.That(metric.Value is null && metric.Reason is not null, "Unavailable readings must remain null with a reason.");
        }
        Check.That(restored.Metrics.Select(m => m.Status).Distinct().SequenceEqual(new[] { "available", "unsupported", "permission_denied", "unavailable", "failure" }), "Explicit availability status round trip.");
        var report = await backend.GetHealthReportAsync(default);
        Check.That(report.Limitations.Count > 0, "Insufficient evidence needs limitations.");
        var session = new DiagnosticSession(1, Guid.NewGuid(), "general", FixtureBackend.Epoch, null, "running", 5, 1, [metrics],
            [new("gpu.temperature", "degC", 0, null, null, null)], [], [], []);
        var roundtrip = Check.Read<DiagnosticSession>(JsonSerializer.SerializeToElement(session, Check.Json));
        Check.That(roundtrip.EndedUtc is null && roundtrip.Statistics[0].Average is null, "Null session/statistic values became zero.");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => backend.SampleAsync(canceled.Token));
    }
}
