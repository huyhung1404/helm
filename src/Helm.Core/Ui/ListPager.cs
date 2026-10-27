using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Core.Ui;

/// <summary>
/// A long list shown a page at a time: the phone's pages draw every row they are given (a list inside the page's
/// scroller is not virtualized), so a long one made them slow to scroll. <see cref="Take{T}"/> keeps the first
/// <see cref="PageSize"/> items and "Show more" adds as many again; another filter or search (its key) starts over at
/// one page, while a refresh of the same list keeps what is shown.
/// </summary>
public sealed partial class ListPager : ObservableObject
{
    private readonly Action _refresh;
    private int _pageSize;
    private int _shown;
    private object? _key;

    /// <summary>"Show more (12 left)"; empty when everything is shown.</summary>
    [ObservableProperty] private string _moreText = "";

    /// <param name="refresh">Lists the items again (after "Show more").</param>
    public ListPager(Action refresh, int pageSize)
    {
        _refresh = refresh;
        PageSize = pageSize;
    }

    /// <summary>The choices of a tool's "Items shown" setting.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [5, 10, 20, 50];

    public static IReadOnlyList<string> SizeNames { get; } = Sizes.Select(n => n.ToString(CultureInfo.CurrentCulture)).ToList();

    /// <summary>A setting's default: a page on the phone, everything on the PC (its lists stay quick).</summary>
    public static int DefaultSize => OperatingSystem.IsAndroid() ? Sizes[0] : 0;

    /// <summary>The place of a saved size in <see cref="Sizes"/> (the first for "everything" or an unknown size).</summary>
    public static int IndexOf(int size)
    {
        for (var i = 0; i < Sizes.Count; i++)
            if (Sizes[i] == size) return i;
        return 0;
    }

    public static int SizeAt(int index) => Sizes[Math.Clamp(index, 0, Sizes.Count - 1)];

    /// <summary>Items per page; 0 or less shows everything. A new size starts over at one page.</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            _pageSize = value;
            _shown = 0;
        }
    }

    public bool HasMore => MoreText.Length > 0;

    partial void OnMoreTextChanged(string value) => OnPropertyChanged(nameof(HasMore));

    /// <summary>The items to show; <paramref name="key"/> names the filter and search they come from.</summary>
    public IReadOnlyList<T> Take<T>(IReadOnlyList<T> items, object? key = null)
    {
        if (_shown <= 0 || !Equals(key, _key))
        {
            _shown = PageSize;
            _key = key;
        }
        var count = PageSize <= 0 ? items.Count : Math.Min(items.Count, _shown);
        var left = items.Count - count;
        MoreText = left > 0 ? $"Show more ({left} left)" : "";
        return count == items.Count ? items : items.Take(count).ToList();
    }

    /// <summary>At least the first <paramref name="count"/> items (one opened from a link must be on the list).</summary>
    public void ShowAtLeast(int count)
    {
        if (PageSize > 0 && count > _shown) _shown = (count + PageSize - 1) / PageSize * PageSize;
    }

    /// <summary>Back to one page (the page opens again).</summary>
    public void Reset() => _shown = 0;

    [RelayCommand]
    private void ShowMore()
    {
        if (PageSize <= 0) return;
        _shown += PageSize;
        _refresh();
    }
}
