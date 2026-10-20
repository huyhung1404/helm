using System.Windows.Controls;
using Helm.App.Services;
using Helm.App.Views.Tabs;

namespace Helm.App.Views.Pages;

/// <summary>
/// The only page the main window's NavigationView shows: the window's tabs (<see cref="TabHost"/>). The menu opens
/// pages as tabs here instead of navigating; each tab scrolls its own page, so this page does not scroll.
/// </summary>
internal sealed class TabsPage : Page
{
    public TabsPage(TabManager tabs)
    {
        Title = "Helm";
        ScrollViewer.SetCanContentScroll(this, false);
        Host = new TabHost(tabs);
        Content = Host;
    }

    public TabHost Host { get; }
}
