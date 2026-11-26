using System.Text.Json;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Mcp;
using ModelContextProtocol;

namespace JamesTools.Mcp.Tests;

internal static class InvestigationTests
{
    internal static async Task RunAsync()
    {
        var wide = InvestigationFixture.CreateReport(1000);
        var correlation = wide.Correlations.Single();
        wide = wide with { Correlations = Enumerable.Range(0, 256).Select(i => correlation with { Group = correlation.Group with { DeviceId = "hash-" + i } }).ToArray() };
        var boundedWide = InvestigationRetention.Bound(wide);
        Check.That(boundedWide.Correlations.Count == 256 && boundedWide.Correlations.All(c => c.EventCount == 1000), "Wide report discarded group statistics before reducing explanatory detail.");
        var production = new PerformanceInvestigationCaptureFactory();
        Check.That(production.ValidateTarget(null) is null, "System-only validation acquired an application scope.");
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
            Check.That(string.Equals(production.ValidateTarget(process.Id), process.MainModule!.FileName, StringComparison.OrdinalIgnoreCase), "Production PID validation did not use the accessible image path.");
        using var directory = new TestDirectory();
        using var backend = new FixtureBackend();
        var factory = new InvestigationFixture();
        var storage = Path.Combine(directory.Path, "sessions");
        Guid id;
        await using (var sessions = new DiagnosticSessionService(backend, storage, factory))
        {
            var access = new DiagnosticAccess(backend, sessions);
            await Check.ThrowsAsync<ArgumentException>(() => sessions.StartInvestigationAsync(301, new(), default));
            await Check.ThrowsAsync<ArgumentException>(() => sessions.StartInvestigationAsync(20, new(DeepInvestigation: true), default));
            await Check.ThrowsAsync<ArgumentException>(() => sessions.StartInvestigationAsync(5, new(DeepUsbTracing: true), default));
            await Check.ThrowsAsync<ArgumentException>(() => sessions.StartInvestigationAsync(5, new(StutterThresholdMs: double.NaN), default));
            await Check.ThrowsAsync<UnauthorizedAccessException>(() => sessions.StartInvestigationAsync(5, new(123), default));
            using var request = new CancellationTokenSource();
            var started = await sessions.StartInvestigationAsync(30, new(4242, true, true), request.Token); id = started.Id;
            request.Cancel();
            await Check.ThrowsAsync<InvalidOperationException>(() => sessions.StartInvestigationAsync(5, new(), default));
            var observed = await WithReport(sessions, id);
            Check.That(backend.Samples == 0 && observed.Samples.Count == 0, "Investigation duplicated backend sampling.");
            Check.That(factory.LastOptions is { DeepInvestigation: true, DeepUsbTracing: true, ProcessId: 4242 }, "Explicit target/deep options were lost.");
            var report = observed.Investigation!;
            Check.That(JsonSerializer.SerializeToUtf8Bytes(observed).Length <= 300 * 1024 && JsonSerializer.SerializeToUtf8Bytes(report).Length <= InvestigationRetention.MaximumBytes, "Report/session budget exceeded.");
            Check.That(report.Metrics.FrameCount == 3600 && report.Correlations.Single().EventCount == 600 && report.Correlations.Single().MatchedStalls == 10, "Reduction discarded full statistics/correlation counts.");
            Check.That(report.Coverage.EventsOmittedByLimit == 600 - report.Events.Count && report.Coverage.SpikesOmittedByLimit == 600 - report.Spikes.Count, "Evidence omissions not counted.");
            Check.That(!JsonSerializer.Serialize(report).Contains("private\\\\secret", StringComparison.Ordinal) && report.Events.All(e => e.Details["privatePath"] == "[redacted]"), "Portable evidence leaked raw path.");
            var page = await access.InvestigationEvents(id, 0, 1, null, "USB", default);
            Check.That(page.Items.Count == 1 && page.NextOffset == 1 && page.RetainedCount == report.Events.Count, "Paging cursor/count incorrect.");
            var next = await access.InvestigationEvents(id, 1, 1, null, null, default);
            Check.That(next.Items.Single().TimestampSeconds > page.Items.Single().TimestampSeconds, "Paging repeats evidence.");
            Check.That((await access.InvestigationDevices(id, 0, 10, default)).Items.Single().ParentHubId == "hub-hash", "Device topology lost.");
            Check.That((await access.InvestigationSpikes(id, 0, 1, default)).Items.Count == 1, "Spike page missing.");
            await Check.ThrowsAsync<McpException>(() => access.InvestigationEvents(id, -1, 50, null, null, default));
            await Check.ThrowsAsync<McpException>(() => access.InvestigationSpikes(id, 0, 201, default));
            await Check.ThrowsAsync<McpException>(() => access.CompareInvestigations(id, id, "", default));
            ((IList<InvestigationEvent>)observed.Investigation!.Events)[0] = new() { Summary = "mutated" };
            Check.That((await sessions.GetAsync(id, default)).Investigation!.Events[0].Summary != "mutated", "Returned report mutates session state.");
            Check.That((await sessions.ListAsync(default)).Single().Investigation is null, "List included report detail.");
            var stopped = await Task.WhenAll(sessions.StopAsync(id, default), sessions.StopAsync(id, default), access.StopInvestigation(id, default));
            Check.That(factory.Stops == 1 && stopped.All(s => s.EndedUtc == stopped[0].EndedUtc), "Concurrent stop did not share finalization.");
        }
        await using (var sessions = new DiagnosticSessionService(backend, storage, factory))
        {
            var persisted = await sessions.GetAsync(id, default);
            Check.That(persisted.Status == "stopped" && persisted.Investigation!.Metrics.FrameCount == 3600 && persisted.InvestigationOptions!.DeepUsbTracing, "Report/options did not roundtrip through SessionStore.");
            var second = await sessions.StartInvestigationAsync(1, new(), default);
            await Task.Delay(1200);
            var complete = await sessions.GetAsync(second.Id, default);
            Check.That(complete.Status == "completed" && complete.Investigation is not null && factory.Stops == 2, "Duration did not clean up logger.");
            var access = new DiagnosticAccess(backend, sessions);
            var comparison = await access.CompareInvestigations(id, second.Id, "Disconnected a device", default);
            Check.That(comparison.MetricDeltas.Single(d => d.Metric == "Average FPS").Delta == 0 && comparison.EventRateChanges.Single().BeforeEvents == 600,
                "Comparison recomputed group rates from reduced event detail.");
            Check.That(comparison.Limitations.Any(l => l.Contains("does not prove", StringComparison.Ordinal)), "Comparison lost attribution limits.");
            var old = await sessions.StartAsync(10, "general", 1, default);
            await Check.ThrowsAsync<McpException>(() => access.InvestigationReport(old.Id, default));
            await sessions.StopAsync(old.Id, default);
        }
        await Unavailable(directory.Path, backend);
        await StartStopDisposeRace(directory.Path, backend);
        await InterruptedRecovery(directory.Path, backend);
    }

