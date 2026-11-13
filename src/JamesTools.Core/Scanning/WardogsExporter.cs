using System.Globalization;
using System.Text;
using System.Text.Json;
using JamesTools.Core.Recording;
namespace JamesTools.Core.Scanning;

public enum WardogsExportKind { Montage, Csv, Srt }
public sealed record WardogsPublishedExport(string Path, Guid? SessionId, WardogsExportKind Kind, double RequestedSeconds, double? VerifiedSeconds, string? CleanupWarning = null);
// One transaction owns its source lock and private staging directory until no-overwrite publication.
public sealed class WardogsExporter(MediaTools media)
{
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    public static SavedWardogsScan Capture(SavedWardogsScan scan)
    {
        if (scan.Status != ScanStatus.Completed || scan.Result is null || scan.Fingerprint is null || scan.Reviews is null || scan.Result.Candidates is null) throw new InvalidDataException("Choose a completed scan with source evidence.");
        var copy = scan with { Reviews = scan.Reviews.ToArray(), Result = scan.Result with { Candidates = scan.Result.Candidates.ToArray() } };
        if (!double.IsFinite(copy.Result.Duration) || copy.Result.Duration is < .001 or > 10_000_000 || copy.Result.Candidates.Count > 100000
            || copy.Result.Candidates.Any(c => c is null || c.Id < 1 || !Enum.IsDefined(c.Kind) || !double.IsFinite(c.Seconds) || c.Seconds < 0 || c.Seconds > copy.Result.Duration || c.Text is null || c.Text.Length > 4096 || !double.IsFinite(c.Confidence) || c.Confidence is < 0 or > 1)
            || new[] { copy.Pre, copy.Post, copy.MergeGap }.Any(v => !double.IsFinite(v) || v is < 0 or > 3600)) throw new InvalidDataException("Invalid candidate or interval metadata.");
        var ids = copy.Result.Candidates.Select(c => c.Id).ToHashSet();
        if (ids.Count != copy.Result.Candidates.Count || copy.Reviews.Length > 100000 || copy.Reviews.Any(r => r is null) || copy.Reviews.Select(r => r.CandidateId).Distinct().Count() != copy.Reviews.Length || copy.Reviews.Any(r => !Enum.IsDefined(r.Decision) || r.Label is null || r.Label.Length > 256 || !ids.Contains(r.CandidateId))) throw new InvalidDataException("Invalid review decisions.");
        var kept = copy.Reviews.Where(r => r.Decision == ReviewDecision.Keep).Select(r => r.CandidateId).ToHashSet();
        foreach (var c in copy.Result.Candidates.Where(c => kept.Contains(c.Id)))
            if (!WardogsIntervals.Plan([c], copy.Result.Duration, copy.Pre, copy.Post).Any()) throw new InvalidDataException("A kept candidate has an empty padded window; adjust pre/post seconds.");
        var windows = copy.Intervals();
        if (windows.Count is 0 or > 200) throw new InvalidDataException("Keep at least one candidate; maximum 200 merged windows per export.");
        foreach (var i in windows) MediaTools.ValidateRange(i.Start, i.End, copy.Result.Duration);
        return copy;
    }
    public static async Task<FileStream> VerifiedSourceAsync(SavedWardogsScan scan, CancellationToken token)
    {
        var guard = new FileStream(MediaTools.LocalVideo(scan.Source), FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        try {
            if (scan.Fingerprint is null || await new NativeWardogsScanWork().FingerprintAsync(scan.Source, token) != scan.Fingerprint) throw new IOException("Source missing or changed; rescan before exporting or previewing.");
            return guard;
        } catch { await guard.DisposeAsync(); throw; }
    }
    private static string Clean(string text) => new(text.Select(c => char.IsControl(c) || c is '<' or '>' ? ' ' : c).ToArray());
    private static string Csv(string text) { text = Clean(text); if (text.TrimStart().StartsWith('=') || text.TrimStart().StartsWith('+') || text.TrimStart().StartsWith('-') || text.TrimStart().StartsWith('@')) text = "'" + text; return "\"" + text.Replace("\"", "\"\"") + "\""; }
    private static string Time(double seconds) { var ms = checked((long)Math.Round(seconds * 1000)); return string.Create(CultureInfo.InvariantCulture, $"{ms / 3600000:00}:{ms / 60000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}"); }
    public static string Format(SavedWardogsScan scan, WardogsExportKind kind)
    {
        scan = Capture(scan); var result = new StringBuilder();
        if (kind == WardogsExportKind.Csv) result.AppendLine("Approximate source seconds,Type,Label,OCR evidence,Confidence,Padded source start seconds,Padded source end seconds");
        else if (kind == WardogsExportKind.Srt) result.Append("1\r\n00:00:00,000 --> ").Append(Time(Math.Floor(Math.Min(1, scan.Result!.Duration) * 1000) / 1000)).Append("\r\nSTART OF RECORDING\r\n\r\n");
        else throw new ArgumentException("Choose a text export format.");
        int index = 2; var reviews = scan.Reviews.Where(r => r.Decision == ReviewDecision.Keep).ToDictionary(r => r.CandidateId);
        foreach (var c in scan.Result!.Candidates.Where(c => reviews.ContainsKey(c.Id)).OrderBy(c => c.Seconds).ThenBy(c => c.Id)) {
            var r = reviews[c.Id]; var w = WardogsIntervals.Plan([c], scan.Result.Duration, scan.Pre, scan.Post, 0).Single();
            if (kind == WardogsExportKind.Csv) result.AppendLine(string.Join(",", N(c.Seconds), Csv(c.Kind.ToString()), Csv(r.Label), Csv(c.Text), N(c.Confidence), N(w.Start), N(w.End)));
            else { var end = Math.Floor(w.End * 1000) / 1000; var start = Math.Max(0, Math.Min(Math.Floor(w.Start * 1000) / 1000, end - .001)); if (end <= start) continue; result.Append(index++).Append("\r\n").Append(Time(start)).Append(" --> ").Append(Time(end)).Append("\r\nApproximate ").Append(Clean(c.Kind + " " + r.Label)).Append("\r\n\r\n"); }
        }
        return result.ToString();
    }
    private sealed record Details(double Duration, string[] Streams);
    private async Task<Details> Probe(string path, CancellationToken token, bool packets = false)
    {
        var args = new List<string> { "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-show_format", "-show_streams", "-show_data_hash", "sha256", "-of", "json" };
        if (packets) args.Add("-count_packets"); args.Add(path);
        var json = await media.Execute("ffprobe", args, TimeSpan.FromMinutes(2), token);
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var duration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Unreadable media duration.");
        var streams = root.GetProperty("streams").EnumerateArray().Where(s => s.GetProperty("codec_type").GetString() is "video" or "audio")
            .Where(s => s.GetProperty("codec_type").GetString() != "video" || !s.TryGetProperty("disposition", out var d)
                || !(d.TryGetProperty("attached_pic", out var pic) && pic.GetInt32() == 1 || d.TryGetProperty("timed_thumbnails", out var thumb) && thumb.GetInt32() == 1)).Select(s => {
            string V(string k) => s.TryGetProperty(k, out var v) ? v.ToString() : "";
            if (V("codec_name").Length == 0) throw new InvalidDataException("Unreadable stream codec.");
            if (packets && (!long.TryParse(V("nb_read_packets"), out var count) || count <= 0)) throw new InvalidDataException("Output contains an empty or unreadable video/audio stream.");
            return string.Join("|", new[] { "codec_type", "codec_name", "profile", "width", "height", "pix_fmt", "sample_fmt", "sample_rate", "channels", "channel_layout", "time_base", "extradata_hash" }.Select(V));
        }).ToArray();
        return new(duration, streams);
    }
    public async Task<WardogsPublishedExport> ExportAsync(SavedWardogsScan input, string destination, WardogsExportKind kind, IProgress<string>? progress, CancellationToken token)
    {
        var scan = Capture(input); token.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Invalid export format.");
        if (!Path.IsPathFullyQualified(destination) || destination.StartsWith(@"\\") || destination.Contains("://") || destination.AsSpan(2).Contains(':')) throw new ArgumentException("Choose a local full destination path.");
        destination = Path.GetFullPath(destination);
        if (destination.StartsWith(@"\\")) throw new ArgumentException("Choose a local full destination path.");
        var extension = kind switch { WardogsExportKind.Montage => ".mkv", WardogsExportKind.Csv => ".csv", _ => ".srt" };
        if (Path.GetExtension(destination).ToLowerInvariant() != extension || File.Exists(destination) || Directory.Exists(destination) || string.Equals(destination, scan.Source, StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a new, nonexisting " + extension + " destination.");
        await using var guard = await VerifiedSourceAsync(scan, token);
        var scratch = Path.Combine(Path.GetDirectoryName(destination)!, ".meh-wardogs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(scratch);
        var staged = Path.Combine(scratch, "output" + extension); double? actual = null; var windows = scan.Intervals(); var requested = windows.Sum(w => w.End - w.Start);
        WardogsPublishedExport? published = null; Exception? failure = null;
        try {
            if (kind != WardogsExportKind.Montage) await File.WriteAllTextAsync(staged, Format(scan, kind), new UTF8Encoding(true), token);
            else {
                var source = await Probe(scan.Source, token); var primary = source.Streams.FirstOrDefault(s => s.StartsWith("video|")) ?? throw new InvalidDataException("Source has no video.");
                var expected = new[] { primary }.Concat(source.Streams.Where(s => s.StartsWith("audio|"))).ToArray();
                double sum = 0;
                for (int i = 0; i < windows.Count; i++) {
                    token.ThrowIfCancellationRequested(); progress?.Report($"Copying / verifying window {i + 1}/{windows.Count} (keyframe approximate)");
                    var w = windows[i]; var segment = Path.Combine(scratch, $"part{i:000}.mkv");
                    await media.Execute("ffmpeg", MediaTools.ExportArguments(scan.Source, segment, w.Start, w.End), TimeSpan.FromMinutes(10), token);
                    var info = await Probe(segment, token, true);
                    // Container time bases can change on remux; compare remuxed segments exactly below.
                    if (!info.Streams.Select(WithoutTimeBase).SequenceEqual(expected.Select(WithoutTimeBase))) throw new InvalidDataException("Segment verification failed: codec/audio parameters changed.");
                    if (info.Duration < w.End - w.Start - .5 || Math.Abs(info.Duration - (w.End - w.Start)) > 10) throw new InvalidDataException("Segment duration is truncated or differs by more than 10 seconds; stream-copy cuts unavailable.");
                    if (i == 0) expected = info.Streams; else if (!info.Streams.SequenceEqual(expected)) throw new InvalidDataException("Concat stream parameters differ.");
                    sum += info.Duration;
                }
                await File.WriteAllLinesAsync(Path.Combine(scratch, "concat.txt"), Enumerable.Range(0, windows.Count).Select(i => $"file 'part{i:000}.mkv'"), token);
                await media.Execute("ffmpeg", ["-nostdin", "-v", "error", "-n", "-f", "concat", "-safe", "1", "-protocol_whitelist", "file", "-i", Path.Combine(scratch, "concat.txt"), "-map", "0", "-c", "copy", staged], TimeSpan.FromMinutes(10), token);
                var final = await Probe(staged, token, true);
                if (!final.Streams.SequenceEqual(expected) || Math.Abs(final.Duration - sum) > .5) throw new InvalidDataException("Final montage stream/duration verification failed.");
                await media.Execute("ffmpeg", ["-nostdin", "-v", "error", "-xerror", "-i", staged, "-map", "0", "-c", "copy", "-f", "null", "-"], TimeSpan.FromMinutes(10), token);
                actual = final.Duration;
            }
            token.ThrowIfCancellationRequested(); File.Move(staged, destination, false);
            published = new(destination, scan.SessionId, kind, requested, actual);
        } catch (Exception ex) { failure = ex; throw; }
        finally {
            try { Directory.Delete(scratch, true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) {
                var warning = "Temporary files could not be removed at " + scratch + ": " + cleanup.Message;
                if (published is not null) published = published with { CleanupWarning = warning };
                else if (failure is not null) failure.Data["CleanupWarning"] = warning;
                else throw;
            }
        }
        return published!;
    }
    private static string WithoutTimeBase(string value) { var parts = value.Split('|'); parts[10] = ""; return string.Join('|', parts); }
}
