using System.Text;
using System.Text.RegularExpressions;

namespace Helm.Modules.Workspace;

public enum MarkdownBlockKind { Paragraph, Heading, Bullet, Numbered, Code, Table, Quote, Rule }

/// <summary>A run of text with one style.</summary>
public sealed record MarkdownSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Link = false);

/// <summary>One block of a Markdown text, ready to draw.</summary>
public sealed record MarkdownBlock
{
    public MarkdownBlockKind Kind { get; init; }

    /// <summary>Heading: 1–6. List item: its depth (0 = top).</summary>
    public int Level { get; init; }

    /// <summary>Numbered item: its number as written ("3.").</summary>
    public string Marker { get; init; } = "";

    public IReadOnlyList<MarkdownSpan> Spans { get; init; } = [];

    /// <summary>Code block: its text and language.</summary>
    public string Code { get; init; } = "";
    public string Language { get; init; } = "";

    /// <summary>Table: rows of cells (the first row is the header), each cell its spans.</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> Rows { get; init; } = [];
}

/// <summary>
/// The Markdown Claude writes, as blocks the phone draws natively: headings, paragraphs, bullet and numbered lists (with
/// nesting), fenced code, tables, quotes and rules; inside them bold, italic, inline code and links. Not a full
/// CommonMark parser: what Claude's answers use, read forgivingly (an unclosed mark stays as text).
/// </summary>
public static partial class MarkdownBlocks
{
    public static IReadOnlyList<MarkdownBlock> Parse(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new StringBuilder();

        void Flush()
        {
            if (paragraph.Length == 0) return;
            blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Paragraph, Spans = Inline(paragraph.ToString()) });
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                Flush();
                continue;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                var fence = trimmed[..3];
                var code = new List<string>();
                var indent = line.Length - line.TrimStart().Length;
                i++;
                for (; i < lines.Length && !lines[i].Trim().StartsWith(fence, StringComparison.Ordinal); i++)
                    code.Add(lines[i].Length >= indent && lines[i][..indent].Trim().Length == 0 ? lines[i][indent..] : lines[i].TrimStart());
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Code, Code = string.Join("\n", code), Language = trimmed[3..].Trim() });
                continue;
            }
            if (trimmed.StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1].Trim()))
            {
                Flush();
                var rows = new List<IReadOnlyList<IReadOnlyList<MarkdownSpan>>> { Cells(trimmed) };
                i += 2;
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++) rows.Add(Cells(lines[i].Trim()));
                i--;
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Table, Rows = rows });
                continue;
            }
            if (Heading().Match(trimmed) is { Success: true } h)
            {
                Flush();
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Heading, Level = h.Groups[1].Length, Spans = Inline(h.Groups[2].Value.TrimEnd('#', ' ')) });
                continue;
            }
            if (Rule().IsMatch(trimmed))
            {
                Flush();
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Rule });
                continue;
            }
            var indentWidth = line.Length - line.TrimStart().Length;
            if (Bullet().Match(line) is { Success: true } b)
            {
                Flush();
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Bullet, Level = Depth(indentWidth), Spans = Inline(ListText(b.Groups[2].Value, lines, ref i)) });
                continue;
            }
            if (Numbered().Match(line) is { Success: true } n)
            {
                Flush();
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Numbered, Level = Depth(indentWidth), Marker = n.Groups[2].Value + ".", Spans = Inline(ListText(n.Groups[3].Value, lines, ref i)) });
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                Flush();
                var quote = new StringBuilder(trimmed.TrimStart('>').Trim());
                while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('>')) quote.Append(' ').Append(lines[++i].Trim().TrimStart('>').Trim());
                blocks.Add(new MarkdownBlock { Kind = MarkdownBlockKind.Quote, Spans = Inline(quote.ToString()) });
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append(line.EndsWith("  ", StringComparison.Ordinal) ? "\n" : " ");
            paragraph.Append(trimmed);
        }
        Flush();
        return blocks;
    }

    private static int Depth(int indent) => Math.Min(4, indent / 2);

    /// <summary>A list item's text with its wrapped lines (indented, not starting a new item or block).</summary>
    private static string ListText(string first, string[] lines, ref int i)
    {
        var text = new StringBuilder(first.Trim());
        while (i + 1 < lines.Length)
        {
            var next = lines[i + 1];
            if (next.Trim().Length == 0 || next.Length == next.TrimStart().Length || Bullet().IsMatch(next) || Numbered().IsMatch(next)
                || next.Trim().StartsWith("```", StringComparison.Ordinal) || next.Trim().StartsWith('|')) break;
            text.Append(' ').Append(next.Trim());
            i++;
        }
        return text.ToString();
    }

    private static IReadOnlyList<IReadOnlyList<MarkdownSpan>> Cells(string row)
    {
        var inner = row.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|') && !inner.EndsWith("\\|", StringComparison.Ordinal)) inner = inner[..^1];
        return SplitCells(inner).Select(c => Inline(c.Trim())).ToList();
    }

    private static IEnumerable<string> SplitCells(string inner)
    {
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '\\' && i + 1 < inner.Length && inner[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { yield return cell.ToString(); cell.Clear(); continue; }
            cell.Append(c);
        }
        yield return cell.ToString();
    }

    /// <summary>Bold, italic, inline code and links of one line of text.</summary>
    public static IReadOnlyList<MarkdownSpan> Inline(string text)
    {
        var spans = new List<MarkdownSpan>();
        Inline(text, false, false, spans);
        // Neighbours with the same style are one run.
        var merged = new List<MarkdownSpan>();
        foreach (var s in spans.Where(s => s.Text.Length > 0))
        {
            if (merged.Count > 0 && merged[^1] with { Text = "" } == s with { Text = "" }) merged[^1] = merged[^1] with { Text = merged[^1].Text + s.Text };
            else merged.Add(s);
        }
        return merged;
    }

    private static void Inline(string text, bool bold, bool italic, List<MarkdownSpan> spans)
    {
        var plain = new StringBuilder();
        void Plain()
        {
            if (plain.Length == 0) return;
            spans.Add(new MarkdownSpan(plain.ToString(), bold, italic));
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_[]()#|>-!".Contains(text[i + 1]))
            {
                plain.Append(text[++i]);
                continue;
            }
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var close = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (close > 0)
                {
                    Plain();
                    spans.Add(new MarkdownSpan(text[(i + ticks)..close].Trim(), bold, italic, Code: true));
                    i = close + ticks - 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var close = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Plain();
                    Inline(text[(i + 2)..close], true, italic, spans);
                    i = close + 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var close = FindItalicEnd(text, i + 1, c);
                if (close > i + 1)
                {
                    Plain();
                    Inline(text[(i + 1)..close], bold, true, spans);
                    i = close;
                    continue;
                }
            }
            if (c == '[')
            {
                var mid = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                var end = mid > 0 ? text.IndexOf(')', mid + 2) : -1;
                if (mid > i + 1 && end > mid && !text[(i + 1)..mid].Contains('\n'))
                {
                    Plain();
                    spans.Add(new MarkdownSpan(text[(i + 1)..mid], bold, italic, Link: true));
                    i = end;
                    continue;
                }
            }
            plain.Append(c);
        }
        Plain();
    }

    private static int FindItalicEnd(string text, int from, char mark)
    {
        for (var j = from; j < text.Length; j++)
        {
            if (text[j] == '`') { var close = text.IndexOf('`', j + 1); if (close < 0) return -1; j = close; continue; }
            if (text[j] != mark || char.IsWhiteSpace(text[j - 1])) continue;
            if (j + 1 < text.Length && text[j + 1] == mark) { j++; continue; }
            if (mark == '_' && j + 1 < text.Length && char.IsLetterOrDigit(text[j + 1])) continue;
            return j;
        }
        return -1;
    }

    /// <summary>The text without its marks (for previews and notifications).</summary>
    public static string Plain(string markdown) =>
        string.Join(" ", Parse(markdown).Select(b => b.Kind == MarkdownBlockKind.Code ? b.Code : string.Concat(b.Spans.Select(s => s.Text)))).Trim();

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();
}
