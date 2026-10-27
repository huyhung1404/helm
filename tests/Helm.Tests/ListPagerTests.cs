using Helm.Core.Ui;

namespace Helm.Tests;

public sealed class ListPagerTests
{
    private static readonly IReadOnlyList<int> Twelve = Enumerable.Range(1, 12).ToList();

    [Fact]
    public void Shows_a_page_and_adds_one_on_show_more()
    {
        var refreshed = 0;
        var pager = new ListPager(() => refreshed++, 5);
        Assert.Equal([1, 2, 3, 4, 5], pager.Take(Twelve, "all"));
        Assert.True(pager.HasMore);
        Assert.Equal("Show more (7 left)", pager.MoreText);

        pager.ShowMoreCommand.Execute(null);
        Assert.Equal(1, refreshed);
        Assert.Equal(10, pager.Take(Twelve, "all").Count);
        pager.ShowMoreCommand.Execute(null);
        Assert.Same(Twelve, pager.Take(Twelve, "all"));
        Assert.False(pager.HasMore);
    }

    [Fact]
    public void Another_key_or_size_or_a_reset_starts_over_and_the_same_key_keeps_what_is_shown()
    {
        var pager = new ListPager(() => { }, 5);
        pager.Take(Twelve, ("All", ""));
        pager.ShowMoreCommand.Execute(null);
        Assert.Equal(10, pager.Take(Twelve, ("All", "")).Count); // a refresh of the same list (a tuple key compares by value)
        Assert.Equal(5, pager.Take(Twelve, ("All", "pho")).Count);

        pager.ShowMoreCommand.Execute(null);
        pager.Reset();
        Assert.Equal(5, pager.Take(Twelve, ("All", "pho")).Count);

        pager.ShowMoreCommand.Execute(null);
        pager.PageSize = 10;
        Assert.Equal(10, pager.Take(Twelve, ("All", "pho")).Count);
    }

    [Fact]
    public void No_page_size_shows_everything()
    {
        var pager = new ListPager(() => { }, 0);
        Assert.Same(Twelve, pager.Take(Twelve));
        Assert.False(pager.HasMore);
        pager.ShowAtLeast(50);
        Assert.Same(Twelve, pager.Take(Twelve));
    }

    [Fact]
    public void Shows_at_least_an_item_further_down_in_whole_pages()
    {
        var pager = new ListPager(() => { }, 5);
        pager.Take(Twelve, "k");
        pager.ShowAtLeast(7);
        Assert.Equal(10, pager.Take(Twelve, "k").Count);
        pager.ShowAtLeast(3); // never fewer
        Assert.Equal(10, pager.Take(Twelve, "k").Count);
    }

    [Fact]
    public void A_setting_maps_to_its_choice()
    {
        Assert.Equal(1, ListPager.IndexOf(10));
        Assert.Equal(0, ListPager.IndexOf(0)); // the PC's "everything" has no choice of its own
        Assert.Equal(50, ListPager.SizeAt(99));
        Assert.Equal(0, ListPager.DefaultSize); // the tests run on Windows: everything
    }
}
