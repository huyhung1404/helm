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
            .ToList();
    }

    /// <summary>Every group is always present so the nav tree mirrors PowerToys, even when empty.</summary>
    public IReadOnlyList<NavGroup> Groups { get; }

    public IReadOnlyList<SearchResult> Search(string text) => _search.Search(text);

    [RelayCommand]
    private void OpenFeedback() => _launcher.OpenUrl(AppInfo.IssuesUrl);

    private static IReadOnlyList<ExtraNavPage> ExtraPagesFor(ModuleGroup group) => group switch
    {
        ModuleGroup.Advanced => [new ExtraNavPage("Diagnostics", SymbolRegular.Bug24, typeof(DiagnosticsPage))],
        _ => [],
    };
}
