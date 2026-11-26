using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JamesTools;
using JamesTools.Core.Diagnostics;

internal static class InvestigationUiTests
{
    public static Task Run(bool saveRender)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var frames = new List<FrameTelemetry>();
        var events = new List<InvestigationEvent>();
        var nextSpike = 4.01;
        for (double time = 0; time < 78;)
        {
            var spike = time >= nextSpike - .000001;
            var ms = spike ? 80d : Math.Min(10, (nextSpike - time) * 1000);
            if (ms <= .00001) ms = 10;
            frames.Add(new(time, time + .3, "QPC", "7:1:Application", ms, spike ? 70 : 5, 1, 3, 0, 3, 4, 5, 6, 0, "Composed", "Application"));
            if (spike)
            {
                events.Add(new() { TimestampSeconds = time - .02, ReceiptSeconds = time + .02, Source = "USB test provider", Category = "usb", EventType = "reset", DeviceId = "device-fixture", Summary = "Synthetic port reset", TimestampBasis = "QPC", UncertaintyMs = 1 });
                nextSpike += 4.01;
            }
            time += ms / 1000;
        }
        var report = InvestigationAnalysis.Analyze(frames, events, [], [], [new("USB test provider", "available", "Deterministic fixture")], 78);
        var window = new StutterInvestigatorWindow();
        var results = (StackPanel)typeof(StutterInvestigatorWindow).GetField("results", flags)!.GetValue(window)!;
        var export = (Button)typeof(StutterInvestigatorWindow).GetField("export", flags)!.GetValue(window)!;
        if (results.Visibility != Visibility.Collapsed || export.Visibility != Visibility.Collapsed)
            throw new Exception("Investigator should hide results and export until it has a report");
        typeof(StutterInvestigatorWindow).GetMethod("Display", flags)!.Invoke(window, [report]);
        if (results.Visibility != Visibility.Visible || export.Visibility != Visibility.Visible)
            throw new Exception("Investigator did not reveal results and export for a completed report");
        var findings = (TextBlock)typeof(StutterInvestigatorWindow).GetField("findings", flags)!.GetValue(window)!;
        if (!findings.Text.Contains("4.010")) throw new Exception("Investigator does not show the detected repeating interval");
        var stalls = (ListBox)typeof(StutterInvestigatorWindow).GetField("stalls", flags)!.GetValue(window)!;
        if (stalls.Items.Count < 18) throw new Exception("Investigator stall list omitted fixture stalls");
        stalls.SelectedIndex = 0;
        var details = (TextBox)typeof(StutterInvestigatorWindow).GetField("details", flags)!.GetValue(window)!;
        if (!details.Text.Contains("Synthetic port reset")) throw new Exception("Selecting a stall does not show nearby USB evidence");
        window.ApplyTemplate();
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1100, 900)); content.Arrange(new Rect(0, 0, 1100, 900)); content.UpdateLayout();
        if (saveRender)
        {
            var directory = Path.GetFullPath("artifacts/validation"); Directory.CreateDirectory(directory);
            var image = new RenderTargetBitmap(1100, 900, 96, 96, PixelFormats.Pbgra32);
            var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle(window.Background, null, new Rect(0, 0, 1100, 900));
            image.Render(background); image.Render(content);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, "stutter-investigator.png")); png.Save(file);
        }
        window.Close();
        Console.WriteLine("PASS Stutter Investigator layout, 4.01-second finding, stall selection, nearby evidence and idle close");
        return Task.CompletedTask;
    }
}
