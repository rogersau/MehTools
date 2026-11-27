using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace JamesTools;

public sealed class StutterInvestigatorWindow : Window
{
    private readonly ComboBox applications = new() { MinWidth = 280, DisplayMemberPath = "Label", IsTextSearchEnabled = true };
    private readonly CheckBox deep = new() { Content = "Deep Investigation" };
    private readonly CheckBox usb = new() { Content = "Deep USB tracing" };
    private readonly TextBox duration = new() { Text = "60", Width = 65, MaxLength = 3 };
    private readonly TextBox threshold = new() { Text = "33.333", Width = 75, MaxLength = 8 };
    private readonly TextBox conditions = new() { MinWidth = 250, MaxLength = 2000, ToolTip = "Scene, workload and settings held constant" };
    private readonly TextBox change = new() { MinWidth = 260, MaxLength = 1000, ToolTip = "The one configuration change you made between recordings" };
    private readonly Button start = new() { Content = "Start recording" }, stop = new() { Content = "Stop and analyse" };
    private readonly Button copyForAi = new() { Content = "Copy for AI" };
    private readonly Button export = new() { Content = "Export report…" };
    private readonly Button compare = new() { Content = "Compare with baseline" };
    private readonly StackPanel results = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly TextBlock findings = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ListBox stalls = new() { MinHeight = 100, Height = 145, DisplayMemberPath = "Label" };
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox technical = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly InvestigationTimeline timeline = new();
    private readonly Slider position = new() { Minimum = 0, Maximum = 60, Width = 260 };
    private readonly ComboBox zoom = new() { ItemsSource = new[] { "Full recording", "20 seconds", "5 seconds", "1 second" }, SelectedIndex = 0, Width = 150 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string? presentMon;
    private PerformanceLogger? logger;
    private InvestigationReport? report, baseline;
    private InvestigationComparisonReport? comparison;
    private string? performanceReport;
    private bool busy, closed, closeAfterStop;
    private double capturedDuration = 60;
    private sealed record ApplicationChoice(string Label, string? Path, int? Pid);
    private sealed record StallChoice(FrameTelemetry Frame, string Label);

    public StutterInvestigatorWindow(string? presentMonPath = null)
    {
        presentMon = PresentMonLocator.Resolve(presentMonPath);
        WindowTheme.Apply(this);
        Title = "Stutter Investigator — Meh Tools"; Width = 1130; Height = 920; MinWidth = 700; MinHeight = 550;
        var content = new StackPanel { Margin = new Thickness(16) };
        Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        content.Children.Add(new TextBlock { Text = "Stutter Investigator", FontSize = 23, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "Record frame timing and nearby Windows events to investigate stutters.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        Field(content, "Application", applications);
        applications.DropDownOpened += (_, _) => RefreshApplications();
        var controls = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; content.Children.Add(controls);
        start.Style = (Style)FindResource("AccentButton");
        stop.Style = start.Style;
        copyForAi.Style = start.Style;
        controls.Children.Add(start); controls.Children.Add(stop); controls.Children.Add(copyForAi); controls.Children.Add(export);
        var openReport = AddButton(controls, "Open report…", (_, _) => OpenReport(false));
        openReport.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(Visibility)) { Source = start });
        start.Click += async (_, _) => await StartAsync();
        stop.Click += async (_, _) => await StopAsync();
        copyForAi.Click += (_, _) => CopyForAi();
        export.Click += (_, _) => Export();
        content.Children.Add(status);

        var settings = new StackPanel();
        content.Children.Add(new Expander { Header = "Recording settings", Content = settings, Padding = new Thickness(0, 7, 0, 0), Margin = new Thickness(0, 0, 0, 8) });
        var inputs = new WrapPanel(); settings.Children.Add(inputs);
        Field(inputs, "Duration (seconds)", duration); Field(inputs, "Stall threshold (ms)", threshold);
        Field(settings, "Scene, workload or settings (optional)", conditions);
        settings.Children.Add(deep);
        var deepOptions = new StackPanel { Visibility = Visibility.Collapsed };
        settings.Children.Add(deepOptions);
        deepOptions.Children.Add(new TextBlock { Text = "Deep tracing requires running Meh Tools as administrator. Raw traces stay local and may contain sensitive details.", TextWrapping = TextWrapping.Wrap });
        deepOptions.Children.Add(usb);
        deep.Checked += (_, _) => deepOptions.Visibility = Visibility.Visible;
        deep.Unchecked += (_, _) => { usb.IsChecked = false; deepOptions.Visibility = Visibility.Collapsed; };

        var retest = new StackPanel();
        content.Children.Add(new Expander { Header = "Compare a retest", Content = retest, Padding = new Thickness(0, 7, 0, 0), Margin = new Thickness(0, 0, 0, 8) });
        Field(retest, "Change made between recordings", change);
        var baselineControls = new WrapPanel(); retest.Children.Add(baselineControls);
        var chooseBaseline = AddButton(baselineControls, "Choose baseline…", (_, _) => OpenReport(true));
        chooseBaseline.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = start });
        baselineControls.Children.Add(compare);
        compare.Click += (_, _) => Compare();

