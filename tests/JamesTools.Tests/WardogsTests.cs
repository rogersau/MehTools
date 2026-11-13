using JamesTools.Core.Recording;
using JamesTools.Core.Scanning;
using System.Security.Cryptography;

internal static class WardogsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    // Labelled synthetic HUD sequences; these do not establish real-game accuracy.
    private sealed class Frames(params OcrRow[][] sequence) : IWardogsFrames
    {
        public string? Scratch;
        public int Calls;
        public bool Fail;
        public CancellationTokenSource? Cancel;
        public Task<MediaInfo> ProbeAsync(string source, CancellationToken token) => Task.FromResult(new MediaInfo(sequence.Length, 1, 0));
        public Task<IReadOnlyList<OcrRow>> ReadAsync(string source, double seconds, string scratchDirectory, WardogsScanOptions options, CancellationToken token)
        {
            Scratch = scratchDirectory; File.WriteAllText(Path.Combine(scratchDirectory, "synthetic.txt"), "fixture");
            Calls++; if (Fail) throw new IOException("synthetic failure");
            Cancel?.Cancel();
            return Task.FromResult<IReadOnlyList<OcrRow>>(sequence[(int)seconds]);
        }
    }
    private static OcrRow Row(string text, double y = .1, double confidence = 90) => new(text, y, confidence);
    public static async Task Run()
    {
        string source = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"); File.WriteAllText(source, "synthetic placeholder; fake adapter only");
        var options = new WardogsScanOptions { SampleFps = 1 };
        try
        {
            async Task<WardogsScanResult> Scan(Frames frames, WardogsScanOptions? settings = null)
            {
                var result = await new WardogsScanner(frames).ScanAsync(source, settings ?? options);
                Check(!Directory.Exists(frames.Scratch), "scratch retained"); return result;
            }
            var positive = await Scan(new([Row("KILL Alice")], [Row("HEAD SHOT KILL Alice"), Row("KILL ASSIST Bob", .3)], [Row("HEADSHOT KILL Alice"), Row("KILL ASSIST Bob", .3)], [Row("HEADSHOT KILL Alice")]));
            Check(positive.Candidates.Count == 2 && positive.Candidates[0].Kind == WardogsEventKind.Headshot && positive.Candidates[1].Kind == WardogsEventKind.Assist, "typed/refined events");
            Check(positive.Candidates[0].Seconds == 0 && positive.Candidates[1].Seconds == 1 && positive.DetectorVersion == WardogsScanner.Version, "source times/metadata");
            var negative = await Scan(new([Row("SKILL"), Row("KILL", .3, 10)], [Row("HEADSHOT"), Row("KILL", .3, 10)]));
            Check(negative.Candidates.Count == 0, "negative labels");
            var adjacent = await Scan(new([Row("KILL A")], [Row("KILL A", .3), Row("KILL B")], [Row("KILL A", .3), Row("KILL B")], [], [Row("KILL B")]));
            Check(adjacent.Candidates.Count == 2, "row shift, nearby events and flicker");
            var duplicates = await Scan(new([Row("KILL"), Row("KILL", .3)], [Row("KILL"), Row("KILL", .3)], [], [], [], [Row("KILL")], [Row("KILL")]));
            Check(duplicates.Candidates.Count == 3, "duplicate positions and reappearance");
            foreach (var invalid in new[] { options with { SampleFps = double.NaN }, options with { Crop = new(.9, 0, .2, .2) }, options with { ConfirmationSamples = 0 }, options with { ProcessTimeoutSeconds = double.PositiveInfinity } })
                await Throws<ArgumentException>(() => new WardogsScanner(new Frames([])).ScanAsync(source, invalid));
            var failure = new Frames(Array.Empty<OcrRow>()) { Fail = true };
            await Throws<IOException>(() => new WardogsScanner(failure).ScanAsync(source, options));
            Check(!Directory.Exists(failure.Scratch), "failure cleanup");
            using var cancel = new CancellationTokenSource(); var cancelled = new Frames([], []) { Cancel = cancel };
            await Throws<OperationCanceledException>(() => new WardogsScanner(cancelled).ScanAsync(source, options, token: cancel.Token));
            Check(cancelled.Calls == 1 && !Directory.Exists(cancelled.Scratch), "cancel cleanup/bounded scheduling");
            await Throws<InvalidDataException>(() => new WardogsScanner(new Frames([], [])).ScanAsync(source, options with { MaxSamples = 1 }));
            var intervals = WardogsIntervals.Plan([new(1, WardogsEventKind.Kill, 1, "", 1, 0, 1), new(2, WardogsEventKind.Kill, 18, "", 1, 0, 1)], 20, mergeGap: 2);
            Check(intervals.Count == 1 && intervals[0] == new HighlightInterval(0, 20), "interval clamp/merge");
            var tsv = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n1\t1\t0\t0\t0\t0\t0\t0\t200\t100\t-1\t\n5\t1\t1\t1\t1\t1\t0\t10\t20\t10\t90\tKILL\n";
            Check(FfmpegTesseractFrames.ParseTsv(tsv).Single() == Row("KILL"), "TSV evidence");
            var mixedTops = tsv[..tsv.IndexOf("5\t", StringComparison.Ordinal)]
                + "5\t1\t2\t1\t1\t1\t70\t10\t20\t10\t90\tKILL\n"
                + "5\t1\t1\t1\t1\t1\t0\t11\t25\t10\t95\tHEAD\n"
                + "5\t1\t3\t1\t1\t1\t30\t12\t25\t10\t92\tSHOT\n";
            Check(FfmpegTesseractFrames.ParseTsv(mixedTops).Single().Text == "HEAD SHOT KILL", "OCR reading order across blocks/glyph heights");
            await Throws<InvalidDataException>(() => Task.FromResult(FfmpegTesseractFrames.ParseTsv(tsv.Replace("\t90\tKILL", "\tNaN\tKILL"))));
        }
        finally { File.Delete(source); }
    }

    // Opt-in production-path fixture: --wardogs-integration <absolute tesseract.exe path>.
    public static async Task Integration(string tesseract)
    {
        var root = Path.Combine(Path.GetTempPath(), "meh-wardogs-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var ffmpeg = MediaTools.Resolve("", "ffmpeg");
            var ffprobe = MediaTools.Resolve("", "ffprobe");
            var source = Path.Combine(root, "synthetic HUD & audio tail.mkv");
            var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf").Replace('\\', '/').Replace(":", @"\:");
            var filter = $"drawtext=fontfile='{font}':text='HEADSHOT KILL ALICE':fontsize=26:fontcolor=black:x=850:y=40," +
                $"drawtext=fontfile='{font}':text='KILL ASSIST BOB':fontsize=26:fontcolor=black:x=850:y=100:enable='gte(t,1)'";
            await MediaTools.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=white:s=1280x720:r=10:d=4",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-vf", filter, "-c:v", "mpeg4", "-q:v", "2", "-c:a", "pcm_s16le", source], TimeSpan.FromSeconds(30), default);
            var hash = SHA256.HashData(File.ReadAllBytes(source));
            var scanner = new WardogsScanner(new FfmpegTesseractFrames(ffmpeg, ffprobe, tesseract));
            var result = await scanner.ScanAsync(source);
            Check(result.Candidates.Count == 2, "synthetic production scan event count: " + string.Join(" / ", result.Candidates));
            Check(result.Candidates[0].Kind == WardogsEventKind.Headshot && result.Candidates[0].Seconds == 0, "synthetic headshot/timestamp");
            Check(result.Candidates[1].Kind == WardogsEventKind.Assist && Math.Abs(result.Candidates[1].Seconds - 1) <= .5, "synthetic assist/timestamp");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "scanner changed source");
            Console.WriteLine("Synthetic WARDOGS integration: FFmpeg crop + Tesseract, headshot and assist, timestamps, persistent rows, audio tail and unchanged source passed.");
        }
        finally { Directory.Delete(root, true); }
    }
}
