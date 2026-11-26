using JamesTools.Core.Recording;

internal static class BundledToolTests
{
    static void Check(bool value) { if (!value) throw new Exception("Tool resolution assertion failed."); }
    static void Missing(Action action) { try { action(); } catch (FileNotFoundException) { return; } throw new Exception("Expected a missing-tool error."); }
    public static Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "meh-tool-resolution-" + Guid.NewGuid().ToString("N"));
        string bundled = Path.Combine(root, "tools", "ffmpeg"), legacy = Path.Combine(root, "tools"), custom = Path.Combine(root, "custom"), search = Path.Combine(root, "path");
        Directory.CreateDirectory(bundled); Directory.CreateDirectory(custom); Directory.CreateDirectory(search);
        try
        {
            foreach (var dir in new[] { bundled, legacy, custom, search }) File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "fixture");
            Check(MediaTools.Resolve("", "ffmpeg", root, search) == Path.Combine(bundled, "ffmpeg.exe"));
            Check(MediaTools.Resolve(custom, "ffmpeg", root, search) == Path.Combine(custom, "ffmpeg.exe"));
            Missing(() => MediaTools.Resolve(custom, "ffprobe", root, search));
            File.Delete(Path.Combine(bundled, "ffmpeg.exe"));
            Check(MediaTools.Resolve("", "ffmpeg", root, search) == Path.Combine(legacy, "ffmpeg.exe"));
            File.Delete(Path.Combine(legacy, "ffmpeg.exe"));
            Check(MediaTools.Resolve("", "ffmpeg", root, search) == Path.Combine(search, "ffmpeg.exe"));
            Missing(() => MediaTools.Resolve("", "ffmpeg", root, "relative-folder"));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
