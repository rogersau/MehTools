using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JamesTools.Core.Sessions;

namespace JamesTools.Core.Recording;

[Flags]
public enum SteamWardogsEventCategory { Kills = 1, Assists = 2, Revives = 4, Other = 8, All = Kills | Assists | Revives | Other }

public sealed record SteamWardogsEvent(long Id, long TimelineMs, string Title, string Description, string Icon,
    bool HasFootage)
{
    public SteamWardogsEventCategory Category => Icon switch
    {
        "wd-kill" => SteamWardogsEventCategory.Kills,
        "wd-assist" => SteamWardogsEventCategory.Assists,
        "wd-respawn" => SteamWardogsEventCategory.Revives,
        _ => SteamWardogsEventCategory.Other
    };
}
public sealed record SteamWardogsTimeline(string Id, DateTimeOffset RecordedAt, IReadOnlyList<SteamWardogsEvent> Events)
{
    public DateTimeOffset? EndedAt { get; init; }
    internal IReadOnlyList<SteamRecordingSpan> Spans { get; init; } = [];

    public SteamWardogsKillDeaths? KillDeaths()
    {
        int kills = 0, deaths = 0;
        foreach (var ev in Events)
        {
            if (ev.Icon == "wd-kill") kills++;
            else if (ev.Icon == "wd-killed") deaths++;
        }
        return kills == 0 && deaths == 0 ? null : new(kills, deaths);
    }
}

public sealed record SteamWardogsKillDeaths(int Kills, int Deaths)
{
    public double? Ratio => Deaths > 0 ? (double)Kills / Deaths : null;
}
public sealed record SteamWardogsCatalog(IReadOnlyList<SteamWardogsTimeline> Timelines, int UnreadableTimelines);
internal sealed record SteamRecordingSpan(SteamFragmentRecording Recording, long StartMs, long EndMs, long ZeroMs)
{
    internal bool Covers(long time) => time >= StartMs && time < EndMs && Recording.HasFragmentsAt(time - ZeroMs);
}

/// <summary>Read-only adapter for Steam's local WARDOGS recordings (app 1867240).</summary>
public static class SteamWardogsLibrary
{
    private const ulong AppId = 1867240;
    private const int FileLimit = 16 * 1024 * 1024;
    private static readonly Regex TimelineId = new(@"\Atimeline_1867240[0-9]{8}_[0-9]{6}\z", RegexOptions.CultureInvariant);
    private static readonly Regex RecordingId = new(@"\Abg_1867240_[0-9]{8}_[0-9]{6}\z", RegexOptions.CultureInvariant);

    public static bool IsWardogs(GameSession session) => IsWardogs(session.GameName, session.ExecutableKey);

    public static bool IsWardogs(string? name, string? path)
        => (!string.IsNullOrWhiteSpace(name) && name.Contains("wardogs", StringComparison.OrdinalIgnoreCase)) ||
           (!string.IsNullOrWhiteSpace(path) && Path.GetFileNameWithoutExtension(path).Contains("wardogs", StringComparison.OrdinalIgnoreCase));

    public static bool IsCompletedWardogsSession(GameSession session)
        => session.Status == SessionStatus.Completed && IsHistoricalWardogsSession(session);

    public static bool IsHistoricalWardogsSession(GameSession session)
        => session.Status is SessionStatus.Completed or SessionStatus.Interrupted or SessionStatus.WatchingStopped &&
            session.EndedUtc > session.StartedUtc && IsWardogs(session);

    public static SteamWardogsTimeline? MatchCompletedSession(GameSession session, SteamWardogsCatalog catalog)
        => IsCompletedWardogsSession(session) ? MatchHistoricalSession(session, catalog) : null;

    public static SteamWardogsTimeline? MatchHistoricalSession(GameSession session, SteamWardogsCatalog catalog)
    {
        if (!IsHistoricalWardogsSession(session) || session.EndedUtc is not { } ended) return null;
        var start = new DateTimeOffset(session.StartedUtc.ToUniversalTime());
        var end = new DateTimeOffset(ended.ToUniversalTime());
        var minimumOverlap = (end - start) / 2;
        var matches = catalog.Timelines.Where(t => t.EndedAt is { } steamEnd &&
            t.RecordedAt <= start.AddSeconds(35) && (steamEnd - end).Duration() <= TimeSpan.FromSeconds(90) &&
            (steamEnd < end ? steamEnd : end) - (t.RecordedAt > start ? t.RecordedAt : start) >= minimumOverlap)
            .Take(2).ToArray();
        return matches is [var only] ? only : null;
    }

