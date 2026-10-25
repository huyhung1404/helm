using System.Text.RegularExpressions;
using Helm.Modules.NovelReader.Books;

namespace Helm.Modules.NovelReader.Scraping;

/// <summary>A chapter taken from a web page: its title and paragraphs, in Chinese.</summary>
public sealed record ScrapedChapter(string Title, IReadOnlyList<string> Paragraphs);

/// <summary>The book's name and its text written the way <see cref="ChapterSplitter"/> reads it back.</summary>
public static partial class ScrapedBook
{
    [GeneratedRegex(@"(最新章节列表|最新章節列表|最新章节|最新章節|章节目录|章節目錄|在线免费阅读|在線免費閱讀|全文免费阅读|全文免費閱讀|全文阅读|全文閱讀|免费阅读|免費閱讀|在线阅读|線上閱讀|在線閱讀|无弹窗|無彈窗|全集|完整版|txt下载|TXT下载|小说|小說)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleSuffix();

    [GeneratedRegex(@"\s*[(（\[【][^()（）\[\]【】]*[)）\]】]\s*$")]
    private static partial Regex AuthorInBrackets();

    private static readonly char[] TitleSeparators = ['_', '|', '｜', '-', '－', '—', '–', ',', '，', '、'];

    /// <summary>
    /// The book's name: the page's novel meta tag when it has one, else the start of the page title without the site's
    /// words (死亡作业最新章节,死亡作业无弹窗全文阅读_飘天文学 → 死亡作业), or the page's heading when that start holds it
    /// (十日终焉完整版在线免费阅读 → 十日终焉; a heading that is the site's name, 半夏小說, is not in it).
    /// </summary>
    public static string Title(string? bookName, string? heading, string? pageTitle)
    {
        if (!string.IsNullOrWhiteSpace(bookName)) return bookName.Trim();
        var title = pageTitle?.Trim() ?? "";
        var cut = title.IndexOfAny(TitleSeparators);
        title = WithoutSiteWords(cut > 0 ? title[..cut] : title);
        heading = WithoutSiteWords(heading ?? "");
        if (heading.Length is > 0 and <= 30 && title.Contains(heading, StringComparison.Ordinal)) return heading;
        if (title.Length > 0) return title;
        return heading.Length > 0 ? heading : "Novel";
    }

    /// <summary>The site's name: the last part of a page title (…_飘天文学, … - 半夏小說), or null.</summary>
    public static string? SiteName(string? pageTitle)
    {
        var parts = (pageTitle ?? "").Split(TitleSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 && parts[^1].Length is >= 2 and <= 15 ? parts[^1] : null;
    }

    private static string WithoutSiteWords(string title)
    {
        title = title.Trim();
        for (var changed = true; changed && title.Length > 0;)
        {
            var before = title;
            title = TitleSuffix().Replace(title, "").Trim();
            title = AuthorInBrackets().Replace(title, "").Trim();
            changed = title != before;
        }
        return title;
    }

    /// <summary>Chapters per stored part: a few new chapters re-upload one part of about 300 KB, not the whole novel.</summary>
    public const int ChaptersPerPart = 100;

    /// <summary>
    /// The text in parts of <see cref="ChaptersPerPart"/> chapters, each written like <see cref="Text"/>; read back one
    /// after another (a blank line between them) they are the same text.
    /// </summary>
    public static IReadOnlyList<Library.TextPart> Parts(IReadOnlyList<ScrapedChapter> chapters) =>
        chapters.Chunk(ChaptersPerPart).Select(c => new Library.TextPart(Text(c), c.Length)).ToList();

    /// <summary>
    /// The text: each chapter's title, then its paragraphs one blank line apart, chapters two blank lines apart (the
    /// layout the splitter knows, so a title without "第N章" still starts a chapter).
    /// </summary>
    public static string Text(IEnumerable<ScrapedChapter> chapters) =>
        string.Join("\n\n\n", chapters.Select(c => string.Join("\n\n", c.Paragraphs.Select(Paragraph).Prepend(Heading(c.Title))))) + "\n";

    /// <summary>
    /// A paragraph the splitter would take for a heading ("番外很早就已經寫好了，只是沒有發布出去。") starts with a zero-width
    /// space, which the reader drops when it shows the line, so it stays in its chapter.
    /// </summary>
    private static string Paragraph(string paragraph) => ChapterSplitter.LooksLikeHeading(paragraph) ? "​" + paragraph : paragraph;

    /// <summary>
    /// A chapter's title as written into the text, so the splitter reads it back as one: as it is when it can be, cut to
    /// the splitter's length, and in 【】 when it ends like a sentence without "第N章" (蓝念到底是谁？ → 【蓝念到底是谁？】).
    /// Matching chapters by title ignores the brackets.
    /// </summary>
    public static string Heading(string title)
    {
        var line = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return "【】";
        if (line.Length > ChapterSplitter.MaxHeadingLength - 2) line = line[..(ChapterSplitter.MaxHeadingLength - 2)];
        return ChapterSplitter.CanBeHeading(line) ? line : $"【{line}】";
    }
}
