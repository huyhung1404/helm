using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Helm.App.Services;
using Wpf.Ui.Controls;

namespace Helm.App.Views.Tabs;

/// <summary>
/// One page's tab: its header on a strip and the presenter that shows the page. Both are made once and move with the
/// tab between windows, so the page keeps everything it shows (scroll position, an open item, a terminal).
/// </summary>
internal sealed class ToolTab
{
    private readonly Func<object> _page;
    private NavigationViewContentPresenter? _presenter;

    public ToolTab(Type pageType, TabLook look, Func<object> page)
    {
        PageType = pageType;
        Look = look;
        _page = page;
        Header = new TabHeader(this);
    }

    public Type PageType { get; }

    public TabLook Look { get; }

    public TabHeader Header { get; }

    /// <summary>The window's tab host the tab is in; null while closed.</summary>
    public TabHost? Host { get; internal set; }

    /// <summary>The page in a WPF-UI presenter, as NavigationView shows pages (its scrolling and transitions).</summary>
    public NavigationViewContentPresenter Presenter => _presenter ??= CreatePresenter();

    private NavigationViewContentPresenter CreatePresenter()
    {
        var presenter = new NavigationViewContentPresenter
        {
            Transition = Wpf.Ui.Animations.Transition.FadeInWithSlide,
            TransitionDuration = 150,
            JournalOwnership = System.Windows.Navigation.JournalOwnership.OwnsJournal,
            Focusable = false,
        };
        presenter.Navigated += (_, e) =>
        {
            UseThemeForeground(e.Content);
            // Nothing to go back to inside a tab: the journal would only keep the page twice.
            while (presenter.CanGoBack) presenter.RemoveBackEntry();
        };
        presenter.Loaded += (_, _) => presenter.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ConstrainWidth(presenter));
        presenter.Navigate(_page());
        return presenter;
    }

    /// <summary>
    /// A page's text inherits its colour from WPF-UI's scroll viewer around the page. A page that opts out of it
    /// (ScrollViewer.CanContentScroll="False", e.g. Vault, which scrolls its own list) inherits nothing and falls back
    /// to WPF's default black, unreadable in the dark theme. Every page therefore gets the theme's text colour unless it
    /// sets its own.
    /// </summary>
    private static void UseThemeForeground(object? page)
    {
        if (page is Page p && p.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue)
            p.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
    }

    /// <summary>
    /// WPF-UI hosts pages in a scroll viewer that also scrolls horizontally, so a page is measured with infinite
    /// width and one long line (e.g. an update tile) makes the whole page wider than the window — cards then get
    /// clipped on the right. Disabling horizontal scrolling on the presenter's own scroll viewers (not those inside the
    /// page) makes every page fit the viewport.
    /// </summary>
    private static void ConstrainWidth(DependencyObject presenter)
    {
        foreach (var viewer in Descendants<ScrollViewer>(presenter))
            viewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    /// <summary>The presenter's own elements, without going into the page.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Page) continue;
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