    static async Task Unavailable(string root, FixtureBackend backend)
    {
        await using var service = new DiagnosticSessionService(backend, Path.Combine(root, "failure"), new InvestigationFixture { Unavailable = true });
        var started = await service.StartInvestigationAsync(2, new(), default);
        var failed = await service.StopAsync(started.Id, default);
        Check.That(failed.Status == "failed" && failed.Investigation!.Collectors.Single().Availability == "permission_denied", "Unavailable collector lost explicit status.");
        Check.That(!JsonSerializer.Serialize(failed).Contains("secret raw"), "Collector error leaked internal data.");
    }

    static async Task StartStopDisposeRace(string root, FixtureBackend backend)
    {
        var factory = new InvestigationFixture { StartGate = new(), StopGate = new() };
        var service = new DiagnosticSessionService(backend, Path.Combine(root, "race"), factory);
        var started = await service.StartInvestigationAsync(30, new(), default);
        using var request = new CancellationTokenSource();
        var stop = service.StopAsync(started.Id, request.Token);
        request.Cancel();
        await Check.ThrowsAsync<OperationCanceledException>(() => stop);
        var disposal = service.DisposeAsync().AsTask();
        factory.StartGate.SetResult(); factory.StopGate.SetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(8));
        Check.That(factory.Stops == 1, "Stop/disposal while starting leaked or stopped the collector repeatedly.");
        await service.DisposeAsync();
    }

    static async Task InterruptedRecovery(string root, FixtureBackend backend)
    {
        var storage = Path.Combine(root, "recovery");
        await using var sessions = new DiagnosticSessionService(backend, storage, new InvestigationFixture());
        var started = await sessions.StartInvestigationAsync(10, new(), default);
        var current = await WithReport(sessions, started.Id);
        await sessions.StopAsync(started.Id, default);
        // Simulate a durable recording whose owner/lease vanished, using exactly the store envelope.
        var path = Path.Combine(storage, "mcp-sessions", started.Id.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Version = 1, WrittenUtc = DateTimeOffset.UtcNow, Session = current }));
        await using var recovered = new DiagnosticSessionService(backend, storage, new InvestigationFixture());
        var interrupted = await recovered.GetAsync(started.Id, default);
        Check.That(interrupted.Status == "interrupted" && interrupted.Investigation!.Metrics.FrameCount == 3600, "Interrupted owner recovery lost bounded report.");
    }

    internal static async Task<DiagnosticSession> WithReport(IDiagnosticSessions sessions, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var result = await sessions.GetAsync(id, timeout.Token);
            if (result.Investigation is not null) return result;
            await Task.Delay(20, timeout.Token);
        }
    }
}
