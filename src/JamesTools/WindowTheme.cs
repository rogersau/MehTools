using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JamesTools;

/// <summary>One opt-in presentation policy for app-owned windows; never changes system theme settings.</summary>
public static class WindowTheme
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WindowTheme), new PropertyMetadata(false, Changed));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static void Apply(Window window) => SetEnabled(window, true);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs e)
    {
        if (value is not Window window || e.NewValue is not true) return;
        window.SetResourceReference(FrameworkElement.StyleProperty, "AppWindowStyle");
        window.SourceInitialized += (_, _) => {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            int dark = 1;
            // Unsupported title-bar attributes fail harmlessly; standard OS window controls are retained.
            nint hwnd = new WindowInteropHelper(window).Handle;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            Caption(35, "BackgroundBrush"); Caption(36, "TextBrush");
            void Caption(int attribute, string brush)
            {
                if (window.TryFindResource(brush) is not System.Windows.Media.SolidColorBrush value) return;
                var color = value.Color; int rgb = color.R | (color.G << 8) | (color.B << 16);
                _ = DwmSetWindowAttribute(hwnd, attribute, ref rgb, sizeof(int));
            }
        };
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