    public static GameSession? FindHistoricalSession(SteamWardogsTimeline timeline, IEnumerable<GameSession> sessions)
    {
        var catalog = new SteamWardogsCatalog([timeline], 0);
        var matches = sessions.Where(s => MatchHistoricalSession(s, catalog) is not null).Take(2).ToArray();
        return matches is [var only] ? only : null;
    }

    public static SteamWardogsCatalog Read(string root, CancellationToken token = default)
        => ReadCatalog(root, includeFootage: true, token);

    /// <summary>Reads marker metadata for session statistics without opening the video or requiring the retained-footage index.</summary>
    public static SteamWardogsCatalog ReadMarkers(string root, CancellationToken token = default)
        => ReadCatalog(root, includeFootage: false, token);

    private static SteamWardogsCatalog ReadCatalog(string root, bool includeFootage, CancellationToken token)
    {
        root = LocalDirectory(root);
        var jsonDirectory = Path.Combine(root, "timelines");
        if (!File.Exists(Path.Combine(root, "gamerecording.pb")) && !Directory.Exists(jsonDirectory))
            throw new DirectoryNotFoundException("Choose the Game Recording folder from Steam settings, not its video or timelines subfolder.");
        var index = includeFootage ? ReadIndex(root, token) : null;
        if (!Directory.Exists(jsonDirectory)) return new([], 0);
        var result = new List<SteamWardogsTimeline>();
        int unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(jsonDirectory, "timeline_1867240*.json"))
        {
            token.ThrowIfCancellationRequested();
            string id = Path.GetFileNameWithoutExtension(path);
            if (!TimelineId.IsMatch(id)) continue;
            try
            {
                using var doc = JsonDocument.Parse(ReadBounded(path));
                var json = doc.RootElement;
                if (!ReadNumber(json, "daterecorded", out long date) || date is < 1 or > 253402300799)
                    throw new InvalidDataException("The Steam timeline has no valid recording date.");
                // Never associate footage using nearby filenames or timestamps alone.
                var row = index?.GetValueOrDefault(id) ?? default;
                bool indexed = row.Date == date;
                var spans = indexed ? row.Spans! : [];
                var recordedAt = DateTimeOffset.FromUnixTimeSeconds(date);
                // JSON supplies the game timeline end. The index may stop earlier if video stopped before the game exited.
                // The automatic job also waits for two stable reads after the game has exited.
                bool hasEnd = ReadNumber(json, "endtime", out long end) && end is > 0 and <= int.MaxValue &&
                    end <= (DateTimeOffset.MaxValue - recordedAt).TotalMilliseconds;
                bool indexAgrees = indexed && row.Duration is > 0 and <= int.MaxValue && row.Duration <= end + 2000;
                DateTimeOffset? endedAt = hasEnd && (!includeFootage || indexAgrees)
                        ? recordedAt.AddMilliseconds(end) : null;
                var events = new List<SteamWardogsEvent>();
                var seen = new HashSet<long>();
                if (json.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                    foreach (var item in entries.EnumerateArray())
                    {
                        token.ThrowIfCancellationRequested();
                        if (ReadString(item, "type") != "event" || ReadString(item, "icon") is not { } icon ||
                            !icon.StartsWith("wd-", StringComparison.Ordinal) ||
                            !ReadNumber(item, "id", out long eventId) || eventId < 1 ||
                            !ReadNumber(item, "time", out long time) || time < 0 || !seen.Add(eventId)) continue;
                        var matches = spans.Count(span => span.Covers(time));
                        events.Add(new(eventId, time, ReadString(item, "title") ?? icon, ReadString(item, "description") ?? "", icon, matches == 1));
                    }
                if (events.Count > 0 || spans.Count > 0 || endedAt is not null)
                    result.Add(new(id, recordedAt, events.OrderBy(e => e.TimelineMs).ToArray()) { Spans = spans, EndedAt = endedAt });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { unreadable++; }
        }
        return new(result.OrderByDescending(t => t.RecordedAt).ToArray(), unreadable);
    }

    private static Dictionary<string, (long Date, long Duration, IReadOnlyList<SteamRecordingSpan> Spans)> ReadIndex(string root, CancellationToken token)
    {
        var result = new Dictionary<string, (long, long, IReadOnlyList<SteamRecordingSpan>)>(StringComparer.Ordinal);
        byte[] bytes;
        try { bytes = ReadBounded(Path.Combine(root, "gamerecording.pb")); }
        catch (FileNotFoundException) { return result; }
        var file = new SteamProtoReader(bytes);
        while (file.Read(out int field, out ReadOnlySpan<byte> data, out _))
        {
            token.ThrowIfCancellationRequested();
            if (field != 1 || data.IsEmpty) continue;
            var timeline = new SteamProtoReader(data);
            string? id = null;
            ulong game = 0, date = 0, duration = 0;
            while (timeline.Read(out var key, out var value, out var number))
            {
                if (key == 1 && value.Length is > 0 and <= 64) id = Encoding.ASCII.GetString(value);
                else if (key == 2) game = number;
                else if (key == 3) date = number;
                else if (key == 4) duration = number;
            }
            if (game != AppId || date is 0 or > 253402300799 || id is null || !TimelineId.IsMatch(id)) continue;
            if (result.ContainsKey(id)) throw new InvalidDataException("Steam's recording index contains duplicate WARDOGS timeline IDs.");
            var spans = new List<SteamRecordingSpan>();
            timeline = new SteamProtoReader(data);
            while (timeline.Read(out var key, out var value, out _))
            {
                if (key == 5 && !value.IsEmpty && ReadSpan(root, value) is { } span) spans.Add(span);
            }
            result.Add(id, ((long)date, duration <= int.MaxValue ? (long)duration : 0, spans));
        }
        return result;
    }

    private static SteamRecordingSpan? ReadSpan(string root, ReadOnlySpan<byte> data)
    {
        var recording = new SteamProtoReader(data);
        string? id = null;
        ulong? start = null, zero = null;
        ulong duration = 0;
        while (recording.Read(out var field, out var value, out var number))
        {
            if (field == 1 && value.Length is > 0 and <= 64) id = Encoding.ASCII.GetString(value);
            else if (field == 2) start = number;
            else if (field == 3) duration = number;
            else if (field == 10) zero = number;
        }
        if (id is null || !RecordingId.IsMatch(id) || start is null || zero is null || zero > start ||
            start > int.MaxValue || duration is 0 or > int.MaxValue) return null;
        var directory = Path.Combine(root, "video", id);
        try
        {
            var source = SteamFragmentRecording.Read(directory);
            var retainedStart = (long)(start.Value - zero.Value);
            // Original fragment numbering and the retained offset must agree to within one fragment.
            if (Math.Abs(source.FirstMs - retainedStart) > source.FragmentMs) return null;
            var finish = Math.Min((long)(start.Value + duration), (long)zero.Value + source.EndMs);
            var begin = Math.Max((long)start.Value, (long)zero.Value + source.FirstMs);
            return finish > begin ? new(source, begin, finish, (long)zero.Value) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException or FormatException or OverflowException) { return null; }
    }

    private static string LocalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("://"))
            throw new ArgumentException("Choose a local Steam Game Recording folder.");
        return Path.GetFullPath(path);
    }

    internal static byte[] ReadBounded(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > FileLimit) throw new InvalidDataException("Steam recording metadata is larger than supported.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool ReadNumber(JsonElement item, string name, out long number)
    {
        number = 0;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var v) &&
            (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out number) ||
             v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number));
    }
}

