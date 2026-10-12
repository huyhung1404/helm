using Helm.Core.Text;

namespace Helm.Tests;

/// <summary>The search every tool shares: accents and case do not matter, the start of a title counts most.</summary>
public sealed class TextSearchTests
{
    [Theory]
    [InlineData("Ghi chú", "ghi chu")]
    [InlineData("Đường Điện Biên Phủ", "duong dien bien phu")]
    [InlineData("TIẾNG VIỆT", "tieng viet")]
    [InlineData("Café", "cafe")]
    [InlineData("", "")]
    public void Folding_drops_accents_and_case(string text, string folded) => Assert.Equal(folded, TextSearch.Fold(text));

    [Fact]
    public void Scores_prefer_the_start_then_word_starts_then_anywhere_then_the_detail()
    {
        double S(string query, string title, string? detail = null) => TextSearch.Score(TextSearch.Terms(query), title, detail);

        Assert.True(S("not", "Notes") > S("not", "My notes"));
        Assert.True(S("not", "My notes") > S("ote", "Notes"));
        Assert.True(S("ote", "Notes") > S("milk", "Shopping", "buy milk"));
        Assert.True(S("milk", "Shopping", "buy milk") > 0);
        Assert.Equal(0, S("milk bread", "Shopping", "buy milk")); // every word must match
        Assert.True(S("ghi chu", "Ghi chú họp") > 0);
        Assert.True(S("qc", "Quick Capture") > 0); // initials
        Assert.Equal(0, S("xyz", "Quick Capture"));
        Assert.True(S("notes", "Notes") > S("notes", "Notes settings")); // shorter wins a tie
    }
}
