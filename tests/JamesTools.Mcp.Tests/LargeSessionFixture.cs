using System.Text.Json;
using JamesTools.Core.Mcp;

namespace JamesTools.Mcp.Tests;

internal static class LargeSessionFixture
{
    internal static readonly Guid Id = Guid.Parse("52761608-caa8-4f50-bd13-be6a830b906d");

    internal static void Write(string storageDirectory)
    {
        var ended = DateTimeOffset.UtcNow;
        var started = ended.AddHours(-1);
        var samples = Enumerable.Range(0, 40).Select(i =>
        {
            var timestamp = started.AddSeconds(i * 30);
            return new MetricsSnapshot(1, timestamp, Enumerable.Range(0, 16).Select(m => new Metric(
                "fixture.sensor" + m, null, "degC", timestamp, "unsupported", "deterministic-wide-fixture",
                new string('x', 128))).ToArray(), []);
        }).ToArray();
        var statistics = Enumerable.Range(0, 16).Select(m => new MetricStatistics("fixture.sensor" + m, "degC", 0, null, null, null)).ToArray();
        var session = new DiagnosticSession(1, Id, "general", started, ended, "completed", 3600, 30, samples, statistics, [], [], ["Maximum-duration, full retained-window fixture."]);
        Check.That(JsonSerializer.SerializeToUtf8Bytes(session).Length <= 300 * 1024, "Wide fixture exceeds production session budget.");
        foreach (var snapshot in samples) Check.That(JsonSerializer.SerializeToUtf8Bytes(snapshot).Length <= 6 * 1024, "Wide fixture exceeds snapshot budget.");
        var target = Path.Combine(storageDirectory, "mcp-sessions");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, Id.ToString("N") + ".json"), JsonSerializer.Serialize(new { Version = 1, WrittenUtc = ended, Session = session }));
    }
}
