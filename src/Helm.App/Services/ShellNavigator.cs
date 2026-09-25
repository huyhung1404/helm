using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Helm.App.Services;

internal interface IShellNavigator
{
    /// <summary>Shows the main window and navigates to <paramref name="pageType"/>; optionally scrolls to a settings card.</summary>
    void Navigate(Type pageType, FrameworkElement? focus = null);
}

/// <summary>Bridges view models to the MainWindow's NavigationView without them touching the view.</summary>
internal sealed class ShellNavigator : IShellNavigator
{
    private NavigationView? _navigation;
    private Action? _showWindow;

    public void Attach(NavigationView navigation, Action showWindow)
    {
        _navigation = navigation;
        _showWindow = showWindow;
    }

    public void Navigate(Type pageType, FrameworkElement? focus = null)
    {
        if (_navigation is null) return;
        _showWindow?.Invoke();
        _navigation.Navigate(pageType);
        if (focus is null) return;

        // Let the page load and lay out before scrolling to the card.
        _navigation.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Reveal(focus));
    }

    private static void Reveal(FrameworkElement target)
    {
        for (DependencyObject? p = target; p is not null; p = LogicalTreeHelperParent(p))
        {
            if (p is Expander expander) expander.IsExpanded = true;
        }
        target.BringIntoView();
        if (target.Focusable) target.Focus();

        var original = target.Opacity;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = false,
        };
        target.BeginAnimation(UIElement.OpacityProperty, animation);
        target.Opacity = original;
    }

    private static DependencyObject? LogicalTreeHelperParent(DependencyObject d) =>
        LogicalTreeHelper.GetParent(d) ?? (d is FrameworkElement fe ? fe.TemplatedParent ?? System.Windows.Media.VisualTreeHelper.GetParent(fe) : null);
}
