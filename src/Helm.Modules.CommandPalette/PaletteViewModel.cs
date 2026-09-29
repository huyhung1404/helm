using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Helm.Core.Palette;

namespace Helm.Modules.CommandPalette;

/// <summary>One line of results.</summary>
public sealed class PaletteResult(PaletteItem item)
{
    public PaletteItem Item { get; } = item;

    public string Title => Item.Title;

    public string Subtitle => Item.Subtitle;

    public PaletteKind Kind => Item.Kind;
}

/// <summary>The palette box: the query, the ranked results, the highlighted one. UI thread only.</summary>
public sealed partial class PaletteViewModel(Func<PaletteQuery, IReadOnlyList<PaletteItem>> search) : ObservableObject
{
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteResult? _selected;

    public ObservableCollection<PaletteResult> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    public bool HasNoResults => Results.Count == 0 && Query.Trim().Length > 0;

    /// <summary>Raised when a result was chosen; the window closes and then runs it.</summary>
    public event EventHandler<PaletteItem>? Chosen;

    /// <summary>The box opens: a fresh search (the query is kept selected so typing replaces it).</summary>
    public void Refresh() => Update();

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    /// <summary>Enter: runs the highlighted result (the first one when none is).</summary>
    public void Choose(PaletteResult? result = null)
    {
        result ??= Selected ?? Results.FirstOrDefault();
        if (result is not null) Chosen?.Invoke(this, result.Item);
    }

    partial void OnQueryChanged(string value) => Update();

    private void Update()
    {
        var items = search(new PaletteQuery(Query));
        Results.Clear();
        foreach (var item in items) Results.Add(new PaletteResult(item));
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
    }
}
