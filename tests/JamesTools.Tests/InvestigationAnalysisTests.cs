using JamesTools.Core.Diagnostics;

internal static class InvestigationAnalysisTests
{
    private const double Duration = 75;
    private static readonly double[] Onsets = Enumerable.Range(0, 18).Select(i => 2 + i * 4.01).ToArray();
    private static readonly InvestigationCollectorStatus[] Collectors = [new("USB", "available", "Fixture"), new("CPU", "available", "Fixture"), new("Audio", "available", "Fixture")];

    public static Task Run()
    {
        RepeatedUsbAndDominantStream();
        UnrelatedAndDenseBackground();
        IndependentCpuPeriodicity();
        UnexplainedAndCompetingEvidence();
        TimingAndMissingEvidence();
        LossAndOneToOneMatching();
        ComparisonAndPortableRoundtrip();
        BoundariesAndEmptyEvidence();
        IndependentPeriodicPhaseSweep();
        return Task.CompletedTask;
    }

    private static void RepeatedUsbAndDominantStream()
    {
        var frames = Frames(Onsets);
        var mixed = frames.Concat(Enumerable.Range(0, 100).Select(i => Frame(i * .5, 450, "overlay"))).ToArray();
        var r = Analyze(mixed, Usb());
        Assert(r.DominantStream == "game", "mixed swapchains were not separated");
        Assert(r.Spikes.Count == 18 && r.Metrics.SlowFrameCount == 18 && r.Clusters.Count == 18, "18 independent spikes were not retained");
        Assert(r.Periodicity.IsPeriodic && r.Periodicity.Occurrences == 18, "automatic recurrence was not detected");
        Near(r.Periodicity.MeanIntervalSeconds, 4.01, "recurrence mean");
        Near(r.Periodicity.IntervalVarianceSecondsSquared, 0, "recurrence variance");
        var c = r.Correlations.Single();
        Assert(c.MatchedStalls == 18 && c.Confidence == "moderate association", $"periodic coincidence must retain descriptive association without independent-trial strong confidence: {c.Confidence}, phase probability {c.PhaseShiftProbability}, adjusted {c.AdjustedChanceProbability}");
        Near(c.MeanOffsetMs, -40, "preceding offset");
        Assert(c.ExpectedChanceMatches < 2 && c.AdjustedChanceProbability > .01 && c.PhaseShiftProbability > c.IndependentTrialProbability,
            "phase dependence must prevent vanishing chance probabilities from repeated periodic coincidences");
        Assert(r.Coverage.ReliableForAssociation && r.Coverage.OtherStreamFrames == 100, "stream coverage incorrect");
        Near(r.Metrics.MaximumStallMs, 80, "maximum stall");
        Assert(r.Metrics.AverageFps > r.Metrics.OnePercentLowFps && r.Metrics.OnePercentLowFps >= r.Metrics.PointOnePercentLowFps, "FPS lows are incorrect");
        Assert(r.Metrics.Distribution.Values.Sum() == r.Metrics.FrameCount, "distribution lost samples");
        var app = Frames([]).Take(80).Select(f => f with { Stream = "application", FrameType = "Application" });
        Assert(Analyze(mixed.Concat(app).ToArray(), []).DominantStream == "application", "application frames must take priority over overlay/generated frames");
        var clustered = Frames(Onsets, paired: true);
        var pair = Analyze(clustered, Usb());
        Assert(pair.Spikes.Count == 36 && pair.Clusters.Count == 18 && pair.Correlations.Single().MatchedStalls == 18,
            "adjacent slow frames inflated event association counts");
    }

    private static void UnrelatedAndDenseBackground()
    {
        var state = 47u;
        var random = Enumerable.Range(0, 18).Select(_ =>
        {
            state = unchecked(state * 1664525 + 1013904223);
            return Event((state / (double)uint.MaxValue) * Duration);
        }).ToArray();
        var r = Analyze(Frames(Onsets), random);
        Assert(r.Correlations.All(c => !Associated(c)), "random unrelated USB was assigned association confidence");
        var dense = Enumerable.Range(0, 7500).Select(i => Event(i * .01)).ToArray();
        var d = Analyze(Frames(Onsets), dense).Correlations.Single();
        Assert(d.MatchedStalls == 18 && d.ChanceMatchProbability > .9 && !Associated(d), "dense background coincidences were treated as meaningful");
        var single = Analyze(Frames(Onsets), [Event(Onsets[0] - .04)]).Correlations.Single();
        Assert(single.MatchedStalls == 1 && !Associated(single), "one coincidence became a culprit");
    }

