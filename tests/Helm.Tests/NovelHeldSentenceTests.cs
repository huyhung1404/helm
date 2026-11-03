using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Tests;

/// <summary>A long press on the text: the sentence around the word, its Chinese and pinyin, and the word itself.</summary>
public sealed class NovelHeldSentenceTests
{
    private const string Pinyin = "他: tā\n她: tā\n走: zǒu\n了: le\n沉默: chén mò\n良久: liáng jiǔ\n终于: zhōng yú\n点头: diǎn tóu\n";

    // "他沉默良久, 终于点头. 她走了." as the converter cuts it (punctuation already ASCII, as the page's lines have it).
    private static readonly ConvertedLine Line = new("他沉默良久, 终于点头. 她走了.",
    [
        new Segment(0, 1, "Hắn", SegmentKind.Phrase, ""),
        new Segment(1, 2, "trầm mặc", SegmentKind.Phrase, " "),
        new Segment(3, 2, "hồi lâu", SegmentKind.Phrase, " "),
        new Segment(5, 1, ",", SegmentKind.Text, ""),
        new Segment(7, 2, "cuối cùng", SegmentKind.Phrase, " "),
        new Segment(9, 2, "gật đầu", SegmentKind.Phrase, " "),
        new Segment(11, 1, ".", SegmentKind.Text, ""),
        new Segment(13, 1, "Nàng", SegmentKind.Name, " "),
        new Segment(14, 2, "đi rồi", SegmentKind.Phrase, " "),
        new Segment(16, 1, ".", SegmentKind.Text, ""),
    ]);

    [Fact]
    public void A_press_on_a_word_holds_the_sentence_reading_aloud_would_read_with_its_pinyin()
    {
        var sentences = SentenceSplitter.Split(Line);
        var pinyin = PinyinDictionary.Parse(Pinyin);

        var second = HeldSentence.At(4, Line, sentences, 8, pinyin)!;
        Assert.Equal((4, 7, 3), (second.Paragraph, second.FirstSegment, second.SegmentCount));
        Assert.Equal("她走了.", second.Chinese);
        // A name reads like one.
        Assert.Equal("Tā zǒu le.", second.Pinyin);
        Assert.Equal(new SentenceWord("走了", "zǒu le", "đi rồi"), second.Word);

        var first = HeldSentence.At(4, Line, sentences, 1, pinyin)!;
        Assert.Equal((0, 7), (first.FirstSegment, first.SegmentCount));
        Assert.Equal("他沉默良久, 终于点头.", first.Chinese);
        Assert.Equal("tā chén mò liáng jiǔ, zhōng yú diǎn tóu.", first.Pinyin);

        // Punctuation is in a sentence but is no word to learn; no pinyin list, no pinyin.
        var comma = HeldSentence.At(4, Line, sentences, 3, null)!;
        Assert.Equal(0, comma.FirstSegment);
        Assert.Equal("", comma.Word.Word);
        Assert.Equal("", comma.Pinyin);
    }

    [Fact]
    public void Nothing_is_held_off_the_line_or_on_a_scene_break_and_a_line_without_sentences_is_held_whole()
    {
        Assert.Null(HeldSentence.At(0, Line, SentenceSplitter.Split(Line), 10, null));
        Assert.Null(HeldSentence.At(0, Line, SentenceSplitter.Split(Line), -1, null));
        Assert.Null(HeldSentence.At(0, new ConvertedLine("*", [new Segment(0, 1, "*", SegmentKind.Text, "")], isSceneBreak: true), [], 0, null));

        var whole = HeldSentence.At(0, Line, [], 4, null)!;
        Assert.Equal((0, 10), (whole.FirstSegment, whole.SegmentCount));
        Assert.Equal(Line.Source, whole.Chinese);
    }

    [Fact]
    public void The_held_sentence_is_highlighted_until_its_sheet_closes_and_reading_aloud_shows_again()
    {
        var paragraph = new ParagraphViewModel(4, "他沉默良久，终于点头。她走了。", false) { Line = Line, SpeakingSentence = 0 };
        Assert.Equal((0, 7), paragraph.HighlightSegments);

        paragraph.HeldSegments = (7, 3);
        Assert.Equal((7, 3), paragraph.HighlightSegments);

        paragraph.HeldSegments = null;
        Assert.Equal((0, 7), paragraph.HighlightSegments);
    }
}
