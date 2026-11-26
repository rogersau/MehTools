using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace JamesTools;

public sealed class ActionMenu : Button
{
    public ActionMenu()
    {
        ContextMenu = new ContextMenu();
        SetResourceReference(StyleProperty, typeof(Button));
    }

    public MenuItem Add(string label, RoutedEventHandler action)
    {
        var item = new MenuItem { Header = label };
        item.Click += action;
        ContextMenu.Items.Add(item);
        return item;
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (ContextMenu is not { } menu) return;
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
