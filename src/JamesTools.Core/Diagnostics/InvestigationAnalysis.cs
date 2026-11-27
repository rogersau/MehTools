namespace JamesTools.Core.Diagnostics;

/// <summary>Deterministic retained-evidence analysis. The caller supplies one recording's elapsed clock.</summary>
public static class InvestigationAnalysis
{
    public const int MaximumAnalyzedFrames = 1_000_000;
    public const int MaximumRetainedEvents = 50_000;
    public const int MaximumRetainedSpikes = 10_000;
    public const int MaximumRetainedClusters = 10_000;
    public const int MaximumAnalyzedGroups = 256;
    public const double PrecedingWindowSeconds = .100;
    public const double FollowingWindowSeconds = .050;
    private const double ClusterGapSeconds = .100;

    public static InvestigationReport Analyze(IReadOnlyList<FrameTelemetry> frames,
        IReadOnlyList<InvestigationEvent> events, IReadOnlyList<InvestigationDevice> devices,
        IReadOnlyList<DiagnosticHardwareSample> hardware, IReadOnlyList<InvestigationCollectorStatus> collectors,
        double durationSeconds, double thresholdMs = 33.333)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(collectors);
        if (!double.IsFinite(durationSeconds) || durationSeconds < 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (!double.IsFinite(thresholdMs) || thresholdMs <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdMs));

        var limits = new List<string>
        {
            "Temporal association is not causation. Confidence labels describe repeated timing evidence only; controlled retests and thread-level evidence are needed.",
            "Warm-up exclusion is 0 seconds. Only complete frame intervals and event timestamps inside [0, duration] are analyzed; caller must exclude loading/warm-up before capture if required.",
            "Average FPS is inverse mean frame interval. Lows use the mean of the slowest ceil(1%/0.1%) intervals. Percentiles reuse 0.1 ms upper-edge histogram buckets; values above 10 seconds have unavailable percentiles.",
            "Spike and event periodicity uses consecutive cluster/event onsets: at least four occurrences, mean interval >=0.2 s and interval coefficient of variation <=0.10. Missing occurrences and changing periods can hide a pattern.",
            "Event groups use the full source/category/type/device/process/thread tuple. PIDs and thread IDs can be reused; unidentified devices and uninstrumented sources cannot be excluded.",
            "One-to-one event/cluster matches use event duration plus 100 ms before and 50 ms after the stalled frame interval. Negative offsets precede the stall; offsets do not establish a mechanism.",
            "Expected chance uses event timing-window occupancy. Confidence also uses 128 phase shifts preserving the event pattern within the overlapping observation window, allowing one boundary mismatch when at least eight stalls remain. Adjusted probability is the larger of this empirical null and the independent-trial estimate, Bonferroni-adjusted across groups. Finite recordings and shared clocks still limit interpretation.",
            "Hardware samples cover collection/counter windows, not individual frames. CPU activity or periodic events without stalled frames are not identified as culprits."
        };

