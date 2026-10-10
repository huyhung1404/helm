namespace Helm.Modules.NovelReader.Conversion;

/// <summary>How a stretch of the Chinese text was translated.</summary>
public enum SegmentKind
{
    /// <summary>Punctuation, spaces, Latin letters and digits, copied as they are.</summary>
    Text,
    /// <summary>A character read as Hán Việt because no phrase covered it.</summary>
    HanViet,
    /// <summary>A Han character with no reading at all, copied as it is.</summary>
    Unknown,
    /// <summary>A VietPhrase entry (shared or the user's own).</summary>
    Phrase,
    /// <summary>A name (Names.txt, the user's names for all novels, or for this novel).</summary>
    Name,
    /// <summary>A LuatNhan rule or a chapter heading ("第12章" → "Chương 12").</summary>
    Rule,
}

/// <summary>What the reader shows.</summary>
public enum ConvertMode
{
    VietPhrase,
    HanViet,
    Chinese,
}

/// <summary>
/// One piece of a converted line: the Chinese it came from (<see cref="Start"/>/<see cref="Length"/> in
/// <see cref="ConvertedLine.Source"/>), the text shown and the separator before it ("" or " ").
/// </summary>
public sealed record Segment(int Start, int Length, string Text, SegmentKind Kind, string Before)
{
    /// <summary>A word the reader can click to look up or fix (not punctuation).</summary>
    public bool IsWord => Kind is not SegmentKind.Text;
}

/// <summary>One converted paragraph.</summary>
public sealed class ConvertedLine
{
    public ConvertedLine(string source, IReadOnlyList<Segment> segments, bool isSceneBreak = false)
    {
        Source = source;
        Segments = segments;
        IsSceneBreak = isSceneBreak;
        Text = isSceneBreak ? "* * *" : string.Concat(segments.Select(s => s.Before + s.Text));
    }

    /// <summary>The paragraph after <see cref="Text.ChineseText.NormalizeLine"/>; segment positions refer to it.</summary>
    public string Source { get; }

    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>The whole converted paragraph.</summary>
    public string Text { get; }

    /// <summary>A line of only "*" (or similar) between scenes: shown as "* * *", never read aloud.</summary>
    public bool IsSceneBreak { get; }

    public string SourceOf(Segment segment) => Source.Substring(segment.Start, segment.Length);
}
