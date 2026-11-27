namespace JamesTools.Core.Diagnostics;

public sealed record InvestigationMetricDelta(string Metric, string Unit, double? Before, double? After,
    double? Delta, double? PercentChange);

public sealed record InvestigationCorrelationChange(InvestigationEventGroup Group, string BeforeConfidence,
    string AfterConfidence, int BeforeMatchedStalls, int AfterMatchedStalls, double? BeforeMatchFraction,
    double? AfterMatchFraction, double? MatchFractionDelta, double? BeforeExpectedChanceMatches,
    double? AfterExpectedChanceMatches);

public sealed record InvestigationEventRateChange(InvestigationEventGroup Group, int BeforeEvents, int AfterEvents,
    double? BeforeEventsPerMinute, double? AfterEventsPerMinute, double? DeltaEventsPerMinute);

public sealed record InvestigationComparisonReport
{
    public string ChangeDescription { get; init; } = "";
    public string Conclusion { get; init; } = "";
    public IReadOnlyList<InvestigationMetricDelta> MetricDeltas { get; init; } = [];
    public IReadOnlyList<InvestigationEventRateChange> EventRateChanges { get; init; } = [];
    public IReadOnlyList<InvestigationCorrelationChange> CorrelationChanges { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public static class InvestigationComparison
{
    public static InvestigationComparisonReport Compare(InvestigationReport before, InvestigationReport after, string changeDescription)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(changeDescription);
        var limits = new List<string>
        {
            "A before/after difference does not prove that the described change caused it. Scenes, workload, caps, settings and background activity are not established as controlled.",
            "Frame rates use observed frame exposure; event rates use recording duration. Different durations change exposure to rare events. Missing event groups are unobserved evidence, not proof that the source stopped.",
            "Correlation confidence describes temporal association only. Changing sample size, background event rates or competing evidence can change its label.",
            "Group identity includes process/thread IDs. These can change or be reused between recordings; only stable pseudonymized device IDs support device identity comparisons."
        };
        var compatible = before.ThresholdMs == after.ThresholdMs && before.WarmupSeconds == after.WarmupSeconds;
        if (!compatible) limits.Add("Threshold or warm-up differs: spike/cluster count and rate deltas are unavailable; other metrics remain descriptive.");
        if (!before.Coverage.ReliableForAssociation || !after.Coverage.ReliableForAssociation)
            limits.Add("One or both reports have incomplete/imprecise evidence. Missing frames, collector loss and retention limits can change apparent improvement and event associations.");
        if (before.DurationSeconds != after.DurationSeconds) limits.Add("Recording durations differ; compare normalized rates together with counts and coverage.");
        if (before.DominantStream != after.DominantStream) limits.Add("Selected stream identities differ. Check that both recordings contain the same application and presentation frame type.");
        if (before.Conditions != after.Conditions) limits.Add("Declared recording conditions differ; workload and settings are not directly comparable.");
        else if (string.IsNullOrWhiteSpace(before.Conditions.Scene)) limits.Add("Scene/workload conditions were not supplied; matching empty conditions do not establish a controlled retest.");
        limits.AddRange(before.Limitations.Select(l => "Before: " + l));
        limits.AddRange(after.Limitations.Select(l => "After: " + l));

        var metrics = new List<InvestigationMetricDelta>();
        void Add(string name, string unit, double? a, double? b)
        {
            var delta = a.HasValue && b.HasValue ? b - a : null;
            metrics.Add(new(name, unit, a, b, delta, a is not null and not 0 && delta is not null ? 100 * delta / Math.Abs(a.Value) : null));
        }
        var a = before.Metrics; var b = after.Metrics;
        Add("Average FPS", "fps", a.AverageFps, b.AverageFps);
        Add("1% low FPS", "fps", a.OnePercentLowFps, b.OnePercentLowFps);
        Add("0.1% low FPS", "fps", a.PointOnePercentLowFps, b.PointOnePercentLowFps);
        Add("Mean frame interval", "ms", a.MeanFrameMs, b.MeanFrameMs);
        Add("P99 frame interval", "ms", a.P99Ms, b.P99Ms);
        Add("Maximum stall", "ms", a.MaximumStallMs, b.MaximumStallMs);
        Add("Observed frame exposure", "s", before.Coverage.ObservedFrameSeconds, after.Coverage.ObservedFrameSeconds);
        Add("Frame coverage", "fraction", before.Coverage.FrameCoverageFraction, after.Coverage.FrameCoverageFraction);
        Add("Slow frames", "frames", compatible ? a.SlowFrameCount : null, compatible ? b.SlowFrameCount : null);
        Add("Stall clusters", "clusters", compatible ? a.ClusterCount : null, compatible ? b.ClusterCount : null);
        Add("Spikes per minute", "/min", compatible ? a.SpikesPerMinute : null, compatible ? b.SpikesPerMinute : null);
        Add("Stall clusters per minute", "/min", compatible ? a.ClustersPerMinute : null, compatible ? b.ClustersPerMinute : null);
        Add("Events per minute", "/min", a.EventsPerMinute, b.EventsPerMinute);
        Add("Stall recurrence mean interval", "s", before.Periodicity.MeanIntervalSeconds, after.Periodicity.MeanIntervalSeconds);

        var eventsA = before.Events.GroupBy(Group).ToDictionary(g => g.Key, g => g.Count());
        var eventsB = after.Events.GroupBy(Group).ToDictionary(g => g.Key, g => g.Count());
        var eventChanges = eventsA.Keys.Union(eventsB.Keys).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var countA = eventsA.GetValueOrDefault(g); var countB = eventsB.GetValueOrDefault(g);
            var rateA = DiagnosticComparison.RatePerMinute(countA, before.DurationSeconds);
            var rateB = DiagnosticComparison.RatePerMinute(countB, after.DurationSeconds);
            return new InvestigationEventRateChange(g, countA, countB, rateA, rateB, rateB - rateA);
        }).ToArray();
        var corrA = before.Correlations.ToDictionary(c => c.Group);
        var corrB = after.Correlations.ToDictionary(c => c.Group);
        var changes = corrA.Keys.Union(corrB.Keys).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            corrA.TryGetValue(g, out var x); corrB.TryGetValue(g, out var y);
            return new InvestigationCorrelationChange(g, x?.Confidence ?? "unobserved", y?.Confidence ?? "unobserved", x?.MatchedStalls ?? 0,
                y?.MatchedStalls ?? 0, x?.MatchFraction, y?.MatchFraction, y?.MatchFraction - x?.MatchFraction,
                x?.ExpectedChanceMatches, y?.ExpectedChanceMatches);
        }).ToArray();
        var conclusion = "Frame evidence is insufficient to compare stall rates.";
        if (!compatible) conclusion = "Stall rates cannot be compared because threshold or warm-up differs.";
        else if (a.ClustersPerMinute is { } oldRate && b.ClustersPerMinute is { } newRate)
            conclusion = FormattableString.Invariant($"Observed stall clusters changed from {oldRate:F2} to {newRate:F2} per measured minute ({(newRate < oldRate ? "lower" : newRate > oldRate ? "higher" : "unchanged")}). This does not establish the effect of the described change.");
        return new() { ChangeDescription = InvestigationReportWriter.SafeText(changeDescription), Conclusion = conclusion,
            MetricDeltas = metrics, EventRateChanges = eventChanges, CorrelationChanges = changes, Limitations = limits.Distinct().ToArray() };
    }

    private static InvestigationEventGroup Group(InvestigationEvent e) => new(e.Source, e.Category, e.EventType, e.DeviceId, e.ProcessId, e.ThreadId);
}
