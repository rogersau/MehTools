using System.Globalization;
using System.Text.Json;
using JamesTools.Core.Configuration;

namespace JamesTools.Core.Recording;

public sealed record SteamMontageResult(string? Path, int IncludedEvents, int SkippedEvents, int Windows, MediaInfo? Media);

public sealed partial class SteamWardogsClips
{
    internal static (IReadOnlyList<ClipWindow> Windows, int IncludedEvents) PlanMontage(
        SteamWardogsTimeline timeline, int beforeSeconds, int afterSeconds, CancellationToken token = default)
        => PlanMontage(timeline, timeline.Events, beforeSeconds, afterSeconds, token);

    private static (IReadOnlyList<ClipWindow> Windows, int IncludedEvents) PlanMontage(
        SteamWardogsTimeline timeline, IEnumerable<SteamWardogsEvent> events, int beforeSeconds, int afterSeconds, CancellationToken token)
    {
        ValidatePadding(beforeSeconds, afterSeconds);
        var clips = new List<ClipWindow>();
        foreach (var marker in events)
        {
            token.ThrowIfCancellationRequested();
            if (marker.HasFootage && FindWindow(timeline, marker, beforeSeconds, afterSeconds) is { } window)
                clips.Add(window);
        }
        var merged = new List<ClipWindow>();
        foreach (var group in clips.GroupBy(c => c.Span))
        {
            ClipWindow? current = null;
            foreach (var next in group.OrderBy(c => c.First).ThenBy(c => c.Last))
            {
                if (current is not null && next.First <= current.Last + 1)
                {
                    current = current with { Last = Math.Max(current.Last, next.Last), LastMarkerMs = Math.Max(current.LastMarkerMs, next.LastMarkerMs) };
                    continue;
                }
                if (current is not null) merged.Add(current);
                current = next;
            }
            if (current is not null) merged.Add(current);
        }
        return (merged.OrderBy(c => c.StartTimelineMs).ToArray(), clips.Count);
    }

