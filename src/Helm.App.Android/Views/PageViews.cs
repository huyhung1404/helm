using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Helm.App.Android.ViewModels;

namespace Helm.App.Android.Views;

/// <summary>Builds the view of a tab's page, for the main window's tabs and for tool windows alike.</summary>
internal static class PageViews
{
    /// <summary>A page that lays itself out (fixed bars, its own scrolling) gets the whole area and no app bar.</summary>
    public static bool IsFull(ShellTab tab) => tab.Page is ModulePage page && typeof(Helm.Core.Ui.IFullPage).IsAssignableFrom(page.PageType);

    /// <summary>The page, inside its own scroller unless it is a full page (so each tab keeps its scroll position).</summary>
    public static Control Build(ShellTab tab)
    {
        Control page = tab.Page switch
        {
            HomeViewModel => new HomeView(),
            GeneralViewModel => new GeneralView(),
            ModulePage module => new ModulePageTemplate().Build(module)!,
            _ => new TextBlock { Text = tab.Title },
        };
        // As the shell's content control did: a page that does not set its own data context gets its view model.
        if (page.DataContext is null) page.DataContext = tab.Page;
        if (IsFull(tab)) return page;
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Margin = new Thickness(16, 16, 16, 24), Child = page },
        };
    }
}
