using JamesTools.Core.Configuration;

namespace JamesTools.Core.Scanning;

/// <summary>Schedules one replay save for a burst of selected live HUD events.</summary>
public sealed class WardogsHighlights
{
    private readonly LiveWardogsSettings settings;
    private readonly TimeSpan maximumDelay;
    private readonly HashSet<long> handled = [];
    private TimeSpan? due;
    private TimeSpan latestSave;
    private TimeSpan expires;

    public WardogsHighlights(LiveWardogsSettings settings, int replaySeconds)
    {
        if (!settings.HighlightsValid || replaySeconds is < 15 or > 600)
            throw new ArgumentException("Choose highlight events, 3–10 seconds after an event, and a replay buffer of 15–600 seconds.");
        this.settings = settings.Copy();
        // Reserve some lead-in even when a long streak keeps extending the post-roll.
        maximumDelay = TimeSpan.FromSeconds(replaySeconds - 5);
    }

    public static bool SupportsExecutable(string? path) =>
        string.Equals(Path.GetFileName(path), "WardogsClient-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFileName(path), "Wardogs-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase);

    public bool Pending => due.HasValue;

    public void Observe(IReadOnlyList<LiveKill> events, TimeSpan now)
    {
        if (now >= expires) due = null;
        if (events.Count == 0) return;
        handled.RemoveWhere(id => id < events[0].Id);
        foreach (var entry in events)
        {
            if (!Includes(entry.Kind) || !handled.Add(entry.Id)) continue;
            if (due is null)
            {
                latestSave = now + maximumDelay;
                expires = latestSave + TimeSpan.FromSeconds(3);
            }
            var after = now + TimeSpan.FromSeconds(settings.HighlightPostSeconds);
            due = after <= latestSave ? after : latestSave;
        }
    }

    public bool TakeDue(TimeSpan now)
    {
        if (due is not { } scheduled || now < scheduled) return false;
        due = null;
        return now < expires;
    }

    private bool Includes(WardogsEventKind kind) => kind switch
    {
        WardogsEventKind.Kill or WardogsEventKind.RevengeKill => settings.HighlightKills,
        WardogsEventKind.Headshot => settings.HighlightHeadshots,
        WardogsEventKind.Assist => settings.HighlightAssists,
        WardogsEventKind.Revive or WardogsEventKind.ReviveAssist or WardogsEventKind.Heal or WardogsEventKind.WaitedForRevive => settings.HighlightSupport,
        _ => false
    };
}
