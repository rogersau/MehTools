using System.Windows;

namespace JamesTools;

public partial class MainWindow
{
    private McpSettingsWindow? _mcpSettingsWindow;

    private void McpSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_mcpSettingsWindow is null)
        {
            _mcpSettingsWindow = new McpSettingsWindow(_dataDirectory) { Owner = this };
            _mcpSettingsWindow.Closed += (_, _) => _mcpSettingsWindow = null;
            _mcpSettingsWindow.Show();
        }
        else
        {
            if (_mcpSettingsWindow.WindowState == WindowState.Minimized)
                _mcpSettingsWindow.WindowState = WindowState.Normal;
            _mcpSettingsWindow.Activate();
        }
    }
}