        bool ValidFrame(FrameTelemetry f) => double.IsFinite(f.OffsetSeconds) && double.IsFinite(f.FrameMs) && f.FrameMs > 0 &&
            double.IsFinite(f.IntervalEndSeconds) && f.IntervalStartSeconds >= 0 && f.IntervalEndSeconds <= durationSeconds;
        var validCount = frames.Count(ValidFrame);
        var bounded = frames.Where(ValidFrame).OrderBy(f => f.IntervalStartSeconds).ThenBy(f => f.Stream, StringComparer.Ordinal)
            .ThenBy(f => f.FrameMs).Take(MaximumAnalyzedFrames).ToArray();
        var unique = bounded.DistinctBy(f => (f.Stream, f.OffsetSeconds, f.FrameMs, f.FrameTimeSource)).ToArray();
        // Match FrameReportCollector's application-frame preference and count-based stream selection; break ties ordinally.
        var streams = unique.GroupBy(f => f.Stream).OrderByDescending(g => g.Any(f => IsApplication(f.FrameType)))
            .ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var selected = streams.FirstOrDefault()?.Select(CleanFrame).ToArray() ?? [];
        var analysis = new FrameAnalysis();
        var pacing = new FramePacing(new DiagnosticOptions { StutterThresholdMs = thresholdMs, WarmupSeconds = 0 });
        foreach (var f in selected) { analysis.Add(f.FrameMs, f.OffsetSeconds); pacing.Add(f); }
        var summary = analysis.GetSummary();
        var pacingSummary = pacing.Summary();
        var exposure = UnionSorted(selected.Select(f => (f.IntervalStartSeconds, f.IntervalEndSeconds)));
        var observedSeconds = exposure.Sum(p => p.End - p.Start);
        var coverageFraction = durationSeconds > 0 ? Math.Clamp(observedSeconds / durationSeconds, 0, 1) : 0;
        var gaps = exposure.Zip(exposure.Skip(1)).Count(p => p.Second.Start - p.First.End > .100);
        var overlapping = selected.Zip(selected.Skip(1)).Any(p => p.First.IntervalEndSeconds - p.Second.IntervalStartSeconds > .005);
        var spikes = selected.Where(f => f.FrameMs > thresholdMs).ToArray();
        var allClusters = Cluster(spikes);
        var clusters = allClusters.Take(MaximumRetainedClusters).Select(c => new InvestigationSpikeCluster(c.StartSeconds,
            c.EndSeconds, c.FrameCount, c.Peak, FrameDiagnosis.Investigate(c.Peak))).ToArray();
        var impreciseFrames = selected.Count(f => !PreciseFrame(f));

        bool ValidEvent(InvestigationEvent e) => double.IsFinite(e.TimestampSeconds) && e.TimestampSeconds >= 0 && e.TimestampSeconds <= durationSeconds &&
            double.IsFinite(e.ReceiptSeconds) && (e.DurationMs is null || double.IsFinite(e.DurationMs.Value) && e.DurationMs >= 0) &&
            (e.UncertaintyMs is null || double.IsFinite(e.UncertaintyMs.Value) && e.UncertaintyMs >= 0);
        var validEvents = new List<InvestigationEvent>(events.Count);
        var outOfOrder = 0;
        var latest = double.NegativeInfinity;
        foreach (var e in events)
        {
            if (!ValidEvent(e)) continue;
            validEvents.Add(e);
            if (e.TimestampSeconds < latest) outOfOrder++;
            latest = Math.Max(latest, e.TimestampSeconds);
        }
        var groupKeys = new Dictionary<InvestigationEventGroup, string>();
        string GroupKey(InvestigationEventGroup group)
        {
            if (groupKeys.TryGetValue(group, out var key)) return key;
            return groupKeys[group] = group.Key;
        }
        var validEventCount = validEvents.Count;
        var accepted = validEvents.OrderBy(e => e.TimestampSeconds).ThenBy(e => GroupKey(Group(e)), StringComparer.Ordinal)
            .ThenBy(e => e.ReceiptSeconds).ThenBy(e => e.DurationMs).Take(MaximumRetainedEvents).ToArray();
        var delayed = accepted.Count(e => e.ReceiptSeconds - e.TimestampSeconds > .250);
        var lost = collectors.Sum(c => Math.Max(0, c.EventsLost));
        var dropped = collectors.Sum(c => Math.Max(0, c.EventsDropped));
        var groups = accepted.GroupBy(Group).OrderBy(g => GroupKey(g.Key), StringComparer.Ordinal).ToArray();
        var reliable = selected.Length >= 30 && coverageFraction >= .98 && gaps == 0 && !overlapping && impreciseFrames == 0 &&
            lost == 0 && dropped == 0 && validCount <= MaximumAnalyzedFrames && validEventCount <= MaximumRetainedEvents &&
            validEventCount == events.Count && !frames.Any(f => !double.IsFinite(f.FrameMs) || f.FrameMs <= 0 || !double.IsFinite(f.OffsetSeconds)) &&
            allClusters.Count <= MaximumRetainedClusters && groups.Length <= MaximumAnalyzedGroups;
        var coverage = new InvestigationCoverage
        {
            InputFrames = frames.Count, SelectedFrames = selected.Length, OtherStreamFrames = unique.Length - selected.Length,
            RejectedFrames = frames.Count - validCount, DuplicateFrames = bounded.Length - unique.Length,
            FramesOmittedByLimit = Math.Max(0, validCount - bounded.Length), InputEvents = events.Count,
            RejectedEvents = events.Count - validEventCount, EventsOmittedByLimit = Math.Max(0, validEventCount - accepted.Length),
            SpikesOmittedByLimit = Math.Max(0, spikes.Length - MaximumRetainedSpikes),
            ClustersOmittedByLimit = Math.Max(0, allClusters.Count - clusters.Length),
            GroupsOmittedByLimit = Math.Max(0, groups.Length - MaximumAnalyzedGroups),
            FrameGapCount = gaps, ObservedFrameSeconds = observedSeconds, FrameCoverageFraction = coverageFraction,
            ImpreciseFrames = impreciseFrames, ImpreciseEvents = accepted.Count(e => !PreciseEvent(e)),
            DelayedEvents = delayed, OutOfOrderEvents = outOfOrder, HardwareSamples = hardware.Count,
            CollectorEventsLost = lost, CollectorEventsDropped = dropped, ReliableForAssociation = reliable
        };

