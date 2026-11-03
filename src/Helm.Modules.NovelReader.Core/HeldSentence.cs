using System.Text;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Speech;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The sentence a long press landed in: its segments (to highlight it, as reading aloud does), its Chinese (what is
/// explained and said), its pinyin, and the word under the finger (offered for Vocabulary even before an explanation).
/// </summary>
/// <param name="Paragraph">The paragraph's place on the page.</param>
/// <param name="FirstSegment">The sentence's first segment in the paragraph's line.</param>
/// <param name="SegmentCount">How many segments it covers.</param>
/// <param name="Chinese">The sentence's Chinese, as the reader's text has it (punctuation already ASCII).</param>
/// <param name="Pinyin">Its pinyin, word by word; "" without the pinyin dictionary.</param>
/// <param name="Word">The held word: its Chinese, pinyin and Helm's Vietnamese for it.</param>
public sealed record HeldSentence(int Paragraph, int FirstSegment, int SegmentCount, string Chinese, string Pinyin, SentenceWord Word)
{
    /// <summary>
    /// The sentence of <paramref name="line"/> that holds segment <paramref name="segment"/>, cut as reading aloud cuts
    /// it (<paramref name="sentences"/>); the whole line when none does. Null for a scene break or a segment off the line.
    /// </summary>
    public static HeldSentence? At(int paragraph, ConvertedLine line, IReadOnlyList<Sentence> sentences, int segment, PinyinDictionary? pinyin)
    {
        if (line.IsSceneBreak || segment < 0 || segment >= line.Segments.Count) return null;
        var (first, count) = sentences.FirstOrDefault(s => s.Contains(segment)) is { } sentence
            ? (sentence.FirstSegment, sentence.SegmentCount)
            : (0, line.Segments.Count);
        var start = line.Segments[first].Start;
        var last = line.Segments[first + count - 1];
        var chinese = Slice(line.Source, start, last.Start + last.Length - start).Trim();
        if (chinese.Length == 0) return null;
        var held = line.Segments[segment];
        var word = held.IsWord ? new SentenceWord(Slice(line.Source, held.Start, held.Length), ReadingOf(line, held, pinyin), held.Text.Trim()) : new SentenceWord("", "", "");
        return new HeldSentence(paragraph, first, count, chinese, PinyinOf(line, first, count, pinyin), word);
    }

    /// <summary>The pinyin of the sentence's words, spaced, with its punctuation where it falls ("Tā chénmò liángjiǔ, …").</summary>
    private static string PinyinOf(ConvertedLine line, int first, int count, PinyinDictionary? pinyin)
    {
        if (pinyin is not { Count: > 0 }) return "";
        var text = new StringBuilder();
        for (var i = first; i < first + count; i++)
        {
            var segment = line.Segments[i];
            if (!segment.IsWord)
            {
                text.Append(Slice(line.Source, segment.Start, segment.Length).Trim());
                continue;
            }
            if (ReadingOf(line, segment, pinyin) is not { Length: > 0 } reading) continue;
            if (text.Length > 0 && text[^1] != ' ' && text[^1] is not ('“' or '"' or '(')) text.Append(' ');
            text.Append(reading);
        }
        return text.ToString().Trim();
    }

    /// <summary>A word's pinyin, a name's capitalised as study mode shows it.</summary>
    private static string ReadingOf(ConvertedLine line, Segment segment, PinyinDictionary? pinyin)
    {
        if (pinyin is not { Count: > 0 }) return "";
        var reading = pinyin.Of(Slice(line.Source, segment.Start, segment.Length));
        return segment.Kind == SegmentKind.Name ? PinyinDictionary.Capitalize(reading) : reading;
    }

    private static string Slice(string source, int start, int length)
    {
        start = Math.Clamp(start, 0, source.Length);
        return source.Substring(start, Math.Clamp(length, 0, source.Length - start));
    }
}
