using JamesTools.Core.Recording;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JamesTools.Core.Scanning;

public enum WardogsEventKind { Kill, Headshot, Assist }
public sealed record HudCrop(double X = .65, double Y = 0, double Width = .35, double Height = .35);
public sealed record WardogsScanOptions
{
    public HudCrop Crop { get; init; } = new();
    public double SampleFps { get; init; } = 2;
    public int ConfirmationSamples { get; init; } = 2;
    public int MissingSamples { get; init; } = 3;
    public int ImageWidth { get; init; } = 960;
    public int MaxSamples { get; init; } = 100000;
    public int MaxCandidates { get; init; } = 10000;
    public double MinimumOcrConfidence { get; init; } = 35;
    public double RowTolerance { get; init; } = .035;
    public double ProcessTimeoutSeconds { get; init; } = 30;
    public void Validate()
    {
        static bool Range(double v, double low, double high) => double.IsFinite(v) && v >= low && v <= high;
        if (Crop is null || !Range(Crop.X, 0, 1) || !Range(Crop.Y, 0, 1) || !Range(Crop.Width, .01, 1) || !Range(Crop.Height, .01, 1) || Crop.X + Crop.Width > 1 || Crop.Y + Crop.Height > 1
            || !Range(SampleFps, .1, 10) || ConfirmationSamples is < 1 or > 20 || MissingSamples is < 1 or > 100 || ImageWidth is < 64 or > 2048
            || MaxSamples is < 1 or > 1000000 || MaxCandidates is < 1 or > 100000 || !Range(MinimumOcrConfidence, 0, 100) || !Range(RowTolerance, .001, .2) || !Range(ProcessTimeoutSeconds, 1, 300))
            throw new ArgumentException("Invalid WARDOGS sampling, crop, retention or OCR options.");
    }
}
public sealed record OcrRow(string Text, double Y, double Confidence);
public sealed record WardogsCandidate(int Id, WardogsEventKind Kind, double Seconds, string Text, double Confidence, double RowY, int Observations);
public sealed record WardogsProgress(int Samples, double Seconds, double Duration);
public sealed record WardogsScanResult(string DetectorVersion, string Preset, WardogsScanOptions Options, double Duration, IReadOnlyList<WardogsCandidate> Candidates);

// The adapter owns extraction/OCR; the scanner owns its scratch directory and source read lock.
public interface IWardogsFrames
{
    Task<MediaInfo> ProbeAsync(string source, CancellationToken token);
    Task<IReadOnlyList<OcrRow>> ReadAsync(string source, double seconds, string scratchDirectory, WardogsScanOptions options, CancellationToken token);
}

