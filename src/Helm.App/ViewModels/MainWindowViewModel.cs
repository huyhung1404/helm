using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.App.Services;
using Helm.App.Views.Pages;
using Helm.Core.Modules;
using Helm.Core.Services;
using Wpf.Ui.Controls;

namespace Helm.App.ViewModels;

/// <summary>A non-module page listed inside a nav group (e.g. Diagnostics under Advanced).</summary>
internal sealed record ExtraNavPage(string Title, SymbolRegular Icon, Type PageType);

internal sealed record NavGroup(ModuleGroup Group, IReadOnlyList<IHelmModule> Modules, IReadOnlyList<ExtraNavPage> ExtraPages);

internal sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly SearchService _search;
    private readonly IProcessLauncher _launcher;

    public MainWindowViewModel(IModuleHost modules, SearchService search, IProcessLauncher launcher)
    {
        _search = search;
        _launcher = launcher;
        Groups = Enum.GetValues<ModuleGroup>()
            .Select(g => new NavGroup(
                g,
                modules.Modules.Where(m => m.Group == g).OrderBy(m => m.DisplayName).ToList(),
                ExtraPagesFor(g)))
            .Where(g => ShowEmptyGroups || g.Modules.Count > 0 || g.ExtraPages.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Set to true to show every group (with a "coming soon" placeholder) like PowerToys. Off while Helm only
    /// ships Zones, so the nav only lists groups that contain something.
    /// </summary>
    private const bool ShowEmptyGroups = false;

    /// <summary>Nav groups built from the registered modules' <see cref="ModuleGroup"/>.</summary>
    public IReadOnlyList<NavGroup> Groups { get; }

    public IReadOnlyList<SearchResult> Search(string text) => _search.Search(text);

    [RelayCommand]
    private void OpenFeedback() => _launcher.OpenUrl(AppInfo.IssuesUrl);

    /// <summary>
    /// Non-module pages inside groups. The Diagnostics page (Advanced) is hidden for now; it still opens with
    /// "--page Diagnostics". Re-enable: ModuleGroup.Advanced => [new ExtraNavPage("Diagnostics", SymbolRegular.Bug24, typeof(DiagnosticsPage))]
    /// </summary>
    private static IReadOnlyList<ExtraNavPage> ExtraPagesFor(ModuleGroup group) => group switch
    {
        _ => [],
    };
}
