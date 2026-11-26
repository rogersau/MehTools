using JamesTools.Core.Recording;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JamesTools.Core.Scanning;

public enum WardogsEventKind { Kill, Headshot, Assist, RevengeKill, ReviveAssist, Revive, Heal, WaitedForRevive }
public enum WardogsOcrEngine { Windows = 1, Legacy = 2 }
public sealed record HudCrop(double X = .65, double Y = 0, double Width = .35, double Height = .35);
public sealed record WardogsScanOptions
{
    public const int DefaultDecodeThreads = 4;
    public const int MaximumDecodeThreads = 16;

    public HudCrop Crop { get; init; } = new();
    public double SampleFps { get; init; } = 2;
    public int DecodeThreads { get; init; } = DefaultDecodeThreads;
    public int ConfirmationSamples { get; init; } = 2;
    public int MissingSamples { get; init; } = 3;
    public int ImageWidth { get; init; } = 960;
    public int MaxSamples { get; init; } = 100000;
    public int MaxCandidates { get; init; } = 10000;
    public double RowTolerance { get; init; } = .035;
    public double ProcessTimeoutSeconds { get; init; } = 30;
    public void Validate()
    {
        static bool Range(double v, double low, double high) => double.IsFinite(v) && v >= low && v <= high;
        if (DecodeThreads is < 1 or > MaximumDecodeThreads)
            throw new ArgumentOutOfRangeException(nameof(DecodeThreads), $"Video decode threads must be between 1 and {MaximumDecodeThreads}.");
        if (Crop is null || !Range(Crop.X, 0, 1) || !Range(Crop.Y, 0, 1) || !Range(Crop.Width, .01, 1) || !Range(Crop.Height, .01, 1) || Crop.X + Crop.Width > 1 || Crop.Y + Crop.Height > 1
            || !Range(SampleFps, .1, 10) || ConfirmationSamples is < 1 or > 20 || MissingSamples is < 1 or > 100 || ImageWidth is < 64 or > 2048
            || MaxSamples is < 1 or > 1000000 || MaxCandidates is < 1 or > 100000 || !Range(RowTolerance, .001, .2) || !Range(ProcessTimeoutSeconds, 1, 300))
            throw new ArgumentException("Invalid WARDOGS sampling, crop, retention or OCR options.");
    }
}
public sealed record OcrRow(string Text, double Y)
{
    public double? X { get; init; }
}
public sealed record WardogsCandidate(int Id, WardogsEventKind Kind, double Seconds, string Text, double? Confidence, double RowY, int Observations)
{
    public long? Reward { get; init; }
    internal bool HasValidConfidence(WardogsOcrEngine engine) => Confidence is >= 0 and <= 1
        || Confidence is null && engine == WardogsOcrEngine.Windows;
}
public sealed record WardogsProgress(int Samples, double Seconds, double Duration)
{
    public TimeSpan? EstimatedRemaining { get; init; }
}
public sealed record WardogsScanResult(string DetectorVersion, string Preset, WardogsScanOptions Options, double Duration, IReadOnlyList<WardogsCandidate> Candidates)
{
    public WardogsOcrEngine OcrEngine { get; init; } = WardogsOcrEngine.Legacy;
    public WardogsBalances? Balances { get; init; }
}

// The adapter owns extraction/OCR. The scanner holds the source read lock.
public interface IWardogsFrames
{
    Task<MediaInfo> ProbeAsync(string source, CancellationToken token);
    IAsyncEnumerable<IReadOnlyList<OcrRow>> ReadSamplesAsync(string source, WardogsScanOptions options, int count, CancellationToken token);
}

public sealed class WardogsScanner(IWardogsFrames frames)
{
    public const string Version = "wardogs-native-3";
    private static readonly SemaphoreSlim Slots = new(2);

    internal static TimeSpan? EstimateRemaining(int samples, int totalSamples, TimeSpan elapsedSinceFirstSample)
    {
        if (samples >= totalSamples) return TimeSpan.Zero;
        if (samples < 3 || elapsedSinceFirstSample < TimeSpan.FromSeconds(2)) return null;
        var secondsPerSample = elapsedSinceFirstSample.TotalSeconds / (samples - 1);
        return TimeSpan.FromSeconds(secondsPerSample * (totalSamples - samples));
    }

