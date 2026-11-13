using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using JamesTools.Core.Automation;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;
using JamesTools.Core.Platform;

namespace JamesTools;

public partial class MainWindow
{
    private PerformanceOverlay? _overlay;

    private void BrowsePresentMon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose the PresentMon program", Filter = "Executable files (*.exe)|*.exe" };
        if (picker.ShowDialog(this) == true) PresentMonPath.Text = picker.FileName;
    }

    private void ClearPresentMon_Click(object sender, RoutedEventArgs e) => PresentMonPath.Clear();

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();

    private void ToggleOverlay()
    {
        if (_overlay is not null) { _overlay.Close(); return; }

        LiveFrameSampler? frames = null;
        GameRule? selectedGame = OverlayGame.SelectedItem as GameRule;
        var effective = EffectiveGameOptionsResolver.Resolve(_settings, selectedGame);
        var wantsFrames = effective.OverlayShowFps || effective.OverlayShowLatency;
        var configuredPresentMon = (PresentMonPath?.Text ?? _settings.PresentMonPath ?? "").Trim();
        string? frameMessage = wantsFrames ? null : "Monitor open. FPS and frame delay are switched off.";

        if (wantsFrames)
            frameMessage = TryStartFrameSampler(selectedGame, configuredPresentMon, out frames);
        else if (configuredPresentMon.Length > 0 && !File.Exists(configuredPresentMon))
            frameMessage = "PresentMon override not found: " + configuredPresentMon + ". FPS and frame delay are off, so CPU/GPU readings still open.";

        if (selectedGame is not null) _settings.LastOverlayGamePath = selectedGame.ExecutablePath;
        try
        {
            var options = new OverlayOptions(effective.OverlayShowCpu, effective.OverlayShowGpu, effective.OverlayShowFps, effective.OverlayShowLatency,
                _settings.OverlayOpacity, _settings.OverlayLeft, _settings.OverlayTop, _settings.OverlayClickThrough);
            _overlay = new PerformanceOverlay(frames, options);
            if (frameMessage is not null) _overlay.FrameStatus.Text = frameMessage;
            _overlay.Closed += (_, _) =>
            {
                if (_overlay is not null)
                {
                    _settings.OverlayLeft = _overlay.Left;
                    _settings.OverlayTop = _overlay.Top;
                }
                _overlay = null;
                OverlayButton.Content = "Show monitor";
                OverlayGame.IsEnabled = true;
                OverlayStatus.Text = "Monitor closed. Position saved (Save settings to keep).";
            };
            _overlay.Show();
            OverlayGame.IsEnabled = false;
            OverlayButton.Content = "Hide monitor";
            OverlayStatus.Text = frameMessage ?? "Monitor open. Waiting for FPS…";
        }
        catch
        {
            frames?.Dispose();
            _overlay = null;
            throw;
        }
    }

    private static string? TryStartFrameSampler(GameRule? game, string configuredPresentMon, out LiveFrameSampler? frames)
    {
        frames = null;
        if (configuredPresentMon.Length > 0 && !File.Exists(configuredPresentMon))
            return "PresentMon override not found: " + configuredPresentMon + ". CPU/GPU readings still work; choose a valid file or Clear to use the bundled copy.";

        if (game is null || string.IsNullOrWhiteSpace(game.ExecutablePath))
            return "Choose a game under Live numbers to see FPS/frame delay. CPU and GPU readings still work.";

        var presentMon = PresentMonLocator.Resolve(configuredPresentMon);
        if (presentMon is null)
            return "PresentMon wasn't found (the bundled copy may be missing). CPU/GPU readings still work; browse for PresentMon to add FPS.";

        try
        {
            var processes = ProcessSnapshot.Capture();
            var targetPath = AutomationPolicy.ResolveObservedTarget(game, processes, out var targetNotice);
            if (targetPath is null) return targetNotice + " CPU/GPU readings still work.";
            frames = new LiveFrameSampler(presentMon, targetPath);
            return targetNotice + " PresentMon filters by executable name, so identically named processes can still be included.";
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return "Couldn't start PresentMon: " + ex.Message + " CPU/GPU readings still work.";
        }
    }


    private void OverlayPresetMinimal_Click(object sender, RoutedEventArgs e) { _settings.OverlayShowFps = true; _settings.OverlayShowLatency = false; _settings.OverlayShowCpu = false; _settings.OverlayShowGpu = false; RefreshOverlayBindings(); }
    private void OverlayPresetFps_Click(object sender, RoutedEventArgs e) { _settings.OverlayShowFps = true; _settings.OverlayShowLatency = true; _settings.OverlayShowCpu = false; _settings.OverlayShowGpu = false; RefreshOverlayBindings(); }
    private void OverlayPresetFull_Click(object sender, RoutedEventArgs e) { _settings.OverlayShowFps = true; _settings.OverlayShowLatency = true; _settings.OverlayShowCpu = true; _settings.OverlayShowGpu = true; RefreshOverlayBindings(); }
    private void RefreshOverlayBindings()
    {
        var overlayGame = OverlayGame?.SelectedItem as GameRule;
        var performanceGame = PerformanceGame?.SelectedItem as GameRule;
        DataContext = null;
        DataContext = _settings;
        RestoreGameSelection(OverlayGame, overlayGame);
        RestoreGameSelection(PerformanceGame, performanceGame);
        if (overlayGame is not null || performanceGame is not null)
        {
            // ItemsSource bindings can settle after the DataContext swap; restore again once they have.
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                RestoreGameSelection(OverlayGame, overlayGame);
                RestoreGameSelection(PerformanceGame, performanceGame);
            }));
        }
        OverlayStatus.Text = "Overlay preset applied. Reopen monitor to apply option changes.";
    }

    private void RestoreGameSelection(System.Windows.Controls.ComboBox? box, GameRule? previous)
    {
        if (box is null || previous is null) return;
        var match = _settings.Games.FirstOrDefault(game => ReferenceEquals(game, previous))
            ?? _settings.Games.FirstOrDefault(game => string.Equals(game.ExecutablePath, previous.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (match is not null) box.SelectedItem = match;
    }
}
