using System.ComponentModel;
using System.Windows;
using JamesTools.Core.Configuration;
using JamesTools.Core.Diagnostics;

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

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_overlay is not null) { _overlay.Close(); return; }
        LiveFrameSampler? frames = null;
        var path = PresentMonPath.Text.Trim();
        if (!string.IsNullOrEmpty(path))
        {
            if (OverlayGame.SelectedItem is not GameRule game || string.IsNullOrWhiteSpace(game.ExecutablePath))
            {
                OverlayStatus.Text = "Choose a game to see FPS, or clear PresentMon to show only CPU and GPU.";
                return;
            }
            try { frames = new LiveFrameSampler(path, game.ExecutablePath); }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                OverlayStatus.Text = "Couldn't start PresentMon: " + ex.Message;
                return;
            }
        }
        try
        {
            _overlay = new PerformanceOverlay(frames);
            _overlay.Closed += (_, _) =>
            {
                _overlay = null;
                OverlayButton.Content = "Show monitor";
                OverlayGame.IsEnabled = true;
                OverlayStatus.Text = "Monitor closed.";
            };
            _overlay.Show();
            OverlayGame.IsEnabled = false;
            OverlayButton.Content = "Hide monitor";
            OverlayStatus.Text = frames is null ? "Monitor open. Add PresentMon and choose a game to see FPS." : "Monitor open. Waiting for FPS…";
        }
        catch
        {
            frames?.Dispose();
            _overlay = null;
            throw;
        }
    }
}
