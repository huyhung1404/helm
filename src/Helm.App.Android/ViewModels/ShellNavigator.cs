using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using Helm.App.Android.Services;
using Helm.Core.Modules;
using Helm.Core.Services;
using Helm.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Helm.App.Android.ViewModels;

/// <summary>
/// A tool's page: <see cref="PageType"/> is its settings page (<see cref="IAndroidModule.PageType"/>) or its content
/// page (<see cref="IModuleContent.ContentPageType"/>); the shell's data template creates it from DI.
/// </summary>
public sealed record ModulePage(IAndroidModule Module, Type PageType);

/// <summary>
/// An open page (Home, General, a tool's content or settings page). <see cref="Key"/> names it ("home", "general",
/// "content:notes", "settings:notes"): a page is open at most once, in a tab or in a window of its own.
/// </summary>
public sealed partial class ShellTab(string key, object page, string title, Symbol icon, IImage? image, IAndroidModule? module) : ObservableObject
{
    public string Key { get; } = key;

    /// <summary>The page's view model (<see cref="HomeViewModel"/>, <see cref="GeneralViewModel"/>, <see cref="ModulePage"/>).</summary>
    public object Page { get; } = page;

    public string Title { get; } = title;

    public Symbol Icon { get; } = icon;

    public IImage? Image { get; } = image;

    /// <summary>The tool the page belongs to; null for Home and General (they always stay in Helm's main window).</summary>
    public IAndroidModule? Module { get; } = module;

    public bool CanOpenInWindow => Module is not null;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>The view the shell built for this tab, kept while the tab is open so the page keeps its state.</summary>
    internal Control? View { get; set; }
}

/// <summary>
/// Helm's tabs on the phone. Like a browser, every page opens in a tab of its own and stays as it was while another tab
/// is shown; there is no tab strip (a phone has no room for one): the app bar's tab button and the drawer open the tab
/// list. A tool's tab can move to a window of its own (<see cref="ToolWindows"/>), e.g. beside Helm in split screen.
/// MainView builds each tab's view once (see <see cref="ShellTab.View"/>), so switching tabs keeps every page's state.
/// </summary>
public sealed partial class ShellNavigator(IServiceProvider services, ISettingsStoreFactory settings) : ObservableObject, IShellNavigation
{
    private readonly ISettingsStore<GeneralSettings> _general = settings.Get<GeneralSettings>(GeneralSettings.StoreId);
    private bool _restored;

