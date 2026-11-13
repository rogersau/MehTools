namespace JamesTools.Core.Sessions;

public enum SessionStatus
{
    Active,
    Completed,
    Interrupted,
    WatchingStopped
}

public enum SessionAttachmentKind
{
    Recording,
    Clip,
    PerformanceReport
}

public sealed record SessionAttachment
{
    public SessionAttachmentKind Kind { get; init; }
    public string Path { get; init; } = "";
    public DateTime ObservedUtc { get; init; }
}

public sealed record GameSession
{
    public Guid Id { get; init; }
    public string GameName { get; init; } = "";
    public string ExecutableKey { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime? EndedUtc { get; init; }
    public DateTime LastObservedUtc { get; init; }
    public SessionStatus Status { get; init; }
    public string Notes { get; init; } = "";
    public bool Favourite { get; init; }
    public List<SessionAttachment> Attachments { get; init; } = [];

    public TimeSpan DetectedDuration => (EndedUtc ?? LastObservedUtc) > StartedUtc
        ? (EndedUtc ?? LastObservedUtc) - StartedUtc : TimeSpan.Zero;
}

public sealed record SessionGameObservation(string Name, string ExecutablePath);