// Subset of the local Steam client schema, documented in docs/steam-wardogs.md.
internal ref struct SteamProtoReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> remaining = data;

    internal bool Read(out int field, out ReadOnlySpan<byte> bytes, out ulong number)
    {
        field = 0; bytes = default; number = 0;
        if (remaining.IsEmpty) return false;
        ulong tag = ReadVarint();
        if (tag >> 3 is 0 or > 536870911) throw InvalidIndex();
        field = (int)(tag >> 3);
        switch (tag & 7)
        {
            case 0: number = ReadVarint(); break;
            case 1: Take(8); break;
            case 2: bytes = Take(ReadVarint()); break;
            case 5: Take(4); break;
            default: throw InvalidIndex();
        }
        return true;
    }

    private ReadOnlySpan<byte> Take(ulong length)
    {
        if (length > (ulong)remaining.Length) throw InvalidIndex();
        var value = remaining[..(int)length]; remaining = remaining[(int)length..];
        return value;
    }

    private ulong ReadVarint()
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            byte next = Take(1)[0];
            if (shift == 63 && next > 1) throw InvalidIndex();
            value |= (ulong)(next & 127) << shift;
            if ((next & 128) == 0) return value;
        }
        throw InvalidIndex();
    }

    private static InvalidDataException InvalidIndex() => new("Steam's recording index is incomplete or uses an unsupported format. Refresh after Steam finishes writing.");
}
