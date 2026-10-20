using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Helm.Core.Services;
using Wpf.Ui.Controls;

namespace Helm.App.Services;

internal interface IShellNavigator
{
    /// <summary>Shows <paramref name="pageType"/>'s tab (opening it if needed) and its window; optionally scrolls to a settings card.</summary>
    void Navigate(Type pageType, FrameworkElement? focus = null);

    /// <summary>Navigates to <paramref name="pageType"/> and reveals the card whose header title is <paramref name="cardTitle"/>.</summary>
    void NavigateToCard(Type pageType, string cardTitle);
}

/// <summary>Bridges view models and modules to the windows' tabs without them touching the views.</summary>
internal sealed class ShellNavigator(SearchService search, TabManager tabs) : IShellNavigator, IShellNavigation
{
    public void ShowPage(Type pageType) => Navigate(pageType);

    public void NavigateToCard(Type pageType, string cardTitle) => Navigate(pageType, search.FindCard(pageType, cardTitle));

    public void Navigate(Type pageType, FrameworkElement? focus = null)
    {
        if (!tabs.IsAttached) return;
        tabs.Open(pageType);
        if (focus is null) return;

        // Let the page load and lay out before scrolling to the card.
        focus.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Reveal(focus));
    }

    private static void Reveal(FrameworkElement target)
    {
        for (DependencyObject? p = target; p is not null; p = LogicalTreeHelperParent(p))
        {
            if (p is Expander expander) expander.IsExpanded = true;
            if (p is CardExpander cardExpander) cardExpander.IsExpanded = true;
        }
        target.BringIntoView();
        if (target.Focusable) target.Focus();

        var animation = new System.Windows.Media.Animation.DoubleAnimation(0.35, 1.0, TimeSpan.FromMilliseconds(700));
        target.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private static DependencyObject? LogicalTreeHelperParent(DependencyObject d) =>
        LogicalTreeHelper.GetParent(d) ?? (d is FrameworkElement fe ? fe.TemplatedParent ?? System.Windows.Media.VisualTreeHelper.GetParent(fe) : null);
}
