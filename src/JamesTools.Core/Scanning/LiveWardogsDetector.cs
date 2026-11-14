using System.Collections.Immutable;
using System.Diagnostics;

namespace JamesTools.Core.Scanning;

// Source returns only its latest owned, immutable cropped grayscale buffer; never a queue.
public sealed record HudFrame(string Source, long Epoch, TimeSpan Timestamp, int Width, int Height, int Stride, byte[] Gray)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Source) || Source.Length > 256 || Epoch < 0 || Timestamp < TimeSpan.Zero || Width is < 16 or > 1024 || Height is < 16 or > 512 || Stride < Width || Stride > 2048 || Gray is null || Gray.Length != checked(Stride * Height))
            throw new InvalidDataException("Invalid bounded grayscale HUD frame.");
    }
}
public interface ILatestHudFrameSource { ValueTask<HudFrame?> LatestAsync(CancellationToken token); }
public interface IHudOcr : IAsyncDisposable { double WorkerCpuMilliseconds => 0; Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, CancellationToken token); }
public sealed record LiveWardogsOptions(double SamplesPerSecond = 2, double DutyBudget = .25, double RecheckSeconds = 2, double ResetGapSeconds = 4)
{
    internal void Validate()
    {
        if (!double.IsFinite(SamplesPerSecond) || SamplesPerSecond is < .25 or > 4 || !double.IsFinite(DutyBudget) || DutyBudget is < .01 or > 1 || !double.IsFinite(RecheckSeconds) || RecheckSeconds is < .25 or > 4 || !double.IsFinite(ResetGapSeconds) || ResetGapSeconds is < 2 or > 30) throw new ArgumentException("Invalid live pacing options.");
    }
}
public sealed record LiveKill(long Id, WardogsEventKind Kind, TimeSpan Timestamp);
public sealed record LiveWardogsSnapshot(long Samples, long OcrCalls, long Stale, long Unchanged, double SampleRate, double OcrRate, double OcrMilliseconds, double EndToEndMilliseconds, bool Throttled, ImmutableArray<LiveKill> Events, double WorkerCpuMilliseconds, double WorkerCpuElapsedSeconds, long Kills, long Headshots, long Assists);

public sealed class LiveWardogsDetector(ILatestHudFrameSource source, IHudOcr ocr, LiveWardogsOptions? options = null, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private int running;
    private sealed class Track(string key, double y, WardogsEventKind kind, bool baseline)
    {
        internal string Key = key; internal double Y = y; internal WardogsEventKind Kind = kind;
        internal bool Baseline = baseline; internal int Hits; internal int Missing; internal long? Id;
        internal TimeSpan First;
    }
    // Run owns OCR for its lifetime. Cancellation is Stop; terminal errors propagate, never restart.
    public async Task RunAsync(Action<LiveWardogsSnapshot> publish, CancellationToken token)
    {
        var settings = options ?? new(); settings.Validate();
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("Already running.");
        var tracks = new List<Track>(); var history = new List<LiveKill>();
        HudFrame? previous = null; byte[]? image = null; TimeSpan lastOcr = TimeSpan.MinValue;
        long samples = 0, calls = 0, stale = 0, unchanged = 0, id = 0, kills = 0, headshots = 0, assists = 0;
        double latency = 0;
        double initialCpu = ocr.WorkerCpuMilliseconds;
        var start = clock.GetTimestamp();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested(); var tick = clock.GetTimestamp();
                var frame = await source.LatestAsync(token);
                if (frame is not null)
                {
                    frame.Validate(); samples++;
                    bool reset = previous is null || frame.Source != previous.Source || frame.Epoch != previous.Epoch || frame.Width != previous.Width || frame.Height != previous.Height || frame.Stride != previous.Stride;
                    if (!reset && frame.Timestamp <= previous!.Timestamp) stale++;
                    else
                    {
                        reset |= previous is not null && frame.Timestamp - previous.Timestamp > TimeSpan.FromSeconds(settings.ResetGapSeconds);
                        if (reset) { tracks.Clear(); image = null; }
                        bool pending = tracks.Any(t => t.Hits < 2 || t.Missing > 0);
                        if (!reset && !pending && image is not null && frame.Gray.AsSpan().SequenceEqual(image) && frame.Timestamp - lastOcr < TimeSpan.FromSeconds(settings.RecheckSeconds)) unchanged++;
                        else
                        {
                            var began = clock.GetTimestamp(); var rows = await ocr.ReadAsync(frame, token);
                            latency = clock.GetElapsedTime(began).TotalMilliseconds; calls++;
                            if (rows.Count > 256) throw new InvalidDataException("Too many OCR rows.");
                            var used = new HashSet<Track>();
                            foreach (var row in rows)
                            {
                                if (row.Text is null || row.Text.Length > 4096 || !double.IsFinite(row.Y) || row.Y is < 0 or > 1 || !double.IsFinite(row.Confidence) || row.Confidence is < 35 or > 100) continue;
                                var kind = WardogsText.Parse(row.Text); if (kind is null) continue;
                                var key = WardogsText.Identity(row.Text);
                                var track = tracks.Where(t => !used.Contains(t) && t.Key == key && (t.Kind == kind || t.Kind != WardogsEventKind.Assist && kind != WardogsEventKind.Assist) && (key.Length > 0 || Math.Abs(t.Y - row.Y) <= .035)).OrderBy(t => Math.Abs(t.Y - row.Y)).FirstOrDefault();
                                if (track is null) { if (tracks.Count >= 256) continue; track = new(key, row.Y, kind.Value, reset) { First = frame.Timestamp }; tracks.Add(track); }
                                used.Add(track); track.Y = row.Y; track.Missing = 0; track.Hits = Math.Min(2, track.Hits + 1);
                                var priorKind = track.Kind;
                                if (kind == WardogsEventKind.Headshot) track.Kind = kind.Value;
                                if (track.Hits >= 2 && !track.Baseline)
                                {
                                    if (track.Id is null) {
                                        track.Id = ++id; history.Add(new(id, track.Kind, track.First)); if (history.Count > 256) history.RemoveAt(0);
                                        if (track.Kind == WardogsEventKind.Assist) assists++; else { kills++; if (track.Kind == WardogsEventKind.Headshot) headshots++; }
                                    }
                                    else { if (priorKind != WardogsEventKind.Headshot && track.Kind == WardogsEventKind.Headshot) headshots++; int index = history.FindIndex(e => e.Id == track.Id); if (index >= 0) history[index] = history[index] with { Kind = track.Kind }; }
                                }
                            }
                            foreach (var track in tracks.Where(t => !used.Contains(t))) track.Missing++;
                            tracks.RemoveAll(t => t.Missing >= 3);
                            image = (byte[])frame.Gray.Clone(); lastOcr = frame.Timestamp;
                        }
                        previous = frame with { Gray = [] };
                    }
                }
                double work = clock.GetElapsedTime(tick).TotalSeconds;
                double interval = Math.Max(1 / settings.SamplesPerSecond, work / settings.DutyBudget);
                double elapsed = Math.Max(.001, clock.GetElapsedTime(start).TotalSeconds);
                publish(new(samples, calls, stale, unchanged, samples / elapsed, calls / elapsed, latency, work * 1000, interval > 1 / settings.SamplesPerSecond, history.ToImmutableArray(), Math.Max(0, ocr.WorkerCpuMilliseconds - initialCpu), elapsed, kills, headshots, assists));
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, interval - work)), clock, token);
            }
        }
        finally { try { await ocr.DisposeAsync(); } finally { Volatile.Write(ref running, 0); } }
    }
}