    private static void IndependentCpuPeriodicity()
    {
        var cpu = Onsets.Select(t => Event(t, "CPU", "scheduler", "activity") with { ProcessId = 99, ThreadId = 7 }).ToArray();
        var hardware = new[] { new DiagnosticHardwareSample(0, Duration, 0, 0, null, null, new Dictionary<string, double?> { ["cpuPercent"] = 99 }) };
        var r = InvestigationAnalysis.Analyze(Frames([]), cpu, [], hardware, Collectors, Duration);
        Assert(r.Spikes.Count == 0 && r.Correlations.Single().MatchedStalls == 0, "CPU activity manufactured a frame stall");
        Assert(r.EventPeriodicity.Single().Periodicity.IsPeriodic, "independent periodic activity was not detected");
        Assert(r.Correlations.All(c => !Associated(c)) && r.Findings.Any(f => f.Contains("without matching frame spikes")), "periodic CPU activity became a culprit");
    }

    private static void UnexplainedAndCompetingEvidence()
    {
        var unknown = Analyze(Frames(Onsets), []);
        Assert(unknown.Findings.Any(f => f.Contains("unexplained")), "unexplained spikes were not reported");
        var competitors = Usb().Concat(Onsets.Select(t => Event(t - .02, "Audio", "audio", "reset") with { DeviceId = "device-2" })).ToArray();
        var r = Analyze(Frames(Onsets), competitors);
        Assert(r.Correlations.Count == 2 && r.Correlations.All(c => c.Confidence == "moderate association" && c.CompetingGroups.Count == 1),
            "competing evidence was collapsed into a unique culprit");
        var separated = Usb().Concat(Onsets.Select(t => Event(t - .02) with { ProcessId = 9, ThreadId = 10, DeviceId = "device-2" })).ToArray();
        Assert(Analyze(Frames(Onsets), separated).Correlations.Count == 2, "device/process/thread grouping collapsed distinct evidence");
    }

    private static void TimingAndMissingEvidence()
    {
        var reversed = Usb().Reverse().Select(e => e with { ReceiptSeconds = e.TimestampSeconds + 2 }).ToArray();
        var r = Analyze(Frames(Onsets).Reverse().ToArray(), reversed);
        Assert(r.Coverage.DelayedEvents == 18 && r.Coverage.OutOfOrderEvents == 17, "delivery diagnostics were lost");
        Assert(r.Correlations.Single().MatchedStalls == 18 && Associated(r.Correlations.Single()), "precise original timestamps were replaced by delivery order/time");
        var approximate = Analyze(Frames(Onsets), Usb().Select(e => e with { TimestampBasis = "callback receipt", UncertaintyMs = 1000 }).ToArray());
        Assert(approximate.Correlations.Single().MatchedStalls == 0 && approximate.Coverage.ImpreciseEvents == 18, "receipt-time evidence became precise");
        Assert(approximate.Correlations.Single().NotificationCoincidentStalls == 18 && !Associated(approximate.Correlations.Single()),
            "Notification-delivery coincidences should remain visible without precise/cause confidence");
        var receiptFrames = Analyze(Frames(Onsets).Select(f => f with { TimestampSource = "CSV receipt (relative)" }).ToArray(), Usb());
        Assert(!receiptFrames.Coverage.ReliableForAssociation && !Associated(receiptFrames.Correlations.Single()), "imprecise frames received association confidence");
        var missing = Analyze(Frames(Onsets).Where(f => f.OffsetSeconds < 25 || f.OffsetSeconds > 35).ToArray(), Usb());
        Assert(missing.Coverage.FrameGapCount > 0 && !missing.Coverage.ReliableForAssociation && !Associated(missing.Correlations.Single()), "missing frames did not limit confidence");
        var noSpikes = Analyze(Frames([]).Where(f => f.OffsetSeconds < 25 || f.OffsetSeconds > 35).ToArray(), Usb());
        Assert(noSpikes.Spikes.Count == 0, "missing delivery was invented as a long frame");
    }

