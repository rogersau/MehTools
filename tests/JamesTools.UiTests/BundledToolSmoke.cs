using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JamesTools;
using JamesTools.Core.Recording;
using JamesTools.Core.Scanning;

internal static class BundledToolSmoke
{
    // Run the default resolvers from a relocated published folder, including spaces in its path.
    public static async Task StageAndRun(string publishDirectory)
    {
        var stage = Path.Combine(Path.GetTempPath(), "Meh Tools bundled smoke " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var source = Path.GetFullPath(publishDirectory);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(stage, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
            }
            foreach (var name in new[] { "JamesTools.UiTests.exe", "JamesTools.UiTests.dll" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(stage, name));
            // The harness uses the published app's self-contained runtime and dependency graph.
            // A framework-dependent test runtimeconfig would instead request an installed runtime.
            File.Copy(Path.Combine(source, "MehTools.runtimeconfig.json"), Path.Combine(stage, "JamesTools.UiTests.runtimeconfig.json"));
            var dependencies = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "MehTools.deps.json")))!;
            var target = dependencies["runtimeTarget"]!["name"]!.GetValue<string>();
            var assembly = typeof(BundledToolSmoke).Assembly.GetName();
            var library = assembly.Name + "/" + assembly.Version!.ToString(3);
            dependencies["targets"]![target]![library] = new JsonObject { ["runtime"] = new JsonObject { ["JamesTools.UiTests.dll"] = new JsonObject() } };
            dependencies["libraries"]![library] = new JsonObject { ["type"] = "project", ["serviceable"] = false, ["sha512"] = "" };
            File.WriteAllText(Path.Combine(stage, "JamesTools.UiTests.deps.json"), dependencies.ToJsonString());
            Console.WriteLine(await MediaTools.RunAsync(Path.Combine(stage, "JamesTools.UiTests.exe"), ["--bundled-tools-child"], TimeSpan.FromMinutes(2), default));
        }
        finally { Directory.Delete(stage, true); }
    }

    public static async Task Run()
    {
        Environment.SetEnvironmentVariable("PATH", Environment.SystemDirectory);
        var ffmpeg = MediaTools.Resolve("", "ffmpeg"); var ffprobe = MediaTools.Resolve("", "ffprobe");
        foreach (var tool in new[] { ffmpeg, ffprobe })
            if (!tool.StartsWith(Path.Combine(AppContext.BaseDirectory, "tools") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Tool escaped the published package.");
        var scratch = Path.Combine(AppContext.BaseDirectory, "synthetic media"); Directory.CreateDirectory(scratch);
        // Rasterize the font outline on the CPU: headless/RDP sessions can return a blank
        // RenderTargetBitmap even though native OCR works correctly.
        var outline = new FormattedText("HEADSHOT KILL ALICE", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Arial"), 36, Brushes.Black, 1).BuildGeometry(new Point(20, 40));
        var pixels = Enumerable.Repeat((byte)255, 640 * 240).ToArray();
        for (int y = 0; y < 240; y++) for (int x = 0; x < 640; x++)
            if (outline.FillContains(new Point(x + .5, y + .5))) pixels[y * 640 + x] = 0;
        var bitmap = BitmapSource.Create(640, 240, 96, 96, PixelFormats.Gray8, null, pixels, 640);
        var image = Path.Combine(scratch, "synthetic HUD.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(image)) encoder.Save(file);
        var source = Path.Combine(scratch, "two audio tracks.mkv");
        await MediaTools.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-loop", "1", "-i", image,
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-f", "lavfi", "-i", "sine=frequency=880:duration=3",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-t", "3", "-r", "10", "-c:v", "mpeg4", "-q:v", "2", "-g", "10", "-c:a", "pcm_s16le", source], TimeSpan.FromSeconds(30), default);
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var scan = await CheckReviewScan(scratch, source) ?? new WardogsScanResult("synthetic", "fixture", new(), 3,
            [new(1, WardogsEventKind.Headshot, .5, "HEADSHOT KILL ALICE", null, .1, 2)]) { OcrEngine = WardogsOcrEngine.Windows };
        if (scan.DetectorVersion == WardogsScanner.Version) await CheckBackgroundScan(scratch, source);
        var media = new MediaTools(); var clip = Path.Combine(scratch, "exported clip.mkv");
        var exported = await media.ExportAsync(source, clip, 0, 2, default);
        if (exported.AudioTracks != 2 || exported.VideoTracks != 1 || exported.Duration is < 1.5 or > 2.5) throw new Exception("Bundled trim did not preserve duration/tracks.");
        var thumbnail = await media.ThumbnailAsync(source, 1, default); File.Delete(thumbnail);
        SourceFingerprint fingerprint;
        using (var file = File.OpenRead(source)) fingerprint = SourceFingerprint.Read(file);
        var saved = new SavedWardogsScan { Source = source, Status = ScanStatus.Completed, Result = scan,
            Fingerprint = fingerprint,
            Reviews = scan.Candidates.Select(c => new CandidateReview(c.Id, ReviewDecision.Keep)).ToArray(), Pre = 0, Post = 2 };
        var montage = await new WardogsExporter(media).ExportAsync(saved, Path.Combine(scratch, "verified montage.mkv"), WardogsExportKind.Montage, null, default);
        if (montage.VerifiedSeconds is null || (await media.ProbeAsync(montage.Path, default)).AudioTracks != 2) throw new Exception("Bundled montage lost verification/audio tracks.");
        if (!hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source)))) throw new Exception("Source changed during scan/export.");
        Console.WriteLine("PASS bundled probe, thumbnail, trim and verified montage preserving both audio tracks and source hash");
    }

    private static async Task CheckBackgroundScan(string scratch, string source)
    {
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var directory = Path.Combine(scratch, "background library");
        var settings = new JamesTools.Core.Configuration.SettingsStore(directory);
        settings.Save(new JamesTools.Core.Configuration.AppSettings { ShowNotifications = false });
        var sessions = new JamesTools.Core.Sessions.SessionLibraryStore(directory);
        var now = DateTime.UtcNow;
        var session = sessions.Create("WARDOGS", "wardogs.exe", now.AddMinutes(-3));
        sessions.End(session.Id, now.AddMinutes(-2), JamesTools.Core.Sessions.SessionStatus.Completed);
        sessions.AddAttachment(session.Id, null, session.ExecutableKey, JamesTools.Core.Sessions.SessionAttachmentKind.Recording, source, now);
        var window = (MainWindow)Activator.CreateInstance(typeof(MainWindow), members, null, [directory], null)!;
        try
        {
            var option = (System.Windows.Controls.CheckBox)window.FindName("WardogsBackgroundOption");
            if (option.IsChecked == true) throw new Exception("Background OCR must be opt-in.");
            option.IsChecked = true;
            option.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var status = (System.Windows.Controls.TextBlock)window.FindName("WardogsBackgroundStatus");
            while (!status.Text.StartsWith("Background scan finished", StringComparison.Ordinal)) await Task.Delay(50, timeout.Token);
            var store = (WardogsReviewStore)typeof(MainWindow).GetField("_wardogsReviewStore", members)!.GetValue(window)!;
            var completed = (await store.SnapshotAsync()).Scans.Single();
            if (!completed.Background || completed.SessionId != session.Id || completed.Status != ScanStatus.Completed || settings.Load()?.BackgroundWardogsOcr != true)
                throw new Exception("Background OCR did not persist the setting and a completed scan under its session.");
            if (typeof(MainWindow).GetField("_wardogsWindow", members)!.GetValue(window) is not null)
                throw new Exception("Background OCR opened the review window.");
            Console.WriteLine("PASS opt-in background OCR scans a linked session recording with review closed");
        }
        finally
        {
            typeof(MainWindow).GetField("_exiting", members)!.SetValue(window, true);
            await (Task)typeof(MainWindow).GetMethod("StopWardogsBackgroundAsync", members)!.Invoke(window, null)!;
            await ((IAsyncDisposable)typeof(MainWindow).GetField("_automation", members)!.GetValue(window)!).DisposeAsync();
            ((IDisposable)typeof(MainWindow).GetField("_tray", members)!.GetValue(window)!).Dispose();
            window.Close();
        }
    }

    private static async Task<WardogsScanResult?> CheckReviewScan(string scratch, string source)
    {
        var directory = Path.Combine(scratch, "review library");
        var review = new WardogsReviewWindow(directory, "", (_, _, _, _) => { })
        {
            ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000
        };
        T Field<T>(string name) => (T)typeof(WardogsReviewWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(review)!;
        review.Show();
        try
        {
            var threads = Field<System.Windows.Controls.TextBox>("decodeThreads");
            threads.Text = "8";
            Field<System.Windows.Controls.TextBox>("crop").Text = "0,0,1,1";
            bool windowsAvailable = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Any(language =>
                language.LanguageTag.Equals("en", StringComparison.OrdinalIgnoreCase) || language.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
            await review.AddFilesAsync([source], null).WaitAsync(TimeSpan.FromSeconds(10));
            await Field<Task>("pump").WaitAsync(TimeSpan.FromSeconds(30));
            var status = Field<System.Windows.Controls.TextBlock>("status");
            if (!windowsAvailable)
            {
                if (!status.Text.Contains("Windows OCR needs English", StringComparison.Ordinal))
                    throw new Exception("Missing Windows OCR language was not reported: " + status.Text);
                Console.WriteLine("PASS review explains missing English Windows OCR");
                return null;
            }
            if (!status.Text.StartsWith("Scan complete for two audio tracks.mkv", StringComparison.Ordinal))
                throw new Exception("Review did not show a completed scan: " + status.Text);
            var saved = Field<WardogsReviewStore>("store");
            await saved.InitializeAsync();
            var snapshot = await saved.SnapshotAsync();
            var completed = snapshot.Scans.Single();
            if (completed.Preferences.Options?.DecodeThreads != 8 || completed.Result?.Options.DecodeThreads != 8 || snapshot.Preferences.Options?.DecodeThreads != 8)
                throw new Exception("The configured decode thread count did not reach the scan or its saved settings.");
            if (completed.Result.OcrEngine != WardogsOcrEngine.Windows || !completed.Result.Candidates.Any(candidate => candidate is { Kind: WardogsEventKind.Headshot, Confidence: null }))
                throw new Exception("The review scanner did not persist the expected OCR engine and confidence behavior.");
            Console.WriteLine("PASS review scan used Windows OCR and saved the synthetic headshot");

            var invalid = Path.Combine(scratch, "unreadable.mkv");
            File.WriteAllText(invalid, "Invalid media fixture");
            await review.AddFilesAsync([invalid], null).WaitAsync(TimeSpan.FromSeconds(10));
            await Field<Task>("pump").WaitAsync(TimeSpan.FromSeconds(30));
            if (!status.Text.StartsWith("Couldn't scan unreadable.mkv", StringComparison.Ordinal))
                throw new Exception("Review did not show the scan error: " + status.Text);
            Console.WriteLine("PASS recording review automatically scans added files and shows completion and errors");
            return completed.Result;
        }
        finally
        {
            await review.ShutdownAsync();
            review.Close();
        }
    }
}
