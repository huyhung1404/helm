using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Modules;
using Helm.Shell.Tabs;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>
/// A tool as the shell shows it (menu row, tab, search result): no platform types, so each app maps its own modules
/// (<c>IHelmModule</c>, <c>IAndroidModule</c>) onto it. <see cref="IsEnabled"/> follows the module's switch.
/// </summary>
public sealed partial class ShellToolItem(string id, string name, ModuleGroup group, IImage? icon, bool isEnabled = true) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public ModuleGroup Group { get; } = group;

    /// <summary>The tool's picture (<c>Helm.ToolIcons</c>); null draws <see cref="Glyph"/>.</summary>
    public IImage? Icon { get; } = icon;

    public Symbol Glyph { get; init; } = Symbol.Apps;

    [ObservableProperty]
    private bool _isEnabled = isEnabled;

    /// <summary>The tool for <paramref name="module"/>, its <see cref="IsEnabled"/> kept in step with the module's.</summary>
    public static ShellToolItem From(IModule module, IImage? icon)
    {
        var item = new ShellToolItem(module.Id, module.DisplayName, module.Group, icon, module.IsEnabled);
        module.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IModule.IsEnabled)) item.IsEnabled = module.IsEnabled;
        };
        return item;
    }
}

/// <summary>A page a tab can show: its key (<see cref="ShellPages"/> or a tool's id), title and icon.</summary>
public sealed record ShellPageInfo(string Key, string Title, IImage? Icon, Symbol Glyph, ShellToolItem? Tool = null);

/// <summary>
/// Every page of the shell in menu order: Home, the tools grouped by <see cref="ModuleGroup"/> (groups in the enum's
/// order, tools by name), then Bug reports and General.
/// </summary>
public sealed class ShellCatalog
{
    private readonly Dictionary<string, ShellPageInfo> _pages = [];

    public ShellCatalog(IEnumerable<ShellToolItem> tools)
    {
        Home = Add(new ShellPageInfo(ShellPages.Home, "Home", null, Symbol.Home));
        Groups = tools
            .GroupBy(t => t.Group)
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<ShellPageInfo>)g.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(t => Add(new ShellPageInfo(t.Id, t.Name, t.Icon, t.Glyph, t)))
                .ToList()))
            .ToList();
        BugReports = Add(new ShellPageInfo(ShellPages.BugReports, Helm.Shell.BugReports.BugReportsServices.PageTitle, null, Symbol.Bug));
        General = Add(new ShellPageInfo(ShellPages.General, "General", null, Symbol.Settings));
    }

    public ShellPageInfo Home { get; }

    public IReadOnlyList<(ModuleGroup Group, IReadOnlyList<ShellPageInfo> Pages)> Groups { get; }

    public ShellPageInfo BugReports { get; }

    public ShellPageInfo General { get; }

    public IEnumerable<ShellPageInfo> Tools => Groups.SelectMany(g => g.Pages);

    /// <summary>The tab strip's "+": Home, every tool by name, General.</summary>
    public IReadOnlyList<ShellPageInfo> Launchable =>
        [Home, .. Tools.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase), BugReports, General];

    public ShellPageInfo? Find(string key) => _pages.GetValueOrDefault(key);

    public bool Knows(string key) => _pages.ContainsKey(key);

    public string TitleOf(string key) => Find(key)?.Title ?? key;

    private ShellPageInfo Add(ShellPageInfo page)
    {
        _pages[page.Key] = page;
        return page;
    }
}
