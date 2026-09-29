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

/// <summary>
/// The palette box: the query, the ranked results, the highlighted one. Quick sources answer on every key; slow ones
/// (<paramref name="searchSlow"/>, e.g. files) once typing pauses, and their results are merged in when they arrive,
/// unless the query changed meanwhile. UI thread only.
/// </summary>
public sealed partial class PaletteViewModel(
    Func<PaletteQuery, IReadOnlyList<PaletteItem>> search,
    Func<PaletteQuery, CancellationToken, Task<IReadOnlyList<PaletteItem>>>? searchSlow = null) : ObservableObject
{
    /// <summary>Typing pauses this long before the slow sources are asked.</summary>
    public static TimeSpan SlowDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    private CancellationTokenSource? _slow;
    private IReadOnlyList<PaletteItem> _quick = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteResult? _selected;
    [ObservableProperty] private bool _isSearching;

    public ObservableCollection<PaletteResult> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    public bool HasNoResults => Results.Count == 0 && Query.Trim().Length > 0 && !IsSearching;

    /// <summary>Raised when a result was chosen; the window closes and then runs it.</summary>
    public event EventHandler<PaletteItem>? Chosen;

    /// <summary>The box opens: a fresh search (the query is kept selected so typing replaces it).</summary>
    public void Refresh() => Update();

    /// <summary>The slow search of the current query, for tests and for waiting in the view.</summary>
    public Task? PendingSlowSearch { get; private set; }

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

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));

    private void Update()
    {
        _slow?.Cancel();
        _slow = null;
        var query = new PaletteQuery(Query);
        _quick = search(query);
        Show(_quick, keepSelection: false);
        if (searchSlow is null || query.IsEmpty)
        {
            IsSearching = false;
            PendingSlowSearch = null;
            return;
        }
        var cts = _slow = new CancellationTokenSource();
        IsSearching = true;
        PendingSlowSearch = RunSlowAsync(query, cts);
    }

    private async Task RunSlowAsync(PaletteQuery query, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(SlowDelay, cts.Token).ConfigureAwait(true);
            var slow = await searchSlow!(query, cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested) return;
            Show(PaletteSearch.Order(_quick.Concat(slow)), keepSelection: true);
        }
        catch (OperationCanceledException)
        {
            // A newer query took over.
        }
        finally
        {
            if (ReferenceEquals(_slow, cts))
            {
                IsSearching = false;
                _slow = null;
            }
            cts.Dispose();
        }
    }

    /// <param name="keepSelection">Results that arrive late keep the line the user moved to, when it is still there.</param>
    private void Show(IReadOnlyList<PaletteItem> items, bool keepSelection)
    {
        var selected = keepSelection && Selected is { } s && !ReferenceEquals(Selected, Results.FirstOrDefault()) ? s.Item : null;
        Results.Clear();
        foreach (var item in items) Results.Add(new PaletteResult(item));
        Selected = (selected is null ? null : Results.FirstOrDefault(r => ReferenceEquals(r.Item, selected))) ?? Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
    }
}
