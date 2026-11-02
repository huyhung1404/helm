using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>pc:MoreMenu.OpensOnClick="True"</c> on a button with a ContextMenu (the ⋯ buttons): a left click opens the menu
/// under the button, as a drop-down.
/// </summary>
public static class MoreMenu
{
    public static readonly DependencyProperty OpensOnClickProperty =
        DependencyProperty.RegisterAttached("OpensOnClick", typeof(bool), typeof(MoreMenu), new PropertyMetadata(false, OnChanged));

    public static bool GetOpensOnClick(DependencyObject o) => (bool)o.GetValue(OpensOnClickProperty);

    public static void SetOpensOnClick(DependencyObject o, bool value) => o.SetValue(OpensOnClickProperty, value);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ButtonBase button) return;
        if ((bool)e.NewValue) button.Click += OnClick;
        else button.Click -= OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
