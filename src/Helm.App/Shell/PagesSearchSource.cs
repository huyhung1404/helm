using Helm.Core.Modules;
using Helm.General;
using Helm.Shell.Search;
using Helm.Shell.Tabs;

namespace Helm.App.Shell;

/// <summary>
/// What the title bar's search finds on Windows: Home, Bug reports, every tool, and General's parts (each opens General
/// on that part). Tools' own settings cards can join later with an <see cref="ISearchSource"/> of their own.
/// </summary>
internal sealed class PagesSearchSource(IEnumerable<IHelmModule> modules, Func<GeneralViewModel> general) : ISearchSource
{
    public IEnumerable<SearchEntry> Entries()
    {
        yield return new SearchEntry("Home", "Helm", ShellPages.Home);
        yield return new SearchEntry("General", "Helm", ShellPages.General);
        yield return new SearchEntry("Bug reports", "Helm", ShellPages.BugReports);
        foreach (var module in modules) yield return new SearchEntry(module.DisplayName, module.Group.DisplayName(), module.Id);
        foreach (var section in general().Sections) yield return new SearchEntry(section.Title, "General", ShellPages.General, section.Id, 1);
    }
}
