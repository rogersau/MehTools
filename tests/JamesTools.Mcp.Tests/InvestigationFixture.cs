using JamesTools.Core.Diagnostics;
using JamesTools.Core.Mcp;

namespace JamesTools.Mcp.Tests;

internal sealed class InvestigationFixture : IInvestigationCaptureFactory
{
    internal int Starts, Stops;
    internal bool Unavailable;
    internal TaskCompletionSource? StartGate, StopGate;
    internal StutterInvestigationOptions? LastOptions;
    internal InvestigationReport Report = CreateReport();
    public string? ValidateTarget(int? processId) => processId switch { null => null, 4242 => "fixture-application", _ => throw new UnauthorizedAccessException() };
    public async Task<IInvestigationCapture> StartAsync(string? target, int duration, StutterInvestigationOptions options, CancellationToken token)
    {
        Interlocked.Increment(ref Starts); LastOptions = options;
        if (StartGate is { } gate) await gate.Task; // Deliberately independent of request cancellation.
        if (Unavailable) throw new UnauthorizedAccessException("secret raw device identifier must never escape");
        return new Capture(this);
    }
    sealed class Capture(InvestigationFixture owner) : IInvestigationCapture
    {
        public InvestigationReport Snapshot() => owner.Report;
        public async Task<InvestigationReport> StopAsync()
        {
            Interlocked.Increment(ref owner.Stops);
            if (owner.StopGate is { } gate) await gate.Task;
            return owner.Report;
        }
    }
    internal static InvestigationReport CreateReport(int count = 600)
    {
        var group = new InvestigationEventGroup("Windows devices", "USB", "Reconnect", "device-hash", null, null);
        var frame = new FrameTelemetry(1, 1.1, "QPC", "app-stream", 50, 10, 20, null, null, null, null, null, null, null, "Composed", "Application");
        return new()
        {
            DurationSeconds = 60, DominantStream = "app-stream",
            Metrics = new() { FrameCount = 3600, AverageFps = 60, SlowFrameCount = 600, ClusterCount = 200, ClustersPerMinute = 200 },
            Coverage = new() { InputFrames = 3600, SelectedFrames = 3600, InputEvents = count, ReliableForAssociation = true, FrameCoverageFraction = 1 },
            Devices = [new() { Id = "device-hash", Name = "Fixture USB device", ControllerId = "controller-hash", ParentHubId = "hub-hash", Topology = "controller/hub/device" }],
            Events = Enumerable.Range(0, count).Select(i => new InvestigationEvent { TimestampSeconds = i / 10d, ReceiptSeconds = i / 10d + .01,
                Source = group.Source, Category = group.Category, EventType = group.EventType, DeviceId = group.DeviceId, Summary = new string('Ω', 250),
                Details = new Dictionary<string, string> { ["state"] = new string('x', 240), ["privatePath"] = @"C:\Users\private\secret", ["note"] = "observed" } }).ToArray(),
            Spikes = Enumerable.Range(0, count).Select(i => frame with { OffsetSeconds = i / 10d }).ToArray(),
            Correlations = [new() { Group = group, EventCount = count, EventRatePerMinute = count, MatchedStalls = 10, StallCount = 200,
                Confidence = "weak", Matches = Enumerable.Range(0, 100).Select(i => new InvestigationEventMatch(i, i + .1, i + .01, 10, 10)).ToArray(),
                Limitations = ["Timing is an association and never establishes causation."] }],
            Collectors = [new("Windows devices", "available", "fixture"), new("PresentMon", "unavailable", "Fixture frame collection unavailable; numeric metrics here exercise report serialization only.")],
            Findings = ["Fixture statistics remain available when evidence detail is reduced."],
            Limitations = ["Deterministic fixture; no hardware was collected."]
        };
    }
}
