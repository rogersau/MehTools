using JamesTools.Core.Recording;
using JamesTools.Core.Scanning;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class WardogsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    // Labelled synthetic HUD sequences; these do not establish real-game accuracy.
    private sealed class Frames(params OcrRow[][] sequence) : IWardogsFrames
    {
        public int Calls;
        public bool Fail;
        public CancellationTokenSource? Cancel;
        public Task<MediaInfo> ProbeAsync(string source, CancellationToken token) => Task.FromResult(new MediaInfo(sequence.Length, 1, 0));
        public async IAsyncEnumerable<IReadOnlyList<OcrRow>> ReadSamplesAsync(string source, WardogsScanOptions options, int count, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.CompletedTask;
            for (int i = 0; i < count; i++)
            {
                Calls++;
                if (Fail) throw new IOException("synthetic failure");
                Cancel?.Cancel();
                yield return sequence[(int)(i / options.SampleFps)];
            }
        }
    }
    private static OcrRow Row(string text, double y = .1) => new(text, y);
    public static async Task Run()
    {
        string source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"); File.WriteAllText(source, "synthetic placeholder; fake adapter only");
        var options = new WardogsScanOptions { SampleFps = 1 };
        try
        {
            Check(WardogsScanner.EstimateRemaining(1, 20, TimeSpan.Zero) is null, "first sample has no measured rate");
            Check(WardogsScanner.EstimateRemaining(3, 20, TimeSpan.FromSeconds(1)) is null, "ETA waits for a measured pace");
            Check(WardogsScanner.EstimateRemaining(6, 20, TimeSpan.FromSeconds(5)) == TimeSpan.FromSeconds(14), "ETA uses remaining samples and measured scan rate");
            Check(WardogsScanner.EstimateRemaining(20, 20, TimeSpan.FromSeconds(19)) == TimeSpan.Zero, "ETA ends when sampling finishes");
            var legacy = JsonSerializer.Deserialize<WardogsScanOptions>("{\"SampleFps\":1}")!;
            Check(legacy.DecodeThreads == WardogsScanOptions.DefaultDecodeThreads, "saved scans without a thread count must use the default");
            async Task<WardogsScanResult> Scan(Frames frames, WardogsScanOptions? settings = null)
            {
                var result = await new WardogsScanner(frames).ScanAsync(source, settings ?? options);
                return result;
            }
            var positive = await Scan(new([Row("KILL Alice")], [Row("HEAD SHOT KILL Alice"), Row("KILL ASSIST Bob", .3)], [Row("HEADSHOT KILL Alice"), Row("KILL ASSIST Bob", .3)], [Row("HEADSHOT KILL Alice")]));
            Check(positive.Candidates.Count == 2 && positive.Candidates[0].Kind == WardogsEventKind.Headshot && positive.Candidates[1].Kind == WardogsEventKind.Assist, "typed/refined events");
            Check(positive.Candidates[0].Seconds == 0 && positive.Candidates[1].Seconds == 1 && positive.DetectorVersion == WardogsScanner.Version, "source times/metadata");
            var support = await Scan(new([Row("REVIVED TEAMMATE +$900"), Row("HEALED TEAMMATE 45XP", .3), Row("REVIVED PLAYER ASSIST +$5", .5), Row("WAITED TO BE REVIVED +$200", .7)],
                [Row("REVIVED TEAMMATE 250XP"), Row("HEALED TEAMMATE 45XP", .3), Row("REVIVED PLAYER ASSIST 8XP", .5), Row("WAITED TO BE REVIVED +$200", .7)],
                [Row("REVIVED TEAMMATE 250XP"), Row("HEALED TEAMMATE 45XP", .3), Row("REVIVED PLAYER ASSIST 8XP", .5), Row("WAITED TO BE REVIVED +$200", .7)]));
            Check(support.Candidates.Select(c => c.Kind).SequenceEqual([WardogsEventKind.Revive, WardogsEventKind.Heal, WardogsEventKind.ReviveAssist, WardogsEventKind.WaitedForRevive]), "classify support notifications and keep each reward through the XP swap");
            Check(WardogsMoneySummary.ForRecording(support).DetectedRewards == 1105, "cash-to-XP transitions count rewards once");
            var revenge = await Scan(new([Row("REVENGE KILL +$4,500")], [Row("REVENGE KILL 300XP")], [Row("REVENGE KILL 300XP")]));
            Check(revenge.Candidates.Single() is { Kind: WardogsEventKind.RevengeKill, Reward: 4500 }, "revenge kill retains its payout");
            var flickeringReward = await Scan(new([Row("REVIVED PLAYER ASSIST +$5")], [Row("REVIVED PLAYER ASSIST")],
                [Row("REVIVED PLAYER ASSIST exp +")], [Row("REVIVED PLAYER ASSIST 8XP")], [Row("REVIVED PLAYER ASSIST +$5")]));
            Check(flickeringReward.Candidates.Single() is { Observations: 5, Reward: 5 }, "flickering cash and experience labels are one event");
            OcrRow Balance(string text, double x) => Row(text, .025 / options.Crop.Height) with { X = (x - options.Crop.X) / options.Crop.Width };
            var menu = Balance("$1,020,994 11 71", .805);
            var match = Balance("−$12,510 $1,008,484", .88);
            var allowance = Balance("$10,000", .944);
            var money = await Scan(new([allowance], [allowance], [menu], [menu], [match], [match], [Balance("-$500 $8,888,888", .88)], []));
            Check(money.Balances == new WardogsBalances(new(1_020_994, 2), new(1_008_484, 5)), "confirm menu and match balances and ignore lone allowance and one-frame OCR errors");
            var split = await Scan(new([Balance("-$12,510", .88), Balance("$1,008,484", .945)], [Balance("-$12,510", .88), Balance("$1,008,484", .945)]));
            Check(split.Balances?.Closing.Amount == 1_008_484, "recognize the match balance when OCR splits the pair");
            var negative = await Scan(new([Row("SKILL"), Row("TEAM KILL -$2000", .5), Row("NO VALUE KILL", .7)],
                [Row("HEADSHOT"), Row("TEAM KILL -$2000", .5), Row("NO VALUE KILL", .7)]));
            Check(negative.Candidates.Count == 0, "negative labels");
            var adjacent = await Scan(new([Row("KILL A")], [Row("KILL A", .3), Row("KILL B")], [Row("KILL A", .3), Row("KILL B")], [], [Row("KILL B")]));
            Check(adjacent.Candidates.Count == 2, "row shift, nearby events and flicker");
            var duplicates = await Scan(new([Row("KILL"), Row("KILL", .3)], [Row("KILL"), Row("KILL", .3)], [], [], [], [Row("KILL")], [Row("KILL")]));
            Check(duplicates.Candidates.Count == 3, "duplicate positions and reappearance");
            foreach (var invalid in new[] { options with { SampleFps = double.NaN }, options with { Crop = new(.9, 0, .2, .2) }, options with { ConfirmationSamples = 0 }, options with { ProcessTimeoutSeconds = double.PositiveInfinity }, options with { DecodeThreads = 0 }, options with { DecodeThreads = WardogsScanOptions.MaximumDecodeThreads + 1 } })
                await Throws<ArgumentException>(() => new WardogsScanner(new Frames([])).ScanAsync(source, invalid));
            var failure = new Frames(Array.Empty<OcrRow>()) { Fail = true };
            await Throws<IOException>(() => new WardogsScanner(failure).ScanAsync(source, options));
            using var cancel = new CancellationTokenSource(); var cancelled = new Frames([], []) { Cancel = cancel };
            await Throws<OperationCanceledException>(() => new WardogsScanner(cancelled).ScanAsync(source, options, token: cancel.Token));
            Check(cancelled.Calls == 1, "cancellation stops sampling");
            await Throws<InvalidDataException>(() => new WardogsScanner(new Frames([], [])).ScanAsync(source, options with { MaxSamples = 1 }));
            var intervals = WardogsIntervals.Plan([new(1, WardogsEventKind.Kill, 1, "", 1, 0, 1), new(2, WardogsEventKind.Kill, 18, "", 1, 0, 1)], 20, mergeGap: 2);
            Check(intervals.Count == 1 && intervals[0] == new HighlightInterval(0, 20), "interval clamp/merge");

        }
        finally { File.Delete(source); }
    }

}
