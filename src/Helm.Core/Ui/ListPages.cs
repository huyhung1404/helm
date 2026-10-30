using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Core.Ui;

/// <summary>
/// A long list shown one page at a time, with Previous and Next: only <see cref="PageSize"/> rows are ever drawn, so
/// the list stays quick however long it grows (unlike <see cref="ListPager"/>, whose "Show more" keeps adding rows).
/// Another filter or search (its key) goes back to the first page; a refresh of the same list keeps the page.
/// </summary>
public sealed partial class ListPages : ObservableObject
{
    private readonly Action _refresh;
    private int _pageSize;
    private object? _key;

    /// <summary>The page shown, from 0.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))] private int _page;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NextPageCommand), nameof(PreviousPageCommand))] private int _pageCount = 1;

    /// <summary>"21–40 of 135"; empty when everything fits on one page.</summary>
    [ObservableProperty] private string _rangeText = "";

    /// <param name="refresh">Lists the items again (after Previous or Next).</param>
    public ListPages(Action refresh, int pageSize)
    {
        _refresh = refresh;
        _pageSize = pageSize;
    }

    /// <summary>Raised after Previous or Next, so the page can scroll back to the list's top.</summary>
    public event EventHandler? Turned;

    /// <summary>Rows per page; 0 or less shows everything. A new size goes back to the first page.</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            _pageSize = value;
            Page = 0;
        }
    }

    public bool HasPages => PageCount > 1;

    /// <summary>"Page 2 of 7".</summary>
    public string PageText => $"Page {Page + 1} of {PageCount}";

    partial void OnPageChanged(int value) => OnPropertyChanged(nameof(PageText));

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(PageText));
    }

    /// <summary>The rows of the page shown; <paramref name="key"/> names the filter and search they come from.</summary>
    public IReadOnlyList<T> Take<T>(IReadOnlyList<T> items, object? key = null)
    {
        if (!Equals(key, _key))
        {
            _key = key;
            Page = 0;
        }
        if (PageSize <= 0)
        {
            PageCount = 1;
            Page = 0;
            RangeText = "";
            return items;
        }
        PageCount = Math.Max(1, (items.Count + PageSize - 1) / PageSize);
        // The list got shorter (a word deleted, synced away): the last page that still has rows.
        Page = Math.Clamp(Page, 0, PageCount - 1);
        var first = Page * PageSize;
        var rows = items.Skip(first).Take(PageSize).ToList();
        RangeText = PageCount > 1 ? $"{first + 1}–{first + rows.Count} of {items.Count}" : "";
        return rows;
    }

    /// <summary>The page that holds the item at <paramref name="index"/> (a word walked to with Next, back in the list).</summary>
    public void ShowPageOf(int index)
    {
        if (PageSize > 0 && index >= 0) Page = index / PageSize;
    }

    private bool CanGoNext() => Page < PageCount - 1;

    private bool CanGoPrevious() => Page > 0;

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage() => Turn(1);

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage() => Turn(-1);

    private void Turn(int step)
    {
        Page = Math.Clamp(Page + step, 0, PageCount - 1);
        _refresh();
        Turned?.Invoke(this, EventArgs.Empty);
    }
}
