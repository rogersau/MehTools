using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using JamesTools.Core.Configuration;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace JamesTools;

public partial class MainWindow
{
    private bool _gamesCollectionTracked;

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_gamesCollectionTracked) return;
        _gamesCollectionTracked = true;
        _settings.Games.CollectionChanged += Games_CollectionChanged;
    }

    private void Games_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is null || e.NewItems.Count == 0) return;
        if (e.NewItems[0] is not GameRule added) return;

        // Let the grid finish binding the new row before selecting and revealing it.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!_settings.Games.Contains(added)) return;
            GameGrid.SelectedItem = added;
            GameGrid.ScrollIntoView(added);
        }));
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        Save_Click(this, new RoutedEventArgs());
    }

    private void WholeNumber_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
        {
            if (c is >= '0' and <= '9') continue;
            e.Handled = true;
            return;
        }
    }

    private void WholeNumber_Pasting(object sender, System.Windows.DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)) &&
            e.DataObject.GetData(typeof(string)) is string text &&
            text.Any(c => c is < '0' or > '9'))
            e.CancelCommand();
    }
}
