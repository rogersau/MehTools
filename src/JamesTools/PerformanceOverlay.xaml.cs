using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using JamesTools.Core.Diagnostics;

namespace JamesTools;

public partial class PerformanceOverlay : Window
{
    private readonly LiveHardwareSampler _hardware;
    private readonly LiveFrameSampler? _frames;
    private readonly DispatcherTimer _timer;

    public PerformanceOverlay(LiveFrameSampler? frames)
    {
        InitializeComponent();
        _frames = frames;
        _hardware = new LiveHardwareSampler();
        Left = SystemParameters.WorkArea.Left + 16;
        Top = SystemParameters.WorkArea.Top + 16;
        _hardware.Sample();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += UpdateReadings;
        _timer.Start();
        FrameStatus.Text = frames is null ? "Choose PresentMon and a game to see FPS." : "Waiting for FPS…";
        Closed += (_, _) => { _timer.Stop(); _frames?.Dispose(); _hardware.Dispose(); };
    }

    private void UpdateReadings(object? sender, EventArgs e)
    {
        var (cpu, gpu) = _hardware.Sample();
        CpuValue.Text = cpu is { } c ? $"{c:F0}%" : "—";
        GpuValue.Text = gpu is { } g ? $"{g:F0}%" : "—";
        var reading = _frames?.Sample();
        FpsValue.Text = reading?.Fps is { } fps ? $"{fps:F0}" : "—";
        LatencyValue.Text = reading?.Latency is { } latency ? $"{latency:F1} ms" : "—";
        if (reading is { } result) FrameStatus.Text = result.Status;
    }

    private void Drag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