public sealed class WardogsScanner(IWardogsFrames frames)
{
    public const string Version = "wardogs-native-1";
    private static readonly SemaphoreSlim Slots = new(2);
    public Task<WardogsScanResult> ScanAsync(string source, WardogsScanOptions? options = null, IProgress<WardogsProgress>? progress = null, CancellationToken token = default)
    {
        var settings = options ?? new(); settings.Validate();
        source = MediaTools.LocalVideo(source);
        return Task.Run(async () =>
        {
            await Slots.WaitAsync(token);
            string? scratch = null;
            try
            {
                using var guard = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                var media = await frames.ProbeAsync(source, token);
                if (!double.IsFinite(media.Duration) || media.Duration <= 0 || media.VideoTracks < 1) throw new InvalidDataException("The source must contain video with a finite positive duration.");
                var count = Math.Ceiling(media.Duration * settings.SampleFps);
                if (!double.IsFinite(count) || count > settings.MaxSamples) throw new InvalidDataException("Video exceeds the configured sample limit; lower SampleFps or increase MaxSamples.");
                scratch = Path.Combine(Path.GetTempPath(), "meh-wardogs-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                var tracker = new RowTracker(settings);
                for (int i = 0; i < (int)count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double seconds = i / settings.SampleFps;
                    var rows = await frames.ReadAsync(source, seconds, scratch, settings, token);
                    if (rows.Count > 256) throw new InvalidDataException("OCR returned too many rows.");
                    tracker.Observe(rows, seconds);
                    progress?.Report(new(i + 1, seconds, media.Duration));
                }
                token.ThrowIfCancellationRequested();
                return new WardogsScanResult(Version, "Wardogs top-right (unverified preset)", settings, media.Duration, tracker.Results.ToArray());
            }
            finally
            {
                try { if (scratch is not null) Directory.Delete(scratch, true); }
                finally { Slots.Release(); }
            }
        }, token);
    }
}

internal static partial class WardogsText
{
    [GeneratedRegex(@"\b(?:HEAD\s*SHOT\s*KILL|KILL\s*ASSIST|KILL)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventPattern();
    internal static WardogsEventKind? Parse(string text)
    {
        var match = EventPattern().Match(text);
        if (!match.Success) return null;
        return match.Value.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase) ? WardogsEventKind.Headshot : match.Value.Contains("ASSIST", StringComparison.OrdinalIgnoreCase) ? WardogsEventKind.Assist : WardogsEventKind.Kill;
    }
    internal static string Identity(string text) => Regex.Replace(EventPattern().Replace(text.ToUpperInvariant(), " "), @"[^\p{L}\p{N}]", "");
}

internal sealed class RowTracker(WardogsScanOptions options)
{
    private sealed class Track
    {
        public required string Identity;
        public required OcrRow Row;
        public WardogsEventKind Kind;
        public double First;
        public int Hits;
        public int Missing;
        public int? Result;
    }
    private readonly List<Track> tracks = [];
    internal List<WardogsCandidate> Results { get; } = [];
    internal void Observe(IReadOnlyList<OcrRow> rows, double seconds)
    {
        var used = new HashSet<Track>();
        foreach (var row in rows.OrderBy(r => r.Y))
        {
            if (row.Text is null || row.Text.Length > 4096 || !double.IsFinite(row.Y) || row.Y < 0 || row.Y > 1 || !double.IsFinite(row.Confidence) || row.Confidence < options.MinimumOcrConfidence || row.Confidence > 100) continue;
            var kind = WardogsText.Parse(row.Text); if (kind is null) continue;
            string identity = WardogsText.Identity(row.Text);
            // A named row may move. Bare identical messages only match near the previous position.
            var track = tracks.Where(t => !used.Contains(t) && t.Identity == identity && (t.Kind == kind || t.Kind != WardogsEventKind.Assist && kind != WardogsEventKind.Assist)
                && (identity.Length > 0 || Math.Abs(t.Row.Y - row.Y) <= options.RowTolerance))
                .OrderBy(t => Math.Abs(t.Row.Y - row.Y)).FirstOrDefault();
            if (track is null) { track = new() { Identity = identity, Row = row, Kind = kind.Value, First = seconds }; tracks.Add(track); }
            used.Add(track); track.Missing = 0; track.Hits++;
            if (kind == WardogsEventKind.Headshot) track.Kind = kind.Value;
            track.Row = row;
            if (track.Hits >= options.ConfirmationSamples)
            {
                if (track.Result is null)
                {
                    if (Results.Count >= options.MaxCandidates) throw new InvalidDataException("Candidate retention limit reached.");
                    track.Result = Results.Count;
                    Results.Add(new(Results.Count + 1, track.Kind, track.First, row.Text, row.Confidence / 100, row.Y, track.Hits));
                }
                else
                {
                    var previous = Results[track.Result.Value];
                    Results[track.Result.Value] = previous with { Kind = track.Kind, Text = row.Text, Confidence = Math.Max(previous.Confidence, row.Confidence / 100), RowY = row.Y, Observations = track.Hits };
                }
            }
        }
        foreach (var track in tracks.Where(t => !used.Contains(t))) track.Missing++;
        tracks.RemoveAll(t => t.Missing >= options.MissingSamples);
    }
}

public sealed record HighlightInterval(double Start, double End);
public static class WardogsIntervals
{
    public static IReadOnlyList<HighlightInterval> Plan(IEnumerable<WardogsCandidate> selected, double duration, double pre = 10, double post = 5, double mergeGap = 0)
    {
        if (!double.IsFinite(duration) || duration <= 0 || new[] { pre, post, mergeGap }.Any(v => !double.IsFinite(v) || v < 0)) throw new ArgumentException("Invalid interval settings.");
        var windows = selected.Select(c =>
        {
            if (!double.IsFinite(c.Seconds) || c.Seconds < 0 || c.Seconds > duration) throw new ArgumentException("Candidate is outside the video.");
            return new HighlightInterval(Math.Max(0, c.Seconds - pre), Math.Min(duration, c.Seconds + post));
        }).Where(w => w.End > w.Start).OrderBy(w => w.Start);
        var result = new List<HighlightInterval>();
        foreach (var window in windows)
        {
            if (result.Count > 0 && window.Start - result[^1].End <= mergeGap) result[^1] = result[^1] with { End = Math.Max(result[^1].End, window.End) };
            else result.Add(window);
        }
        return result;
    }
}
