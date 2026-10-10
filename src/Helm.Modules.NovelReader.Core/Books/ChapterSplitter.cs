using System.Text.RegularExpressions;

namespace Helm.Modules.NovelReader.Books;

/// <summary>One chapter: its title line and its paragraphs (non-blank lines), both still in Chinese.</summary>
public sealed record Chapter(string Title, IReadOnlyList<string> Paragraphs)
{
    public int Length => Paragraphs.Sum(p => p.Length);
}

/// <summary>
/// Splits a novel into chapters. A line is a heading when it is short, does not end like a sentence, and either looks
/// like one ("第12章 …", "第1 章 …", "番外 …", "楔子") or stands alone the way downloaders write headings: two blank lines
/// before it and one after, in a file whose paragraphs are separated by single blank lines. Text before the first
/// heading becomes "前言"; a file with no heading at all is cut into parts of about 8,000 characters.
/// </summary>
public static partial class ChapterSplitter
{
    public const int MaxHeadingLength = 40;

    private const int PartLength = 8000;

    [GeneratedRegex(@"^\s*(第\s*[0-9０-９零〇一二两三四五六七八九十百千万]+\s*[章节回卷集]|番外|序章|序言|楔子|引子|尾声|后记|终章|完本感言)")]
    private static partial Regex HeadingPattern();

    public static IReadOnlyList<Chapter> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blank = lines.Select(l => IsBlank(l)).ToArray();
        var useLayout = HasHeadingLayout(blank);
        var chapters = new List<Chapter>();
        string? title = null;
        var paragraphs = new List<string>();

        void Close()
        {
            if (title is null && paragraphs.Count == 0) return;
            // A heading straight after another one (a book title line, a volume line) has nothing of its own.
            if (paragraphs.Count > 0 || title is null) chapters.Add(new Chapter(title ?? "前言", paragraphs));
            paragraphs = [];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (blank[i]) continue;
            var line = Clean(lines[i]);
            if (IsHeading(line, i, blank, useLayout))
            {
                Close();
                title = line;
                continue;
            }
            paragraphs.Add(line);
        }
        Close();
        if (chapters.Count == 1 && chapters[0].Title == "前言") return Parts(chapters[0].Paragraphs);
        return chapters.Count == 0 ? [new Chapter("前言", [])] : chapters;
    }

    /// <summary>"第12章 …" or "番外 …": a heading even when its title ends like a sentence ("你…很难受吗？").</summary>
    public static bool LooksLikeHeading(string line) => line.Length <= MaxHeadingLength && HeadingPattern().IsMatch(line);

    private static bool IsHeading(string line, int index, bool[] blank, bool useLayout)
    {
        if (LooksLikeHeading(line)) return true;
        if (!useLayout || line.Length > MaxHeadingLength || EndsLikeSentence(line)) return false;
        var twoBlankBefore = index == 0 || (index >= 2 && blank[index - 1] && blank[index - 2]);
        var blankAfter = index + 1 < blank.Length && blank[index + 1];
        return twoBlankBefore && blankAfter;
    }

    /// <summary>
    /// Paragraphs are separated by one blank line and chapters by two (what downloaders such as biquge scripts write):
    /// two-line gaps exist but are far fewer than one-line gaps.
    /// </summary>
    private static bool HasHeadingLayout(bool[] blank)
    {
        int single = 0, @double = 0, run = 0;
        foreach (var b in blank)
        {
            if (b)
            {
                run++;
                continue;
            }
            if (run == 1) single++;
            else if (run == 2) @double++;
            run = 0;
        }
        return @double > 0 && single >= @double * 5;
    }

    private static IReadOnlyList<Chapter> Parts(IReadOnlyList<string> paragraphs)
    {
        var parts = new List<Chapter>();
        var current = new List<string>();
        var length = 0;
        foreach (var p in paragraphs)
        {
            current.Add(p);
            length += p.Length;
            if (length < PartLength) continue;
            parts.Add(new Chapter($"第{parts.Count + 1}章", current));
            current = [];
            length = 0;
        }
        if (current.Count > 0 || parts.Count == 0) parts.Add(new Chapter($"第{parts.Count + 1}章", current));
        return parts;
    }

    private static bool EndsLikeSentence(string line) => line[^1] is '。' or '！' or '？' or '”' or '"' or '…' or '，' or '：';

    private static bool IsBlank(string line)
    {
        foreach (var c in line)
            if (!char.IsWhiteSpace(c) && c != '　' && c != '﻿') return false;
        return true;
    }

    private static string Clean(string line) => line.Trim().Trim('　', '﻿').Trim();
}