    private static void LossAndOneToOneMatching()
    {
        var lost = Collectors.Select(c => c.Source == "USB" ? c with { EventsLost = 400, EventsDropped = 10 } : c).ToArray();
        var r = InvestigationAnalysis.Analyze(Frames(Onsets), Usb(), [], [], lost, Duration);
        Assert(!Associated(r.Correlations.Single()) && r.Coverage.CollectorEventsLost == 400 && r.Coverage.CollectorEventsDropped == 10, "selective event loss permitted a false positive");
        var none = InvestigationAnalysis.Analyze(Frames(Onsets), Usb(), [], [], [], Duration);
        Assert(!Associated(none.Correlations.Single()), "missing collector coverage permitted a false positive");
        var longEvent = Analyze(Frames(Onsets), [Event(0) with { DurationMs = 75000 }]);
        Assert(longEvent.Correlations.Single().MatchedStalls == 1, "one long event was reused for many stalls");
        var aggregated = Analyze(Frames(Onsets), Usb().Select(e => e with { DurationMs = 2000,
            Details = new Dictionary<string, string> { ["count"] = "100", ["lastSeconds"] = (e.TimestampSeconds + .5).ToString(System.Globalization.CultureInfo.InvariantCulture) } }).ToArray());
        Assert(!Associated(aggregated.Correlations.Single()) && aggregated.Correlations.Single().MatchedStalls == 18,
            "aggregated durations manufactured precise occurrence confidence");
        var multiEvents = Usb().SelectMany(e => new[] { e, e with { TimestampSeconds = e.TimestampSeconds + .001 } }).ToArray();
        Assert(Analyze(Frames(Onsets), multiEvents).Correlations.Single().MatchedStalls == 18, "multiple events inflated the stall match count");
    }

    private static void ComparisonAndPortableRoundtrip()
    {
        var hardware = new DiagnosticHardwareSample(1, 2, .5, .75, 1.25, 1.5, new Dictionary<string, double?> { ["cpuPercent"] = 80, ["gpuPercent"] = 90 });
        var before = Analyze(Frames(Onsets), Usb()) with { Hardware = [hardware], Conditions = new RecordingConditions { Scene = "Fixed replay" } };
        var after = Analyze(Frames([]), []);
        var comparison = InvestigationComparison.Compare(before, after, "Moved USB port");
        Assert(comparison.Conclusion.Contains("lower") && comparison.MetricDeltas.Single(m => m.Metric == "Stall clusters per minute").Delta < 0, "improved retest was not compared");
        Assert(comparison.EventRateChanges.Single().DeltaEventsPerMinute < 0 && comparison.CorrelationChanges.Single().AfterConfidence == "unobserved", "event/correlation changes were lost");
        var mismatch = InvestigationComparison.Compare(before, after with { ThresholdMs = 50 }, "Threshold change");
        Assert(mismatch.MetricDeltas.Single(m => m.Metric == "Stall clusters per minute").Delta is null, "incompatible thresholds yielded rate deltas");
        var zero = InvestigationComparison.Compare(after, after, "Same capture");
        Assert(zero.MetricDeltas.Single(m => m.Metric == "Slow frames").PercentChange is null, "zero baseline produced infinity");
        var json = InvestigationReportWriter.ToJson(before);
        var read = InvestigationReportWriter.FromJson(json);
        Assert(InvestigationReportWriter.ToJson(read) == json && read.Spikes.Count == 18 && read.Correlations.Single().Matches.Count == 18, "public report JSON did not roundtrip");
        Assert(read.Hardware.Single().CounterWindowStartSeconds == .5 && read.Hardware.Single().GpuCollectionEndSeconds == 1.5 && read.Conditions.Scene == "Fixed replay", "hardware windows or declared conditions were lost");
        var privateReport = before with
        {
            Events = [Event(1, "=SUM(1,2)") with { ThreadId = 3, Summary = @"C:\Users\Alice\secret.txt alice@example.com", RawEvidenceReference = @"C:\secret.bin", Details = new Dictionary<string, string> { ["private"] = "raw serial id", ["location"] = "driver.sys+0x123", ["instanceId"] = @"USB\VID_1234\SERIAL", ["module"] = "driver.sys" } }],
            Collectors = [new("USB", "stopped", "Bounded ETW capture; module offsets only") { CollectionMilliseconds = 12, EventsLost = 4, EventsDropped = 5 }],
            Devices = [new InvestigationDevice { Id = "device-1", Name = @"C:\Users\Alice\private", Topology = @"USB\VID_1234\SERIAL" }]
        };
        var portable = InvestigationReportWriter.ToJson(privateReport);
        Assert(!portable.Contains("Alice") && !portable.Contains("example.com") && !portable.Contains("secret.bin") && !portable.Contains("raw serial id"), "portable export leaked private free text");
        var safe = InvestigationReportWriter.FromJson(portable);
        Assert(safe.Events.Single().Details["location"] == "driver.sys+0x123" && safe.Events.Single().Details["module"] == "driver.sys" &&
            safe.Collectors.Single().Detail.Contains("module offsets"), "portable export erased sanitized module or collector evidence");
        var text = InvestigationReportWriter.ToText(privateReport);
        Assert(text.Contains("driver.sys+0x123") && text.Contains("thread 3") && text.Contains("lost 4") && text.Contains("12.000 ms") && text.Contains("Events by category:") && text.Contains("Device inventory:"), "readable report omitted integration evidence");
        Assert(InvestigationReportWriter.EventsCsv(privateReport).Contains("'=SUM(1,2)"), "CSV formula was not neutralized");
        Assert(InvestigationReportWriter.ToText(before).Contains("never causal confidence"), "readable report omitted confidence limits");
    }

