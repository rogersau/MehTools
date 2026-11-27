using System.Windows;

namespace JamesTools;

public partial class MainWindow
{
    private void StutterInvestigator_Click(object sender, RoutedEventArgs e)
    {
        if (_performanceLogger is not null || _performanceBusy)
        {
            System.Windows.MessageBox.Show(this, "Stop the active performance log before starting an investigation.", "Performance recording active");
            return;
        }
        new StutterInvestigatorWindow(_settings.PresentMonPath) { Owner = this }.ShowDialog();
    }
}
