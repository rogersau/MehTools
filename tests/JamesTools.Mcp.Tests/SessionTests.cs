using System.Text.Json;
using JamesTools.Core.Mcp;

namespace JamesTools.Mcp.Tests;

internal static class SessionTests
{
    internal static async Task RunAsync()
    {
        using var directory = new TestDirectory();
        using var backend = new FixtureBackend { DelayMilliseconds = 40 };
        var storage = Path.Combine(directory.Path, "sessions");
        Guid stoppedId;
        await using (var sessions = new DiagnosticSessionService(backend, storage))
        {
            var reads = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => sessions.GetLiveMetricsAsync(default)));
            Check.That(backend.Samples == 1, "Concurrent live calls must share the same backend sample.");
            foreach (var read in reads) EndpointTests.VerifyMetrics(read);
            Check.That(reads[0].Metrics.Single(m => m.Name == "gpu.temperature").Value is null, "Unsupported temperature became zero.");
            foreach (var duration in new[] { 0, -1, 3601 })
                await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => sessions.StartAsync(duration, "general", 1, default));
            await Check.ThrowsAsync<ArgumentException>(() => sessions.StartAsync(5, "unknown", 1, default));
            await Check.ThrowsAsync<ArgumentOutOfRangeException>(() => sessions.StartAsync(5, "general", 0, default));
            var maximum = await sessions.StartAsync(3600, "gaming", 1, default);
            stoppedId = maximum.Id;
            Check.That(JsonSerializer.SerializeToUtf8Bytes(maximum, Check.Json).Length <= 300 * 1024, "Maximum-duration initial response is oversized.");
            await WaitForSamplesAsync(sessions, maximum.Id);
            var current = await sessions.GetAsync(maximum.Id, default);
            Check.That(current.DurationSeconds == 3600 && current.Samples.Count <= 40, "Maximum duration did not preserve bounded snapshot window.");
            Check.That(JsonSerializer.SerializeToUtf8Bytes(current, Check.Json).Length <= 300 * 1024, "Maximum-duration response exceeds session budget.");
            foreach (var sample in current.Samples) EndpointTests.VerifyMetrics(sample);
            var unsupported = current.Statistics.FirstOrDefault(s => s.Name == "gpu.temperature");
            Check.That(unsupported is not null && unsupported.Count == 0 && unsupported.Average is null && unsupported.Minimum is null && unsupported.Maximum is null,
                "Unsupported sensor contributed numeric statistics.");
            var others = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sessions.StartAsync(3600, "cpu", 1, default)));
            await Check.ThrowsAsync<InvalidOperationException>(() => sessions.StartAsync(3600, "cpu", 1, default));
            var stopped = await sessions.StopAsync(maximum.Id, default);
            Check.That(stopped.Status == "stopped" && stopped.EndedUtc is not null, "Session did not stop.");
            var again = await sessions.StopAsync(maximum.Id, default);
            Check.That(again.Id == stopped.Id && again.EndedUtc == stopped.EndedUtc, "Stop is not idempotent.");
            foreach (var other in others) await sessions.StopAsync(other.Id, default);
            foreach (var (profile, interval) in new[] { ("general", 5), ("storage", 5), ("gaming", 1), ("cpu", 1), ("gpu", 1) })
            {
                var preset = await sessions.StartAsync(1, profile, null, default);
                Check.That(preset.SamplingIntervalSeconds == interval, profile + " default interval changed.");
                await sessions.StopAsync(preset.Id, default);
            }
            var automatic = await sessions.StartAsync(1, "general", 1, default);
            using (var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                DiagnosticSession completed;
                do { await Task.Delay(40, completionTimeout.Token); completed = await sessions.GetAsync(automatic.Id, completionTimeout.Token); }
                while (completed.Status == "recording");
                Check.That(completed.Status == "completed" && completed.EndedUtc is not null, "Duration expiry did not complete capture.");
            }
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Check.ThrowsAsync<OperationCanceledException>(() => sessions.GetLiveMetricsAsync(cancel.Token));
            await Check.ThrowsAsync<ArgumentException>(() => sessions.GetHistoryAsync(new(Limit: -1), default));
            var history = await sessions.GetHistoryAsync(new(FromUtc: maximum.StartedUtc, ToUtc: DateTimeOffset.UtcNow, Limit: 1), default);
            Check.That(history.Samples.Count == 1, "Recorded history must contain a sample and respect the limit.");
        }
        await using (var reopened = new DiagnosticSessionService(backend, storage))
        {
            var persisted = await reopened.GetAsync(stoppedId, default);
            Check.That(persisted.Status == "stopped", "Stopped capture did not survive restart.");
            Check.That((await reopened.ListAsync(default)).Count <= 12, "Retained sessions are unbounded.");
        }
    }

    private static async Task WaitForSamplesAsync(IDiagnosticSessions sessions, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while ((await sessions.GetAsync(id, timeout.Token)).Samples.Count == 0) await Task.Delay(40, timeout.Token);
    }
}
