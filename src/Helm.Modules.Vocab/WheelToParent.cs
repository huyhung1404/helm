using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Helm.Modules.Vocab;

/// <summary>
/// <c>pc:WheelToParent.IsEnabled="True"</c> on a sideways-only ScrollViewer (the tree of parts): the mouse wheel goes on
/// scrolling the page instead of stopping dead over it; Shift+wheel still scrolls it sideways.
/// </summary>
public static class WheelToParent
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(WheelToParent), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not ScrollViewer viewer) return;
        if ((bool)e.NewValue) viewer.PreviewMouseWheel += OnWheel;
        else viewer.PreviewMouseWheel -= OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        if (VisualTreeHelper.GetParent(viewer) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
    }
}
