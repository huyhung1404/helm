using System.Globalization;
using Helm.Core.Text;

namespace Helm.Tests;

public sealed class DateTimeTextTests
{
    // Sunday 4 October 2026, 09:20.
    private static readonly DateTime Now = new(2026, 10, 4, 9, 20, 0);

    [Theory]
    [InlineData("14:30", 2026, 10, 4, "14:30")]
    [InlineData("hôm qua 18h45", 2026, 10, 3, "18:45")]
    [InlineData("hom qua 18h45", 2026, 10, 3, "18:45")]
    [InlineData("Yesterday", 2026, 10, 3, null)]
    [InlineData("hôm kia", 2026, 10, 2, null)]
    [InlineData("3/10 9h", 2026, 10, 3, "09:00")]
    [InlineData("3/10/2026 21:05", 2026, 10, 3, "21:05")]
    [InlineData("2026-09-30", 2026, 9, 30, null)]
    [InlineData("3h chiều", 2026, 10, 4, "15:00")]
    [InlineData("2pm", 2026, 10, 4, "14:00")]
    [InlineData("1430", 2026, 10, 4, "14:30")]
    [InlineData("now", 2026, 10, 4, "09:20")]
    [InlineData("bây giờ", 2026, 10, 4, "09:20")]
    [InlineData("thứ 6", 2026, 10, 2, null)] // the last Friday
    [InlineData("t6 20h", 2026, 10, 2, "20:00")]
    [InlineData("2 ngày trước", 2026, 10, 2, null)]
    [InlineData("3 days ago 8:15", 2026, 10, 1, "08:15")]
    [InlineData("Hôm nay, 14:30", 2026, 10, 4, "14:30")] // what the box shows reads back
    public void Reads_past(string text, int y, int m, int d, string? time)
    {
        Assert.True(DateTimeText.TryParse(text, Now, DateLean.Past, out var date, out var t), text);
        Assert.Equal(new DateOnly(y, m, d), date);
        Assert.Equal(time, t is { } v ? DateTimeText.Clock(v) : null);
    }

    [Theory]
    [InlineData("mai 9h", 2026, 10, 5, "09:00")]
    [InlineData("tomorrow", 2026, 10, 5, null)]
    [InlineData("ngày kia", 2026, 10, 6, null)]
    [InlineData("thứ 6", 2026, 10, 9, null)] // the next Friday
    [InlineData("friday 17:00", 2026, 10, 9, "17:00")]
    [InlineData("chủ nhật", 2026, 10, 4, null)] // today is Sunday
    [InlineData("in 3 days", 2026, 10, 7, null)]
    [InlineData("10 ngày nữa", 2026, 10, 14, null)]
    [InlineData("5/1", 2027, 1, 5, null)] // January next year, when looking ahead
    public void Reads_future(string text, int y, int m, int d, string? time)
    {
        Assert.True(DateTimeText.TryParse(text, Now, DateLean.Future, out var date, out var t), text);
        Assert.Equal(new DateOnly(y, m, d), date);
        Assert.Equal(time, t is { } v ? DateTimeText.Clock(v) : null);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("hôm qua lúc nào đó")]
    [InlineData("31/2")]
    [InlineData("25:00")]
    public void Refuses_what_it_cannot_read(string text) =>
        Assert.False(DateTimeText.TryParse(text, Now, DateLean.Past, out _, out _));

    [Fact]
    public void Empty_is_no_date()
    {
        Assert.True(DateTimeText.TryParse("  ", Now, DateLean.Past, out var date, out var time));
        Assert.Null(date);
        Assert.Null(time);
    }

    [Fact]
    public void Formats_a_short_phrase()
    {
        var today = DateOnly.FromDateTime(Now);
        var en = CultureInfo.GetCultureInfo("en-GB");
        Assert.Equal("Today, 14:30", DateTimeText.Format(today, new TimeSpan(14, 30, 0), today, en));
        Assert.Equal("Yesterday", DateTimeText.Format(today.AddDays(-1), null, today, en));
        Assert.Equal("Tomorrow, 09:05", DateTimeText.Format(today.AddDays(1), new TimeSpan(9, 5, 0), today, en));
        Assert.Equal("Fri 2 Oct", DateTimeText.Format(today.AddDays(-2), null, today, en));
        Assert.Equal("3 Oct 2025", DateTimeText.Format(new DateOnly(2025, 10, 3), null, today, en));
        Assert.Equal("", DateTimeText.Format(null, null, today, en));
        Assert.Equal("Saturday 3 October 2026 · 18:45", DateTimeText.Describe(new DateOnly(2026, 10, 3), new TimeSpan(18, 45, 0), en));
    }
}
