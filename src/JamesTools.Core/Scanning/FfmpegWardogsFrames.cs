using System.Globalization;
using System.Runtime.CompilerServices;
using JamesTools.Core.Recording;

namespace JamesTools.Core.Scanning;

public sealed class FfmpegWardogsFrames(string ffmpeg, string ffprobe, IHudOcr ocr) : IWardogsFrames
{
    public async Task<MediaInfo> ProbeAsync(string source, CancellationToken token) => MediaTools.ParseProbe(await MediaTools.RunAsync(ffprobe,
        ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-show_entries", "format=duration:stream=codec_type", "-of", "json", source], TimeSpan.FromSeconds(20), token));

    public async IAsyncEnumerable<IReadOnlyList<OcrRow>> ReadSamplesAsync(string source, WardogsScanOptions options,
        int count, [EnumeratorCancellation] CancellationToken token)
    {
        var crop = options.Crop;
        static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var timeout = TimeSpan.FromSeconds(options.ProcessTimeoutSeconds);
        var filter = $"fps={N(options.SampleFps)}:start_time=0:round=up,crop=iw*{N(crop.Width)}:ih*{N(crop.Height)}:iw*{N(crop.X)}:ih*{N(crop.Y)},scale={options.ImageWidth}:{options.ImageWidth}:force_original_aspect_ratio=decrease,format=gray,pad=max(iw\\,16):max(ih\\,16):color=black";
        await using var decoder = new WardogsFrameStream(ffmpeg, source, filter, count, options.DecodeThreads, timeout);
        bool videoEnded = false;
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var frame = videoEnded ? null : await decoder.ReadAsync(token);
            videoEnded = frame is null;
            if (frame is null) yield return []; // The container can have an audio-only tail.
            else yield return await ReadAsync(new("recording", 0, TimeSpan.FromSeconds(i / options.SampleFps), frame.Width, frame.Height, frame.Width, frame.Gray), timeout, token);
        }
        await decoder.FinishAsync(token);
    }

    private async Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { return await ocr.ReadAsync(frame, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("Windows OCR timed out while reading a frame."); }
    }
}
