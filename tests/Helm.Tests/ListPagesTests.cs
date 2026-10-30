using Helm.Core.Ui;

namespace Helm.Tests;

public sealed class ListPagesTests
{
    [Fact]
    public void A_list_is_shown_a_page_at_a_time()
    {
        var items = Enumerable.Range(1, 45).ToList();
        IReadOnlyList<int> shown = [];
        var turned = 0;
        ListPages? pages = null;
        pages = new ListPages(() => shown = pages!.Take(items, "all"), 20);
        pages.Turned += (_, _) => turned++;

        shown = pages.Take(items, "all");
        Assert.Equal(Enumerable.Range(1, 20), shown);
        Assert.Equal(3, pages.PageCount);
        Assert.Equal("Page 1 of 3", pages.PageText);
        Assert.Equal("1–20 of 45", pages.RangeText);
        Assert.False(pages.PreviousPageCommand.CanExecute(null));

        pages.NextPageCommand.Execute(null);
        pages.NextPageCommand.Execute(null);
        Assert.Equal(Enumerable.Range(41, 5), shown);
        Assert.False(pages.NextPageCommand.CanExecute(null));
        Assert.Equal(2, turned);

        // A refresh of the same list keeps the page; a shorter list keeps the last page that has rows.
        Assert.Equal(Enumerable.Range(41, 5), pages.Take(items, "all"));
        Assert.Equal(Enumerable.Range(21, 10), pages.Take(items.Take(30).ToList(), "all"));

        // Another filter or search starts at the first page.
        Assert.Equal(Enumerable.Range(1, 20), pages.Take(items, "search"));

        pages.ShowPageOf(44);
        Assert.Equal(Enumerable.Range(41, 5), pages.Take(items, "search"));
    }

    [Fact]
    public void A_short_list_has_no_pages()
    {
        var pages = new ListPages(() => { }, 20);
        Assert.Equal([1, 2, 3], pages.Take([1, 2, 3]));
        Assert.False(pages.HasPages);
        Assert.Equal("", pages.RangeText);
    }
}