        content.Children.Add(results);
        results.Children.Add(findings);
        results.Children.Add(timeline);
        var navigation = new WrapPanel(); results.Children.Add(navigation);
        Field(navigation, "Zoom", zoom); Field(navigation, "Window position", position);
        zoom.SelectionChanged += (_, _) => DrawRange(); position.ValueChanged += (_, _) => DrawRange();
        timeline.TimeSelected += SelectTime;
        var stallList = new StackPanel(); results.Children.Add(stallList);
        stallList.Children.Add(new TextBlock { Text = "Significant stalls", FontWeight = FontWeights.SemiBold });
        stallList.Children.Add(stalls);
        stallList.SetBinding(VisibilityProperty, new System.Windows.Data.Binding(nameof(stalls.HasItems)) { Source = stalls, Converter = new BooleanToVisibilityConverter() });
        results.Children.Add(details);
        stalls.SelectionChanged += (_, _) =>
        {
            if (stalls.SelectedItem is not StallChoice item) return;
            zoom.SelectedIndex = 2; position.Value = Math.Max(0, item.Frame.OffsetSeconds - 2.5); SelectTime(item.Frame.OffsetSeconds);
        };
        results.Children.Add(new Expander { Header = "Technical evidence", Content = technical, Margin = new Thickness(0, 10, 0, 0) });
        timer.Tick += async (_, _) =>
        {
            if (logger is null || busy) return;
            if (!logger.IsRecording) { await StopAsync(); return; }
            var snapshot = logger.GetInvestigationSnapshot();
            capturedDuration = Math.Max(1, snapshot.DurationSeconds);
            timeline.Frames = snapshot.Frames; timeline.Events = snapshot.Events; timeline.Hardware = snapshot.Hardware;
            position.Maximum = capturedDuration; DrawRange();
            status.Text = $"Recording {snapshot.DurationSeconds:F1} / {duration.Text} s · {snapshot.Events.Count} recent events retained for live view. Stop at any time.";
            details.Text = string.Join(Environment.NewLine, snapshot.Events.TakeLast(12).Select(EventText));
            technical.Text = string.Join(Environment.NewLine, snapshot.Collectors.Select(c => $"{c.Source}: {c.Availability} — {c.Detail}"));
        };
        Closing += OnClosing;
        Closed += (_, _) => { closed = true; timer.Stop(); };
        RefreshApplications();
        SetControls(true);
    }

    private static void Field(Panel panel, string label, System.Windows.Controls.Control control)
    {
        var field = new StackPanel { Margin = new Thickness(0, 5, 12, 5) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 3) }); field.Children.Add(control); panel.Children.Add(field);
    }
    private static Button AddButton(Panel panel, string text, RoutedEventHandler action)
    {
        var button = new Button { Content = text }; button.Click += action; panel.Children.Add(button); return button;
    }
    private void RefreshApplications()
    {
        if (logger is not null || busy) return;
        var previous = applications.SelectedItem as ApplicationChoice;
        var choices = new[] { new ApplicationChoice("System activity only (no frame capture)", null, null) }
            .Concat(ProcessSnapshot.Capture().Where(p => p.FullPath is not null).OrderBy(p => p.Name)
                .Select(p => new ApplicationChoice($"{p.Name} · PID {p.Id}", p.FullPath, p.Id))).ToArray();
        applications.ItemsSource = choices;
        applications.SelectedItem = choices.FirstOrDefault(p => p.Pid == previous?.Pid && p.Path == previous?.Path) ?? choices[0];
    }
    private async Task StartAsync()
    {
        if (busy || logger is not null) return;
        busy = true; SetControls(false);
        try
        {
            if (!int.TryParse(duration.Text, out var seconds) || seconds is < 1 or > 300)
                throw new ArgumentException("Enter a duration from 1 to 300 seconds; 30–90 is recommended.");
            if (!double.TryParse(threshold.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                throw new ArgumentException("Enter the stall threshold in milliseconds using a decimal point.");
            var selected = (ApplicationChoice?)applications.SelectedItem;
            if (selected?.Pid is { } pid && !ProcessSnapshot.Capture().Any(p => p.Id == pid && p.FullPath == selected.Path))
                throw new InvalidOperationException("The selected application exited or changed. Open the list to choose it again.");
            var options = new DiagnosticOptions { InvestigateStutter = true, DeepInvestigation = deep.IsChecked == true,
                DeepUsbTracing = usb.IsChecked == true, InvestigationDurationSeconds = seconds, StutterThresholdMs = ms };
            options.Validate();
            options.Conditions.Scene = conditions.Text; options.Conditions.Notes = change.Text;
            status.Text = "Starting collectors and capturing device inventory…";
            logger = await Task.Run(() => PerformanceLogger.StartAsync(selected?.Path, presentMon, null, new NetworkProbeOptions(false, []), options));
            logger.SetContext(conditions.Text, change.Text);
            report = null; comparison = null; performanceReport = null; findings.Text = ""; stalls.ItemsSource = null;
            details.Clear(); technical.Clear(); timeline.Frames = []; timeline.Events = []; timeline.Hardware = [];
            results.Visibility = Visibility.Visible;
            capturedDuration = seconds; position.Value = 0; position.Maximum = seconds; timer.Start(); stop.IsEnabled = true;
            status.Text = "Recording. Collector availability appears below; unavailable sources do not imply an absence of events.";
        }
        catch (Exception ex) { status.Text = "Could not start: " + ex.Message; SetControls(true); }
        finally
        {
            busy = false;
            if (closeAfterStop)
            {
                if (logger is not null) await StopAsync();
                else { closed = true; Close(); }
            }
        }
    }
    private void SetControls(bool idle)
    {
        start.IsEnabled = idle; applications.IsEnabled = idle; deep.IsEnabled = idle; duration.IsEnabled = idle;
        threshold.IsEnabled = idle; usb.IsEnabled = idle; stop.IsEnabled = !idle && logger is not null;
        start.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        stop.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        conditions.IsEnabled = idle; change.IsEnabled = idle;
        copyForAi.Visibility = export.Visibility = idle && report is not null ? Visibility.Visible : Visibility.Collapsed;
        compare.Visibility = idle && report is not null && baseline is not null ? Visibility.Visible : Visibility.Collapsed;
        results.Visibility = logger is not null || report is not null ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task StopAsync()
    {
        if (busy || logger is null) return;
        busy = true; timer.Stop(); stop.IsEnabled = false; status.Text = "Stopping collectors and analysing evidence…";
        var capture = logger;
        try
        {
            performanceReport = await capture.StopAsync();
            report = capture.CompletedInvestigation;
            timeline.Hardware = capture.GetInvestigationSnapshot().Hardware;
            if (report is not null) Display(report);
            status.Text = "Capture saved locally. Export a summary to share; raw traces remain separate. " + (capture.RawExportError is null ? "" : "Raw export incomplete: " + capture.RawExportError);
        }
        catch (Exception ex) { status.Text = "Capture ended with an error: " + ex.Message; }
        finally
        {
            try { await capture.DisposeAsync(); } catch { }
            logger = null; busy = false; SetControls(true);
            if (closeAfterStop && !closed) { closed = true; Close(); }
        }
    }
    private void Display(InvestigationReport value)
    {
        report = value; comparison = null; capturedDuration = Math.Max(1, value.DurationSeconds); position.Maximum = capturedDuration; position.Value = 0;
        timeline.Frames = value.Spikes; timeline.Events = value.Events; timeline.Hardware = value.Hardware;
        findings.Text = string.Join(Environment.NewLine, value.Findings.Take(7));
        stalls.ItemsSource = value.Clusters.OrderByDescending(s => s.Peak.FrameMs).Take(1000)
            .Select(s => new StallChoice(s.Peak, $"{s.StartSeconds:F3} s · {s.Peak.FrameMs:F2} ms · {s.FrameCount} slow frame(s) · CPU busy {s.Peak.CpuBusyMs?.ToString("F1") ?? "unknown"} ms · GPU busy {s.Peak.GpuBusyMs?.ToString("F1") ?? "unknown"} ms")).ToArray();
        technical.Text = InvestigationReportWriter.ToText(value);
        if (value.Devices.Any(d => d.LocalName is not null))
            technical.Text += "\n\nLocal device labels (not exported)\n" + string.Join("\n", value.Devices.Where(d => d.LocalName is not null).Select(d => $"{d.LocalName} · {d.Id} · {d.Topology}"));
        details.Text = value.Periodicity.Assessment + Environment.NewLine + string.Join(Environment.NewLine, value.Limitations.Take(6));
        if (logger is null) SetControls(true);
        DrawRange();
    }
    private void DrawRange()
    {
        var span = zoom.SelectedIndex switch { 1 => 20d, 2 => 5d, 3 => 1d, _ => capturedDuration };
        span = Math.Min(capturedDuration, span);
        timeline.StartSeconds = zoom.SelectedIndex == 0 ? 0 : Math.Clamp(position.Value, 0, Math.Max(0, capturedDuration - span));
        timeline.EndSeconds = timeline.StartSeconds + span; timeline.InvalidateVisual();
    }
    private void SelectTime(double seconds)
    {
        timeline.SelectedSeconds = seconds; timeline.InvalidateVisual();
        var events = timeline.Events.Where(e => Math.Abs(e.TimestampSeconds - seconds) <= .5).OrderBy(e => e.TimestampSeconds).Take(100);
        var frame = timeline.Frames.MinBy(f => Math.Abs(f.OffsetSeconds - seconds));
        var text = $"Evidence around {seconds:F3} s (±0.5 s). Delivery and timestamp uncertainty must be considered.\n";
        if (frame is not null && Math.Abs(frame.OffsetSeconds - seconds) <= .5)
        {
            var diagnosis = FrameDiagnosis.Investigate(frame);
            text += diagnosis.Observation + "\nPossible explanation: " + diagnosis.PossibleCause + "\n" + diagnosis.ConfidenceAndLimitations + "\n";
        }
        var lines = events.Select(EventText).ToArray(); text += lines.Length > 0 ? string.Join("\n", lines) : "No retained Windows event in this window. This does not rule out unmeasured activity.";
        foreach (var sample in timeline.Hardware.Where(h => h.CounterWindowStartSeconds <= seconds && h.CollectionEndSeconds >= seconds))
            text += $"\nHardware interval {sample.CounterWindowStartSeconds:F3}–{sample.CollectionEndSeconds:F3} s: " + string.Join(", ", sample.Values.Where(v => v.Value is not null && v.Key is "cpuPercent" or "gpuPercent" or "diskLatencyMs" or "gameCpuPercent").Select(v => $"{v.Key}={v.Value:F2}"));
        details.Text = text;
    }
    private static string EventText(InvestigationEvent e) => $"{e.TimestampSeconds:F3} s · {e.Category}/{e.EventType} · {e.Summary} [{e.TimestampBasis}; {e.Quality}; delivered {e.ReceiptSeconds:F3} s]";
    private void OpenReport(bool asBaseline)
    {
        if (logger is not null || busy) return;
        var dialog = new OpenFileDialog { Filter = "Investigation reports|*.json;*.md;*.txt|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var loaded = InvestigationExports.Read(dialog.FileName);
            if (asBaseline) { baseline = loaded; comparison = null; SetControls(true); status.Text = "Baseline selected. Enter the change you made and repeat the same workload."; }
            else { performanceReport = null; timeline.Hardware = []; Display(loaded); status.Text = "Saved investigation opened."; }
        }
        catch (Exception ex) { status.Text = "Could not open report: " + ex.Message; }
    }
    private void Compare()
    {
        if (report is null || baseline is null) { status.Text = "Choose a baseline and record or open the after session first."; return; }
        if (string.IsNullOrWhiteSpace(change.Text)) { status.Text = "Describe the configuration change you made between recordings."; return; }
        comparison = InvestigationComparison.Compare(baseline, report, change.Text);
        details.Text = comparison.Conclusion + "\n\n" + string.Join("\n", comparison.MetricDeltas.Select(d => $"{d.Metric}: {d.Before:F2} → {d.After:F2} {d.Unit}")) + "\n\n" + string.Join("\n", comparison.Limitations);
        technical.Text = JsonSerializer.Serialize(comparison, new JsonSerializerOptions { WriteIndented = true });
        status.Text = "Before/after comparison. Workload differences and unmeasured evidence limit the conclusion.";
    }
    private void CopyForAi()
    {
        if (report is null) return;
        try
        {
            var text = performanceReport ?? InvestigationReportWriter.ToText(report);
            if (comparison is not null)
                text += "\n\n## Controlled comparison\n" + comparison.Conclusion + "\n" +
                    string.Join("\n", comparison.MetricDeltas.Select(d => $"- {d.Metric}: {d.Before:F2} → {d.After:F2} {d.Unit}")) + "\n" + string.Join("\n", comparison.Limitations);
            System.Windows.Clipboard.SetText(text);
            status.Text = "Report copied to clipboard — ready to paste into an AI chat.";
        }
        catch (Exception ex) { status.Text = "Couldn't copy report: " + ex.Message; }
    }
    private void Export()
    {
        if (report is null) return;
        var dialog = new SaveFileDialog { Filter = "Readable report|*.md", FileName = "Stutter-Investigation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var json = Path.ChangeExtension(dialog.FileName, ".json");
            var csv = Path.ChangeExtension(dialog.FileName, ".events.csv");
            var compared = Path.ChangeExtension(dialog.FileName, ".comparison.json");
            if (File.Exists(json) || File.Exists(csv) || comparison is not null && File.Exists(compared))
                throw new IOException("Companion export files already exist. Choose a new report filename to keep previous exports intact.");
            File.WriteAllText(dialog.FileName, performanceReport ?? InvestigationReportWriter.ToText(report));
            using (var writer = new StreamWriter(new FileStream(json, FileMode.CreateNew))) writer.Write(InvestigationReportWriter.ToJson(report));
            using (var writer = new StreamWriter(new FileStream(csv, FileMode.CreateNew))) writer.Write(InvestigationReportWriter.EventsCsv(report));
            if (comparison is not null)
            {
                using var writer = new StreamWriter(new FileStream(compared, FileMode.CreateNew));
                writer.Write(JsonSerializer.Serialize(comparison, new JsonSerializerOptions { WriteIndented = true }));
                File.AppendAllText(dialog.FileName, "\n\n## Controlled comparison\n" + comparison.Conclusion + "\n" +
                    string.Join("\n", comparison.MetricDeltas.Select(d => $"- {d.Metric}: {d.Before:F2} → {d.After:F2} {d.Unit}")) + "\n" + string.Join("\n", comparison.Limitations));
            }
            status.Text = "Exported readable report, structured JSON and event CSV. Review user-entered notes before sharing.";
        }
        catch (Exception ex) { status.Text = "Export failed: " + ex.Message; }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        if (logger is not null || busy)
        {
            e.Cancel = true; closeAfterStop = true;
            if (!busy) _ = StopAsync();
        }
    }
}
