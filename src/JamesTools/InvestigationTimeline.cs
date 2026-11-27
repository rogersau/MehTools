using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using JamesTools.Core.Diagnostics;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace JamesTools;

/// <summary>Windowed evidence plot. Hardware values occupy their measured windows, never interpolated lines.</summary>
public sealed class InvestigationTimeline : FrameworkElement
{
    public IReadOnlyList<FrameTelemetry> Frames { get; set; } = [];
    public IReadOnlyList<InvestigationEvent> Events { get; set; } = [];
    public IReadOnlyList<DiagnosticHardwareSample> Hardware { get; set; } = [];
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; } = 60;
    public double? SelectedSeconds { get; set; }
    public event Action<double>? TimeSelected;
    private static readonly string[] Labels = ["Frame time", "CPU / threads", "GPU", "USB / PnP", "Audio", "Driver / I/O", "Processes"];
    private static readonly Brush[] Colors = [Brushes.OrangeRed, Brushes.DeepSkyBlue, Brushes.MediumSeaGreen, Brushes.Orange, Brushes.MediumOrchid, Brushes.Goldenrod, Brushes.CornflowerBlue];
    public InvestigationTimeline()
    {
        Height = 285; MinWidth = 400; ClipToBounds = true;
        ToolTip = "Click a timestamp to inspect the surrounding evidence. Bars show measured sample windows; blank space means no retained reading.";
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(22, 27, 35)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        var width = Math.Max(1, ActualWidth - 120); var span = Math.Max(.001, EndSeconds - StartSeconds);
        double X(double t) => 112 + (t - StartSeconds) / span * width;
        for (var row = 0; row < Labels.Length; row++)
        {
            dc.DrawLine(new Pen(Brushes.DimGray, .5), new Point(110, 32 + row * 34), new Point(ActualWidth - 4, 32 + row * 34));
            Text(dc, Labels[row], 4, 12 + row * 34, Colors[row]);
        }
        for (var i = 0; i <= 5; i++)
        {
            var t = StartSeconds + span * i / 5;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(45, 200, 200, 200)), .5), new Point(X(t), 5), new Point(X(t), 248));
            Text(dc, t.ToString("F2", CultureInfo.InvariantCulture) + " s", Math.Clamp(X(t) - 15, 110, Math.Max(110, ActualWidth - 60)), 252, Brushes.LightGray);
        }
        // One maximum per pixel preserves visible spikes without painting every high-refresh frame.
        foreach (var group in Frames.Where(f => f.OffsetSeconds >= StartSeconds && f.OffsetSeconds <= EndSeconds)
                     .GroupBy(f => (int)X(f.OffsetSeconds)))
        {
            var frame = group.MaxBy(f => f.FrameMs)!;
            dc.DrawLine(new Pen(Colors[0], 1.5), new Point(group.Key, 32), new Point(group.Key, 32 - Math.Min(28, frame.FrameMs / 100 * 28)));
        }
        foreach (var sample in Hardware.Where(h => h.CollectionEndSeconds >= StartSeconds && h.CollectionStartSeconds <= EndSeconds))
        {
            Bar(1, sample.CpuWindowStartSeconds ?? sample.CounterWindowStartSeconds, sample.CollectionEndSeconds, sample.Values.GetValueOrDefault("cpuPercent"));
            if (sample.GpuCollectionStartSeconds is { } start)
                Bar(2, start, sample.GpuCollectionEndSeconds ?? start, sample.Values.GetValueOrDefault("gpuPercent"));
        }
        foreach (var group in Events.Where(e => e.TimestampSeconds >= StartSeconds && e.TimestampSeconds <= EndSeconds)
                     .GroupBy(e => (Row(e.Category), Pixel: (int)X(e.TimestampSeconds))))
        {
            var row = group.Key.Item1;
            dc.DrawLine(new Pen(Colors[row], 2), new Point(group.Key.Pixel, 10 + row * 34), new Point(group.Key.Pixel, 31 + row * 34));
        }
        if (SelectedSeconds is { } selected && selected >= StartSeconds && selected <= EndSeconds)
            dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(X(selected), 2), new Point(X(selected), 248));
        void Bar(int row, double start, double end, double? value)
        {
            if (value is null || !double.IsFinite(value.Value)) return;
            var left = X(Math.Max(start, StartSeconds)); var right = X(Math.Min(end, EndSeconds));
            if (right < left) return;
            var height = Math.Clamp(value.Value, 0, 100) * .25;
            dc.DrawRectangle(Colors[row], null, new Rect(left, 32 + row * 34 - height, Math.Max(1, right - left), Math.Max(1, height)));
        }
    }
    private static int Row(string category) => category.ToLowerInvariant() switch
    {
        "cpu" or "thread" or "scheduling" => 1,
        "gpu" => 2,
        "usb" or "pnp" or "device" => 3,
        "audio" => 4,
        "process" => 6,
        _ => 5
    };
    private void Text(DrawingContext dc, string text, double x, double y, Brush brush) => dc.DrawText(
        new FormattedText(text, CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var fraction = Math.Clamp((e.GetPosition(this).X - 112) / Math.Max(1, ActualWidth - 120), 0, 1);
        SelectedSeconds = StartSeconds + fraction * (EndSeconds - StartSeconds);
        TimeSelected?.Invoke(SelectedSeconds.Value); InvalidateVisual();
    }
}
