namespace JamesTools.Core.Diagnostics;

/// <summary>Bounded summary statistics for frame intervals in one presentation stream.</summary>
internal sealed class FrameAnalysis
{
    private const double HistogramResolutionMs = 0.1;
    private const double HistogramMaximumMs = 10_000;
    private const int OverflowBucket = 100_001;
    private const int WorstFrameLimit = 10;

    // Sparse and allocated only after the first valid sample. The key space is fixed (100,002 buckets).
    private Dictionary<int, long>? _histogram;
    private readonly List<FrameRecord> _worstFrames = new(WorstFrameLimit);
    private long _count;
    private long _over33Ms;
    private long _over50Ms;
    private long _over100Ms;
    private long _overflowCount;
    private double _meanMs;

    public void Add(double frameMs, double elapsedSeconds)
    {
        if (!double.IsFinite(frameMs) || frameMs <= 0 ||
            !double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            return;

        _count++;
        // This weighted form avoids overflowing an intermediate (frameMs - meanMs).
        _meanMs = _meanMs * ((_count - 1d) / _count) + frameMs / _count;
        if (frameMs > 33.333) _over33Ms++;
        if (frameMs > 50) _over50Ms++;
        if (frameMs > 100) _over100Ms++;

        int bucket;
        if (frameMs > HistogramMaximumMs)
        {
            bucket = OverflowBucket;
            _overflowCount++;
        }
        else
        {
            bucket = Math.Max(1, (int)Math.Ceiling(frameMs / HistogramResolutionMs));
        }
        _histogram ??= new Dictionary<int, long>();
        _histogram.TryGetValue(bucket, out var bucketCount);
        _histogram[bucket] = bucketCount + 1;

        var record = new FrameRecord(elapsedSeconds, frameMs);
        if (_worstFrames.Count < WorstFrameLimit)
        {
            _worstFrames.Add(record);
            _worstFrames.Sort(static (a, b) => b.FrameMs.CompareTo(a.FrameMs));
        }
        else if (frameMs > _worstFrames[^1].FrameMs)
        {
            _worstFrames[^1] = record;
            _worstFrames.Sort(static (a, b) => b.FrameMs.CompareTo(a.FrameMs));
        }
    }

    public FrameAnalysisSummary GetSummary()
    {
        var effectiveFps = _count == 0 ? 0 : 1000 / _meanMs;
        var (p50, p95, p99, p999) = Percentiles();
        return new FrameAnalysisSummary(
            _count,
            _meanMs,
            effectiveFps,
            p50,
            p95,
            p99,
            p999,
            _over33Ms,
            _over50Ms,
            _over100Ms,
            _worstFrames.ToArray(),
            _overflowCount);
    }

    private (double? P50, double? P95, double? P99, double? P999) Percentiles()
    {
        if (_count == 0 || _histogram is null) return (null, null, null, null);

        // Nearest rank: rank = ceil(p * N), with ranks starting at one.
        var p50Rank = (long)Math.Ceiling(0.50 * _count);
        var p95Rank = (long)Math.Ceiling(0.95 * _count);
        var p99Rank = (long)Math.Ceiling(0.99 * _count);
        var p999Rank = (long)Math.Ceiling(0.999 * _count);
        double? p50 = null, p95 = null, p99 = null, p999 = null;
        long cumulative = 0;
        foreach (var (bucket, count) in _histogram.OrderBy(pair => pair.Key))
        {
            cumulative += count;
            if (p50 is null && cumulative >= p50Rank) p50 = PercentileValue(bucket);
            if (p95 is null && cumulative >= p95Rank) p95 = PercentileValue(bucket);
            if (p99 is null && cumulative >= p99Rank) p99 = PercentileValue(bucket);
            if (p999 is null && cumulative >= p999Rank) p999 = PercentileValue(bucket);
            if (p50 is not null && p95 is not null && p99 is not null && p999 is not null) break;
        }

        return (p50, p95, p99, p999);
    }

    private static double? PercentileValue(int bucket) =>
        bucket == OverflowBucket ? null : bucket * HistogramResolutionMs;
}

internal sealed record FrameRecord(double OffsetSeconds, double FrameMs);

internal sealed record FrameAnalysisSummary(
    long Count,
    double MeanMs,
    double EffectiveFps,
    double? P50Ms,
    double? P95Ms,
    double? P99Ms,
    double? P999Ms,
    long Over33Ms,
    long Over50Ms,
    long Over100Ms,
    IReadOnlyList<FrameRecord> WorstFrames,
    long OverflowCount);
