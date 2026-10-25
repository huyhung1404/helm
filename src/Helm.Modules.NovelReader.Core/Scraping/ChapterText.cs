using System.Text.RegularExpressions;

namespace Helm.Modules.NovelReader.Scraping;

/// <summary>
/// Turns the text of a chapter's page into its paragraphs: the site's buttons (上一章 返回目录 下一章, 关灯 护眼 大 中 小),
/// its notices (本章未完, 记住本站地址, 最新章节…), dates and the chapter's own title repeated on top are left out, and
/// paragraphs a site runs together with "　　" are taken apart.
/// </summary>
public static partial class ChapterText
{
    private static readonly string[] Buttons =
    [
        "上一章", "下一章", "上一页", "下一页", "上一頁", "下一頁", "返回目录", "返回目錄", "返回书页", "返回書頁", "回目录", "章节目录", "章節目錄",
        "目录", "目錄", "书页", "加入书签", "加入書籤", "书签", "書籤", "加入书架", "投推荐票", "推荐本书", "章节报错", "章節報錯", "报错",
        "关灯", "開燈", "關燈", "开灯", "护眼", "護眼", "字体", "字體", "默认", "默認", "阅读全文", "閱讀全文", "首页", "首頁", "书架", "書架",
        "设置", "設置", "上一", "下一",
    ];

    [GeneratedRegex(@"本章未完|点击下一页|點擊下一頁|继续阅读|繼續閱讀|最新章节|最新章節|一秒记住|一秒記住|记住本站|記住本站|本站地址|首发域名|首發域名|手机阅读|手機閱讀|手机版|手機版|无弹窗|無彈窗|笔趣阁|筆趣閣|请收藏|請收藏|章节错误|章節錯誤|点此举报|點此舉報|加入书架|加入書架|本章完|www\.|https?://|\.com\b|\.net\b|\.org\b|\.cc\b|\.tw\b")]
    private static partial Regex SiteNotice();

    [GeneratedRegex(@"求收藏|求推荐|求推薦|推荐票|推薦票|月票|打赏|打賞|订阅|訂閱")]
    private static partial Regex AuthorNotice();

    [GeneratedRegex(@"^\d{4}[-/.年]\d{1,2}[-/.月]\d{1,2}日?(\s+\d{1,2}:\d{2}(:\d{2})?)?$")]
    private static partial Regex DateLine();

    [GeneratedRegex(@"[　 ]{2,}| {4,}")]
    private static partial Regex RunTogether();

    [GeneratedRegex(@"订阅本章|購買本章|购买本章|VIP章节|VIP章節|本章为VIP|本章為VIP|登录后|登錄後|登入後|付费章节|付費章節|解锁本章|解鎖本章|立即订阅|需要订阅|订阅后|訂閱後|畅读全文|暢讀全文|SVIP|下载APP免费读|扫码下载")]
    private static partial Regex LockNotice();

    /// <summary>
    /// The paragraphs of a chapter page's text. <paramref name="siteName"/> (半夏小說, from the page title) leaves out the
    /// site's own short lines too ("半夏小說，快樂很多").
    /// </summary>
    public static IReadOnlyList<string> Clean(string text, string? bookTitle, string? siteName, params string?[] chapterTitles)
    {
        var titles = chapterTitles.Select(Key).Where(t => t.Length > 0).Distinct().ToList();
        var book = Key(bookTitle);
        var paragraphs = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            foreach (var part in RunTogether().Split(raw))
            {
                var line = Trim(part);
                if (line.Length == 0 || IsSiteText(line)) continue;
                if (!string.IsNullOrEmpty(siteName) && line.Length <= 30 && line.Contains(siteName, StringComparison.Ordinal)) continue;
                // The page repeats the chapter's title (sometimes after the book's) above the text; a heading there that
                // is the start of the title, or the title the start of it ("第 53 章" for "第 53 章 归人"), is it too.
                if (paragraphs.Count == 0 && (IsTitle(Key(line), titles, book) || IsShortTitle(line, titles))) continue;
                paragraphs.Add(line);
            }
        }
        return paragraphs;
    }

    /// <summary>A page that asks to log in, subscribe or pay instead of giving the chapter.</summary>
    public static bool LooksLocked(IReadOnlyList<string> paragraphs, string pageText) =>
        paragraphs.Sum(p => p.Length) < 300 && LockNotice().IsMatch(pageText);

    private static bool IsSiteText(string line)
    {
        // The font size buttons stand alone on their lines (大 中 小); a line "小！" is the story's.
        if (line is "大" or "中" or "小" || OnlyButtons(line) || DateLine().IsMatch(line)) return true;
        // Counts between paragraphs (Qidian's comments on each paragraph: 999, 265).
        if (line.All(char.IsAsciiDigit)) return true;
        if (line.Length <= 80 && SiteNotice().IsMatch(line)) return true;
        return line.Length <= 40 && AuthorNotice().IsMatch(line);
    }

    /// <summary>A row of the site's buttons ("上一章 ← 返回目录 → 下一章"); a line of only "……" is the story's.</summary>
    private static bool OnlyButtons(string line)
    {
        var rest = line;
        foreach (var button in Buttons) rest = rest.Replace(button, "", StringComparison.Ordinal);
        return rest.Length < line.Length && rest.All(c => char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c));
    }

    private static bool IsShortTitle(string line, List<string> titles)
    {
        if (!Books.ChapterSplitter.LooksLikeHeading(line)) return false;
        var key = Key(line);
        return key.Length > 0 && titles.Any(t => t.StartsWith(key, StringComparison.Ordinal) || key.StartsWith(t, StringComparison.Ordinal));
    }

    private static bool IsTitle(string line, List<string> titles, string book)
    {
        if (line.Length == 0) return false;
        if (book.Length > 0 && line == book) return true;
        return titles.Any(title => line == title || (book.Length > 0 && line.StartsWith(book, StringComparison.Ordinal) && line.EndsWith(title, StringComparison.Ordinal)));
    }

    private static string Key(string? text) => BookUpdate.Key(text);

    private static string Trim(string line) => line.Trim().Trim('　', ' ', '﻿').Trim();
}
