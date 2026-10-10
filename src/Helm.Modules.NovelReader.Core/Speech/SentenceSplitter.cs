using System.Text;
using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader.Speech;

/// <summary>
/// A sentence of a converted paragraph: the segments it covers (so the page can highlight it) and the text to speak.
/// </summary>
public sealed record Sentence(int Index, int FirstSegment, int SegmentCount, string Text)
{
    public bool Contains(int segment) => segment >= FirstSegment && segment < FirstSegment + SegmentCount;
}

/// <summary>
/// Cuts a converted paragraph into sentences, the unit reading aloud speaks, skips and highlights: a sentence ends after
/// ". ! ? ..." (with the closing quotes that follow), and a very long one is also cut at a comma so the voice starts
/// quickly. Punctuation-only pieces join the sentence before them.
/// </summary>
public static class SentenceSplitter
{
    /// <summary>Past this length a sentence ends at the next comma or semicolon.</summary>
    public const int LongSentence = 220;

    public static IReadOnlyList<Sentence> Split(ConvertedLine? line)
    {
        if (line is null || line.IsSceneBreak || line.Segments.Count == 0) return [];
        var segments = line.Segments;
        var sentences = new List<Sentence>();
        var text = new StringBuilder();
        var first = 0;

        void Close(int endExclusive)
        {
            var spoken = text.ToString().Trim();
            text.Clear();
            if (endExclusive <= first) return;
            if (!spoken.Any(char.IsLetterOrDigit) && sentences.Count > 0)
            {
                // Only punctuation: it belongs to the sentence before.
                var last = sentences[^1];
                sentences[^1] = last with { SegmentCount = endExclusive - last.FirstSegment, Text = (last.Text + spoken).Trim() };
            }
            else if (spoken.Length > 0)
            {
                sentences.Add(new Sentence(sentences.Count, first, endExclusive - first, spoken));
            }
            first = endExclusive;
        }

        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            text.Append(segment.Before).Append(segment.Text);
            var end = segment.Text.TrimEnd();
            if (end.Length == 0) continue;
            var last = end[^1];
            var ends = last is '.' or '!' or '?' or '…'
                || (text.Length >= LongSentence && last is ',' or ';' or ':');
            if (!ends) continue;
            // Closing quotes and brackets right after the end belong to this sentence.
            while (i + 1 < segments.Count && IsClosing(segments[i + 1]))
            {
                i++;
                text.Append(segments[i].Before).Append(segments[i].Text);
            }
            Close(i + 1);
        }
        Close(segments.Count);
        return sentences;
    }

    private static bool IsClosing(Segment segment) =>
        segment.Kind == SegmentKind.Text && segment.Text is "”" or "’" or "\"" or "'" or ")" or "]" or "»" or "." or "!" or "?";
}
