using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using JamesTools.Core.Diagnostics;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using MessageBox = System.Windows.MessageBox;

namespace JamesTools;

public partial class PerformanceTimelineWindow : Window
{
    private readonly ParsedPerformanceReport _report;
    private readonly double _duration;
    private readonly List<ExcludedIntervalEntry> _exclusions = [];
    private bool _initializing = true;

    public PerformanceTimelineWindow(ParsedPerformanceReport report, string title)
    {
        InitializeComponent();
        _report = report;
        ReportTitle.Text = title;
        var duration = Math.Max(report.Summary.DurationSeconds ?? 0, report.Rows.Max(r => Math.Max(r.SampleEndSeconds ?? 0, r.FrameWindowEndSeconds ?? 0)));
        _duration = duration <= 0 ? 1 : duration;
        RangeStart.Text = "0";
        RangeEnd.Text = _duration.ToString("F3", CultureInfo.InvariantCulture);
        MarkerList.ItemsSource = report.Markers.Select(m => new MarkerEntry(m, $"{m.OffsetSeconds:F3} s · {m.Note}")).ToArray();
        if (report.Markers.Count == 0)
        {
            MarkersPanel.Visibility = Visibility.Collapsed;
            Grid.SetColumn(ReportDetails, 0);
            Grid.SetColumnSpan(ReportDetails, 2);
            ReportDetails.Margin = new Thickness(0);
        }
        if (SampleTable is not null)
        {
            SampleTable.Columns.Add(Column("Time (s)", "Time"));
            SampleTable.Columns.Add(Column("CPU (%)", "Cpu"));
            SampleTable.Columns.Add(Column("GPU (%)", "Gpu"));
            SampleTable.Columns.Add(Column("Memory used (MiB)", "Memory"));
            SampleTable.Columns.Add(Column("Disk read (MiB/s)", "DiskRead"));
            SampleTable.Columns.Add(Column("Disk write (MiB/s)", "DiskWrite"));
            SampleTable.Columns.Add(Column("Frame max (ms)", "FrameMax"));
            SampleTable.Columns.Add(Column("Frames >50 ms", "Over50"));
            SampleTable.Columns.Add(Column("Selection", "Selection"));
        }
        SummaryText.Text = $"Original full-report summary: duration {report.Summary.DurationSeconds?.ToString("F1", CultureInfo.InvariantCulture) ?? "unavailable"} s; system samples {report.Summary.SystemSampleCount?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}; CPU/GPU avg {F(report.Summary.CpuAverage)}/{F(report.Summary.GpuAverage)}%; memory avg {F(report.Summary.MemoryAverageMiB)} MiB; FPS avg {F(report.Summary.FpsAverage)}; frame time avg {F(report.Summary.FrameMsAverage)} ms ({report.Summary.FpsMethod ?? "method unavailable"}; {report.Summary.FrameCount?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"} frames). This full-report summary is separate from the selection below.";
        StutterPanel.Visibility = report.Investigations.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StutterList.ItemsSource = report.Investigations.Select(e => new StutterEntry(e, $"{e.Frame.OffsetSeconds:F3} s - {e.Frame.FrameMs:F2} ms")).ToArray();
        NetworkTarget.ItemsSource = report.Network?.Targets ?? [];
        NetworkTarget.Visibility = NetworkTarget.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        NetworkPanel.Visibility = report.Network is { Enabled: true } ? Visibility.Visible : Visibility.Collapsed;
        NetworkTarget.SelectedIndex = 0;
        _initializing = false;
        StutterList.SelectedIndex = report.Investigations.Count > 0 ? 0 : -1;
        RefreshAnalysis();
    }

    private sealed record StutterEntry(StutterInvestigation Event, string Display);
    private void StutterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || StutterList.SelectedItem is not StutterEntry selected) return;
        var investigation = selected.Event;
        RangeStart.Text = investigation.StartSeconds.ToString("F3", CultureInfo.InvariantCulture);
        RangeEnd.Text = Math.Min(_duration, investigation.EndSeconds).ToString("F3", CultureInfo.InvariantCulture);
        StutterText.Text = "OBSERVATION: " + investigation.Finding.Observation + "\nPOSSIBLE CAUSE: " + investigation.Finding.PossibleCause +
            "\nCONFIDENCE / LIMITATIONS: " + investigation.Finding.ConfidenceAndLimitations +
            $"\nFrame plot contains {investigation.Frames.Count} of {investigation.ContextFramesBeforeReportReduction} retained context frames (worst per group and immediate neighbours if reduced); raw CSV preserves all accepted frames. Plot uses actual per-frame timestamps. Dots mark hardware collection times; counter windows and GPU query times are listed below. Missing measurements are not interpolated.";
        StutterHardware.ItemsSource = investigation.Hardware.SelectMany(h => h.Values.Where(v => !v.Key.StartsWith("cpuFrequencyMHz:")).Select(v =>
        {
            var (name, unit, source, scope) = DiagnosticMeasurements.Describe(v.Key);
            return new
            {
                Collection = $"{h.CollectionStartSeconds:F3}-{h.CollectionEndSeconds:F3} s",
                CounterStart = F(h.CounterWindowStartSeconds),
                CpuStart = F(h.CpuWindowStartSeconds),
                ProcessStart = F(h.ProcessWindowStartSeconds),
                ProcessEnd = F(h.ProcessWindowEndSeconds),
                GpuQuery = $"{F(h.GpuCollectionStartSeconds)}-{F(h.GpuCollectionEndSeconds)} s",
                Measurement = name,
                Value = v.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "Unavailable",
                Unit = unit,
                Scope = scope,
                Source = source
            };
        })).ToArray();
        RefreshAnalysis(); DrawStutter();
    }
    private void DrawStutter()
    {
        if (StutterList.SelectedItem is not StutterEntry selected) return;
        var e = selected.Event; var canvas = StutterPlot; canvas.Children.Clear();
        var width = Math.Max(300, canvas.ActualWidth); var height = 180d;
        const double left = 58, top = 24, bottom = 22;
        var end = Math.Min(_duration, e.EndSeconds); var span = Math.Max(.01, end - e.StartSeconds);
        var max = Math.Max(1, e.Frames.Select(f => f.FrameMs).DefaultIfEmpty(1).Max() * 1.1);
        double X(double t) => left + (t - e.StartSeconds) / span * (width - left - 14);
        double Y(double ms) => top + (1 - ms / max) * (height - top - bottom);
        AddText(canvas, "Per-frame ms (white), CPU Busy (blue), GPU Busy (purple); green ticks = hardware samples", 8, 3, 11, Brushes.White);
        AddText(canvas, max.ToString("F1", CultureInfo.InvariantCulture), 4, top, 10, Brushes.LightGray);
        AddText(canvas, $"{e.StartSeconds:F2}s", left, height - 18, 10, Brushes.LightGray);
        AddText(canvas, $"{end:F2}s", width - 65, height - 18, 10, Brushes.LightGray);
        foreach (var f in e.Frames.Where(f => f.OffsetSeconds >= e.StartSeconds && f.OffsetSeconds <= end))
        {
            var x = X(f.OffsetSeconds);
            AddLine(canvas, x, Y(0), x, Y(f.FrameMs), Brushes.White, 1);
            if (f.CpuBusyMs is { } cpu) AddLine(canvas, x - 1, Y(0), x - 1, Y(Math.Min(max, cpu)), Brushes.DodgerBlue, 1);
            if (f.GpuBusyMs is { } gpu) AddLine(canvas, x + 1, Y(0), x + 1, Y(Math.Min(max, gpu)), Brushes.MediumPurple, 1);
        }
        foreach (var h in e.Hardware.Where(h => h.CollectionStartSeconds >= e.StartSeconds && h.CollectionStartSeconds <= end))
            AddLine(canvas, X(h.CollectionStartSeconds), height - bottom, X(h.CollectionStartSeconds), height - bottom - 8, Brushes.LightGreen, 2);
        foreach (var m in e.Markers) AddLine(canvas, X(m.OffsetSeconds), top, X(m.OffsetSeconds), height - bottom, Brushes.Gold, 1);
        AddLine(canvas, X(e.Frame.OffsetSeconds), top, X(e.Frame.OffsetSeconds), height - bottom, Brushes.OrangeRed, 2);
    }

    private static DataGridTextColumn Column(string title, string path) => new() { Header = title, Binding = new System.Windows.Data.Binding(path), Width = DataGridLength.Auto };

    private void ApplyRange_Click(object sender, RoutedEventArgs e) => RefreshAnalysis();

    private void AddExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (!Read(ExcludeStart.Text, out var start) || !Read(ExcludeEnd.Text, out var end) || start < 0 || end <= start)
        { MessageBox.Show(this, "Enter a valid excluded interval with end greater than start.", "Excluded interval", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        _exclusions.Add(new(start, end, string.IsNullOrWhiteSpace(ExcludeReason.Text) ? "Excluded" : ExcludeReason.Text.Trim()));
        UpdateExclusions(); RefreshAnalysis();
    }

    private void RemoveExclusion_Click(object sender, RoutedEventArgs e)
    {
        if (ExclusionList.SelectedItem is not ExcludedIntervalEntry selected) return;
        _exclusions.Remove(selected); UpdateExclusions(); RefreshAnalysis();
    }

    private void UpdateExclusions()
    {
        ExclusionList.ItemsSource = _exclusions.Select(x => x with { }).ToArray();
        ExclusionsPanel.Visibility = _exclusions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || MarkerList.SelectedItem is not MarkerEntry selected) return;
        var start = Math.Max(0, selected.Marker.OffsetSeconds - 10);
        var end = Math.Min(_duration, selected.Marker.OffsetSeconds + 10);
        if (end <= start) end = Math.Min(_duration, start + 1);
        RangeStart.Text = start.ToString("F3", CultureInfo.InvariantCulture);
        RangeEnd.Text = end.ToString("F3", CultureInfo.InvariantCulture); RefreshAnalysis();
    }

    private void Plot_SizeChanged(object sender, SizeChangedEventArgs e) { if (!_initializing) { RefreshAnalysis(); DrawStutter(); } }

    private void RefreshAnalysis()
    {
        if (!Read(RangeStart.Text, out var start) || !Read(RangeEnd.Text, out var end) || start < 0 || end <= start || end > _duration + 0.001)
        {
            AnalysisText.Text = $"Enter a valid range from 0 to {_duration:F3} seconds."; return;
        }
        var intervals = _exclusions.Select(x => new ReportExclusion(x.Start, x.End, x.Reason)).ToArray();
        var analysis = ReportTimeline.Analyze(_report, start, end, intervals);
        AnalysisText.Text = $"Selection {start:F2}–{end:F2} s (retained samples/windows only): hardware samples {analysis.HardwareSamples}; complete frame windows {analysis.FrameWindows}; frames {analysis.Frames}; CPU avg {F(analysis.AverageCpuPercent)}%; GPU avg {F(analysis.AverageGpuPercent)}%; memory avg {F(analysis.AverageMemoryMiB)} MiB; disk read/write avg {F(analysis.AverageDiskReadMiBPerSecond)}/{F(analysis.AverageDiskWriteMiBPerSecond)} MiB/s; weighted frame time {F(analysis.AverageFrameTimeMs)} ms; frames >50 ms {analysis.FramesOver50Ms?.ToString(CultureInfo.InvariantCulture) ?? "—"}. {string.Join(" ", analysis.Limitations)}";
        SampleTable.ItemsSource = _report.Rows.Where(r => r.SampleEndSeconds is double t && t >= start && t <= end)
            .Select(r => new SampleRow(F(r.SampleEndSeconds), F(r.CpuPercent), F(r.GpuPercent), F(r.MemoryUsedMiB), F(r.DiskReadMiBPerSecond), F(r.DiskWriteMiBPerSecond), F(r.FrameMaxMs), r.FramesOver50Ms?.ToString(CultureInfo.InvariantCulture) ?? "—",
                intervals.Any(x => r.SampleEndSeconds >= x.StartSeconds && r.SampleEndSeconds < x.EndSeconds) ? "Excluded" : "Retained")).ToArray();
        DrawPlots();
    }

    private void NetworkTarget_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing) RefreshAnalysis();
    }
    private void RefreshNetwork(double start, double end, IReadOnlyList<ReportExclusion> exclusions)
    {
        var all = (_report.Network?.Observations ?? []).Where(r => r.Target == NetworkTarget.SelectedItem as string &&
            r.SendSeconds >= start && r.ReceiveSeconds <= end).ToArray();
        bool Included(NetworkObservation r) => !exclusions.Any(x => r.ReceiveSeconds >= x.StartSeconds && r.SendSeconds < x.EndSeconds);
        var summary = NetworkReports.AnalyzeSelection(all, NetworkTarget.SelectedItem as string ?? "", start, end, exclusions);
        NetworkSummary.Text = (_report.Network is null ? "No network diagnostics in this report. " : summary + " ") + NetworkReports.Explanation;
        NetworkTable.ItemsSource = all.Select(r => new { r.Target, r.Address, r.SendSeconds, r.ReceiveSeconds, r.Status, r.RttMs, Selection = Included(r) ? "Retained" : "Excluded" }).ToArray();
        NetworkPlot.Children.Clear();
        var width = Math.Max(300, NetworkPlot.ActualWidth);
        const double left = 58, right = 14;
        var plotWidth = Math.Max(1, width - left - right);
        double X(double seconds) => left + (seconds - start) / (end - start) * plotWidth;
        var max = Math.Max(1, all.Select(r => r.RttMs ?? 0).DefaultIfEmpty().Max());
        AddText(NetworkPlot, $"RTT ms (top {max:F1}); red = timeout, orange = error; range {start:F1}–{end:F1}s", 8, 3, 11, Brushes.White);
        foreach (var r in all)
        {
            var point = new Ellipse { Width = 5, Height = 5, Fill = !Included(r) ? Brushes.Gray : r.Status == "Success" ? Brushes.LightBlue : r.Status == "TimedOut" ? Brushes.Red : Brushes.Orange };
            Canvas.SetLeft(point, X(r.SendSeconds) - 2.5);
            Canvas.SetTop(point, r.RttMs is double ms ? 120 - ms / max * 90 : 128); NetworkPlot.Children.Add(point);
        }
        foreach (var marker in _report.Markers.Where(m => m.OffsetSeconds >= start && m.OffsetSeconds <= end))
        {
            var x = X(marker.OffsetSeconds);
            AddLine(NetworkPlot, x, 25, x, 135, Brushes.Yellow, 1);
        }
    }

    private void DrawPlots()
    {
        if (_initializing || !Read(RangeStart.Text, out var start) || !Read(RangeEnd.Text, out var end) || end <= start) return;
        var excluded = _exclusions.ToArray();
        RefreshNetwork(start, end, excluded.Select(x => new ReportExclusion(x.Start, x.End, x.Reason)).ToArray());
        var rows = _report.Rows;
        Draw(FramePlot, "Frame window time (ms) · max / window average", start, end, excluded,
            (r) => (r.FrameWindowStartSeconds is double s && r.FrameWindowEndSeconds is double e ? (s + e) / 2 : null, r.FrameMaxMs),
            (r) => (r.FrameWindowStartSeconds is double s && r.FrameWindowEndSeconds is double e ? (s + e) / 2 : null, r.Fps is > 0 ? 1000 / r.Fps.Value : null),
            new SolidColorBrush(Color.FromRgb(255, 111, 97)), new SolidColorBrush(Color.FromRgb(93, 191, 181)), "Max interval", "Window average");
        Draw(CpuGpuPlot, "System load (%) · CPU / GPU", start, end, excluded,
            r => (r.SampleEndSeconds, r.CpuPercent), r => (r.SampleEndSeconds, r.GpuPercent),
            new SolidColorBrush(Color.FromRgb(91, 167, 255)), new SolidColorBrush(Color.FromRgb(191, 143, 255)), "CPU", "GPU", fixedMax: 100);
        Draw(MemoryPlot, "Physical memory used (MiB)", start, end, excluded,
            r => (r.SampleEndSeconds, r.MemoryUsedMiB), r => ((double?)null, (double?)null),
            new SolidColorBrush(Color.FromRgb(255, 190, 92)), new SolidColorBrush(Colors.Transparent), "Memory used", "");
        Draw(DiskPlot, "System disk activity (MiB/s) · read / write", start, end, excluded,
            r => (r.SampleEndSeconds, r.DiskReadMiBPerSecond), r => (r.SampleEndSeconds, r.DiskWriteMiBPerSecond),
            new SolidColorBrush(Color.FromRgb(99, 204, 133)), new SolidColorBrush(Color.FromRgb(255, 111, 183)), "Read", "Write");
    }

    private void Draw(Canvas canvas, string title, double start, double end, ExcludedIntervalEntry[] excluded,
        Func<ReportTimelineRow, (double? X, double? Y)> first, Func<ReportTimelineRow, (double? X, double? Y)> second,
        Brush color1, Brush color2, string legend1, string legend2, double? fixedMax = null)
    {
        canvas.Children.Clear();
        var width = Math.Max(300, canvas.ActualWidth); var height = Math.Max(100, canvas.ActualHeight);
        const double left = 58, right = 14, top = 24, bottom = 22;
        var plotW = Math.Max(1, width - left - right); var plotH = Math.Max(1, height - top - bottom);
        var selected = _report.Rows.ToArray();
        var data = selected.SelectMany(r => new[] { first(r), second(r) })
            .Where(point => point.X is double x && x >= start && x <= end && point.Y is double y && double.IsFinite(y))
            .Select(point => point.Y!.Value).ToArray();
        var max = fixedMax ?? (data.Length == 0 ? 1 : Math.Max(1, data.Max() * 1.1));
        AddText(canvas, title, 8, 3, 12, Brushes.White);
        AddText(canvas, legend1, Math.Max(left + 8, width - (legend2.Length > 0 ? 230 : 110)), 5, 10, color1);
        if (legend2.Length > 0) AddText(canvas, legend2, width - 115, 5, 10, color2);
        AddText(canvas, "" + max.ToString("F1", CultureInfo.InvariantCulture), 4, top, 10, Brushes.LightGray);
        AddText(canvas, "0", 30, top + plotH - 8, 10, Brushes.LightGray);
        AddLine(canvas, left, top, left, top + plotH, new SolidColorBrush(Color.FromRgb(86, 102, 110)), 1);
        AddLine(canvas, left, top + plotH, width - right, top + plotH, new SolidColorBrush(Color.FromRgb(86, 102, 110)), 1);
        AddText(canvas, $"{start:F1}s", left, height - 18, 10, Brushes.LightGray);
        AddText(canvas, $"{end:F1}s", width - right - 36, height - 18, 10, Brushes.LightGray);
        foreach (var x in excluded.Where(x => x.End > start && x.Start < end))
        {
            var xa = MapX(Math.Max(start, x.Start)); var xb = MapX(Math.Min(end, x.End));
            var rect = new Rectangle { Width = Math.Max(2, xb - xa), Height = plotH, Fill = new SolidColorBrush(Color.FromArgb(48, 240, 180, 80)), IsHitTestVisible = false };
            Canvas.SetLeft(rect, xa); Canvas.SetTop(rect, top); canvas.Children.Add(rect);
        }
        PlotSeries(canvas, selected.Select(first).ToArray(), start, end, left, top, plotW, plotH, max, color1, excluded);
        PlotSeries(canvas, selected.Select(second).ToArray(), start, end, left, top, plotW, plotH, max, color2, excluded);
        if (data.Length == 0) AddText(canvas, "No available readings in this report.", left + 12, top + 12, 11, Brushes.LightGray);
        foreach (var marker in _report.Markers.Where(m => m.OffsetSeconds >= start && m.OffsetSeconds <= end))
        {
            var x = MapX(marker.OffsetSeconds); AddLine(canvas, x, top, x, top + plotH, new SolidColorBrush(Color.FromArgb(145, 250, 250, 250)), 1);
        }
        double MapX(double t) => left + Math.Clamp((t - start) / (end - start), 0, 1) * plotW;
    }

    private void PlotSeries(Canvas canvas, (double? X, double? Y)[] values, double start, double end, double left, double top, double plotW, double plotH, double max, Brush brush, ExcludedIntervalEntry[] excluded)
    {
        Point? previous = null; double? previousXValue = null;
        for (var i = 0; i < values.Length; i++)
        {
            var (x, y) = values[i];
            if (x is null || y is null || x < start || x > end || !double.IsFinite(y.Value)) { previous = null; previousXValue = null; continue; }
            var point = new Point(left + (x.Value - start) / (end - start) * plotW, top + plotH - Math.Clamp(y.Value / max, 0, 1) * plotH);
            var spansExcluded = previousXValue is double priorTime && excluded.Any(ex => ex.End > priorTime && ex.Start < x.Value);
            var gapTooWide = previousXValue is double prior && x.Value - prior > 5;
            if (previous is Point p && previousXValue is double px && i > 0 && values[i - 1].X is double priorX && px == priorX && !spansExcluded && !gapTooWide)
                AddLine(canvas, p.X, p.Y, point.X, point.Y, brush, 1.5);
            var dot = new Ellipse { Width = 5, Height = 5, Fill = brush, ToolTip = $"{x.Value:F3}s: {y.Value:F2}" };
            Canvas.SetLeft(dot, point.X - 2.5); Canvas.SetTop(dot, point.Y - 2.5); canvas.Children.Add(dot);
            previous = point; previousXValue = x;
        }
    }

    private static void AddText(Canvas canvas, string value, double x, double y, double size, Brush brush)
    {
        var text = new TextBlock { Text = value, FontSize = size, Foreground = brush };
        Canvas.SetLeft(text, x); Canvas.SetTop(text, y); canvas.Children.Add(text);
    }
    private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, IsHitTestVisible = false };
        canvas.Children.Add(line);
    }
    private static bool Read(string? text, out double value) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private static string F(double? value) => value?.ToString("F2", CultureInfo.InvariantCulture) ?? "—";
    private sealed record MarkerEntry(ReportMarker Marker, string Display);
    private sealed record ExcludedIntervalEntry(double Start, double End, string Reason)
    { public string Display => $"{Start:F2}–{End:F2} s · {Reason}"; }
    private sealed record SampleRow(string Time, string Cpu, string Gpu, string Memory, string DiskRead, string DiskWrite, string FrameMax, string Over50, string Selection);
}
