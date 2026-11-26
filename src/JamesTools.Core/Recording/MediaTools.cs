using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
namespace JamesTools.Core.Recording;

public sealed record MediaInfo(double Duration, int VideoTracks, int AudioTracks);
public sealed class MediaTools(string folder = "", Func<string, IEnumerable<string>, TimeSpan, CancellationToken, Task<string>>? runner = null)
{
    private static readonly SemaphoreSlim ThumbnailSlots = new(2);
    internal Task<string> Execute(string name, IEnumerable<string> args, TimeSpan timeout, CancellationToken token) => runner is null ? RunAsync(Resolve(folder, name), args, timeout, token) : runner(name, args, timeout, token);
    public static string Resolve(string folder, string name)
        => Resolve(folder, name, AppContext.BaseDirectory, Environment.GetEnvironmentVariable("PATH") ?? "");
    internal static string Resolve(string folder, string name, string appDirectory, string searchPath)
    {
        var candidates = string.IsNullOrWhiteSpace(folder)
            ? new[] { Path.Combine(appDirectory, "tools", "ffmpeg"), Path.Combine(appDirectory, "tools") }.Concat(searchPath.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified))
            : new[] { folder };
        return candidates.Select(p => Path.Combine(p, name + ".exe")).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("FFmpeg tools are missing. Reinstall Meh Tools or browse to a folder containing ffmpeg.exe and ffprobe.exe; clear the folder to use bundled tools or PATH.");
    }
    public static string LocalVideo(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\") || path.Contains("://") || path.AsSpan(2).Contains(':')) throw new ArgumentException("Choose an existing local video file.");
        path = Path.GetFullPath(path);
        if (path.StartsWith(@"\\")) throw new ArgumentException("Choose an existing local video file.");
        if (!new[] { ".mkv", ".mp4", ".mov", ".webm", ".avi", ".m4v" }.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path)) throw new ArgumentException("Choose an existing MKV, MP4, MOV, WebM, AVI or M4V file.");
        return path;
    }
    public static MediaInfo ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = double.NaN;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d)) double.TryParse(d.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("Media duration is unavailable; range export is disabled.");
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        return new(duration, streams.Count(s => s.GetProperty("codec_type").GetString() == "video"), streams.Count(s => s.GetProperty("codec_type").GetString() == "audio"));
    }
    public static void ValidateRange(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(duration) || start < 0 || start >= end || end > duration) throw new ArgumentException("Enter seconds with 0 <= start < end <= duration.");
    }
    public static string[] ExportArguments(string source, string temp, double start, double end) =>
        ["-nostdin", "-v", "error", "-n", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-ss", start.ToString("R", CultureInfo.InvariantCulture), "-i", source, "-t", (end-start).ToString("R", CultureInfo.InvariantCulture), "-map", "0:V:0", "-map", "0:a?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", "matroska", temp];
    public async Task<MediaInfo> ProbeAsync(string path, CancellationToken token) => ParseProbe(await Execute("ffprobe", ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-show_entries", "format=duration:stream=codec_type", "-of", "json", LocalVideo(path)], TimeSpan.FromSeconds(20), token));
    public async Task<MediaInfo> ExportAsync(string source, string destination, double start, double end, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(destination) || destination.Contains("://") || destination.AsSpan(2).Contains(':'))
            throw new ArgumentException("Choose a full destination file path.");
        source = LocalVideo(source); destination = Path.GetFullPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination) || string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(destination).ToLowerInvariant() != ".mkv") throw new IOException("Choose a new, nonexisting MKV destination.");
        // Windows share mode excludes writers and deletion for the entire probe/export/verification transaction.
        using var guard = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var input = await ProbeAsync(source, token); ValidateRange(start, end, input.Duration);
        if (input.VideoTracks == 0) throw new InvalidDataException("No video stream is available to export.");
        var temp = Path.Combine(Path.GetDirectoryName(destination)!, ".meh-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            await Execute("ffmpeg", ExportArguments(source, temp, start, end), TimeSpan.FromMinutes(10), token);
            var output = await ProbeAsync(temp, token);
            if (output.VideoTracks != 1 || output.AudioTracks != input.AudioTracks) throw new InvalidDataException("Export verification failed: video/audio tracks differ.");
            if (output.Duration > input.Duration + 2) throw new InvalidDataException("Export verification failed: output duration exceeds the source duration.");
            token.ThrowIfCancellationRequested(); File.Move(temp, destination, false); return output;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public Task<string> ThumbnailAsync(string path, CancellationToken token) => ThumbnailAsync(path, 0, token);
    public async Task<string> ThumbnailAsync(string path, double seconds, CancellationToken token)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        path = LocalVideo(path);
        var temp = Path.Combine(Path.GetTempPath(), "meh-thumb-" + Guid.NewGuid().ToString("N") + ".png");
        await ThumbnailSlots.WaitAsync(token);
        try {
            await Execute("ffmpeg", ["-nostdin", "-v", "error", "-n", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-ss", seconds.ToString("R", CultureInfo.InvariantCulture), "-i", path, "-map", "0:V:0", "-frames:v", "1", "-vf", "scale=320:-1", temp], TimeSpan.FromSeconds(20), token);
            return temp;
        } catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        finally { ThumbnailSlots.Release(); }
    }
    public static async Task<string> RunAsync(string executable, IEnumerable<string> args, TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        process.Start();
        var stdout = DrainOutputAsync(process.StandardOutput, 65536, deadline.Token);
        var stderr = DrainOutputAsync(process.StandardError, 65536, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException) { }
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new TimeoutException("Media operation timed out.");
        }
        if (process.ExitCode != 0) throw new IOException("Media tool failed: " + (await stderr)[..Math.Min(2000, (await stderr).Length)]);
        return await stdout;
    }

    internal static async Task<string> DrainOutputAsync(StreamReader reader, int limit, CancellationToken token = default)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
            if (result.Length < limit) result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
}
