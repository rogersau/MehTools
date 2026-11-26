using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JamesTools.Core.Diagnostics;

/// <summary>Portable analysis of retained evidence, never a diagnosis of causation.</summary>
public sealed record InvestigationReport
{
    public int FormatVersion { get; init; } = 1;
    public double DurationSeconds { get; init; }
    public double WarmupSeconds { get; init; }
    public double ThresholdMs { get; init; } = 33.333;
    public string? DominantStream { get; init; }
    public InvestigationMetrics Metrics { get; init; } = new();
    public InvestigationCoverage Coverage { get; init; } = new();
    public IReadOnlyList<InvestigationEvent> Events { get; init; } = [];
    public IReadOnlyList<InvestigationDevice> Devices { get; init; } = [];
    public IReadOnlyList<DiagnosticHardwareSample> Hardware { get; init; } = [];
    public RecordingConditions Conditions { get; init; } = new();
    /// <summary>Slow frames in the selected stream, ordered by event time, bounded by MaximumRetainedSpikes.</summary>
    public IReadOnlyList<FrameTelemetry> Spikes { get; init; } = [];
    public IReadOnlyList<InvestigationSpikeCluster> Clusters { get; init; } = [];
    public InvestigationPeriodicity Periodicity { get; init; } = new();
    public IReadOnlyList<InvestigationEventPeriodicity> EventPeriodicity { get; init; } = [];
    public IReadOnlyList<InvestigationCorrelation> Correlations { get; init; } = [];
    public IReadOnlyList<InvestigationCollectorStatus> Collectors { get; init; } = [];
    public IReadOnlyList<string> Findings { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

public sealed record InvestigationMetrics
{
    public long FrameCount { get; init; }
    public double? AverageFps { get; init; }
    /// <summary>1000 / mean of the slowest ceil(N * .01) intervals; not inverse P99.</summary>
    public double? OnePercentLowFps { get; init; }
    public double? PointOnePercentLowFps { get; init; }
    public double? MeanFrameMs { get; init; }
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public double? P999Ms { get; init; }
    public double? MaximumStallMs { get; init; }
    public double MeasuredFrameSeconds { get; init; }
    public long SlowFrameCount { get; init; }
    public long ClusterCount { get; init; }
    public double? SpikesPerMinute { get; init; }
    public double? ClustersPerMinute { get; init; }
    public double? EventsPerMinute { get; init; }
    public IReadOnlyDictionary<string, long> Distribution { get; init; } = new Dictionary<string, long>();
}

public sealed record InvestigationCoverage
{
    public int InputFrames { get; init; }
    public int SelectedFrames { get; init; }
    public int OtherStreamFrames { get; init; }
    public int RejectedFrames { get; init; }
    public int DuplicateFrames { get; init; }
    public int FramesOmittedByLimit { get; init; }
    public int InputEvents { get; init; }
    public int RejectedEvents { get; init; }
    public int EventsOmittedByLimit { get; init; }
    public int SpikesOmittedByLimit { get; init; }
    public int ClustersOmittedByLimit { get; init; }
    public int GroupsOmittedByLimit { get; init; }
    public int FrameGapCount { get; init; }
    public double ObservedFrameSeconds { get; init; }
    public double FrameCoverageFraction { get; init; }
    public int ImpreciseFrames { get; init; }
    public int ImpreciseEvents { get; init; }
    public int DelayedEvents { get; init; }
    public int OutOfOrderEvents { get; init; }
    public int HardwareSamples { get; init; }
    public long CollectorEventsLost { get; init; }
    public long CollectorEventsDropped { get; init; }
    public bool ReliableForAssociation { get; init; }
}

public sealed record InvestigationSpikeCluster(double StartSeconds, double EndSeconds, int FrameCount,
    FrameTelemetry Peak, FrameFinding Diagnosis);

public sealed record InvestigationPeriodicity
{
    public int Occurrences { get; init; }
    public int IntervalCount { get; init; }
    public double? MeanIntervalSeconds { get; init; }
    /// <summary>Population variance of consecutive onset intervals, in seconds squared.</summary>
    public double? IntervalVarianceSecondsSquared { get; init; }
    public double? IntervalStandardDeviationSeconds { get; init; }
    public double? CoefficientOfVariation { get; init; }
    public bool IsPeriodic { get; init; }
    public string Assessment { get; init; } = "Insufficient occurrences (at least four required).";
}

public sealed record InvestigationEventGroup(string Source, string Category, string EventType,
    string? DeviceId, int? ProcessId, int? ThreadId)
{
    /// <summary>Unambiguous, stable tuple key; process/thread identities are scoped to this recording.</summary>
    public string Key => JsonSerializer.Serialize(new object?[] { Source, Category, EventType, DeviceId, ProcessId, ThreadId });
}

public sealed record InvestigationEventPeriodicity(InvestigationEventGroup Group, InvestigationPeriodicity Periodicity);
public sealed record InvestigationEventMatch(double StallStartSeconds, double StallEndSeconds, double EventSeconds,
    double OffsetMs, double ReceiptDelayMs);

public sealed record InvestigationCorrelation
{
    public InvestigationEventGroup Group { get; init; } = new("", "", "", null, null, null);
    public int EventCount { get; init; }
    public int PreciseEventCount { get; init; }
    public int MatchedStalls { get; init; }
    public int NotificationCoincidentStalls { get; init; }
    public double? NotificationMeanOffsetMs { get; init; }
    public int StallCount { get; init; }
    public double? EventRatePerMinute { get; init; }
    public double? BackgroundEventsPerMinute { get; init; }
    public double? MatchFraction { get; init; }
    public double? MeanOffsetMs { get; init; }
    public double? OffsetStandardDeviationMs { get; init; }
    public double ExpectedChanceMatches { get; init; }
    public double ChanceMatchProbability { get; init; }
    public double AdjustedChanceProbability { get; init; } = 1;
    public double PhaseShiftProbability { get; init; } = 1;
    public double IndependentTrialProbability { get; init; } = 1;
    /// <summary>insufficient, weak, moderate association, or strong association; never causal confidence.</summary>
    public string Confidence { get; init; } = "insufficient";
    public string Assessment { get; init; } = "";
    public IReadOnlyList<string> CompetingGroups { get; init; } = [];
    public IReadOnlyList<InvestigationEventMatch> Matches { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Portable exports retain bounded, sanitized evidence. IDs must already be pseudonymized by collectors.</summary>
public static class InvestigationReportWriter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly Regex Sensitive = new(@"[A-Za-z]:[\\/]|\\\\|/(?:home|Users|root|tmp)/|\b[^\s@]+@[^\s@]+\.[^\s@]+|\b(?:serial(?:number)?|password|token|secret)\s*[:=]|(?:USB|HID|PCI|ACPI|SWD|BTHENUM|HDAUDIO|ROOT|DISPLAY|SCSI|STORAGE|USBSTOR)[\\#]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SensitiveKey = new(@"serial|password|token|secret|instance|(?:raw|user|file|device)?path|email|username|private", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string ToJson(InvestigationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var portable = report with
        {
            Events = report.Events.Select(e => e with { Summary = SafeText(e.Summary), RawEvidenceReference = null,
                Details = e.Details.OrderBy(p => p.Key, StringComparer.Ordinal).Take(32)
                    .GroupBy(p => SafeText(p.Key), StringComparer.Ordinal).ToDictionary(g => g.Key,
                        g => SensitiveKey.IsMatch(g.First().Key) ? "[redacted]" : SafeText(g.First().Value), StringComparer.Ordinal) }).ToArray(),
            Collectors = report.Collectors.Select(c => c with { Detail = SafeText(c.Detail) }).ToArray()
        };
        var root = JsonSerializer.SerializeToNode(portable, Options)!;
        Scrub(root);
        return root.ToJsonString(Options);
    }

    public static InvestigationReport FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var report = JsonSerializer.Deserialize<InvestigationReport>(json, Options) ?? throw new JsonException("Investigation report is null.");
        if (report.FormatVersion != 1) throw new JsonException("Unsupported investigation report version.");
        return report;
    }

    private static void Scrub(JsonNode node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var s)) obj[key] = SafeText(s);
                else if (obj[key] is { } child) Scrub(child);
                if (Sensitive.IsMatch(key))
                {
                    var safeKey = "[redacted]";
                    for (var i = 1; obj.ContainsKey(safeKey); i++) safeKey = "[redacted:" + i + "]";
                    var detached = obj[key]; obj.Remove(key); obj[safeKey] = detached;
                }
            }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is JsonValue value && value.TryGetValue<string>(out var s)) array[i] = SafeText(s);
                else if (array[i] is { } child) Scrub(child);
            }
    }

    internal static string SafeText(string? value)
    {
        if (value is null) return "";
        if (Sensitive.IsMatch(value)) return "[redacted]";
        return new string(value.Where(c => !char.IsControl(c)).Take(2000).ToArray());
    }

    public static string ToText(InvestigationReport report)
    {
        var r = FromJson(ToJson(report));
        static string F(double? n) => n?.ToString("F3", CultureInfo.InvariantCulture) ?? "Unavailable";
        var t = new StringBuilder("Stutter Investigator\n");
        t.AppendLine($"Duration {F(r.DurationSeconds)} s; warm-up {F(r.WarmupSeconds)} s; stream {r.DominantStream ?? "Unavailable"}; threshold >{F(r.ThresholdMs)} ms.");
        t.AppendLine($"Average FPS {F(r.Metrics.AverageFps)}; 1% low {F(r.Metrics.OnePercentLowFps)}; 0.1% low {F(r.Metrics.PointOnePercentLowFps)}; maximum stall {F(r.Metrics.MaximumStallMs)} ms.");
        t.AppendLine($"Frames {r.Metrics.FrameCount}; slow frames {r.Metrics.SlowFrameCount}; clusters {r.Metrics.ClusterCount}; observed coverage {F(100 * r.Coverage.FrameCoverageFraction)}%.");
        t.AppendLine("Frame distribution: " + string.Join("; ", r.Metrics.Distribution.Select(p => p.Key + "=" + p.Value)));
        t.AppendLine("Device inventory:");
        foreach (var d in r.Devices)
            t.AppendLine($"  {d.Id}: {d.Name}; class {d.Class ?? "unknown"}; manufacturer {d.Manufacturer ?? "unknown"}; VID/PID {d.Vid}/{d.Pid}; driver {d.DriverName} {d.DriverVersion}; state {d.ConnectionState}; controller {d.ControllerId}; parent {d.ParentId}; hub {d.ParentHubId}; topology {d.Topology}; power {d.PowerManagement}; identity {d.IdentityQuality}.");
        t.AppendLine("Events by category: " + string.Join("; ", r.Events.GroupBy(e => e.Category).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "=" + g.Count())));
        t.AppendLine("Collector coverage and overhead:");
        foreach (var c in r.Collectors)
            t.AppendLine($"  {c.Source}: {c.Availability}; lost {c.EventsLost}; dropped {c.EventsDropped}; collection overhead {F(c.CollectionMilliseconds)} ms. {c.Detail}");
        t.AppendLine($"Hardware: {r.Hardware.Count} retained samples with original collection/counter windows; hardware readings are not per-frame measurements.");
        t.AppendLine("Conditions: " + JsonSerializer.Serialize(r.Conditions));
        t.AppendLine("Thread and module/offset evidence (at most 200 retained observations):");
        foreach (var e in r.Events.Where(e => e.ThreadId is not null || e.Details.ContainsKey("location") || e.Details.ContainsKey("module")).Take(200))
            t.AppendLine($"  {F(e.TimestampSeconds)} s; {e.Source}/{e.Category}/{e.EventType}; process {e.ProcessId}; thread {e.ThreadId}; {e.Summary}; " + string.Join("; ", e.Details.Select(p => p.Key + "=" + p.Value)));
        t.AppendLine($"Spike periodicity: {r.Periodicity.Assessment} Occurrences {r.Periodicity.Occurrences}; mean interval {F(r.Periodicity.MeanIntervalSeconds)} s; variance {F(r.Periodicity.IntervalVarianceSecondsSquared)} s².");
        foreach (var finding in r.Findings) t.AppendLine("Finding: " + finding);
        foreach (var c in r.Correlations)
        {
            t.AppendLine($"Evidence {c.Group.Key}: {c.MatchedStalls}/{c.StallCount} precise matches; {c.Confidence}; expected chance matches {F(c.ExpectedChanceMatches)}; adjusted chance probability {F(c.AdjustedChanceProbability)}; phase-shift probability {F(c.PhaseShiftProbability)}; mean offset {F(c.MeanOffsetMs)} ms. {c.Assessment}");
            if (c.NotificationCoincidentStalls > 0) t.AppendLine($"  Notification deliveries near {c.NotificationCoincidentStalls}/{c.StallCount} stalls; mean delivery offset {F(c.NotificationMeanOffsetMs)} ms. Physical occurrence time and upstream latency are unknown; these are not precise matches.");
            foreach (var limitation in c.Limitations) t.AppendLine("  Evidence limitation: " + limitation);
        }
        foreach (var e in r.EventPeriodicity.Where(e => e.Periodicity.IsPeriodic))
            t.AppendLine($"Periodic events {e.Group.Key}: {e.Periodicity.Occurrences} occurrences; mean interval {F(e.Periodicity.MeanIntervalSeconds)} s; {e.Periodicity.Assessment}");
        foreach (var limitation in r.Limitations) t.AppendLine("Limitation: " + limitation);
        return t.ToString();
    }

    public static string EventsCsv(InvestigationReport report)
    {
        var r = FromJson(ToJson(report));
        var t = new StringBuilder("timestamp_s,receipt_s,source,category,event_type,device_id,process_id,thread_id,duration_ms,timestamp_basis,uncertainty_ms,quality,summary,details_json\n");
        static string Cell(object? v)
        {
            var s = v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : v?.ToString() ?? "";
            if (s.TrimStart().StartsWith('=') || s.TrimStart().StartsWith('+') || s.TrimStart().StartsWith('-') || s.TrimStart().StartsWith('@')) s = "'" + s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
        foreach (var e in r.Events)
            t.AppendLine(string.Join(",", new object?[] { e.TimestampSeconds, e.ReceiptSeconds, e.Source, e.Category, e.EventType,
                e.DeviceId, e.ProcessId, e.ThreadId, e.DurationMs, e.TimestampBasis, e.UncertaintyMs, e.Quality, e.Summary, JsonSerializer.Serialize(e.Details) }.Select(Cell)));
        return t.ToString();
    }
}
