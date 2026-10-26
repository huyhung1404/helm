using Helm.Modules.NovelReader.Conversion;

namespace Helm.Modules.NovelReader.Reports;

/// <summary>
/// Cuts a converted paragraph into Chinese sentences and takes out what a report keeps of one: the Chinese, Helm's
/// Vietnamese for it and the pieces it was made of. Positions are those of <see cref="ConvertedLine.Source"/>, where
/// Chinese punctuation is already ASCII ("。" → ".").
/// </summary>
public static class ReportCapture
{
    /// <summary>A stretch of the source: from <see cref="Start"/>, <see cref="Length"/> characters.</summary>
    public readonly record struct Range(int Start, int Length)
    {
        public int End => Start + Length;
    }

    /// <summary>
    /// The sentences of a normalized paragraph, in order: each ends after ". ! ?" (an ellipsis is one end) and the
    /// closing quotes or brackets right after it; spaces between sentences belong to none.
    /// </summary>
    public static IReadOnlyList<Range> Sentences(string source)
    {
        var list = new List<Range>();
        var start = SkipSpaces(source, 0);
        var i = start;
        while (i < source.Length)
        {
            if (source[i] is '.' or '!' or '?')
            {
                while (i + 1 < source.Length && source[i + 1] is '.' or '!' or '?') i++;
                while (i + 1 < source.Length && source[i + 1] is '”' or '’' or '"' or '\'' or ')' or ']') i++;
                list.Add(new Range(start, i + 1 - start));
                start = SkipSpaces(source, i + 1);
                i = start;
                continue;
            }
            i++;
        }
        var end = source.Length;
        while (end > start && source[end - 1] == ' ') end--;
        if (end > start) list.Add(new Range(start, end - start));
        return list;
    }

    /// <summary>The index of the sentence that holds position <paramref name="at"/> (the nearest one before it between sentences).</summary>
    public static int SentenceAt(IReadOnlyList<Range> sentences, int at)
    {
        for (var i = sentences.Count - 1; i >= 0; i--)
            if (sentences[i].Start <= at) return i;
        return 0;
    }

    /// <summary>Helm's Vietnamese for a stretch of the line: the segments that start in it, joined as the page shows them.</summary>
    public static string TextOf(ConvertedLine line, Range range)
    {
        var parts = InRange(line, range).ToList();
        if (parts.Count == 0) return "";
        // The first segment's space separates it from the sentence before.
        return string.Concat(parts.Select((s, i) => (i == 0 ? "" : s.Before) + s.Text)).Trim();
    }

    /// <summary>The words of the stretch (punctuation left out): the Chinese of each, its Vietnamese and its kind.</summary>
    public static IReadOnlyList<ReportPiece> PiecesOf(ConvertedLine line, Range range) => InRange(line, range)
        .Where(s => s.Kind != SegmentKind.Text)
        .Select(s => new ReportPiece(line.SourceOf(s), s.Text, KindName(s.Kind)))
        .ToList();

    /// <summary>The kind as the report document writes it.</summary>
    public static string KindName(SegmentKind kind) => kind switch
    {
        SegmentKind.Phrase => "phrase",
        SegmentKind.Name => "name",
        SegmentKind.HanViet => "han-viet",
        SegmentKind.Unknown => "unknown",
        SegmentKind.Rule => "rule",
        _ => "text",
    };

    private static IEnumerable<Segment> InRange(ConvertedLine line, Range range) =>
        line.Segments.Where(s => s.Start >= range.Start && s.Start < range.End);

    private static int SkipSpaces(string source, int i)
    {
        while (i < source.Length && source[i] == ' ') i++;
        return i;
    }
}
