using System.Windows;
using System.Windows.Controls;
using JamesTools.Core.Platform;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;

namespace JamesTools;

internal sealed record GameChoice(string Name, string Path)
{
    public override string ToString() => $"{Name}  —  {System.IO.Path.GetFileName(Path)}    {Path}";
}

internal static class GamePicker
{
    public static GameChoice? Browse(Window owner)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Choose the game executable" };
        return picker.ShowDialog(owner) == true
            ? new GameChoice(Path.GetFileNameWithoutExtension(picker.FileName), picker.FileName)
            : null;
    }

    public static async Task<GameChoice?> PickRunningAsync(Window owner, AppDetectionOptions options)
    {
        GameChoice[] choices;
        try
        {
            choices = await Task.Run(() =>
            {
                var catalog = LibraryGameDetector.Discover(options);
                return LibraryGameDetector.FindRunningProcesses(catalog, ProcessSnapshot.Capture())
                    .DistinctBy(match => match.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(match => match.Game.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(match => match.ExecutablePath, StringComparer.OrdinalIgnoreCase)
                    .Select(match => new GameChoice(match.Game.Name, match.ExecutablePath)).ToArray();
            });
        }
        catch (Exception ex)
        {
            if (owner.IsVisible)
                MessageBox.Show(owner, ex.Message + "\n\nYou can still browse to the game's .exe.",
                    "Couldn't find open games", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (!owner.IsVisible) return null;
        if (choices.Length == 0)
        {
            MessageBox.Show(owner, "Open your game and try again, or use Browse for game to select its .exe.",
                "No open game found", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var dialog = new Window
        {
            Owner = owner, Title = "Choose an open game", Width = 760, Height = 400,
            MinWidth = 540, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (System.Windows.Media.Brush)owner.FindResource("BackgroundBrush"),
            Foreground = (System.Windows.Media.Brush)owner.FindResource("TextBrush")
        };
        WindowTheme.Apply(dialog);
        var layout = new DockPanel { Margin = new Thickness(20) };
        var hint = new TextBlock { Text = "Choose the executable that runs while you play. Skip launchers and helper programs.",
            Foreground = (System.Windows.Media.Brush)owner.FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(hint, Dock.Top);
        layout.Children.Add(hint);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var add = new Button { Content = "Choose game", IsDefault = true, Style = (Style)owner.FindResource("AccentButton") };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0) };
        buttons.Children.Add(add); buttons.Children.Add(cancel); layout.Children.Add(buttons);
        var list = new ListBox { ItemsSource = choices, SelectedIndex = choices.Length == 1 ? 0 : -1,
            BorderThickness = new Thickness(0), Padding = new Thickness(5) };
        layout.Children.Add(list);
        add.IsEnabled = list.SelectedItem is GameChoice;
        list.SelectionChanged += (_, _) => add.IsEnabled = list.SelectedItem is GameChoice;
        add.Click += (_, _) => { if (list.SelectedItem is GameChoice) dialog.DialogResult = true; };
        dialog.Content = layout;
        return dialog.ShowDialog() == true ? list.SelectedItem as GameChoice : null;
    }
}