    public async Task<SteamMontageResult> ExportMontageAsync(string root, SteamWardogsTimeline expected, string destination,
        int beforeSeconds = SteamHighlightSettings.DefaultPaddingSeconds,
        int afterSeconds = SteamHighlightSettings.DefaultPaddingSeconds, IProgress<string>? progress = null,
        IReadOnlyCollection<long>? selectedEventIds = null, SteamWardogsEventCategory categories = SteamWardogsEventCategory.All,
        CancellationToken token = default)
    {
        destination = NewDestination(destination);
        var selected = selectedEventIds?.ToHashSet();
        var timeline = SteamWardogsLibrary.Read(root, token).Timelines.SingleOrDefault(t => t.Id == expected.Id);
        if (timeline is null || expected.EndedAt is null || timeline.EndedAt != expected.EndedAt || timeline.RecordedAt != expected.RecordedAt)
            throw new InvalidDataException("Steam's completed timeline changed before export.");
        // A manual review is an exact list of marker IDs. Automatic export uses the saved event types.
        var events = timeline.Events.Where(e => selected?.Contains(e.Id) ?? ((e.Category & categories) != 0)).ToArray();
        var (windows, included) = PlanMontage(timeline, events, beforeSeconds, afterSeconds, token);
        int skipped = (selected?.Count ?? events.Length) - included;
        if (windows.Count == 0) return new(null, included, skipped, 0, null);

        string folder = Path.GetDirectoryName(destination)!;
        var scratch = Directory.CreateDirectory(Path.Combine(folder, $".meh-steam-{Guid.NewGuid():N}")).FullName;
        try
        {
            var parts = new List<(string Path, MediaInfo Info, string[] Layout)>();
            for (int i = 0; i < windows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report($"Writing Steam highlights · {i + 1} of {windows.Count} merged moments");
                string part = Path.Combine(scratch, FormattableString.Invariant($"part-{i:D5}.mp4"));
                var info = await WriteWindowAsync(windows[i], part, token).ConfigureAwait(false);
                parts.Add((part, info, windows.Count > 1 ? await ReadLayoutAsync(part, token).ConfigureAwait(false) : []));
            }
            if (parts.Any(p => !p.Layout.SequenceEqual(parts[0].Layout)))
            {
                progress?.Report("Steam changed recording format during the session. Converting the highlights to one format…");
                int audioTracks = parts.Max(p => p.Info.AudioTracks);
                for (int i = 0; i < parts.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var part = parts[i];
                    string normalized = Path.Combine(scratch, FormattableString.Invariant($"normalized-{i:D5}.mp4"));
                    await media.Execute("ffmpeg", NormalizeArguments(part.Path, normalized, part.Info, audioTracks),
                        TimeSpan.FromMinutes(15), token).ConfigureAwait(false);
                    var info = await media.ProbeAsync(normalized, token).ConfigureAwait(false);
                    if (info.VideoTracks != 1 || info.AudioTracks != audioTracks || Math.Abs(info.Duration - part.Info.Duration) > .25)
                        throw new InvalidDataException("Steam highlight conversion did not preserve the expected duration and tracks.");
                    parts[i] = (normalized, info, await ReadLayoutAsync(normalized, token).ConfigureAwait(false));
                    File.Delete(part.Path);
                }
                if (parts.Any(p => !p.Layout.SequenceEqual(parts[0].Layout)))
                    throw new InvalidDataException("The highlights could not be combined into a single video format.");
            }
            var final = parts[0].Path;
            var finalInfo = parts[0].Info;
            if (parts.Count > 1)
            {
                progress?.Report("Joining and verifying the Steam highlights video…");
                string concat = Path.Combine(scratch, "concat.txt");
                // Safe relative paths containing only our generated ASCII names.
                await File.WriteAllLinesAsync(concat, parts.Select(p => $"file '{Path.GetFileName(p.Path)}'"), token).ConfigureAwait(false);
                final = Path.Combine(scratch, "highlights.mp4");
                await media.Execute("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-n", "-f", "concat", "-safe", "1",
                    "-protocol_whitelist", "file", "-i", concat, "-map", "0", "-c", "copy", "-movflags", "+faststart", final],
                    TimeSpan.FromMinutes(15), token).ConfigureAwait(false);
                finalInfo = await media.ProbeAsync(final, token).ConfigureAwait(false);
                if (finalInfo.VideoTracks != 1 || finalInfo.AudioTracks != parts[0].Info.AudioTracks ||
                    Math.Abs(finalInfo.Duration - parts.Sum(p => p.Info.Duration)) > Math.Max(.5, parts.Count * .03))
                    throw new InvalidDataException("The final Steam highlights video has unexpected tracks or duration.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(final, destination, overwrite: false);
            return new(destination, included, skipped, windows.Count, finalInfo);
        }
        finally
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(scratch)) File.Delete(file);
                Directory.Delete(scratch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<string[]> ReadLayoutAsync(string path, CancellationToken token)
    {
        string[] keys = ["codec_type", "codec_name", "profile", "codec_tag_string", "width", "height", "pix_fmt",
            "sample_rate", "channels", "channel_layout", "time_base", "extradata_hash"];
        var json = await media.Execute("ffprobe", ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "mov",
            "-show_data_hash", "sha256", "-show_entries", "stream=" + string.Join(",", keys), "-of", "json", path],
            TimeSpan.FromSeconds(20), token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("streams").EnumerateArray().Select(stream =>
            string.Join("|", keys.Select(key => stream.TryGetProperty(key, out var value) ? value.ToString() : ""))).ToArray();
    }

    internal static string[] NormalizeArguments(string source, string destination, MediaInfo info, int audioTracks)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-xerror", "-n", "-protocol_whitelist", "file", "-f", "mov", "-i", source };
        if (audioTracks > info.AudioTracks) args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo"]);
        args.AddRange(["-map", "0:v:0"]);
        for (int i = 0; i < audioTracks; i++)
            args.AddRange(["-map", i < info.AudioTracks ? FormattableString.Invariant($"0:a:{i}") : "1:a:0"]);
        // MPEG-4 Part 2 and AAC are both available in Meh Tools' bundled FFmpeg without external encoders.
        args.AddRange(["-vf", "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p",
            "-c:v", "mpeg4", "-q:v", "3", "-g", "30", "-bf", "0"]);
        if (audioTracks > 0) args.AddRange(["-af", "apad", "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "160k"]);
        args.AddRange(["-t", info.Duration.ToString("R", CultureInfo.InvariantCulture), "-movflags", "+faststart", destination]);
        return args.ToArray();
    }
}