        var stallWindows = UnionSorted(clusters.Select(s => (Math.Max(0, s.StartSeconds - PrecedingWindowSeconds), Math.Min(durationSeconds, s.EndSeconds + FollowingWindowSeconds))));
        var backgroundSeconds = Math.Max(0, observedSeconds - IntersectionSeconds(stallWindows, exposure));
        var correlations = groups.Take(MaximumAnalyzedGroups).Select(g => Correlate(g.Key, g.ToArray(), clusters,
            exposure, stallWindows, durationSeconds, observedSeconds, backgroundSeconds, reliable, collectors, groups.Length)).ToArray();
        var contenders = correlations.Where(c => c.Confidence.EndsWith(" association", StringComparison.Ordinal)).Select(c => c.Group.Key).ToArray();
        if (contenders.Length > 1)
            correlations = correlations.Select(c => contenders.Contains(c.Group.Key, StringComparer.Ordinal)
                ? c with { Confidence = "moderate association", CompetingGroups = contenders.Where(k => k != c.Group.Key).ToArray(),
                    Assessment = c.Assessment + " Multiple competing event groups share the timing pattern; attribution is unresolved." } : c).ToArray();
        correlations = correlations.OrderByDescending(c => c.MatchedStalls).ThenBy(c => c.AdjustedChanceProbability)
            .ThenBy(c => c.Group.Key, StringComparer.Ordinal).ToArray();
        var periodicity = Periods(allClusters.Select(c => c.StartSeconds).ToArray());
        var independent = groups.Take(MaximumAnalyzedGroups).Select(g => new InvestigationEventPeriodicity(g.Key,
            Periods(g.Where(PreciseEvent).Select(e => e.TimestampSeconds).Distinct().ToArray()))).ToArray();
        var findings = new List<string>();
        if (selected.Length == 0) findings.Add("No valid frames: frame statistics and stutter attribution are unavailable.");
        else if (spikes.Length == 0) findings.Add("No frames exceed the configured threshold. Background CPU/device activity is not evidence of a stutter culprit.");
        else findings.Add($"{spikes.Length} slow frames form {allClusters.Count} stall clusters in the selected presentation stream.");
        if (periodicity.IsPeriodic) findings.Add(FormattableString.Invariant($"Stall clusters recur at mean {periodicity.MeanIntervalSeconds:F3} s ({periodicity.Occurrences} occurrences); this pattern has no assumed game or event source."));
        if (spikes.Length > 0 && contenders.Length == 0) findings.Add("Stalls remain unexplained: no event group meets the repeated, precise, above-background association criteria.");
        if (contenders.Length > 1) findings.Add("Several competing evidence groups align with stalls. Timing alone cannot select one explanation.");
        foreach (var c in correlations.Where(c => c.Confidence.EndsWith(" association", StringComparison.Ordinal)))
            findings.Add($"{c.Group.Source}/{c.Group.Category}/{c.Group.EventType}: {c.MatchedStalls}/{c.StallCount} stalls matched; {c.Confidence}, never causal confidence.");
        foreach (var c in correlations.Where(c => c.NotificationCoincidentStalls >= 4 && c.NotificationCoincidentStalls >= c.StallCount * .5).Take(8))
            findings.Add($"{c.Group.Source}/{c.Group.EventType}: notification deliveries coincided with {c.NotificationCoincidentStalls}/{c.StallCount} stalls. Occurrence latency is unknown; this suggests a controlled retest, not a precise or causal attribution.");
        if (independent.Any(e => e.Periodicity.IsPeriodic) && spikes.Length == 0)
            findings.Add("Periodic event activity was detected independently, without matching frame spikes.");

