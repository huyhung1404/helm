using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Core.Links;

/// <summary>A linked thing shown as a chip: click opens it in its tool, × removes the link.</summary>
public sealed partial class LinkedItemViewModel(LinksViewModel owner, LinkTarget target) : ObservableObject
{
    public LinkTarget Target { get; } = target;

    public string Kind => Target.Ref.Kind;

    public string Title => Target.Title;

    public string Subtitle => Target.Subtitle;

    public bool IsNote => Kind == LinkKinds.Note;

    public bool IsTask => Kind == LinkKinds.Task;

    public bool IsPerson => Kind == LinkKinds.Person;

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private void Unlink() => owner.Unlink(this);
}

/// <summary>
/// The "linked" part of one thing (a task, a person in the debt book, a note): its links as chips, a search to add
/// one, and optionally "New note" (creates a note titled after the thing, links it and opens it). Shared by both apps;
/// the owner view model calls <see cref="Refresh"/> when <see cref="LinkHub.Changed"/> fires (on the UI thread).
/// </summary>
public sealed partial class LinksViewModel : ObservableObject
{
    private const int MaxResults = 8;
    private readonly LinkHub _hub;
    private readonly IReadOnlyList<string> _kinds;
    private readonly Func<string> _newTitle;

    [ObservableProperty] private bool _isPicking;
    [ObservableProperty] private string _query = "";

    /// <param name="owner">The thing whose links these are.</param>
    /// <param name="kinds">What it may be linked to (the picker searches these).</param>
    /// <param name="newTitle">The title for "New …" (the thing's current title).</param>
    public LinksViewModel(LinkHub hub, LinkRef owner, IReadOnlyList<string> kinds, Func<string> newTitle)
    {
        _hub = hub;
        Owner = owner;
        _kinds = kinds;
        _newTitle = newTitle;
        Refresh();
    }

    public LinkRef Owner { get; }

    public ObservableCollection<LinkedItemViewModel> Items { get; } = [];

    public ObservableCollection<LinkTarget> Results { get; } = [];

    public bool HasItems => Items.Count > 0;

    public bool HasResults => Results.Count > 0;

    /// <summary>"New note" when one of the kinds can be created from here (Notes can).</summary>
    public bool CanCreate => CreatorKind is not null;

    public string CreateText => CreatorKind is { } kind ? $"New {_hub.Provider(kind)!.KindName.ToLowerInvariant()}" : "";

    /// <summary>"Link a note…", "Link a task or person…".</summary>
    public string PickText => "Link " + string.Join(" or ", _kinds.Select(k => _hub.Provider(k)?.KindName.ToLowerInvariant()).Where(n => n is not null)
        .Select((n, i) => i == 0 ? Article(n!) + n : n)) + "…";

    public string EmptyResultsText => Query.Trim().Length == 0 ? "Nothing to link yet." : "Nothing matches.";

    private string? CreatorKind => _kinds.FirstOrDefault(k => _hub.Provider(k) is { } p && p.Kind == LinkKinds.Note);

    /// <summary>Reloads the chips (links made or removed elsewhere, titles changed) and, while picking, the results.</summary>
    public void Refresh()
    {
        var targets = _hub.TargetsOf(Owner);
        if (!Items.Select(i => i.Target).SequenceEqual(targets))
        {
            Items.Clear();
            foreach (var target in targets) Items.Add(new LinkedItemViewModel(this, target));
            OnPropertyChanged(nameof(HasItems));
        }
        if (IsPicking) Search();
    }

    partial void OnQueryChanged(string value)
    {
        if (IsPicking) Search();
    }

    [RelayCommand]
    private void StartPicking()
    {
        Query = "";
        IsPicking = true;
        Search();
    }

    [RelayCommand]
    private void CancelPicking()
    {
        IsPicking = false;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
    }

    [RelayCommand]
    private void Pick(LinkTarget? target)
    {
        if (target is null) return;
        _hub.Link(Owner, target.Ref);
        CancelPicking();
        Refresh();
    }

    /// <summary>Creates a note titled after the owner, links it and opens it, ready to write.</summary>
    [RelayCommand]
    private void Create()
    {
        if (CreatorKind is not { } kind || _hub.Provider(kind) is not { } provider) return;
        var title = _newTitle().Trim();
        if (provider.Create(title.Length == 0 ? "Untitled" : title) is not { } id) return;
        _hub.Link(Owner, new LinkRef(kind, id));
        CancelPicking();
        Refresh();
        provider.Open(id);
    }

    internal void Open(LinkedItemViewModel item) => _hub.Provider(item.Kind)?.Open(item.Target.Ref.Id);

    internal void Unlink(LinkedItemViewModel item)
    {
        _hub.Unlink(Owner, item.Target.Ref);
        Refresh();
    }

    private void Search()
    {
        var linked = _hub.LinksOf(Owner).ToHashSet();
        var perKind = Math.Max(3, MaxResults / Math.Max(1, _kinds.Count));
        var found = _kinds
            .Select(k => _hub.Provider(k))
            .Where(p => p is not null)
            .SelectMany(p => p!.Search(Query.Trim(), perKind + linked.Count))
            .Where(t => t.Ref != Owner && !linked.Contains(t.Ref))
            .Take(MaxResults)
            .ToList();
        Results.Clear();
        foreach (var target in found) Results.Add(target);
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(EmptyResultsText));
    }

    private static string Article(string noun) => "aeiou".Contains(noun[0]) ? "an " : "a ";
}
