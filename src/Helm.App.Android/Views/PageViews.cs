using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Helm.App.Android.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.Views;

/// <summary>Builds the view of a tab's page, for the main window's tabs and for tool windows alike.</summary>
internal static class PageViews
{
    /// <summary>A page that lays itself out (fixed bars, its own scrolling) gets the whole area and no app bar.</summary>
    public static bool IsFull(ShellTab tab) =>
        tab.Page is Helm.Shell.BugReports.BugReportsViewModel or Helm.Shell.BugReports.BugReportFormViewModel
        || tab.Page is ModulePage page && typeof(Helm.Core.Ui.IFullPage).IsAssignableFrom(page.PageType);

    private static BugReports.AndroidBugReporter Reporter => App.Services.GetRequiredService<BugReports.AndroidBugReporter>();

    /// <summary>The page, inside its own scroller unless it is a full page (so each tab keeps its scroll position).</summary>
    public static Control Build(ShellTab tab)
    {
        Control page = tab.Page switch
        {
            Helm.Home.HomeViewModel home => new Helm.Home.HomeView(home),
            Helm.General.GeneralViewModel general => new Helm.General.GeneralView(general),
            // Bug reports: the shared views (Helm.Shell.UI); the phone's part is AndroidBugReporter.
            Helm.Shell.BugReports.BugReportsViewModel bugs => new Helm.BugReports.Ui.BugReportsView(bugs, Reporter),
            Helm.Shell.BugReports.BugReportFormViewModel form => new Helm.BugReports.Ui.BugReportFormView(form, Reporter),
            ModulePage module => new ModulePageTemplate().Build(module)!,
            _ => new TextBlock { Text = tab.Title },
        };
        // As the shell's content control did: a page that does not set its own data context gets its view model.
        if (page.DataContext is null) page.DataContext = tab.Page;
        // Home and General lay themselves out (their own scrolling and margins), under the app bar.
        if (IsFull(tab) || tab.Page is Helm.Home.HomeViewModel or Helm.General.GeneralViewModel) return page;
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Margin = new Thickness(16, 16, 16, 24), Child = page },
        };
    }
}