        if (durationSeconds < 30) limits.Add("Short recording (<30 s): too little exposure to assess rare stalls or stable recurrence reliably.");
        if (coverage.RejectedFrames > 0 || coverage.RejectedEvents > 0) limits.Add($"Rejected invalid/out-of-duration evidence: {coverage.RejectedFrames} frames and {coverage.RejectedEvents} events; boundary intervals are not clipped into artificial stalls.");
        if (coverage.DuplicateFrames > 0) limits.Add($"Removed {coverage.DuplicateFrames} duplicate frame intervals; repeated delivery is not an additional stall.");
        if (streams.Length > 1) limits.Add($"Selected one of {streams.Length} streams; {coverage.OtherStreamFrames} frames from other streams are excluded. Application-frame preference, then frame count, then ordinal stream name determines selection.");
        if (!reliable) limits.Add("Incomplete or imprecise frame/evidence coverage: association confidence is capped at weak. A missing frame record is not an observed stall and absence of events is not proof of absence.");
        if (gaps > 0 || coverageFraction < .98 || overlapping) limits.Add(FormattableString.Invariant($"Frame coverage {coverageFraction:P1}; {gaps} internal gaps >100 ms; overlapping intervals: {overlapping}. At least 98% coverage is required for association confidence. Unobserved time is excluded from background exposure."));
        if (lost > 0 || dropped > 0) limits.Add($"Collector loss: {lost} lost and {dropped} dropped events. Selective retention can create apparent timing associations.");
        if (collectors.Count == 0 || collectors.Any(c => !Available(c))) limits.Add("Collector inventory is missing or includes unavailable/partial sources; unobserved categories cannot be ruled out.");
        if (delayed > 0 || outOfOrder > 0) limits.Add($"{delayed} events delivered >250 ms late; {outOfOrder} arrived out of event-time order. Analysis sorts original event timestamps; receipt time is never substituted for event time.");
        if (coverage.ImpreciseEvents > 0 || impreciseFrames > 0) limits.Add("Receipt-time, unknown-clock, poor-quality or >20 ms uncertainty evidence is excluded from precise matching; no wider window is used to manufacture matches.");
        limits.Add($"Retention limits: {MaximumAnalyzedFrames} frames, {MaximumRetainedEvents} events, {MaximumRetainedSpikes} spikes, {MaximumRetainedClusters} clusters and {MaximumAnalyzedGroups} groups. Limit omissions are explicit in Coverage; complete-frame metrics precede spike/cluster reduction.");
        if (coverage.FramesOmittedByLimit + coverage.EventsOmittedByLimit + coverage.SpikesOmittedByLimit + coverage.ClustersOmittedByLimit + coverage.GroupsOmittedByLimit > 0)
            limits.Add("Retention limits were reached. Results describe retained evidence; event rates and recurrence may undercount the full recording.");

