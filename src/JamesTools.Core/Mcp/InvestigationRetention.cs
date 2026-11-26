using System.Text.Json;
using JamesTools.Core.Diagnostics;

namespace JamesTools.Core.Mcp;

/// <summary>Portable report budget inside the existing 300 KiB session envelope.</summary>
public static class InvestigationRetention
{
    public const int MaximumBytes = 220 * 1024;
    public const string Notice = "MCP report retention: 220 KiB within the 300 KiB session. Raw exports/ETL are disabled. Metrics and correlation statistics precede detail reduction; pages address retained data only. Hardware, devices, matches, explanations and evidence may be omitted.";
    static string S(string? s) => TelemetryData.Text(s, 256);
    static InvestigationEventGroup Group(InvestigationEventGroup g) => g with
    { Source = S(g.Source), Category = S(g.Category), EventType = S(g.EventType), DeviceId = g.DeviceId is null ? null : S(g.DeviceId) };
    public static InvestigationReport Bound(InvestigationReport source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Slice before portable serialization. This also bounds malicious or broken injected collectors.
        var r = source with
        {
            Events = source.Events.Take(512).Select(e => e with { Summary = S(e.Summary), Source = S(e.Source), Category = S(e.Category),
                EventType = S(e.EventType), DeviceId = e.DeviceId is null ? null : S(e.DeviceId), RawEvidenceReference = null,
                Details = e.Details.Take(8).GroupBy(p => S(p.Key), StringComparer.Ordinal).ToDictionary(g => g.Key, g => S(g.First().Value)) }).ToArray(),
            Spikes = source.Spikes.Take(256).ToArray(), Clusters = source.Clusters.Take(64).ToArray(),
            Devices = source.Devices.Take(128).ToArray(), Hardware = source.Hardware.TakeLast(16).ToArray(),
            Collectors = source.Collectors.Take(32).Select(c => c with { Source = S(c.Source), Detail = S(c.Detail) }).ToArray(),
            Correlations = source.Correlations.Take(256).Select(c => c with { Group = Group(c.Group), Assessment = S(c.Assessment),
                Matches = c.Matches.Take(4).ToArray(), Limitations = c.Limitations.Take(2).Select(S).ToArray(),
                CompetingGroups = c.CompetingGroups.Take(4).Select(S).ToArray() }).ToArray(),
            EventPeriodicity = source.EventPeriodicity.Take(256).ToArray(),
            Findings = source.Findings.Take(24).Select(S).ToArray(), Limitations = source.Limitations.Take(24).Select(S).Append(Notice).ToArray()
        };
        r = InvestigationReportWriter.FromJson(InvestigationReportWriter.ToJson(r));
        while (JsonSerializer.SerializeToUtf8Bytes(r).Length > MaximumBytes - 2048)
        {
            if (r.Events.Count > 0) r = r with { Events = r.Events.Take(r.Events.Count / 2).ToArray() };
            else if (r.Spikes.Count > 0) r = r with { Spikes = r.Spikes.Take(r.Spikes.Count / 2).ToArray() };
            else if (r.Clusters.Count > 0) r = r with { Clusters = r.Clusters.Take(r.Clusters.Count / 2).ToArray() };
            else if (r.Hardware.Count > 0) r = r with { Hardware = [] };
            else if (r.Devices.Count > 0) r = r with { Devices = r.Devices.Take(r.Devices.Count / 2).ToArray() };
            else if (r.EventPeriodicity.Count > 0) r = r with { EventPeriodicity = [] };
            else if (r.Correlations.Any(c => c.Matches.Count > 0)) r = r with { Correlations = r.Correlations.Select(c => c with { Matches = [] }).ToArray() };
            else if (r.Correlations.Any(c => c.Assessment.Length > 0 || c.Limitations.Count > 0 || c.CompetingGroups.Count > 0))
                r = r with { Correlations = r.Correlations.Select(c => c with { Assessment = "", Limitations = [], CompetingGroups = [] }).ToArray() };
            else if (r.Correlations.Count > 0) r = r with { Correlations = r.Correlations.Take(r.Correlations.Count - 1).ToArray() };
            else throw new InvalidDataException("Investigation summary exceeds its fixed metadata budget.");
        }
        var omitted = source.Events.Count - r.Events.Count + source.Spikes.Count - r.Spikes.Count + source.Clusters.Count - r.Clusters.Count + source.Correlations.Count - r.Correlations.Count;
        r = r with { Limitations = r.Limitations.Append($"MCP retained/input detail: events {r.Events.Count}/{source.Events.Count}; spikes {r.Spikes.Count}/{source.Spikes.Count}; clusters {r.Clusters.Count}/{source.Clusters.Count}; devices {r.Devices.Count}/{source.Devices.Count}; hardware {r.Hardware.Count}/{source.Hardware.Count}; correlations {r.Correlations.Count}/{source.Correlations.Count}; match examples {r.Correlations.Sum(c => c.Matches.Count)}/{source.Correlations.Sum(c => c.Matches.Count)}; event periodicity {r.EventPeriodicity.Count}/{source.EventPeriodicity.Count}. Input counts refer to the logger's bounded report, not unobserved evidence.").ToArray(), Coverage = r.Coverage with
        {
            EventsOmittedByLimit = source.Coverage.EventsOmittedByLimit + source.Events.Count - r.Events.Count,
            SpikesOmittedByLimit = source.Coverage.SpikesOmittedByLimit + source.Spikes.Count - r.Spikes.Count,
            ClustersOmittedByLimit = source.Coverage.ClustersOmittedByLimit + source.Clusters.Count - r.Clusters.Count,
            GroupsOmittedByLimit = source.Coverage.GroupsOmittedByLimit + source.Correlations.Count - r.Correlations.Count,
            ReliableForAssociation = source.Coverage.ReliableForAssociation && omitted == 0
        } };
        if (JsonSerializer.SerializeToUtf8Bytes(r).Length > MaximumBytes) throw new InvalidDataException("Investigation retention metadata exceeds its budget.");
        return r;
    }

