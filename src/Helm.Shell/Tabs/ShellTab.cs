using CommunityToolkit.Mvvm.ComponentModel;

namespace Helm.Shell.Tabs;

/// <summary>
/// One page's tab, like a browser's: a page (Home, General, Bug reports, a tool) is open in at most one tab of one
/// window. <see cref="Key"/> names the page (<see cref="ShellPages"/> or a tool's module id); the shell turns it into a
/// view and an icon.
/// </summary>
public sealed partial class ShellTab(string key, string title) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty]
    private string _title = title;

    /// <summary>The tab shown in its window.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>The window's strip the tab is on; null while it is closed.</summary>
    public TabStrip? Strip { get; internal set; }
}

/// <summary>The shell's own pages; every other page key is a tool's module id.</summary>
public static class ShellPages
{
    public const string Home = "home";
    public const string General = "general";
    public const string BugReports = "bug-reports";
}

/// <summary>What the main window remembers of its tabs across restarts (general.json).</summary>
public sealed record TabsState(IReadOnlyList<string> Keys, string? Active);