    public ObservableCollection<ShellTab> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(Title), nameof(IsHome))]
    private ShellTab? _activeTab;

    /// <summary>The shown tab's page view model.</summary>
    public object? CurrentPage => ActiveTab?.Page;

    public string Title => ActiveTab?.Title ?? "Helm";

    public bool IsHome => CurrentPage is HomeViewModel;

    public int TabCount => Tabs.Count;

    public void GoHome() => Open(Describe("home")!);

    public void GoGeneral() => Open(Describe("general")!);

    /// <summary>The tool's settings page (Home → Utilities).</summary>
    public void GoModule(IAndroidModule module) => Open(Describe(SettingsKey(module))!);

    /// <summary>What the drawer and Quick access open: the content page when the tool has one, else its settings.</summary>
    public void GoModuleContent(IAndroidModule module) =>
        Open(Describe(module is IModuleContent ? ContentKey(module) : SettingsKey(module))!);

    /// <summary><see cref="IShellNavigation"/> for modules: a module's settings or content page type.</summary>
    public void ShowPage(Type pageType)
    {
        foreach (var module in services.GetServices<IAndroidModule>())
        {
            if (module.PageType == pageType) Open(Describe(SettingsKey(module))!);
            else if ((module as IModuleContent)?.ContentPageType == pageType) Open(Describe(ContentKey(module))!);
            else continue;
            return;
        }
    }

    /// <summary>Shows the page's tab, opening it if needed; a page that has a window of its own brings that window up.</summary>
    public void Open(ShellTab tab)
    {
        if (Tabs.FirstOrDefault(t => t.Key == tab.Key) is { } open)
        {
            Activate(open);
            return;
        }
        if (services.GetRequiredService<ToolWindows>().BringToFront(tab.Key)) return;
        Tabs.Add(tab);
        OnPropertyChanged(nameof(TabCount));
        Activate(tab);
    }

    public void Activate(ShellTab tab)
    {
        if (ActiveTab is { } old) old.IsActive = false;
        tab.IsActive = true;
        ActiveTab = tab;
    }

    /// <summary>Closes the tab (its page is built afresh next time); Home comes back when the last tab closes.</summary>
    public void Close(ShellTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        Tabs.RemoveAt(index);
        tab.IsActive = false;
        tab.View = null;
        OnPropertyChanged(nameof(TabCount));
        if (!ReferenceEquals(ActiveTab, tab)) return;
        if (Tabs.Count == 0) GoHome();
        else Activate(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    public void CloseOthers(ShellTab keep)
    {
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList()) Close(tab);
        Activate(keep);
    }

    /// <summary>Moves a tool's tab out to a window of its own (Android puts it beside Helm in split screen).</summary>
    public void OpenInWindow(ShellTab tab)
    {
        if (!tab.CanOpenInWindow) return;
        Close(tab);
        services.GetRequiredService<ToolWindows>().Open(tab);
    }

    /// <summary>The tab of a page by its key, as <see cref="ShellTab.Key"/> names it; null for an unknown or removed tool.</summary>
    public ShellTab? Describe(string key)
    {
        if (key == "home") return new ShellTab(key, services.GetRequiredService<HomeViewModel>(), "Home", Symbol.Home, null, null);
        if (key == "general") return new ShellTab(key, services.GetRequiredService<GeneralViewModel>(), "General", Symbol.Settings, null, null);
        var colon = key.IndexOf(':');
        if (colon < 0) return null;
        var id = key[(colon + 1)..];
        if (services.GetRequiredService<IModuleHost<IAndroidModule>>().Find(id) is not { } module) return null;
        return key[..colon] switch
        {
            "content" when module is IModuleContent content => new ShellTab(key, new ModulePage(module, content.ContentPageType), module.DisplayName, module.Icon, module.IconImage, module),
            // A tool with content has its options on a second page: its tab says so.
            "settings" => new ShellTab(key, new ModulePage(module, module.PageType),
                module is IModuleContent ? $"{module.DisplayName} settings" : module.DisplayName, module.Icon, module.IconImage, module),
            _ => null,
        };
    }

    /// <summary>Adds the tabs of the last session, once (Home when there were none).</summary>
    public void Restore()
    {
        if (_restored) return;
        _restored = true;
        var windows = services.GetRequiredService<ToolWindows>();
        foreach (var key in _general.Current.Window.Tabs ?? [])
            if (!windows.IsOpen(key) && Tabs.All(t => t.Key != key) && Describe(key) is { } tab) Tabs.Add(tab);
        OnPropertyChanged(nameof(TabCount));
        // A tool window moved back before the main window existed keeps its tab in front.
        var active = ActiveTab ?? Tabs.FirstOrDefault(t => t.Key == _general.Current.Window.ActiveTab) ?? Tabs.FirstOrDefault();
        if (active is not null) Activate(active);
        else GoHome();
    }

    /// <summary>Remembers the open tabs (general.json), when Helm goes to the background.</summary>
    public void Save()
    {
        var keys = Tabs.Select(t => t.Key).ToList();
        var active = ActiveTab?.Key;
        if (keys.SequenceEqual(_general.Current.Window.Tabs ?? []) && active == _general.Current.Window.ActiveTab) return;
        _general.Update(s =>
        {
            s.Window.Tabs = keys;
            s.Window.ActiveTab = active;
        });
    }

    public static string ContentKey(IAndroidModule module) => $"content:{module.Id}";

    public static string SettingsKey(IAndroidModule module) => $"settings:{module.Id}";
}
