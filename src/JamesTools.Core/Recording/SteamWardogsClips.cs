using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using JamesTools.Core.Configuration;

namespace JamesTools.Core.Recording;

// Read the simple local fragment template Steam writes. Do not pass the manifest
// directly to FFmpeg: the bundled build does not include the DASH demuxer.
internal sealed record SteamFragmentRecording(string Directory, long FragmentMs, int FirstNumber, long DurationMs,
    IReadOnlyList<SteamFragmentTrack> Tracks)
{
    internal long FirstMs => (FirstNumber - 1L) * FragmentMs;
    internal long EndMs => FirstMs + DurationMs;
    internal int NumberAt(long mediaMs) => checked((int)(mediaMs / FragmentMs + 1));
    internal bool HasFragmentsAt(long mediaMs) => mediaMs >= FirstMs && mediaMs < EndMs && HasFragments(NumberAt(mediaMs));
    internal bool HasFragments(int number) => Tracks.All(t => File.Exists(Fragment(t, number)));
    internal string Init(SteamFragmentTrack t) => Path.Combine(Directory, $"init-stream{t.Id}.m4s");
    internal string Fragment(SteamFragmentTrack t, int number) =>
        Path.Combine(Directory, FormattableString.Invariant($"chunk-stream{t.Id}-{number:D5}.m4s"));

    internal static SteamFragmentRecording Read(string directory)
    {
        using var stream = new MemoryStream(SteamWardogsLibrary.ReadBounded(Path.Combine(directory, "session.mpd")));
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 });
        XNamespace ns = "urn:mpeg:dash:schema:mpd:2011";
        var root = XDocument.Load(reader).Root;
        if (root?.Name != ns + "MPD" || (string?)root.Attribute("type") != "static" ||
            root.Elements(ns + "Period").ToArray() is not [var period] ||
            XmlConvert.ToTimeSpan((string?)period.Attribute("start") ?? "PT0S") != TimeSpan.Zero) throw Unsupported();
        var duration = (long)XmlConvert.ToTimeSpan((string?)root.Attribute("mediaPresentationDuration") ?? "PT0S").TotalMilliseconds;
        if (duration is < 1 or > int.MaxValue) throw Unsupported();
        var tracks = new List<SteamFragmentTrack>();
        long fragment = 0;
        int first = 0;
        foreach (var set in period.Elements(ns + "AdaptationSet"))
        {
            var type = (string?)set.Attribute("contentType");
            if (type is not ("video" or "audio") ||
                set.Elements(ns + "Representation").ToArray() is not [var track] ||
                track.Elements(ns + "SegmentTemplate").ToArray() is not [var template] ||
                template.HasElements ||
                !int.TryParse((string?)track.Attribute("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is < 0 or > 63 ||
                tracks.Any(t => t.Id == id) ||
                (string?)template.Attribute("initialization") != "init-stream$RepresentationID$.m4s" ||
                (string?)template.Attribute("media") != "chunk-stream$RepresentationID$-$Number%05d$.m4s" ||
                !int.TryParse((string?)template.Attribute("startNumber"), NumberStyles.None, CultureInfo.InvariantCulture, out var trackFirst) ||
                trackFirst is < 1 or > 1_000_000 ||
                !int.TryParse((string?)template.Attribute("timescale"), NumberStyles.None, CultureInfo.InvariantCulture, out var scale) || scale < 1 ||
                !int.TryParse((string?)template.Attribute("duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                1000L * length % scale != 0) throw Unsupported();
            long trackFragment = 1000L * length / scale;
            if (trackFragment is < 1000 or > 10000 || (tracks.Count > 0 && (trackFragment != fragment || trackFirst != first))) throw Unsupported();
            fragment = trackFragment;
            first = trackFirst;
            tracks.Add(new(id, type == "video"));
        }
        if (tracks.Count(t => t.Video) != 1 || tracks.Count > 8) throw Unsupported();
        var source = new SteamFragmentRecording(directory, fragment, first, duration, tracks.OrderByDescending(t => t.Video).ToArray());
        if (!source.Tracks.All(t => File.Exists(source.Init(t)))) throw Unsupported();
        return source;
    }

    private static InvalidDataException Unsupported() => new("This Steam recording uses an unsupported or incomplete fragment layout.");
}

internal sealed record SteamFragmentTrack(int Id, bool Video);
public sealed record SteamClipResult(string Path, TimeSpan EventPosition, MediaInfo Media);

public sealed partial class SteamWardogsClips(MediaTools media)
{
    public async Task<SteamClipResult> ExportAsync(string root, string timelineId, long eventId, string destination,
        int beforeSeconds = SteamHighlightSettings.DefaultPaddingSeconds,
        int afterSeconds = SteamHighlightSettings.DefaultPaddingSeconds, CancellationToken token = default)
    {
        ValidatePadding(beforeSeconds, afterSeconds);
        destination = NewDestination(destination);

        // Refresh eligibility before every export. Steam may have rotated the buffer since review opened.
        var timeline = SteamWardogsLibrary.Read(root, token).Timelines.SingleOrDefault(t => t.Id == timelineId);
        var marker = timeline?.Events.SingleOrDefault(e => e.Id == eventId);
        if (timeline is null || marker is not { HasFootage: true } ||
            FindWindow(timeline, marker, beforeSeconds, afterSeconds) is not { } window)
            throw new InvalidDataException("Steam no longer has uniquely matched footage for this event. Refresh the list.");
        var info = await WriteWindowAsync(window, destination, token).ConfigureAwait(false);
        return new(destination, TimeSpan.FromMilliseconds(marker.TimelineMs - window.StartTimelineMs), info);
    }

    internal sealed record ClipWindow(SteamRecordingSpan Span, int First, int Last, long LastMarkerMs)
    {
        internal long FirstMs => (First - 1L) * Span.Recording.FragmentMs;
        internal long StartTimelineMs => Span.ZeroMs + FirstMs;
    }

    private static ClipWindow? FindWindow(SteamWardogsTimeline timeline, SteamWardogsEvent marker, int beforeSeconds, int afterSeconds)
    {
        if (timeline.Spans.Where(s => s.Covers(marker.TimelineMs)).Take(2).ToArray() is not [var span]) return null;
        var source = span.Recording;
        long start = Math.Max(span.StartMs, marker.TimelineMs - beforeSeconds * 1000L) - span.ZeroMs;
        long end = Math.Min(span.EndMs, marker.TimelineMs + Math.Max(1, afterSeconds * 1000L)) - span.ZeroMs;
        int center = source.NumberAt(marker.TimelineMs - span.ZeroMs);
        int first = center;
        int last = center;
        int wantedFirst = source.NumberAt(start);
        int wantedLast = source.NumberAt(end - 1);
        // Keep clip padding within one contiguous recording and round out to fragment/keyframe boundaries.
        while (first > wantedFirst && source.HasFragments(first - 1)) first--;
        while (last < wantedLast && source.HasFragments(last + 1)) last++;
        return new(span, first, last, marker.TimelineMs);
    }

    private static void ValidatePadding(int beforeSeconds, int afterSeconds)
    {
        if (!SteamHighlightSettings.ValidPadding(beforeSeconds, afterSeconds))
            throw new ArgumentException("Choose 0–60 seconds before and after the event, with at least one side above zero.");
    }

    private static string NewDestination(string destination)
    {
        if (!SteamHighlightSettings.IsLocalFolder(destination) ||
            !string.Equals(Path.GetExtension(destination), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a new local MP4 file.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Choose a new MP4 filename. Existing files aren't overwritten.");
        return destination;
    }

    private async Task<MediaInfo> WriteWindowAsync(ClipWindow window, string destination, CancellationToken token)
    {
        var source = window.Span.Recording;
        var (first, last, firstMs) = (window.First, window.Last, window.FirstMs);
        var inputs = new List<string>();
        string suffix = Guid.NewGuid().ToString("N");
        string folder = Path.GetDirectoryName(destination)!;
        string output = Path.Combine(folder, $".meh-steam-{suffix}.mp4");
        try
        {
            foreach (var track in source.Tracks)
            {
                var input = Path.Combine(folder, $".meh-steam-{suffix}-{track.Id}.mp4");
                using var target = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                inputs.Add(input);
                await AppendAsync(source.Init(track), target, token).ConfigureAwait(false);
                for (int i = first; i <= last; i++)
                    await AppendAsync(source.Fragment(track, i), target, token).ConfigureAwait(false);
            }
            var startTime = await ReadVideoStartAsync(inputs[0], token).ConfigureAwait(false);
            if (Math.Abs(startTime - firstMs / 1000d) > .75)
                throw new InvalidDataException("Steam's fragment timestamps did not match its timeline. No clip was saved.");
            await media.Execute("ffmpeg", Arguments(inputs, output, firstMs), TimeSpan.FromMinutes(2), token).ConfigureAwait(false);
            var info = await media.ProbeAsync(output, token).ConfigureAwait(false);
            var eventSeconds = (window.LastMarkerMs - window.StartTimelineMs) / 1000d;
            if (info.VideoTracks != 1 || info.AudioTracks != inputs.Count - 1 ||
                info.Duration > (last - first + 1) * source.FragmentMs / 1000d + 1 ||
                eventSeconds >= info.Duration)
                throw new InvalidDataException("The exported clip did not contain the expected streams or event time. No clip was saved.");
            token.ThrowIfCancellationRequested();
            File.Move(output, destination, false);
            return info;
        }
        finally
        {
            foreach (var input in inputs.Append(output))
                try { File.Delete(input); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<double> ReadVideoStartAsync(string path, CancellationToken token)
    {
        var json = await media.Execute("ffprobe", ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "mov",
            "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "json", path], TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() == 1 &&
            streams[0].TryGetProperty("start_time", out var start) &&
            double.TryParse(start.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds))
            return seconds;
        throw new InvalidDataException("Steam's video timestamp could not be verified.");
    }

    private static async Task AppendAsync(string path, Stream target, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, useAsync: true);
        if (source.Length is < 1 or > 256L * 1024 * 1024) throw new InvalidDataException("A Steam video fragment is empty or unusually large.");
        await source.CopyToAsync(target, token).ConfigureAwait(false);
    }

    internal static string[] Arguments(IReadOnlyList<string> inputs, string output, long startMs)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-n", "-copyts" };
        var offset = (-startMs / 1000m).ToString(CultureInfo.InvariantCulture);
        foreach (var input in inputs)
            args.AddRange(["-itsoffset", offset, "-protocol_whitelist", "file", "-f", "mov", "-i", input]);
        args.AddRange(["-map", "0:v:0"]);
        for (int i = 1; i < inputs.Count; i++) args.AddRange(["-map", FormattableString.Invariant($"{i}:a:0")]);
        args.AddRange(["-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-f", "mp4", output]);
        return args.ToArray();
    }
}