    public Task<WardogsScanResult> ScanAsync(string source, WardogsScanOptions? options = null, IProgress<WardogsProgress>? progress = null, CancellationToken token = default)
    {
        var settings = options ?? new(); settings.Validate();
        source = MediaTools.LocalVideo(source);
        return Task.Run(async () =>
        {
            await Slots.WaitAsync(token);
            try
            {
                using var guard = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                var media = await frames.ProbeAsync(source, token);
                if (!double.IsFinite(media.Duration) || media.Duration <= 0 || media.VideoTracks < 1) throw new InvalidDataException("The source must contain video with a finite positive duration.");
                var count = Math.Ceiling(media.Duration * settings.SampleFps);
                if (!double.IsFinite(count) || count > settings.MaxSamples) throw new InvalidDataException("Video exceeds the configured sample limit; lower SampleFps or increase MaxSamples.");
                var tracker = new RowTracker(settings);
                var balance = new WardogsBalanceTracker(settings);
                int samples = 0;
                long firstSample = 0;
                await foreach (var rows in frames.ReadSamplesAsync(source, settings, (int)count, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (samples == (int)count) throw new InvalidDataException("Frame source exceeded the requested samples.");
                    if (rows.Count > 256) throw new InvalidDataException("OCR returned too many rows.");
                    double seconds = samples / settings.SampleFps;
                    tracker.Observe(rows, seconds);
                    balance.Observe(rows, seconds);
                    samples++;
                    if (samples == 1) firstSample = Stopwatch.GetTimestamp();
                    progress?.Report(new(samples, seconds, media.Duration)
                    {
                        EstimatedRemaining = EstimateRemaining(samples, (int)count, Stopwatch.GetElapsedTime(firstSample))
                    });
                }
                token.ThrowIfCancellationRequested();
                if (samples != (int)count) throw new InvalidDataException("Frame source did not supply all requested samples.");
                return new WardogsScanResult(Version, "Wardogs top-right", settings, media.Duration, tracker.Results.ToArray())
                {
                    Balances = balance.Balances, OcrEngine = WardogsOcrEngine.Windows
                };
            }
            finally { Slots.Release(); }
        }, token);
    }
}

internal static partial class WardogsText
{
    [GeneratedRegex(@"\b(?:(?<headshot>HEAD\s*SHOT\s*KILL)|(?<revenge>REVENGE\s*KILL)|(?<assist>KILL\s*ASSIST)|(?<reviveAssist>REVIVED\s*PLAYER\s*ASSIST)|(?<revive>REVIVED\s*TEAMMATE)|(?<heal>HEALED\s*TEAMMATE)|(?<waited>WAITED\s*TO\s*BE\s*REVIVED)|KILL)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventPattern();
    [GeneratedRegex(@"\b(?:TEAM\s*KILL|NO\s+VALUE\s+KILL)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExcludedKill();
    [GeneratedRegex(@"\b(?:[0-9]+\s*XP|EXP|XP)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExperienceReward();
    internal static WardogsEventKind? Parse(string text)
    {
        if (ExcludedKill().IsMatch(text)) return null;
        var match = EventPattern().Match(text);
        if (!match.Success) return null;
        if (match.Groups["headshot"].Success) return WardogsEventKind.Headshot;
        if (match.Groups["revenge"].Success) return WardogsEventKind.RevengeKill;
        if (match.Groups["assist"].Success) return WardogsEventKind.Assist;
        if (match.Groups["reviveAssist"].Success) return WardogsEventKind.ReviveAssist;
        if (match.Groups["revive"].Success) return WardogsEventKind.Revive;
        if (match.Groups["heal"].Success) return WardogsEventKind.Heal;
        if (match.Groups["waited"].Success) return WardogsEventKind.WaitedForRevive;
        return WardogsEventKind.Kill;
    }
    internal static string Identity(string text)
    {
        var category = EventPattern().Match(text);
        // Payout text changes from cash to XP and may vanish briefly as the row animates.
        var suffix = text[(category.Index + category.Length)..];
        suffix = ExperienceReward().Replace(WardogsMoney.Currency().Replace(suffix, " "), " ");
        return Regex.Replace((text[..category.Index] + suffix).ToUpperInvariant(), @"[^\p{L}\p{N}]", "");
    }
    internal static bool IsKill(WardogsEventKind kind) => kind is WardogsEventKind.Kill or WardogsEventKind.Headshot or WardogsEventKind.RevengeKill;
    internal static bool MatchKind(WardogsEventKind first, WardogsEventKind second) => first == second || IsKill(first) && IsKill(second);
    internal static WardogsEventKind Refine(WardogsEventKind previous, WardogsEventKind current) =>
        current == WardogsEventKind.Headshot || previous == WardogsEventKind.Kill && current == WardogsEventKind.RevengeKill ? current : previous;

    internal static long? ReadReward(string text)
    {
        var category = EventPattern().Match(text);
        return category.Success && WardogsMoney.TryAmount(WardogsMoney.Currency().Match(text, category.Index + category.Length), out var amount)
            && amount > 0 ? amount : null;
    }
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
        public long? Reward;
    }
    private readonly List<Track> tracks = [];
    internal List<WardogsCandidate> Results { get; } = [];
    internal void Observe(IReadOnlyList<OcrRow> rows, double seconds)
    {
        var used = new HashSet<Track>();
        foreach (var row in rows.OrderBy(r => r.Y))
        {
            if (row.Text is null || row.Text.Length > 4096 || row.Y is not (>= 0 and <= 1)) continue;
            var kind = WardogsText.Parse(row.Text); if (kind is null) continue;
            string identity = WardogsText.Identity(row.Text);
            var reward = WardogsText.ReadReward(row.Text);
            // A named row may move. Bare identical messages only match near the previous position.
            var track = tracks.Where(t => !used.Contains(t) && t.Identity == identity && WardogsText.MatchKind(t.Kind, kind.Value)
                && (identity.Length > 0 || Math.Abs(t.Row.Y - row.Y) <= options.RowTolerance))
                .OrderBy(t => Math.Abs(t.Row.Y - row.Y)).FirstOrDefault();
            if (track is null) { track = new() { Identity = identity, Row = row, Kind = kind.Value, First = seconds }; tracks.Add(track); }
            track.Reward ??= reward;
            used.Add(track); track.Missing = 0; track.Hits++;
            track.Kind = WardogsText.Refine(track.Kind, kind.Value);
            track.Row = row;
            if (track.Hits >= options.ConfirmationSamples)
            {
                if (track.Result is null)
                {
                    if (Results.Count >= options.MaxCandidates) throw new InvalidDataException("Candidate retention limit reached.");
                    track.Result = Results.Count;
                    Results.Add(new(Results.Count + 1, track.Kind, track.First, row.Text, null, row.Y, track.Hits) { Reward = track.Reward });
                }
                else
                {
                    var previous = Results[track.Result.Value];
                    Results[track.Result.Value] = previous with { Kind = track.Kind, Text = row.Text, RowY = row.Y, Observations = track.Hits, Reward = track.Reward };
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
