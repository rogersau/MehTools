using JamesTools.Core.Recording;
using System.Globalization;

namespace JamesTools.Core.Scanning;

// Explicit paths only: no search in the packaged scanner, Downloads, or temp folders.
public sealed class FfmpegTesseractFrames : IWardogsFrames
{
    private readonly string ffmpeg, ffprobe, tesseract;
    private static string Tool(string path) => Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetFullPath(path) : throw new ArgumentException("Choose an existing absolute tool executable path.");
    public FfmpegTesseractFrames(string ffmpegPath, string ffprobePath, string tesseractPath)
    { ffmpeg = Tool(ffmpegPath); ffprobe = Tool(ffprobePath); tesseract = Tool(tesseractPath); }
    public async Task<MediaInfo> ProbeAsync(string source, CancellationToken token) => MediaTools.ParseProbe(await MediaTools.RunAsync(ffprobe,
        ["-v", "error", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-show_entries", "format=duration:stream=codec_type", "-of", "json", source], TimeSpan.FromSeconds(20), token));
    public async Task<IReadOnlyList<OcrRow>> ReadAsync(string source, double seconds, string scratchDirectory, WardogsScanOptions options, CancellationToken token)
    {
        var image = Path.Combine(scratchDirectory, "frame.png");
        var crop = options.Crop;
        static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        // Accurate input seeking decodes from the preceding keyframe. Disable rotation so crop coordinates
        // consistently refer to coded video orientation. Bound both output dimensions even for portrait input.
        var filter = $"crop=iw*{N(crop.Width)}:ih*{N(crop.Height)}:iw*{N(crop.X)}:ih*{N(crop.Y)},scale={options.ImageWidth}:{options.ImageWidth}:force_original_aspect_ratio=decrease";
        var timeout = TimeSpan.FromSeconds(options.ProcessTimeoutSeconds);
        try
        {
            await MediaTools.RunAsync(ffmpeg, ["-nostdin", "-v", "error", "-n", "-threads", "1", "-filter_threads", "1", "-protocol_whitelist", "file", "-format_whitelist", "matroska,webm,mov,avi", "-ss", N(seconds), "-noautorotate", "-i", source, "-map", "0:V:0", "-frames:v", "1", "-vf", filter, image], timeout, token);
            // A valid file can have an audio tail or a final low-rate video frame before this target.
            // A successful decode with no output supplies no evidence; never reuse the previous image.
            if (!File.Exists(image)) return [];
            var tsv = await MediaTools.RunAsync(tesseract, [image, "stdout", "-l", "eng", "--psm", "11", "tsv"], timeout, token);
            return ParseTsv(tsv);
        }
        finally { if (File.Exists(image)) File.Delete(image); }
    }
    public static IReadOnlyList<OcrRow> ParseTsv(string tsv)
    {
        // MediaTools caps stdout at 64 KiB. Refuse a potentially truncated document.
        if (tsv.Length >= 65536) throw new InvalidDataException("OCR output exceeded the capture limit.");
        var words = new List<(string Text, int Left, int Top, int Width, int Height, double Confidence)>();
        int width = 0, height = 0;
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var c = line.TrimEnd('\r').Split('\t');
            if (c.Length < 12) continue;
            if (c[0] == "1" && int.TryParse(c[8], out var w) && int.TryParse(c[9], out var h)) { width = w; height = h; }
            if (c[0] != "5" || string.IsNullOrWhiteSpace(c[11])) continue;
            if (!int.TryParse(c[6], out int left) || !int.TryParse(c[7], out int top) || !int.TryParse(c[8], out int wordWidth) || !int.TryParse(c[9], out int wordHeight)
                || left < 0 || top < 0 || wordWidth <= 0 || wordHeight <= 0 || (long)left + wordWidth > width || (long)top + wordHeight > height
                || !double.TryParse(c[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double confidence) || !double.IsFinite(confidence) || confidence < 0 || confidence > 100)
                throw new InvalidDataException("Malformed OCR geometry/confidence.");
            words.Add((c[11], left, top, wordWidth, wordHeight, confidence));
        }
        if (width <= 0 || height <= 0) throw new InvalidDataException("OCR page geometry is missing.");
        // Sparse-text mode can split words into blocks. Match overlapping text bands, then restore
        // left-to-right word order: glyph tops alone do not establish reading order.
        var bands = new List<List<(string Text, int Left, int Top, int Width, int Height, double Confidence)>>();
        foreach (var word in words.OrderBy(w => w.Top))
        {
            var band = bands.FirstOrDefault(b =>
                Math.Min(b[0].Top + b[0].Height, word.Top + word.Height) - Math.Max(b[0].Top, word.Top) >= Math.Min(b[0].Height, word.Height) * .5);
            if (band is null) bands.Add([word]); else band.Add(word);
        }
        return bands.Select(b => new OcrRow(string.Join(' ', b.OrderBy(w => w.Left).Select(w => w.Text)), (double)b.Min(w => w.Top) / height, b.Min(w => w.Confidence))).ToArray();
    }
}
