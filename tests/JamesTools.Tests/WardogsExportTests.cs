using JamesTools.Core.Recording;
using JamesTools.Core.Scanning;
using System.Security.Cryptography;
internal static class WardogsExportTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static async Task<SavedWardogsScan> Scan(string source) => new() { Source = source, SessionId = Guid.NewGuid(), Status = ScanStatus.Completed, Fingerprint = await new NativeWardogsScanWork().FingerprintAsync(source, default), Pre = 1, Post = 1, Result = new("test", "synthetic", new(), 12, [new(1, WardogsEventKind.Kill, 3, "=OCR,\"evidence\"", .8, .1, 1), new(2, WardogsEventKind.Kill, 9, "second", .9, .1, 1)]), Reviews = [new(1, ReviewDecision.Keep, "@label\n<tag>"), new(2, ReviewDecision.Keep)] };
    public static async Task Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wardogs-export-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var source = Path.Combine(dir, "source.mkv"); await File.WriteAllTextAsync(source, "synthetic bytes"); var scan = await Scan(source);
            var csv = WardogsExporter.Format(scan, WardogsExportKind.Csv); Check(csv.Contains("'@label") && csv.Contains("'=OCR,\"\"evidence\"\""), "CSV formula and quote protection");
            var srt = WardogsExporter.Format(scan, WardogsExportKind.Srt); Check(srt.Contains("START OF RECORDING") && srt.Contains("00:00:02,000 --> 00:00:04,000") && !srt.Contains("<tag>"), "source padded SRT normalization");
            var captured = WardogsExporter.Capture(scan); scan.Reviews[0] = new(1, ReviewDecision.Reject); Check(captured.Intervals().Count == 2, "snapshot array isolation");
            using (await WardogsExporter.VerifiedSourceAsync(captured, default)) {
                bool locked = false; try { using var writer = File.Open(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); } catch (IOException) { locked = true; }
                Check(locked, "verified source remains protected against writes");
            }
            bool emptyRejected = false;
            try { WardogsExporter.Format(captured with { Pre = 0, Post = 0 }, WardogsExportKind.Srt); } catch (InvalidDataException) { emptyRejected = true; }
            Check(emptyRejected, "empty windows rejected before formatting");
            var exporter = new WardogsExporter(new()); var target = Path.Combine(dir, "out.csv"); var result = await exporter.ExportAsync(captured, target, WardogsExportKind.Csv, null, default); Check(result.SessionId == scan.SessionId && result.VerifiedSeconds is null, "pinned ownership");
            bool failed = false; try { await exporter.ExportAsync(captured, target, WardogsExportKind.Csv, null, default); } catch (IOException) { failed = true; } Check(failed, "no overwrite");
            foreach (var mode in new[] { "failure", "cancel", "mismatch", "truncated", "race", "success", "cleanup" }) {
                var video = Path.Combine(dir, mode + ".mkv");
                FileStream? held = null; string? heldDirectory = null;
                var fake = new MediaTools("", async (tool, arguments, timeout, token) => {
                    var a = arguments.ToArray();
                    if (tool == "ffprobe") {
                        var duration = a.Last() == source ? "12" : a.Last().Contains("output") ? "4" : "2";
                        if (mode == "truncated" && a.Last().Contains("output")) duration = "3";
                        var codec = mode == "mismatch" && a.Last() != source ? "mp3" : "aac";
                        return "{\"format\":{\"duration\":\"" + duration + "\"},\"streams\":[{\"nb_read_packets\":\"50\",\"codec_type\":\"video\",\"codec_name\":\"h264\"},{\"nb_read_packets\":\"50\",\"codec_type\":\"audio\",\"codec_name\":\"" + codec + "\"}]}";
                    }
                    if (a.Last() != "-") await File.WriteAllTextAsync(a.Last(), "owned staging");
                    if (mode == "cleanup" && a.Last().Contains("output")) { heldDirectory = Path.GetDirectoryName(a.Last())!; held = new FileStream(Path.Combine(heldDirectory, "locked.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); }
                    if (mode == "failure") throw new IOException("injected failure");
                    if (mode == "cancel") throw new OperationCanceledException();
                    if (mode == "race" && a.Last() == "-") await File.WriteAllTextAsync(video, "unrelated collision");
                    return "";
                });
                bool published = false; WardogsPublishedExport? outcome = null;
                try { outcome = await new WardogsExporter(fake).ExportAsync(captured, video, WardogsExportKind.Montage, null, default); published = true; } catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { }
                finally { held?.Dispose(); }
                Check(published == (mode is "success" or "cleanup"), "fake transaction " + mode);
                if (mode == "cleanup") { Check(outcome?.CleanupWarning is not null && File.Exists(video), "cleanup warning preserves publication success"); if (heldDirectory is not null) Directory.Delete(heldDirectory, true); }
                Check(!Directory.GetDirectories(dir).Any(), "scratch cleanup " + mode);
                if (mode == "race") Check(await File.ReadAllTextAsync(video) == "unrelated collision", "collision preservation");
            }
            await File.WriteAllTextAsync(source, "changed source"); failed = false; try { await exporter.ExportAsync(captured, Path.Combine(dir, "changed.srt"), WardogsExportKind.Srt, null, default); } catch (IOException) { failed = true; } Check(failed && !File.Exists(Path.Combine(dir, "changed.srt")), "changed fingerprint refusal");
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { await exporter.ExportAsync(captured, Path.Combine(dir, "cancel.csv"), WardogsExportKind.Csv, null, cancel.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Check(!Directory.GetDirectories(dir).Any(), "no scratch on failure/cancel");
            var args = MediaTools.ExportArguments(source, "part.mkv", 2, 4); Check(args.Contains("0:a?") && args.Contains("copy"), "all audio copied");
        } finally { Directory.Delete(dir, true); }
    }
    public static async Task Integration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wardogs-montage-integration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try {
            var source = Path.Combine(dir, "source.mkv");
            await MediaTools.RunAsync(MediaTools.Resolve("", "ffmpeg"), ["-nostdin", "-v", "error", "-n", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:duration=12", "-f", "lavfi", "-i", "sine=frequency=880:duration=12", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-g", "25", "-c:a", "aac", source], TimeSpan.FromSeconds(60), default);
            var scan = await Scan(source); byte[] before; using (var initial = File.OpenRead(source)) before = await SHA256.HashDataAsync(initial);
            var media = new MediaTools(); var exporter = new WardogsExporter(media); var target = Path.Combine(dir, "montage.mkv");
            var result = await exporter.ExportAsync(scan, target, WardogsExportKind.Montage, null, default); var info = await media.ProbeAsync(target, default);
            Check(info.VideoTracks == 1 && info.AudioTracks == 2 && info.Duration >= 3 && info.Duration <= 8, "two audio tracks and sane montage duration");
            await MediaTools.RunAsync(MediaTools.Resolve("", "ffmpeg"), ["-nostdin", "-v", "error", "-xerror", "-i", target, "-map", "0", "-f", "null", "-"], TimeSpan.FromSeconds(30), default);
            using (var stream = File.OpenRead(source)) { var after = await SHA256.HashDataAsync(stream); Check(before.SequenceEqual(after), "source unchanged"); }
            bool failed = false; try { await exporter.ExportAsync(scan, target, WardogsExportKind.Montage, null, default); } catch (IOException) { failed = true; } Check(failed, "real no overwrite");
            using var cancellation = new CancellationTokenSource(); var progress = new CancelProgress(cancellation);
            try { await exporter.ExportAsync(scan, Path.Combine(dir, "cancel.mkv"), WardogsExportKind.Montage, progress, cancellation.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
            Check(!Directory.GetDirectories(dir).Any() && !File.Exists(Path.Combine(dir, "cancel.mkv")), "real cancellation cleanup");
            Console.WriteLine($"PASS synthetic montage: requested {result.RequestedSeconds}s, verified {result.VerifiedSeconds}s; H264 + two AAC tracks");
        } finally { Directory.Delete(dir, true); }
    }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<string> { public void Report(string value) => source.Cancel(); }
}
