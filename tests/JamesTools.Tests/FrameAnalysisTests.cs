using JamesTools.Core.Diagnostics;

internal static class FrameAnalysisTests
{
    public static Task Run()
    {
        EffectiveFpsUsesMeanInterval();
        PercentilesUseNearestRank();
        ThresholdsAreStrict();
        InvalidInputsAreIgnored();
        EmptyAndSmallSamples();
        WorstFramesAreRetainedAndOrdered();
        OverflowIsReportedAndPercentilesStayBounded();
        return Task.CompletedTask;
    }

    private static void EffectiveFpsUsesMeanInterval()
    {
        var analysis = new FrameAnalysis();
        analysis.Add(10, 0);
        analysis.Add(40, 0.01);
        var summary = analysis.GetSummary();
        Assert(summary.Count == 2, "frame count was incorrect");
        AssertNear(summary.MeanMs, 25, "mean interval was incorrect");
        AssertNear(summary.EffectiveFps, 40, "effective FPS must use the mean interval");
        Assert(Math.Abs(summary.EffectiveFps - ((1000d / 10 + 1000d / 40) / 2)) > 1,
            "effective FPS matched the mean instantaneous FPS");
    }

    private static void PercentilesUseNearestRank()
    {
        var analysis = new FrameAnalysis();
        for (var i = 1; i <= 100; i++) analysis.Add(i, i);
        var summary = analysis.GetSummary();
        AssertNear(summary.P50Ms!.Value, 50, "P50 did not use nearest rank");
        AssertNear(summary.P95Ms!.Value, 95, "P95 did not use nearest rank");
        AssertNear(summary.P99Ms!.Value, 99, "P99 did not use nearest rank");
        AssertNear(summary.P999Ms!.Value, 100, "P99.9 did not use nearest rank");

        var rounded = new FrameAnalysis();
        rounded.Add(1.01, 0);
        AssertNear(rounded.GetSummary().P50Ms!.Value, 1.1, "percentile did not return bucket upper edge");
    }

    private static void ThresholdsAreStrict()
    {
        var analysis = new FrameAnalysis();
        analysis.Add(33.333, 0);
        analysis.Add(33.334, 1);
        analysis.Add(50, 2);
        analysis.Add(50.001, 3);
        analysis.Add(100, 4);
        analysis.Add(100.001, 5);
        var summary = analysis.GetSummary();
        Assert(summary.Over33Ms == 5, "33.333 ms threshold was not strict");
        Assert(summary.Over50Ms == 3, "50 ms threshold was not strict");
        Assert(summary.Over100Ms == 1, "100 ms threshold was not strict");
    }

    private static void InvalidInputsAreIgnored()
    {
        var analysis = new FrameAnalysis();
        analysis.Add(0, 0);
        analysis.Add(-1, 0);
        analysis.Add(double.NaN, 0);
        analysis.Add(double.PositiveInfinity, 0);
        analysis.Add(10, double.NaN);
        analysis.Add(10, double.NegativeInfinity);
        analysis.Add(10, -0.1);
        var summary = analysis.GetSummary();
        Assert(summary.Count == 0 && summary.Over33Ms == 0 && summary.WorstFrames.Count == 0,
            "invalid samples were retained");
    }

    private static void EmptyAndSmallSamples()
    {
        var empty = new FrameAnalysis().GetSummary();
        Assert(empty.Count == 0 && empty.MeanMs == 0 && empty.EffectiveFps == 0,
            "empty aggregate values were not zero");
        Assert(empty.P50Ms is null && empty.P95Ms is null && empty.P99Ms is null && empty.P999Ms is null,
            "empty percentiles should be null");

        var one = new FrameAnalysis();
        one.Add(16.67, 0.25);
        var summary = one.GetSummary();
        Assert(summary.P50Ms == summary.P95Ms && summary.P95Ms == summary.P99Ms && summary.P99Ms == summary.P999Ms,
            "single-sample percentiles should select that sample");
        Assert(summary.WorstFrames.Count == 1 && summary.WorstFrames[0].OffsetSeconds == 0.25,
            "small sample metadata was not retained");
    }

    private static void WorstFramesAreRetainedAndOrdered()
    {
        var analysis = new FrameAnalysis();
        for (var i = 1; i <= 25; i++) analysis.Add(i, i / 10d);
        var worst = analysis.GetSummary().WorstFrames;
        Assert(worst.Count == 10, "worst-frame retention cap was not enforced");
        for (var i = 0; i < 10; i++)
        {
            AssertNear(worst[i].FrameMs, 25 - i, "worst frames were not sorted descending");
            AssertNear(worst[i].OffsetSeconds, (25 - i) / 10d, "worst-frame offset was not retained");
        }

        var snapshot = analysis.GetSummary();
        analysis.Add(100, 100);
        Assert(snapshot.WorstFrames.Count == 10 && snapshot.WorstFrames[0].FrameMs == 25,
            "previous summary changed after adding a sample");
    }

    private static void OverflowIsReportedAndPercentilesStayBounded()
    {
        var analysis = new FrameAnalysis();
        analysis.Add(10_000, 1);
        analysis.Add(10_000.1, 2);
        var summary = analysis.GetSummary();
        Assert(summary.Count == 2 && summary.OverflowCount == 1, "overflow count was incorrect");
        Assert(summary.P50Ms == 10_000, "in-range percentile was lost due to overflow samples");
        Assert(summary.P95Ms is null && summary.P99Ms is null && summary.P999Ms is null,
            "percentile in overflow bucket should be null");
    }

    private static void AssertNear(double actual, double expected, string message)
    {
        if (Math.Abs(actual - expected) > 1e-9) throw new Exception($"{message}: expected {expected}, got {actual}");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
