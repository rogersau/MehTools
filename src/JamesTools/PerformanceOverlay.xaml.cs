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
    private readonly bool _showCpu, _showGpu, _showFps, _showLatency;

    public PerformanceOverlay(LiveFrameSampler? frames, OverlayOptions? options = null)
    {
        InitializeComponent();
        options ??= new OverlayOptions();
        _frames = frames;
        _showCpu = options.ShowCpu; _showGpu = options.ShowGpu; _showFps = options.ShowFps; _showLatency = options.ShowLatency;
        ApplyMetricsVisibility();
        _hardware = new LiveHardwareSampler();
        Opacity = Math.Clamp(options.Opacity, 0.3, 1.0);
        Left = double.IsFinite(options.Left) ? options.Left : SystemParameters.WorkArea.Left + 16;
        Top = double.IsFinite(options.Top) ? options.Top : SystemParameters.WorkArea.Top + 16;
        _hardware.Sample();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += UpdateReadings;
        _timer.Start();
        FrameStatus.Text = frames is null ? "Select a game (and PresentMon, if needed) to see FPS." : "Waiting for FPS…";
        SourceInitialized += (_, _) => { if (options.ClickThrough) EnableClickThrough(); };
        Closed += (_, _) => { _timer.Stop(); _frames?.Dispose(); _hardware.Dispose(); };
    }

    private void ApplyMetricsVisibility()
    {
        void Set(bool show, params UIElement[] els) { foreach (var e in els) e.Visibility = show ? Visibility.Visible : Visibility.Collapsed; }
        Set(_showFps, FpsLabel, FpsValue);
        Set(_showGpu, GpuLabel, GpuValue);
        Set(_showCpu, CpuLabel, CpuValue);
        Set(_showLatency, LatencyLabel, LatencyValue);
    }

    private void EnableClickThrough()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED);
        }
        catch { }
    }

    private void UpdateReadings(object? sender, EventArgs e)
    {
        var (cpu, gpu) = _hardware.Sample();
        if (_showCpu) CpuValue.Text = cpu is { } c ? $"{c:F0}%" : "—";
        if (_showGpu) GpuValue.Text = gpu is { } g ? $"{g:F0}%" : "—";
        var reading = _frames?.Sample();
        if (_showFps) FpsValue.Text = reading?.Fps is { } fps ? $"{fps:F0}" : "—";
        if (_showLatency) LatencyValue.Text = reading?.Latency is { } latency ? $"{latency:F1} ms" : "—";
        if (reading is { } result) FrameStatus.Text = result.Status;
    }

    private void Drag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

public sealed record OverlayOptions(bool ShowCpu = true, bool ShowGpu = true, bool ShowFps = true, bool ShowLatency = true, double Opacity = 0.92, double Left = double.NaN, double Top = double.NaN, bool ClickThrough = false);
