using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Shell.Views;

/// <summary>One row of the menu: a page. It is marked while its page is the main window's shown tab.</summary>
public sealed partial class NavItemViewModel(ShellPageInfo page) : ObservableObject
{
    public ShellPageInfo Page { get; } = page;

    public string Key => Page.Key;

    public string Title => Page.Title;

    public IImage? Icon => Page.Icon;

    public Symbol Glyph => Page.Glyph;

    /// <summary>Its page is the shown tab.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>A tool that is turned off is still listed (its page turns it on), faded.</summary>
    public bool IsOff => Page.Tool is { IsEnabled: false };

    public string ToolTip => IsOff ? $"{Title} (turned off)" : Title;

    internal void ToolChanged()
    {
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>A tool group in the menu: a small header, then its tools.</summary>
public sealed class NavGroupViewModel(string title, IReadOnlyList<NavItemViewModel> items)
{
    public string Title { get; } = title;

    public IReadOnlyList<NavItemViewModel> Items { get; } = items;
}

/// <summary>
/// The left menu: Home, the tools by group, then Bug reports and General at the bottom. On a wide window it is either
/// pinned open beside the page, or an icon rail that slides open over the page while the mouse rests on it
/// (<see cref="IsPeeking"/>); its open width can be dragged. On a narrow window (a phone) it is a drawer.
/// </summary>
public sealed partial class NavMenuViewModel : ObservableObject
{
    public const double RailWidth = 56;
    public const double MinOpenWidth = 220;
    public const double MaxOpenWidth = 420;

    private readonly Action<string> _open;

    public NavMenuViewModel(ShellCatalog catalog, Action<string> open)
    {
        _open = open;
        Home = new NavItemViewModel(catalog.Home);
        Groups = catalog.Groups
            .Select(g => new NavGroupViewModel(Helm.Core.Modules.ModuleGroups.DisplayName(g.Group), g.Pages.Select(p => new NavItemViewModel(p)).ToList()))
            .ToList();
        BugReports = new NavItemViewModel(catalog.BugReports);
        General = new NavItemViewModel(catalog.General);
        foreach (var item in Groups.SelectMany(g => g.Items))
        {
            var row = item;
            if (row.Page.Tool is { } tool) tool.PropertyChanged += (_, _) => row.ToolChanged();
        }
    }

    public NavItemViewModel Home { get; }

    public IReadOnlyList<NavGroupViewModel> Groups { get; }

    public NavItemViewModel BugReports { get; }

    public NavItemViewModel General { get; }

    public IEnumerable<NavItemViewModel> All => [Home, .. Groups.SelectMany(g => g.Items), BugReports, General];

    /// <summary>Pinned: open beside the page. Unpinned (the default): an icon rail that opens over the page on hover.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsOverlay), nameof(ReservedWidth), nameof(PinToolTip), nameof(PinLabel), nameof(PinGlyph))]
    private bool _isPinned;

    /// <summary>The rail is open over the page for a moment (the mouse rests on it or on the title bar's icon).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen), nameof(IsOverlay))]
    private bool _isPeeking;

    /// <summary>Narrow window: the menu is a drawer over the page, shown while this is true.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private bool _isDrawer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private bool _isDrawerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReservedWidth), nameof(ShownWidth))]
    private double _openWidth = 260;

    /// <summary>Labels and group names show (open, peeking or the drawer); otherwise only the icons.</summary>
    public bool IsOpen => IsDrawer ? IsDrawerOpen : IsPinned || IsPeeking;

    /// <summary>Open over the page (peeking, or the drawer): it has a background and a shadow of its own.</summary>
    public bool IsOverlay => IsDrawer || !IsPinned && IsPeeking;

    /// <summary>The room the page leaves for the menu on a wide window.</summary>
    public double ReservedWidth => IsPinned ? OpenWidth : RailWidth;

    /// <summary>The menu's own width right now.</summary>
    public double ShownWidth => IsOpen || IsDrawer ? OpenWidth : RailWidth;

    public string PinToolTip => IsPinned ? "Collapse the menu to icons; it opens while the mouse rests on it" : "Keep the menu open beside the page";

    public string PinLabel => IsPinned ? "Collapse menu" : "Keep menu open";

    public Symbol PinGlyph => IsPinned ? Symbol.PanelLeftContract : Symbol.PanelLeftExpand;

    partial void OnIsPinnedChanged(bool value)
    {
        if (value) IsPeeking = false;
        OnPropertyChanged(nameof(ShownWidth));
    }

    partial void OnIsPeekingChanged(bool value) => OnPropertyChanged(nameof(ShownWidth));

    partial void OnIsDrawerChanged(bool value)
    {
        if (!value) IsDrawerOpen = false;
        IsPeeking = false;
        OnPropertyChanged(nameof(ShownWidth));
        OnPropertyChanged(nameof(IsOverlay));
    }

    partial void OnIsDrawerOpenChanged(bool value) => OnPropertyChanged(nameof(ShownWidth));

    partial void OnOpenWidthChanged(double value)
    {
        var clamped = Math.Clamp(value, MinOpenWidth, MaxOpenWidth);
        if (!clamped.Equals(value)) OpenWidth = clamped;
    }

    /// <summary>Marks the row of the page the main window shows (none for a page the menu does not list).</summary>
    public void MarkActive(string? key)
    {
        foreach (var item in All) item.IsActive = item.Key == key;
    }

    [RelayCommand]
    private void Open(NavItemViewModel? item)
    {
        if (item is null) return;
        _open(item.Key);
        IsDrawerOpen = false;
    }

    [RelayCommand]
    private void TogglePinned() => IsPinned = !IsPinned;

    [RelayCommand]
    private void ToggleDrawer() => IsDrawerOpen = !IsDrawerOpen;
}
