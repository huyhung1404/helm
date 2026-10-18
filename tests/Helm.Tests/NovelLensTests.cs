using Helm.Modules.NovelReader.Lens;
using Xunit;

namespace Helm.Tests;

/// <summary>Novel Lens: the paragraphs of a reading app's page, from the lines recognised on its screenshot.</summary>
public sealed class NovelLensTests
{
    private const double Width = 2560;
    private const double Body = 40;
    private const double Margin = 280;
    private const double Indent = Margin + 2 * Body;
    private const double FullRight = 2280;

    /// <summary>A body line at row <paramref name="row"/> (paragraph spacing: rows are far apart, wrapped lines close).</summary>
    private static OcrLine Line(string text, double top, bool indented = true, bool full = false, double height = Body) =>
        new(text, indented ? Indent : Margin, top, full ? FullRight : (indented ? Indent : Margin) + text.Length * height, top + height);

    [Fact]
    public void Indented_lines_far_apart_are_paragraphs_and_the_apps_small_text_is_left_out()
    {
        var page = LensPageReader.Read(
        [
            new OcrLine("第一章 春天", Margin, 20, Margin + 200, 44), // the chapter name at the top, small
            Line("他推开门走了进去。", 120),
            Line("屋里很安静。", 240),
            Line("她抬头看了他一眼。", 360),
            new OcrLine("12/300", Margin, 1380, Margin + 150, 1404), // page number
            new OcrLine("22:06", 2200, 1380, 2300, 1404), // clock
        ], Width);

        Assert.Equal(["他推开门走了进去。", "屋里很安静。", "她抬头看了他一眼。"], page.Paragraphs.Select(p => p.Chinese));
        Assert.All(page.Paragraphs, p => Assert.False(p.IsHeading));
        Assert.False(page.ContinuesFromPrevious);
        Assert.False(page.ContinuesOnNext);
    }

    [Fact]
    public void A_wrapped_paragraph_is_joined_and_a_new_indented_line_starts_the_next()
    {
        var page = LensPageReader.Read(
        [
            Line("这是一段很长的话它一直写到了这一行的最右边", 120, full: true),
            Line("然后才在下一行结束。", 170, indented: false),
            Line("新的一段。", 290),
        ], Width);

        Assert.Equal(["这是一段很长的话它一直写到了这一行的最右边然后才在下一行结束。", "新的一段。"], page.Paragraphs.Select(p => p.Chinese));
    }

    [Fact]
    public void A_page_starting_mid_paragraph_and_ending_on_a_full_line_says_so()
    {
        var page = LensPageReader.Read(
        [
            Line("接着上一页说完的话。", 120, indented: false),
            Line("中间的一段。", 240),
            Line("最后一段写满了整整一行还没有说完它要到下一页", 360, full: true),
        ], Width);

        Assert.True(page.ContinuesFromPrevious);
        Assert.True(page.ContinuesOnNext);
        Assert.Equal(3, page.Paragraphs.Count);
    }

    [Fact]
    public void A_larger_line_or_a_chapter_number_is_the_title()
    {
        var page = LensPageReader.Read(
        [
            new OcrLine("第十二章 重逢", Margin, 100, Margin + 400, 160), // larger than the body
            Line("雨下了一整夜。", 240),
            Line("第十三章 离开", 360), // body size, but a chapter number
        ], Width);

        Assert.Equal([true, false, true], page.Paragraphs.Select(p => p.IsHeading));
        Assert.Equal("第十二章 重逢", page.Paragraphs[0].Chinese);
    }

    [Fact]
    public void Pieces_of_one_line_are_joined_left_to_right_and_a_lone_button_glyph_is_dropped()
    {
        var page = LensPageReader.Read(
        [
            new OcrLine("看着窗外。", Indent + 200, 120, Indent + 400, 160),
            new OcrLine("他站起来", Indent, 122, Indent + 160, 161),
            new OcrLine("听", 2300, 600, 2350, 660), // the app's floating "listen" button
            Line("天黑了。", 240),
        ], Width);

        Assert.Equal(["他站起来看着窗外。", "天黑了。"], page.Paragraphs.Select(p => p.Chinese));
    }

    [Theory]
    [InlineData("他 说 ： 好", "他说：好")]
    [InlineData("你在哪?", "你在哪？")]
    [InlineData("好,走吧!", "好，走吧！")]
    [InlineData("ABC 123", "ABC 123")]
    public void Recognised_text_loses_spaces_between_characters_and_gets_full_width_marks(string recognised, string expected) =>
        Assert.Equal(expected, LensPageReader.Clean(recognised));

    [Fact]
    public void Nothing_recognised_is_an_empty_page() =>
        Assert.Empty(LensPageReader.Read([], Width).Paragraphs);
}
