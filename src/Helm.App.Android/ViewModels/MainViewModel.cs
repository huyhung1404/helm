using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.App.Android.Services;

namespace Helm.App.Android.ViewModels;

/// <summary>
/// One row of the navigation drawer: a page, or a group header above that group's tools. The page's row is marked
/// while that page (or the tool's settings page) is shown, as in the Windows menu.
/// </summary>
public sealed partial class NavEntry(string title, Symbol icon, bool isHeader, Action? open, IImage? image = null,
    string? pageKey = null, IAndroidModule? module = null) : ObservableObject
{
    public string Title { get; } = title;

    public Symbol Icon { get; } = icon;

    public bool IsHeader { get; } = isHeader;

    public bool IsPage => !IsHeader;

    public Action? Open { get; } = open;

    public IImage? Image { get; } = image;

    /// <summary>The tool the row opens; null for Home, General and headers.</summary>
    public IAndroidModule? Module { get; } = module;

    [ObservableProperty]
    private bool _isActive;

    /// <remarks>General's row stays marked on its AI features page.</remarks>
    public bool Shows(ShellTab? tab) => tab is not null && (Module is not null ? ReferenceEquals(tab.Module, Module)
        : tab.Key == pageKey || (pageKey == "general" && tab.Key == ShellNavigator.AiFeaturesKey));
}

/// <summary>The shell: app bar, navigation drawer (Home, tools by group, General) and the confirmation overlay.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isPaneOpen;

    /// <summary>The tool whose actions sheet is up (long press on its drawer row); null when none is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSheetOpen))]
    private NavEntry? _sheetEntry;

    public bool IsSheetOpen => SheetEntry is not null;

    /// <summary>The sync button in the app bar.</summary>
    public Helm.Shell.ViewModels.SyncIndicatorViewModel Sync { get; }

    public MainViewModel(ShellNavigator navigator, DialogService dialogs, IModuleHost<IAndroidModule> modules, Helm.Shell.ViewModels.SyncIndicatorViewModel sync)
    {
        Navigator = navigator;
        Dialogs = dialogs;
        Sync = sync;
        sync.OpenSettings = navigator.GoGeneral;

        Entries.Add(new NavEntry("Home", Symbol.Home, false, navigator.GoHome, pageKey: "home"));
        foreach (var group in modules.Modules.GroupBy(m => m.Group).OrderBy(g => g.Key))
        {
            Entries.Add(new NavEntry(group.Key.DisplayName(), Symbol.Apps, true, null));
            foreach (var module in group.OrderBy(m => m.DisplayName))
                Entries.Add(new NavEntry(module.DisplayName, module.Icon, false, () => navigator.GoModuleContent(module), module.IconImage, module: module));
        }
        GeneralEntry = new NavEntry("General", Symbol.Settings, false, navigator.GoGeneral, pageKey: "general");

        navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellNavigator.ActiveTab)) MarkActive();
        };
        navigator.Restore();
        MarkActive();
    }

    public ShellNavigator Navigator { get; }

    public DialogService Dialogs { get; }

    public ObservableCollection<NavEntry> Entries { get; } = [];

    /// <summary>Pinned to the bottom of the drawer, like the Windows navigation pane.</summary>
    public NavEntry GeneralEntry { get; }

    private void MarkActive()
    {
        foreach (var entry in Entries.Append(GeneralEntry)) entry.IsActive = entry.IsPage && entry.Shows(Navigator.ActiveTab);
    }

    [RelayCommand]
    private void TogglePane() => IsPaneOpen = !IsPaneOpen;

    [RelayCommand]
    private void Open(NavEntry? entry)
    {
        if (IsSheetOpen) return; // the click that ends a long press on the row
        entry?.Open?.Invoke();
        IsPaneOpen = false;
    }

    /// <summary>Long press on a tool's drawer row: its actions (a window of its own).</summary>
    public void ShowSheet(NavEntry entry)
    {
        if (entry.Module is not null) SheetEntry = entry;
    }

    [RelayCommand]
    private void HideSheet() => SheetEntry = null;

    [RelayCommand]
    private void OpenSheetEntryInWindow()
    {
        if (SheetEntry?.Module is not { } module) return;
        SheetEntry = null;
        IsPaneOpen = false;
        Navigator.OpenInWindow(module);
    }

    /// <summary>
    /// Android back: close the dialog, the actions sheet or the drawer, then go back inside the tool, then to Home (the
    /// tool's page stays kept); false lets Android leave the app.
    /// </summary>
    public bool HandleBack()
    {
        if (Dialogs.IsOpen)
        {
            Dialogs.Cancel();
            return true;
        }
        if (IsSheetOpen)
        {
            SheetEntry = null;
            return true;
        }
        if (IsPaneOpen)
        {
            IsPaneOpen = false;
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
        // General › AI features: a feature's detail goes back to the list, the list to General.
        if (Navigator.CurrentPage is Helm.Ai.AiFeaturesViewModel features && Navigator.ActiveTab is { } featuresTab)
        {
            if (features.Back()) return true;
            Navigator.GoGeneral();
            Navigator.Close(featuresTab);
            return true;
        }
        if (!Navigator.IsHome)
        {
            Navigator.GoHome();
            return true;
        }
        return false;
    }
}