    internal static InvestigationReport Copy(InvestigationReport r) => JsonSerializer.Deserialize<InvestigationReport>(JsonSerializer.Serialize(r))!;
    public static InvestigationReport Summary(InvestigationReport r) => r with { Events = [], Spikes = [], Clusters = [], Hardware = [], Devices = [] };

    internal static void Validate(InvestigationReport r)
    {
        static bool GroupValid(InvestigationEventGroup? g) => g is not null && g.Source is not null && g.Category is not null && g.EventType is not null;
        static bool FrameValid(FrameTelemetry? f) => f is not null && f.TimestampSource is not null && f.Stream is not null && f.FrameTimeSource is not null;
        if (r.FormatVersion != 1 || r.Metrics is null || r.Metrics.Distribution is null || r.Coverage is null || r.Conditions is null ||
            typeof(RecordingConditions).GetProperties().Any(p => p.GetValue(r.Conditions) is not string) || r.Periodicity is null || r.Periodicity.Assessment is null ||
            !double.IsFinite(r.DurationSeconds) || r.DurationSeconds < 0 || !double.IsFinite(r.ThresholdMs) || r.ThresholdMs is < 1 or > 10000 ||
            r.Events is null || r.Events.Count > 512 || r.Events.Any(e => e is null || e.Source is null || e.Category is null || e.EventType is null || e.Severity is null ||
                e.Summary is null || e.TimestampBasis is null || e.Quality is null || e.Details is null || e.Details.Count > 8 || e.Details.Any(p => p.Value is null)) ||
            r.Spikes is null || r.Spikes.Count > 256 || r.Spikes.Any(f => !FrameValid(f)) ||
            r.Clusters is null || r.Clusters.Count > 64 || r.Clusters.Any(c => c is null || !FrameValid(c.Peak) || c.Diagnosis is null ||
                c.Diagnosis.Observation is null || c.Diagnosis.PossibleCause is null || c.Diagnosis.ConfidenceAndLimitations is null) ||
            r.Devices is null || r.Devices.Count > 128 || r.Devices.Any(d => d is null || d.Id is null || d.Name is null || d.ConnectionState is null || d.IdentityQuality is null) ||
            r.Hardware is null || r.Hardware.Count > 16 || r.Hardware.Any(h => h is null || h.Values is null) ||
            r.Correlations is null || r.Correlations.Count > 256 || r.Correlations.Any(c => c is null || !GroupValid(c.Group) || c.Confidence is null || c.Assessment is null ||
                c.Matches is null || c.Matches.Count > 4 || c.Matches.Any(m => m is null) || c.Limitations is null || c.Limitations.Any(l => l is null) ||
                c.CompetingGroups is null || c.CompetingGroups.Any(g => g is null)) ||
            r.Correlations.DistinctBy(c => c.Group).Count() != r.Correlations.Count ||
            r.Collectors is null || r.Collectors.Count > 32 || r.Collectors.Any(c => c is null || c.Source is null || c.Availability is null || c.Detail is null) ||
            r.EventPeriodicity is null || r.EventPeriodicity.Count > 256 || r.EventPeriodicity.Any(p => p is null || !GroupValid(p.Group) || p.Periodicity is null || p.Periodicity.Assessment is null) ||
            r.Findings is null || r.Findings.Count > 24 || r.Findings.Any(f => f is null) ||
            r.Limitations is null || r.Limitations.Count > 26 || r.Limitations.Any(l => l is null) ||
            JsonSerializer.SerializeToUtf8Bytes(r).Length > MaximumBytes)
            throw new InvalidDataException("Invalid bounded investigation report.");
    }
}

public sealed record InvestigationPage<T>(int SchemaVersion, Guid SessionId, string Status, int Offset, int RetainedCount,
    int? NextOffset, IReadOnlyList<T> Items, InvestigationCoverage Coverage, IReadOnlyList<InvestigationCollectorStatus> Collectors,
    IReadOnlyList<string> Limitations);
