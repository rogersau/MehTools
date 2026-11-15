using JamesTools.Core.Mcp;

namespace JamesTools.Mcp.Tests;

// Test-only dependency. Production has no fixture switch or environment variable.
internal sealed class FixtureBackend : IDiagnosticBackend
{
    internal static readonly DateTimeOffset Epoch = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    internal int Samples;
    internal bool Oversized;
    internal bool Fail;
    internal int DelayMilliseconds;
    internal TaskCompletionSource? InventoryGate;
    internal int InventoryReads;

    public DiagnosticCapabilities GetCapabilities() => new(1,
        ["cpu", "gpu", "memory", "storage", "network", "system"],
        ["general", "gaming", "cpu", "gpu", "storage"],
        ["Deterministic test fixture; no hardware is queried."]);

    public async Task<SystemInventory> GetSystemInfoAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InventoryReads);
        if (InventoryGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fail) throw new InvalidOperationException("fixture failure: sensitive internal path must not escape");
        return new SystemInventory(1, Epoch,
            [new("cpu", "Fixture CPU", new Dictionary<string, string?> { ["logicalProcessors"] = "8", ["serial"] = null }, new("available"))],
            Oversized ? [new string('x', 2 * 1024 * 1024)] : []);
    }

    public async Task<MetricsSnapshot> SampleAsync(CancellationToken cancellationToken)
    {
        if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var index = Interlocked.Increment(ref Samples);
        var observedAt = DateTimeOffset.UtcNow;
        return new(1, observedAt,
        [new("cpu.utilization", 20 + index, "%", observedAt, "available", "fixture"),
         new("memory.physical_used", 4096, "MiB", observedAt, "available", "fixture"),
         new("gpu.temperature", null, "degC", observedAt, "unsupported", "fixture", "No supported sensor."),
         new("cpu.temperature", null, "degC", observedAt, "permission_denied", "fixture", "Permission denied."),
         new("gpu.power", null, "W", observedAt, "unavailable", "fixture", "Temporarily unavailable."),
         new("gpu.clock", null, "MHz", observedAt, "failure", "fixture", "Read failed.")], []);
    }

    public Task<DiagnosticResult> RunDiagnosticsAsync(string category, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DiagnosticResult(1, Epoch, category, "available",
            [new(category, "info", "Fixture observation", "No fault inferred.", "low", ["fixture"], ["Repeat under representative load."])], [], [], []));
    }

    public Task<GameInspection> InspectGameAsync(string game, CancellationToken cancellationToken) =>
        Task.FromResult(game != "fixture-game" ? new GameInspection(1, Epoch, "permission_denied", null, null, [], ["Game is not in the fixture catalog."]) : new GameInspection(1, Epoch, "available", game, null,
            [new("bin/game.exe", "executable", "1.0", "fixture", "unknown"),
             new("nvngx_dlss.dll", "dlss", "3.0", "Installed file only; runtime use unverified.", "unknown")], []));

    public Task<EventLogResult> GetEventLogsAsync(EventLogFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult(new EventLogResult(1, Epoch, "available",
            [new(Epoch, "Fixture", 42, "warning", "system", "Fixture event")], false, []));

    public Task<HealthReport> GetHealthReportAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HealthReport(1, Epoch, "insufficient_evidence", [], ["Unsupported sensors cannot establish hardware health."]));

    public void Dispose() { }
}
