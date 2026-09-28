using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using Helm.Core.Modules;
using Helm.App.Android.Services;

namespace Helm.App.Android.ViewModels;

/// <summary>One row of the navigation drawer: a page, or a group header above that group's tools.</summary>
public sealed record NavEntry(string Title, Symbol Icon, bool IsHeader, Action? Open)
{
    public bool IsPage => !IsHeader;
}

/// <summary>The shell: app bar, navigation drawer (Home, tools by group, General) and the confirmation overlay.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isPaneOpen;

    public MainViewModel(ShellNavigator navigator, DialogService dialogs, IModuleHost<IAndroidModule> modules)
    {
        Navigator = navigator;
        Dialogs = dialogs;

        Entries.Add(new NavEntry("Home", Symbol.Home, false, navigator.GoHome));
        foreach (var group in modules.Modules.GroupBy(m => m.Group).OrderBy(g => g.Key))
        {
            Entries.Add(new NavEntry(group.Key.DisplayName(), Symbol.Apps, true, null));
            foreach (var module in group.OrderBy(m => m.DisplayName))
                Entries.Add(new NavEntry(module.DisplayName, module.Icon, false, () => navigator.GoModule(module)));
        }
        GeneralEntry = new NavEntry("General", Symbol.Settings, false, navigator.GoGeneral);

        if (navigator.CurrentPage is null) navigator.GoHome();
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

    /// <summary>Android back: close the dialog, then the drawer, then return to Home; false lets Android leave the app.</summary>
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
        // A tool with its own back stack (an open item) goes back first.
        if (Navigator.CurrentPage is ModulePage { Module: Helm.Core.Platform.IBackHandler page } && page.HandleBack()) return true;
        if (!Navigator.IsHome)
        {
            Navigator.GoHome();
            return true;
        }
        return false;
    }
}
