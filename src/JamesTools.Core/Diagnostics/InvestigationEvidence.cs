using System.Diagnostics;

namespace JamesTools.Core.Diagnostics;

/// <summary>One QPC origin for all sources in a performance recording. Receipt times are never event times.</summary>
public sealed record InvestigationClock(long OriginQpc, long Frequency, DateTimeOffset OriginUtc)
{
    public static InvestigationClock FromElapsed(Stopwatch elapsed)
    {
        var qpc = Stopwatch.GetTimestamp();
        var seconds = elapsed.Elapsed.TotalSeconds;
        return new(qpc - (long)(seconds * Stopwatch.Frequency), Stopwatch.Frequency, DateTimeOffset.UtcNow.AddSeconds(-seconds));
    }
    public double NowSeconds => FromQpc(Stopwatch.GetTimestamp());
    public double FromQpc(long qpc) => (qpc - OriginQpc) / (double)Frequency;
    public double FromUtc(DateTimeOffset utc) => (utc - OriginUtc).TotalSeconds;
}

/// <summary>Portable, bounded evidence. UTC-derived and callback receipt timestamps carry their uncertainty.</summary>
public sealed record InvestigationEvent
{
    public double TimestampSeconds { get; init; }
    public double ReceiptSeconds { get; init; }
    public string Source { get; init; } = "";
    public string Category { get; init; } = "";
    public string EventType { get; init; } = "";
    public int? ProcessId { get; init; }
    public int? ThreadId { get; init; }
    public string? DeviceId { get; init; }
    public double? DurationMs { get; init; }
    public string Severity { get; init; } = "information";
    public string Summary { get; init; } = "";
    public string? RawEvidenceReference { get; init; }
    public string TimestampBasis { get; init; } = "QPC";
    public double? UncertaintyMs { get; init; }
    public string Quality { get; init; } = "observed";
    public IReadOnlyDictionary<string, string> Details { get; init; } = new Dictionary<string, string>();
}

/// <summary>Redacted device identities remain stable within and between local recordings; raw IDs stay local.</summary>
public sealed record InvestigationDevice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "Unknown device";
    /// <summary>Local inventory description; deliberately excluded from every portable JSON/report.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LocalName { get; init; }
    public string? Class { get; init; }
    public string? Manufacturer { get; init; }
    public string? Vid { get; init; }
    public string? Pid { get; init; }
    public string? DriverName { get; init; }
    public string? DriverVersion { get; init; }
    public string ConnectionState { get; init; } = "unknown";
    public string? ControllerId { get; init; }
    public string? ParentId { get; init; }
    public string? ParentHubId { get; init; }
    public string? Topology { get; init; }
    public string? PowerManagement { get; init; }
    public string IdentityQuality { get; init; } = "Instance identifier redacted";
}

public sealed record InvestigationCollectorStatus(string Source, string Availability, string Detail)
{
    public long EventsLost { get; init; }
    public long EventsDropped { get; init; }
    public double CollectionMilliseconds { get; init; }
    public double ActiveSeconds { get; init; }
}