    private static void BoundariesAndEmptyEvidence()
    {
        var empty = InvestigationAnalysis.Analyze([], [], [], [], [], 0);
        Assert(empty.Metrics.AverageFps is null && empty.Metrics.ClustersPerMinute is null && empty.Periodicity.Occurrences == 0, "empty evidence was represented as measured performance");
        Assert(InvestigationReportWriter.FromJson(InvestigationReportWriter.ToJson(empty)).Events.Count == 0, "empty report JSON failed");
        var invalid = new[] { Frame(-1, 10), Frame(1, double.NaN), Frame(Duration - .01, 80), Frame(1, 33.333), Frame(1, 33.333) };
        var r = Analyze(invalid, [Event(double.NaN), Event(-1), Event(Duration + 1)]);
        Assert(r.Coverage.RejectedFrames == 3 && r.Coverage.DuplicateFrames == 1 && r.Spikes.Count == 0 && r.Coverage.RejectedEvents == 3,
            "invalid, duplicate, boundary or exact-threshold evidence was mishandled");
        var between = Frame(2.08, 80) with { FrameTimeSource = "MsBetweenPresents" };
        Near(Analyze([between], [Event(1.96)]).Correlations.Single().Matches.Single().OffsetMs, -40, "between-presents interval alignment");
        var many = Enumerable.Range(0, InvestigationAnalysis.MaximumRetainedEvents + 1).Select(i => Event(i / 1000d)).ToArray();
        var bounded = Analyze([], many);
        Assert(bounded.Events.Count == InvestigationAnalysis.MaximumRetainedEvents && bounded.Coverage.EventsOmittedByLimit == 1 &&
            !bounded.Coverage.ReliableForAssociation, "retention omissions were not explicit");
    }

    private static void IndependentPeriodicPhaseSweep()
    {
        var frames = Frames(Onsets);
        for (var phase = 0d; phase <= 4; phase += .1)
        {
            var result = Analyze(frames, Usb().Select(e => e with { TimestampSeconds = e.TimestampSeconds + phase, ReceiptSeconds = e.ReceiptSeconds + phase }).ToArray());
            Assert(result.Correlations.Single().Confidence != "strong association", "Independent equal-period timers received strong statistical confidence from one accidental phase alignment");
        }
    }

    private static InvestigationReport Analyze(IReadOnlyList<FrameTelemetry> frames, IReadOnlyList<InvestigationEvent> events) =>
        InvestigationAnalysis.Analyze(frames, events, [new InvestigationDevice { Id = "device-1", Name = "USB fixture" }], [], Collectors, Duration);
    private static InvestigationEvent[] Usb() => Onsets.Select(t => Event(t - .04)).ToArray();
    private static InvestigationEvent Event(double t, string source = "USB", string category = "device", string type = "reset") =>
        new() { TimestampSeconds = t, ReceiptSeconds = t + .001, Source = source, Category = category, EventType = type, DeviceId = "device-1", TimestampBasis = "QPC", UncertaintyMs = .01 };
    private static FrameTelemetry Frame(double t, double ms, string stream = "game") =>
        new(t, t + .001, "QPC", stream, ms, null, null, null, null, null, null, null, null, null, "Independent Flip", null);

    private static FrameTelemetry[] Frames(double[] onsets, bool paired = false)
    {
        var result = new List<FrameTelemetry>();
        var t = 0d;
        foreach (var start in onsets.Append(Duration))
        {
            while (t < start - .0000001)
            {
                var step = Math.Min(.01, start - t);
                result.Add(Frame(t, step * 1000)); t += step;
            }
            if (start >= Duration) break;
            result.Add(Frame(start, paired ? 60 : 80));
            if (paired) result.Add(Frame(start + .06, 60));
            t = start + (paired ? .12 : .08);
        }
        return result.ToArray();
    }

    private static bool Associated(InvestigationCorrelation c) => c.Confidence.EndsWith(" association", StringComparison.Ordinal);
    private static void Near(double? value, double expected, string message)
    {
        if (value is null || Math.Abs(value.Value - expected) > .000001) throw new Exception($"{message}: expected {expected}, got {value}");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
