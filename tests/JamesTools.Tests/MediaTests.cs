using JamesTools.Core.Recording;
using JamesTools.Core.Configuration;
using System.Security.Cryptography;
using System.Diagnostics;
internal static class MediaTests
{
    static void Check(bool b) { if (!b) throw new Exception("Media assertion failed"); }
    static void Reject(Action a) { try { a(); } catch { return; } throw new Exception("Expected rejection"); }
    public static async Task Run()
    {
        var info = MediaTools.ParseProbe("""{"format":{"duration":"12.25"},"streams":[{"codec_type":"video"},{"codec_type":"audio"},{"codec_type":"audio"}]}""");
        Check(info.AudioTracks == 2 && info.Duration == 12.25);
        Reject(() => MediaTools.ParseProbe("""{"format":{"duration":"N/A"},"streams":[]}"""));
        Reject(() => MediaTools.ParseProbe("""{"format":{},"streams":[]}"""));
        foreach (var (a,b) in new[] { (-1d,2d), (2d,2d), (0d,13d), (double.NaN,2d) }) Reject(() => MediaTools.ValidateRange(a,b,12));
        var args = MediaTools.ExportArguments(@"C:\space & $\a.mkv", @"C:\space & $\b.mkv",1,3);
        Check(args.Contains(@"C:\space & $\a.mkv") && args.Contains("0:a?") && args.Contains("copy") && args.Contains("-n"));
        Reject(() => MediaTools.LocalVideo("https://example.org/a.mp4"));
        var settings = new AppSettings { MediaToolsFolder = "sample" }; Check(settings.Snapshot().MediaToolsFolder == "sample");
        Check(SettingsValidation.Validate(settings).Any(e => e.Contains("Media tools")));
        var store = new SettingsStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".json"));
        Check(store.Serialize(settings).Contains("sample"));
        var root = Path.Combine(Path.GetTempPath(), "meh-fake-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            var source = Path.Combine(root, "source.mkv"); File.WriteAllText(source, "synthetic");
            var destination = Path.Combine(root, "clip.mkv");
            foreach (var mode in new[] { "failure", "cancel", "badprobe", "badtracks", "badduration", "race", "success" }) {
                var attached = false;
                var fake = new MediaTools("", async (tool, arguments, timeout, token) => {
                    var a = arguments.ToArray();
                    if (tool == "ffprobe")
                    {
                        if (a.Last() != source)
                        {
                            if (mode == "badprobe") return "{}";
                            if (mode == "badtracks") return """{"format":{"duration":"3"},"streams":[{"codec_type":"video"},{"codec_type":"audio"}]}""";
                            if (mode == "badduration") return """{"format":{"duration":"600"},"streams":[{"codec_type":"video"},{"codec_type":"audio"},{"codec_type":"audio"}]}""";
                        }
                        return """{"format":{"duration":"3"},"streams":[{"codec_type":"video"},{"codec_type":"audio"},{"codec_type":"audio"}]}""";
                    }
                    await File.WriteAllTextAsync(a.Last(), "synthetic output");
                    if (mode == "failure") throw new IOException("fake failure");
                    if (mode == "cancel") throw new OperationCanceledException();
                    if (mode == "race") File.WriteAllText(destination, "other owner's file");
                    return "";
                });
                try { await fake.ExportAsync(source, destination, 0, 2, default); attached = true; } catch { }
                Check(attached == (mode == "success"));
                Check(!Directory.GetFiles(root, ".meh-*").Any());
                if (mode == "race") Check(File.ReadAllText(destination) == "other owner's file");
                if (File.Exists(destination)) File.Delete(destination);
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var invoked = false;
            var neverRun = new MediaTools("", (_, _, _, _) => { invoked = true; throw new Exception("must not run"); });
            try { await neverRun.ExportAsync(source, destination, 0, 2, cancelled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            Check(!invoked);
        } finally { Directory.Delete(root, true); }
    }
    public static async Task Integration()
    {
        var root = Path.Combine(Path.GetTempPath(), "meh-media-"+Guid.NewGuid()); Directory.CreateDirectory(root);
        try {
            var source = Path.Combine(root,"source & two tracks.mkv"); var target = Path.Combine(root,"clip.mkv");
            await MediaTools.RunAsync(MediaTools.Resolve("", "ffmpeg"), ["-nostdin","-v","error","-f","lavfi","-i","testsrc=size=160x90:rate=10:duration=5","-f","lavfi","-i","sine=frequency=440:duration=5","-f","lavfi","-i","sine=frequency=880:duration=5","-map","0:v","-map","1:a","-map","2:a","-c:v","mpeg4","-g","10","-c:a","pcm_s16le",source],TimeSpan.FromSeconds(30),default);
            var hash = SHA256.HashData(File.ReadAllBytes(source)); var tools = new MediaTools();
            var result = await tools.ExportAsync(source,target,1,3,default);
            Check(result.VideoTracks == 1 && result.AudioTracks == 2 && result.Duration > 0 && result.Duration < 5);
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))));
            async Task Fail(string dest, CancellationToken token) { bool rejected=false; try { await tools.ExportAsync(source,dest,1,3,token); } catch { rejected=true; } Check(rejected); }
            await Fail(target,default); await Fail(source,default);
            using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Fail(Path.Combine(root,"cancel.mkv"),cancel.Token);
            Check(!File.Exists(Path.Combine(root,"cancel.mkv")) && !Directory.GetFiles(root,".meh-*").Any());
            using var activeCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var elapsed = Stopwatch.StartNew();
            try
            {
                await MediaTools.RunAsync(MediaTools.Resolve("", "ffmpeg"),
                    ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "testsrc=size=160x90:rate=10:duration=30", "-f", "null", "-"],
                    TimeSpan.FromSeconds(10), activeCancel.Token);
                throw new Exception("Running FFmpeg was not cancelled");
            }
            catch (OperationCanceledException) { }
            Check(elapsed.Elapsed < TimeSpan.FromSeconds(8));
            Console.WriteLine($"Synthetic integration: 1 video / 2 audio, duration {result.Duration}, unchanged original, collisions refused, pre-start and running-process cancellation passed.");
        } finally { Directory.Delete(root,true); }
    }
}
