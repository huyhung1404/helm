using System.Text;
using System.Text.RegularExpressions;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Lens;

/// <summary>A line of text recognised on a screenshot, with its box in pixels.</summary>
public sealed record OcrLine(string Text, double Left, double Top, double Right, double Bottom)
{
    public double Height => Bottom - Top;
    public double Middle => (Top + Bottom) / 2;
}

/// <summary>A paragraph of the page read: its Chinese, and whether it is the chapter's title.</summary>
public sealed record LensParagraph(string Chinese, bool IsHeading);

/// <summary>
/// The page read from a screenshot of the reading app. <see cref="ContinuesFromPrevious"/>: its first paragraph started
/// on the page before; <see cref="ContinuesOnNext"/>: its last one goes on to the next page.
/// </summary>
public sealed record LensPage(IReadOnlyList<LensParagraph> Paragraphs, bool ContinuesFromPrevious, bool ContinuesOnNext)
{
    public static LensPage Empty { get; } = new([], false, false);
}

/// <summary>
/// Turns the lines recognised on a screenshot of a reading app (Fanqie) into the page's paragraphs. The reading app
/// draws the body in one size; smaller text around it (the chapter name and clock at the top, the page number and
/// battery at the bottom, the app's menu) is left out, and larger text is the chapter's title. A paragraph starts
/// indented; a line that is not indented, follows a line filling the width and is no further below it than lines of
/// one paragraph are goes on the paragraph before.
/// </summary>
public static partial class LensPageReader
{
    /// <summary>Lines smaller than this share of the body's line height are the app's own text, not the novel's.</summary>
    private const double SmallShare = 0.78;

    /// <summary>Lines larger than this share of the body's line height are a title.</summary>
    private const double LargeShare = 1.3;

    public static LensPage Read(IReadOnlyList<OcrLine> recognised, double screenWidth)
    {
        var rows = JoinRows(recognised.Where(l => l.Text.Trim().Length > 0 && l.Height > 0).ToList());
        if (rows.Count == 0) return LensPage.Empty;
        var body = Median(rows.Where(r => r.Text.Count(ChineseText.IsHan) >= 4).Select(r => r.Height).DefaultIfEmpty(Median(rows.Select(r => r.Height))));
        var lines = rows
            .Where(r => r.Height >= body * SmallShare)
            // A floating button of the app (听, a share icon) far right of the text: one or two characters alone.
            .Where(r => !(r.Text.Trim().Length <= 2 && r.Left > screenWidth * 0.6))
            .OrderBy(r => r.Top)
            .ToList();
        if (lines.Count == 0) return LensPage.Empty;

        var text = lines.Where(l => l.Height <= body * LargeShare).ToList();
        if (text.Count == 0) text = lines;
        var margin = text.Min(l => l.Left);
        var right = text.Max(l => l.Right);
        // One character is about as wide as the line is high.
        bool Indented(OcrLine l) => l.Left > margin + body * 0.8;
        bool Full(OcrLine l) => l.Right >= right - body * 1.5;
        var anyIndented = text.Any(Indented);

        var paragraphs = new List<(StringBuilder Text, bool Heading)>();
        OcrLine? previous = null;
        foreach (var line in lines)
        {
            var heading = line.Height > body * LargeShare || IsChapterTitle(line.Text);
            var joins = previous is not null && !heading && paragraphs.Count > 0 && !paragraphs[^1].Heading
                        && !Indented(line) && Full(previous) && line.Top - previous.Bottom <= body * 1.2;
            if (joins) paragraphs[^1].Text.Append(Clean(line.Text));
            else paragraphs.Add((new StringBuilder(Clean(line.Text)), heading));
            previous = line;
        }

        var first = lines[0];
        var last = lines[^1];
        var fromPrevious = anyIndented && !Indented(first) && !paragraphs[0].Heading;
        var onNext = Full(last) && !paragraphs[^1].Heading && !EndsSentence(paragraphs[^1].Text.ToString());
        return new LensPage(
            paragraphs.Select(p => new LensParagraph(p.Text.ToString(), p.Heading)).Where(p => p.Chinese.Length > 0).ToList(),
            fromPrevious, onNext);
    }

    /// <summary>Text recognition can cut one line of the page in pieces side by side: they are joined again, left to right.</summary>
    private static List<OcrLine> JoinRows(List<OcrLine> lines)
    {
        var rows = new List<OcrLine>();
        foreach (var line in lines.OrderBy(l => l.Middle))
        {
            var row = rows.FindIndex(r => line.Middle > r.Top && line.Middle < r.Bottom && Math.Abs(line.Height - r.Height) < Math.Max(line.Height, r.Height) * 0.35);
            if (row < 0)
            {
                rows.Add(line);
                continue;
            }
            var r0 = rows[row];
            var (a, b) = r0.Left <= line.Left ? (r0, line) : (line, r0);
            rows[row] = new OcrLine(a.Text + b.Text, a.Left, Math.Min(a.Top, b.Top), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));
        }
        return rows;
    }

    /// <summary>
    /// Recognised Chinese with the recognizer's habits undone: spaces between Chinese characters, and Latin punctuation
    /// next to Chinese turned into the full-width marks the novel uses.
    /// </summary>
    public static string Clean(string text)
    {
        text = text.Trim();
        // "第十二章 重逢": the space after a chapter's number is the title's own.
        if (ChapterTitle().Match(text) is { Success: true } number && number.Length < text.Length && char.IsWhiteSpace(text[number.Length]))
            return Clean(number.Value) + " " + Clean(text[number.Length..]);
        text = SpaceBetweenHan().Replace(text, "");
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var nearHan = (i > 0 && ChineseText.IsHan(text[i - 1])) || (i + 1 < text.Length && ChineseText.IsHan(text[i + 1]));
            result.Append(nearHan ? c switch
            {
                ',' => '，',
                '?' => '？',
                '!' => '！',
                ':' => '：',
                ';' => '；',
                '(' => '（',
                ')' => '）',
                _ => c,
            } : c);
        }
        return result.ToString();
    }

    private static bool IsChapterTitle(string text) => ChapterTitle().IsMatch(text.Trim());

    private static bool EndsSentence(string text) => text.Length > 0 && "。！？…”」』）!?\"".Contains(text[^1]);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    [GeneratedRegex(@"(?<=\p{IsCJKUnifiedIdeographs}|\p{P})\s+(?=\p{IsCJKUnifiedIdeographs}|\p{P})")]
    private static partial Regex SpaceBetweenHan();

    [GeneratedRegex(@"^第[0-9零一二三四五六七八九十百千万两]+[章节回卷]")]
    private static partial Regex ChapterTitle();
}