        var slowest = selected.Select(f => f.FrameMs).OrderDescending().ToArray();
        double? Low(double portion) => slowest.Length == 0 ? null : 1000 / slowest.Take(Math.Max(1, (int)Math.Ceiling(slowest.Length * portion))).Average();
        return new InvestigationReport
        {
            DurationSeconds = durationSeconds, ThresholdMs = thresholdMs, DominantStream = streams.FirstOrDefault()?.Key,
            Metrics = new InvestigationMetrics
            {
                FrameCount = summary.Count, AverageFps = summary.Count > 0 ? summary.EffectiveFps : null,
                MeanFrameMs = summary.Count > 0 ? summary.MeanMs : null, OnePercentLowFps = Low(.01), PointOnePercentLowFps = Low(.001),
                P50Ms = summary.P50Ms, P95Ms = summary.P95Ms, P99Ms = summary.P99Ms, P999Ms = summary.P999Ms,
                MaximumStallMs = slowest.Length > 0 ? slowest[0] : null, SlowFrameCount = spikes.Length, ClusterCount = allClusters.Count,
                MeasuredFrameSeconds = pacingSummary.MeasuredFrameSeconds, Distribution = pacingSummary.Histogram,
                SpikesPerMinute = DiagnosticComparison.RatePerMinute(spikes.Length, observedSeconds),
                ClustersPerMinute = DiagnosticComparison.RatePerMinute(allClusters.Count, observedSeconds),
                EventsPerMinute = DiagnosticComparison.RatePerMinute(accepted.Length, durationSeconds)
            },
            Coverage = coverage, Events = accepted, Devices = devices.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray(), Hardware = hardware.ToArray(),
            Spikes = spikes.Take(MaximumRetainedSpikes).ToArray(), Clusters = clusters, Periodicity = periodicity,
            EventPeriodicity = independent, Correlations = correlations, Collectors = collectors.OrderBy(c => c.Source, StringComparer.Ordinal).ToArray(),
            Findings = findings, Limitations = limits
        };
    }

    private static InvestigationEventGroup Group(InvestigationEvent e) => new(e.Source, e.Category, e.EventType, e.DeviceId, e.ProcessId, e.ThreadId);
    private static bool IsApplication(string? type) => type is "Application" or "ApplicationFrame" or "Application Frame";
    private static bool Qpc(string? basis) => basis?.Contains("QPC", StringComparison.OrdinalIgnoreCase) == true &&
        !basis.Contains("receipt", StringComparison.OrdinalIgnoreCase) && !basis.Contains("approx", StringComparison.OrdinalIgnoreCase);
    private static bool PreciseFrame(FrameTelemetry f) => Qpc(f.TimestampSource);
    private static bool PreciseEvent(InvestigationEvent e) =>
        (Qpc(e.TimestampBasis) || e.TimestampBasis.Equals("UTC", StringComparison.OrdinalIgnoreCase) && e.UncertaintyMs is <= 20) &&
        (e.UncertaintyMs is null or <= 20) && e.Quality.Equals("observed", StringComparison.OrdinalIgnoreCase) &&
        e.ReceiptSeconds >= e.TimestampSeconds - Math.Max(.020, (e.UncertaintyMs ?? 0) / 1000);
    private static bool Available(InvestigationCollectorStatus c) => c.Availability.Equals("available", StringComparison.OrdinalIgnoreCase) ||
        c.Availability.Equals("active", StringComparison.OrdinalIgnoreCase) || c.Availability.Equals("observed", StringComparison.OrdinalIgnoreCase) ||
        c.Availability.Equals("stopped", StringComparison.OrdinalIgnoreCase);

    private static FrameTelemetry CleanFrame(FrameTelemetry f)
    {
        static double? N(double? n) => n is { } d && double.IsFinite(d) ? d : null;
        return f with { ReceiptSeconds = double.IsFinite(f.ReceiptSeconds) ? f.ReceiptSeconds : f.OffsetSeconds,
            CpuBusyMs = N(f.CpuBusyMs), CpuWaitMs = N(f.CpuWaitMs), GpuBusyMs = N(f.GpuBusyMs), GpuWaitMs = N(f.GpuWaitMs),
            GpuTimeMs = N(f.GpuTimeMs), GpuLatencyMs = N(f.GpuLatencyMs), RenderPresentLatencyMs = N(f.RenderPresentLatencyMs),
            DisplayLatencyMs = N(f.DisplayLatencyMs), InPresentApiMs = N(f.InPresentApiMs), OriginalTimestamp = N(f.OriginalTimestamp) };
    }

    private sealed record ClusterInterval(double StartSeconds, double EndSeconds, int FrameCount, FrameTelemetry Peak);

    private static List<ClusterInterval> Cluster(FrameTelemetry[] spikes)
    {
        var result = new List<ClusterInterval>();
        foreach (var f in spikes)
        {
            if (result.Count == 0 || f.IntervalStartSeconds - result[^1].EndSeconds > ClusterGapSeconds)
                result.Add(new(f.IntervalStartSeconds, f.IntervalEndSeconds, 1, f));
            else
            {
                var c = result[^1];
                var peak = f.FrameMs > c.Peak.FrameMs ? f : c.Peak;
                result[^1] = c with { EndSeconds = Math.Max(c.EndSeconds, f.IntervalEndSeconds), FrameCount = c.FrameCount + 1, Peak = peak };
            }
        }
        return result;
    }

    private static InvestigationPeriodicity Periods(double[] times)
    {
        if (times.Length < 2) return new() { Occurrences = times.Length };
        var intervals = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
        var mean = intervals.Average();
        var variance = intervals.Average(d => (d - mean) * (d - mean));
        var sd = Math.Sqrt(variance);
        var cv = mean > 0 ? sd / mean : (double?)null;
        var periodic = times.Length >= 4 && mean >= .2 && cv <= .10;
        return new() { Occurrences = times.Length, IntervalCount = intervals.Length, MeanIntervalSeconds = mean,
            IntervalVarianceSecondsSquared = variance, IntervalStandardDeviationSeconds = sd, CoefficientOfVariation = cv,
            IsPeriodic = periodic, Assessment = times.Length < 4 ? "Insufficient occurrences (at least four required)." :
                periodic ? "Regular consecutive intervals observed; recurrence does not imply a cause." : "No stable consecutive-interval periodicity detected." };
    }

    private static InvestigationCorrelation Correlate(InvestigationEventGroup group, InvestigationEvent[] events,
        InvestigationSpikeCluster[] stalls, List<(double Start, double End)> exposure, List<(double Start, double End)> stallWindows,
        double duration, double observed, double backgroundSeconds,
        bool reliable, IReadOnlyList<InvestigationCollectorStatus> collectors, int testedGroups)
    {
        // Summed durations in native ETW aggregates do not describe contiguous time spans.
        var timed = events.Select(e => new TimedEvent(e)).ToArray();
        var precise = timed.Where(e => PreciseEvent(e.Evidence)).ToArray();
        var delivered = timed.Where(e => !PreciseEvent(e.Evidence)).Select(e => e.Evidence.ReceiptSeconds).Order().ToArray();
        var deliveryOffsets = new List<double>();
        var deliveredIndex = 0;
        foreach (var stall in stalls)
        {
            while (deliveredIndex < delivered.Length && delivered[deliveredIndex] < stall.StartSeconds - PrecedingWindowSeconds) deliveredIndex++;
            if (deliveredIndex < delivered.Length && delivered[deliveredIndex] <= stall.EndSeconds + FollowingWindowSeconds)
                deliveryOffsets.Add((delivered[deliveredIndex++] - stall.StartSeconds) * 1000);
        }
        var matches = new List<InvestigationEventMatch>();
        // Earliest-expiring eligible event first: each event and each cluster contributes at most one match.
        var active = new PriorityQueue<InvestigationEvent, (double End, double Start, int Order)>();
        var index = 0;
        foreach (var s in stalls)
        {
            while (index < precise.Length && precise[index].Start <= s.EndSeconds + FollowingWindowSeconds)
            {
                var e = precise[index];
                active.Enqueue(e.Evidence, (e.End, e.Start, index++));
            }
            while (active.TryPeek(out _, out var p) && p.End < s.StartSeconds - PrecedingWindowSeconds) active.Dequeue();
            if (active.TryDequeue(out var chosen, out _))
                matches.Add(new(s.StartSeconds, s.EndSeconds, chosen.TimestampSeconds, (chosen.TimestampSeconds - s.StartSeconds) * 1000,
                    (chosen.ReceiptSeconds - chosen.TimestampSeconds) * 1000));
        }
        var meanStallSeconds = stalls.Length == 0 ? 0 : stalls.Average(s => s.EndSeconds - s.StartSeconds);
        var eventWindows = UnionSorted(precise.Select(e => (Math.Max(0, e.Start - meanStallSeconds - FollowingWindowSeconds),
            Math.Min(duration, e.End + PrecedingWindowSeconds))));
        var chance = observed > 0 ? Math.Clamp(IntersectionSeconds(eventWindows, exposure) / observed, 0, 1) : 1;
        var expected = stalls.Length * chance;
        var fraction = stalls.Length > 0 ? matches.Count / (double)stalls.Length : (double?)null;
        var independentProbability = BinomialTail(stalls.Length, matches.Count, chance);
        var phaseProbability = matches.Count >= 4 && fraction >= .5 && chance <= .25
            ? PhaseShiftProbability(precise, stalls, duration, matches.Count) : 1;
        var probability = Math.Min(1, Math.Max(independentProbability, phaseProbability) * Math.Max(1, testedGroups));
        var source = collectors.Where(c => c.Source.Equals(group.Source, StringComparison.OrdinalIgnoreCase)).ToArray();
        var sourceReliable = source.Length > 0 && source.All(c => Available(c) && c.EventsLost == 0 && c.EventsDropped == 0);
        var reasons = new List<string>();
        if (!reliable) reasons.Add("Frame coverage, precision, loss or analysis limits prevent reliable association confidence.");
        if (!sourceReliable) reasons.Add("Matching collector status is absent, unavailable, partial or reports event loss.");
        if (precise.Length != events.Length) reasons.Add("Imprecise events are retained for inspection but excluded from matching.");
        if (matches.Count < 4) reasons.Add("Fewer than four distinct stall/event matches; isolated coincidences are insufficient.");
        if (chance > .25) reasons.Add("Dense/long-duration background events make timing coincidence likely.");
        if (stalls.Length == 0) reasons.Add("No observed stall clusters; activity alone cannot explain a stutter.");
        var aggregates = timed.Any(e => e.Aggregated);
        if (aggregates) reasons.Add("Aggregated observations are not individual event occurrences. Only the first timestamp is matched; summed durations are not treated as continuous overlap. Confidence is capped at weak.");
        var confidence = matches.Count == 0 ? "insufficient" : "weak";
        if (reliable && sourceReliable && !aggregates && precise.Length == events.Length && matches.Count >= 4 && fraction >= .5 && chance <= .25 && probability <= .15)
            confidence = matches.Count >= 8 && fraction >= .75 && probability <= .01 ? "strong association" : "moderate association";
        reasons.Add("Phase shifts preserve temporal dependence; a repeating timer is not a set of independent trials. Moderate association is descriptive timing evidence, not statistical proof of a mechanism.");
        double? offset = matches.Count == 0 ? null : matches.Average(m => m.OffsetMs);
        var backgroundCount = 0;
        var windowIndex = 0;
        foreach (var e in timed)
        {
            while (windowIndex < stallWindows.Count && stallWindows[windowIndex].End < e.Start) windowIndex++;
            if (windowIndex >= stallWindows.Count || stallWindows[windowIndex].Start > e.End) backgroundCount++;
        }
        return new()
        {
            Group = group, EventCount = events.Length, PreciseEventCount = precise.Length, MatchedStalls = matches.Count, StallCount = stalls.Length,
            EventRatePerMinute = DiagnosticComparison.RatePerMinute(events.Length, duration),
            BackgroundEventsPerMinute = DiagnosticComparison.RatePerMinute(backgroundCount, backgroundSeconds),
            MatchFraction = fraction, MeanOffsetMs = offset,
            NotificationCoincidentStalls = deliveryOffsets.Count,
            NotificationMeanOffsetMs = deliveryOffsets.Count == 0 ? null : deliveryOffsets.Average(),
            OffsetStandardDeviationMs = offset is { } mean ? Math.Sqrt(matches.Average(m => Math.Pow(m.OffsetMs - mean, 2))) : null,
            ExpectedChanceMatches = expected, ChanceMatchProbability = chance, AdjustedChanceProbability = probability,
            PhaseShiftProbability = phaseProbability, IndependentTrialProbability = independentProbability,
            Confidence = confidence, Matches = matches,
            Assessment = confidence.EndsWith(" association", StringComparison.Ordinal) ? "Repeated timing alignment was observed. Phase-shift/background checks limit confidence; a controlled retest is required to distinguish a shared mechanism from coincident periodic sources." :
                "Timing evidence is insufficient or weak; no culprit is identified.", Limitations = reasons
        };
    }

    private readonly record struct TimedEvent(InvestigationEvent Evidence)
    {
        public double Start => Evidence.TimestampSeconds;
        public bool Aggregated { get; } = Evidence.Details.TryGetValue("count", out var count) &&
            int.TryParse(count, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 1;
        public double End => Start + (Aggregated ? 0 : (Evidence.DurationMs ?? 0) / 1000);
    }

    private static double PhaseShiftProbability(TimedEvent[] events, InvestigationSpikeCluster[] stalls,
        double duration, int observedMatches)
    {
        if (duration <= 0) return 1;
        const int shifts = 128;
        var exceedances = 0;
        var active = new PriorityQueue<double, double>();
        for (var phase = 0; phase < shifts; phase++)
        {
            // Restrict comparison to overlap rather than wrapping a non-integer number of periods.
            // Wrapping would break half the recurring sequence and spuriously lower the null probability.
            var shift = ((phase + .5) / shifts - .5) * duration;
            var lower = Math.Max(0, shift); var upper = Math.Min(duration, duration + shift);
            // Clusters are non-overlapping and ordered, so the trial uses a contiguous range.
            var firstStall = 0;
            while (firstStall < stalls.Length && stalls[firstStall].StartSeconds < lower) firstStall++;
            var lastStall = stalls.Length;
            while (lastStall > firstStall && stalls[lastStall - 1].EndSeconds > upper) lastStall--;
            var trialCount = lastStall - firstStall;
            if (trialCount < 4) { exceedances++; continue; }
            var threshold = Math.Max(1, (int)Math.Ceiling(observedMatches * trialCount / (double)stalls.Length) - (trialCount >= 8 ? 1 : 0));
            var index = 0;
            while (index < events.Length && events[index].Start + shift < lower) index++;
            active.Clear();
            var count = 0;
            for (var i = firstStall; i < lastStall && count < threshold; i++)
            {
                var stall = stalls[i];
                var latestStart = Math.Min(upper, stall.EndSeconds + FollowingWindowSeconds);
                while (index < events.Length && events[index].Start + shift <= latestStart)
                {
                    var e = events[index++];
                    var finish = (e.Start + shift) + (e.End - e.Start);
                    active.Enqueue(finish, finish);
                }
                while (active.TryPeek(out var finish, out _) && finish < stall.StartSeconds - PrecedingWindowSeconds) active.Dequeue();
                if (active.TryDequeue(out _, out _)) count++;
            }
            if (count >= threshold) exceedances++;
        }
        return (exceedances + 1d) / (shifts + 1d);
    }

    private static List<(double Start, double End)> UnionSorted(IEnumerable<(double Start, double End)> intervals)
    {
        var result = new List<(double Start, double End)>();
        foreach (var interval in intervals)
        {
            if (interval.End <= interval.Start) continue;
            if (result.Count > 0 && interval.Start <= result[^1].End + .000001)
                result[^1] = (result[^1].Start, Math.Max(result[^1].End, interval.End));
            else result.Add(interval);
        }
        return result;
    }

    private static double IntersectionSeconds(List<(double Start, double End)> a, List<(double Start, double End)> b)
    {
        var i = 0; var j = 0; var seconds = 0d;
        while (i < a.Count && j < b.Count)
        {
            seconds += Math.Max(0, Math.Min(a[i].End, b[j].End) - Math.Max(a[i].Start, b[j].Start));
            if (a[i].End < b[j].End) i++; else j++;
        }
        return seconds;
    }

    // Log-space binomial tail prevents underflow/overflow for large bounded cluster counts.
    private static double BinomialTail(int n, int k, double p)
    {
        if (k <= 0 || p >= 1) return 1;
        if (p <= 0) return 0;
        var log = 0d;
        for (var i = 1; i <= k; i++) log += Math.Log(n - i + 1d) - Math.Log(i);
        log += k * Math.Log(p) + (n - k) * Math.Log(1 - p);
        var maxLog = log;
        var sum = 1d;
        for (var i = k; i < n; i++)
        {
            log += Math.Log(n - i) - Math.Log(i + 1d) + Math.Log(p) - Math.Log(1 - p);
            if (log > maxLog) { sum = sum * Math.Exp(maxLog - log) + 1; maxLog = log; }
            else sum += Math.Exp(log - maxLog);
        }
        return Math.Clamp(Math.Exp(maxLog + Math.Log(sum)), 0, 1);
    }
}
