using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Symbol = FluentIcons.Common.Symbol;

namespace Helm.Home;

/// <summary>One tool of Helm on the Home page: its name and icon, which part of life it belongs to, and whether it is on.</summary>
public sealed partial class HomeTool(string id, string name, string group, Symbol icon, IImage? image = null, string? summary = null, bool isEnabled = true) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>"Planning", "Money &amp; Media"…: the caption in the list of all tools.</summary>
    public string Group { get; } = group;

    public Symbol Icon { get; } = icon;

    /// <summary>The tool's own picture when it has one (else its symbol is drawn).</summary>
    public IImage? Image { get; } = image;

    /// <summary>A line under the name on its tile ("3 open · 1 overdue", "Locked"); null: none.</summary>
    [ObservableProperty] private string? _summary = summary;

    /// <summary>On or off. The platform turns the tool on or off when this changes.</summary>
    [ObservableProperty] private bool _isEnabled = isEnabled;

    public bool HasSummary => !string.IsNullOrEmpty(Summary);

    partial void OnSummaryChanged(string? value) => OnPropertyChanged(nameof(HasSummary));
}

/// <summary>A shortcut of a tool, as keycaps ("Win", "Ctrl", "T").</summary>
public sealed record HomeShortcut(HomeTool Tool, string Description, IReadOnlyList<string> Keys);

/// <summary>How the update tile looks: all good, something to do, or just information.</summary>
public enum HomeTone
{
    Ok,
    Attention,
    Muted,
}

/// <summary>What a tap on the Home page does: each platform answers with its own pages and services.</summary>
public interface IHomeHost
{
    /// <summary>A tool's tile: its own action when it has one (a new chat), else its page.</summary>
    Task LaunchAsync(HomeTool tool);

    void OpenWhatsNew();

    void OpenConflicts();

    /// <summary>The update tile: check again when up to date, else go to where updates are handled.</summary>
    Task UpdateAsync();
}

/// <summary>
/// The Home page, portable: no platform type in it. The platform fills <see cref="Tools"/> and <see cref="Shortcuts"/>
/// from its modules and the state tiles from its services, and answers taps through <see cref="IHomeHost"/>.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IHomeHost _host;
    private readonly TimeProvider _time;

    [ObservableProperty] private string _updateTitle = "You're up to date";
    [ObservableProperty] private string _updateSubtitle = "Not checked yet";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToneOk), nameof(IsToneAttention), nameof(IsToneMuted))]
    private HomeTone _updateTone;
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private string _conflictSummary = "No conflicts found";
    [ObservableProperty] private bool _hasConflicts;
    [ObservableProperty] private bool _groupByCategory;

    public HomeViewModel(IHomeHost host, string version, bool showsShortcuts, TimeProvider? time = null)
    {
        _host = host;
        Version = version;
        ShowsShortcuts = showsShortcuts;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>"v0.88.0".</summary>
    public string Version { get; }

    /// <summary>The desktop has keyboard shortcuts and can have them clash; a phone has neither tile nor list.</summary>
    public bool ShowsShortcuts { get; }

    /// <summary>All tools, in the order the list of tools shows them.</summary>
    public ObservableCollection<HomeTool> Tools { get; } = [];

    /// <summary>The tools that are on, by name: their tiles.</summary>
    public ObservableCollection<HomeTool> QuickAccess { get; } = [];

    public ObservableCollection<HomeShortcut> Shortcuts { get; } = [];

    public bool IsToneOk => UpdateTone == HomeTone.Ok;

    public bool IsToneAttention => UpdateTone == HomeTone.Attention;

    public bool IsToneMuted => UpdateTone == HomeTone.Muted;

    public bool HasQuickAccess => QuickAccess.Count > 0;

    public bool HasShortcuts => ShowsShortcuts && Shortcuts.Count > 0;

    /// <summary>The line above the greeting: today's date.</summary>
    public string DateLine => _time.GetLocalNow().ToString("dddd, d MMMM", CultureInfo.CurrentCulture);

    public string Greeting => _time.GetLocalNow().Hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>"6 of 24 tools on · v0.88.0".</summary>
    public string Summary => $"{QuickAccess.Count} of {Tools.Count} tools on · {Version}";

    public string SortLabel => GroupByCategory ? "By category" : "A–Z";

    /// <summary>Fills the list of tools; their switches keep the tiles in step.</summary>
    public void SetTools(IEnumerable<HomeTool> tools)
    {
        foreach (var tool in Tools) tool.PropertyChanged -= OnToolChanged;
        Tools.Clear();
        foreach (var tool in tools)
        {
            tool.PropertyChanged += OnToolChanged;
            Tools.Add(tool);
        }
        Refresh();
    }

    public void SetShortcuts(IEnumerable<HomeShortcut> shortcuts)
    {
        Shortcuts.Clear();
        foreach (var shortcut in shortcuts) Shortcuts.Add(shortcut);
        OnPropertyChanged(nameof(HasShortcuts));
    }

    partial void OnGroupByCategoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SortLabel));
        Refresh();
    }

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeTool.IsEnabled)) Refresh();
    }

    private void Refresh()
    {
        var ordered = (GroupByCategory ? Tools.OrderBy(t => t.Group, StringComparer.CurrentCulture).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            : Tools.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        // Reorder in place: a switch the user is dragging must not be rebuilt under the pointer.
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Tools.IndexOf(ordered[i]);
            if (at != i) Tools.Move(at, i);
        }
        var on = Tools.Where(t => t.IsEnabled).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!on.SequenceEqual(QuickAccess))
        {
            QuickAccess.Clear();
            foreach (var tool in on) QuickAccess.Add(tool);
        }
        OnPropertyChanged(nameof(HasQuickAccess));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private Task LaunchAsync(HomeTool? tool) => tool is null ? Task.CompletedTask : _host.LaunchAsync(tool);

    [RelayCommand]
    private void OpenWhatsNew() => _host.OpenWhatsNew();

    [RelayCommand]
    private void OpenConflicts() => _host.OpenConflicts();

    [RelayCommand]
    private Task UpdateAsync() => _host.UpdateAsync();

    [RelayCommand]
    private void ToggleSort() => GroupByCategory = !GroupByCategory;
}
