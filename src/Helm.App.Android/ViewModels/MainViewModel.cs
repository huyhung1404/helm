using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.App.Android.Services;

namespace Helm.App.Android.ViewModels;

/// <summary>One row of the navigation drawer: a page, or a group header above that group's tools.</summary>
public sealed record NavEntry(string Title, Symbol Icon, bool IsHeader, Action? Open, IImage? Image = null)
{
    public bool IsPage => !IsHeader;
}

/// <summary>The shell: app bar, navigation drawer (Home, tools by group, General), the tab list and the confirmation overlay.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isPaneOpen;

    /// <summary>The list of open tabs (from the app bar's tab button or the drawer), over the page.</summary>
    [ObservableProperty]
    private bool _isTabsOpen;

    /// <summary>The sync button in the app bar.</summary>
    public Helm.Shell.ViewModels.SyncIndicatorViewModel Sync { get; }

    public MainViewModel(ShellNavigator navigator, DialogService dialogs, IModuleHost<IAndroidModule> modules, Helm.Shell.ViewModels.SyncIndicatorViewModel sync)
    {
        Navigator = navigator;
        Dialogs = dialogs;
        Sync = sync;
        sync.OpenSettings = navigator.GoGeneral;

        Entries.Add(new NavEntry("Home", Symbol.Home, false, navigator.GoHome));
        foreach (var group in modules.Modules.GroupBy(m => m.Group).OrderBy(g => g.Key))
        {
            Entries.Add(new NavEntry(group.Key.DisplayName(), Symbol.Apps, true, null));
            foreach (var module in group.OrderBy(m => m.DisplayName))
                Entries.Add(new NavEntry(module.DisplayName, module.Icon, false, () => navigator.GoModuleContent(module), module.IconImage));
        }
        GeneralEntry = new NavEntry("General", Symbol.Settings, false, navigator.GoGeneral);

        navigator.Restore();
    }

    public ShellNavigator Navigator { get; }

    public DialogService Dialogs { get; }

    public ObservableCollection<NavEntry> Entries { get; } = [];

    /// <summary>Pinned to the bottom of the drawer, like the Windows navigation pane.</summary>
    public NavEntry GeneralEntry { get; }

    [RelayCommand]
    private void TogglePane() => IsPaneOpen = !IsPaneOpen;

    [RelayCommand]
    private void Open(NavEntry? entry)
    {
        entry?.Open?.Invoke();
        IsPaneOpen = false;
    }

    [RelayCommand]
    private void ShowTabs()
    {
        IsPaneOpen = false;
        IsTabsOpen = true;
    }

    [RelayCommand]
    private void HideTabs() => IsTabsOpen = false;

    [RelayCommand]
    private void SelectTab(ShellTab? tab)
    {
        if (tab is not null) Navigator.Activate(tab);
        IsTabsOpen = false;
    }

    [RelayCommand]
    private void CloseTab(ShellTab? tab)
    {
        if (tab is not null) Navigator.Close(tab);
    }

    [RelayCommand]
    private void CloseOtherTabs()
    {
        if (Navigator.ActiveTab is { } active) Navigator.CloseOthers(active);
    }

    [RelayCommand]
    private void OpenInWindow(ShellTab? tab)
    {
        if (tab is null) return;
        IsTabsOpen = false;
        Navigator.OpenInWindow(tab);
    }

    /// <summary>
    /// Android back: close the dialog, the drawer or the tab list, then go back inside the tool, then to the Home tab
    /// (the tool's tab stays open); false lets Android leave the app.
    /// </summary>
    public bool HandleBack()
    {
        if (Dialogs.IsOpen)
        {
            Dialogs.Cancel();
            return true;
        }
        if (IsPaneOpen)
        {
            IsPaneOpen = false;
            return true;
        }
        if (IsTabsOpen)
        {
            IsTabsOpen = false;
            return true;
        }
        if (Navigator.CurrentPage is ModulePage page)
        {
            var content = (page.Module as IModuleContent)?.ContentPageType;
            // A tool's settings tab gives way to its content page, where the menu and Quick access lead.
            if (content is not null && page.PageType != content && Navigator.ActiveTab is { } settings)
            {
                Navigator.GoModuleContent(page.Module);
                Navigator.Close(settings);
                return true;
            }
            // A tool with its own back stack (an open item) goes back first; only on the page that shows that stack.
            if (page.Module is Helm.Core.Platform.IBackHandler handler && handler.HandleBack()) return true;
        }
        if (!Navigator.IsHome)
        {
            Navigator.GoHome();
            return true;
        }
        return false;
    }
}
