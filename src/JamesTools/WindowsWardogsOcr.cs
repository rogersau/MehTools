using System.IO;
using System.Runtime.InteropServices;
using JamesTools.Core.Scanning;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace JamesTools;

internal sealed class WindowsWardogsOcr(OcrEngine engine) : IHudOcr
{
    public static IHudOcr Create() => TryCreate() ?? throw new InvalidOperationException(
        "Windows OCR needs English optical character recognition. Add it in Windows Settings under Time & language → Language & region.");

    public static Task<IHudOcr> CreateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Create());
    }

    private static IHudOcr? TryCreate()
    {
        try
        {
            var english = OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            if (english is not null) return new WindowsWardogsOcr(english);
            var language = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(item =>
                item.LanguageTag.Equals("en", StringComparison.OrdinalIgnoreCase)
                || item.LanguageTag.StartsWith("en-", StringComparison.OrdinalIgnoreCase));
            return language is not null && OcrEngine.TryCreateFromLanguage(language) is { } fallback
                ? new WindowsWardogsOcr(fallback) : null;
        }
        catch (Exception ex) when (ex is COMException or PlatformNotSupportedException) { return null; }
    }

    public async Task<IReadOnlyList<OcrRow>> ReadAsync(HudFrame frame, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > OcrEngine.MaxImageDimension || frame.Height > OcrEngine.MaxImageDimension
            || frame.Stride != frame.Width || frame.Gray.Length != checked(frame.Width * frame.Height))
            throw new InvalidDataException("Invalid grayscale image dimensions for Windows OCR.");

        using var writer = new DataWriter();
        writer.WriteBytes(frame.Gray);
        using var image = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Gray8, frame.Width, frame.Height);
        var result = await engine.RecognizeAsync(image).AsTask(token);
        return result.Lines.Where(line => line.Words.Count > 0)
            .Select(line => new OcrRow(line.Text, Math.Clamp(line.Words.Min(word => word.BoundingRect.Top) / frame.Height, 0, 1))
            {
                X = Math.Clamp(line.Words.Min(word => word.BoundingRect.Left) / frame.Width, 0, 1)
            })
            .ToArray();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
