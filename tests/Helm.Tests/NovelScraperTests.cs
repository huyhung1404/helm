using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Scraping;
using Xunit;

namespace Helm.Tests;

/// <summary>Scrape from web: chapters picked from a list page, a chapter page's text cleaned, the book written out.</summary>
public sealed class NovelScraperTests
{
    private static WebLink L(string text, string url) => new(text, url);

    [Fact]
    public void Only_the_chapters_are_picked_not_the_books_page_or_a_bookmark_script()
    {
        const string toc = "https://www.piaotia.com/html/15/15400/";
        var chapters = ChapterLinks.Pick(toc,
        [
            L("小说死亡作业简介", "https://www.piaotia.com/bookinfo/15/15400.html"),
            L("收藏[死亡作业]以便下次阅读", "javascript:d=document;window.external.AddFavorite('x')"),
            L("第1章诡异的作业", "https://www.piaotia.com/html/15/15400/10810865.html"),
            L("第2章消失的同学", "https://www.piaotia.com/html/15/15400/10810866.html"),
            L("第3章死人好友", "https://www.piaotia.com/html/15/15400/10810868.html"),
            L("飘天文学", "https://www.piaotia.com/"),
        ]);

        Assert.Equal(["第1章诡异的作业", "第2章消失的同学", "第3章死人好友"], chapters.Select(c => c.Text));
    }

    [Fact]
    public void The_latest_chapters_block_above_the_list_does_not_put_the_last_chapters_first_and_other_books_are_left_out()
    {
        const string toc = "https://ixdzs8.com/read/38804/";
        var links = new List<WebLink>
        {
            L("萧玄魂天帝篇", "https://ixdzs8.com/read/38804/p5.html"),
            L("第4章 新书", "https://ixdzs8.com/read/38804/p4.html"),
            L("立即阅读", "https://ixdzs8.com/read/38804/p1.html"),
        };
        for (var i = 1; i <= 5; i++) links.Add(L(i == 5 ? "萧玄魂天帝篇" : $"第{i}章 章{i}", $"https://ixdzs8.com/read/38804/p{i}.html"));
        links.Add(L("https://ixdzs8.com/read/159066/", "https://ixdzs8.com/read/159066/"));
        links.Add(L("https://ixdzs8.com/read/170492/", "https://ixdzs8.com/read/170492/"));

        var chapters = ChapterLinks.Pick(toc, links);

        Assert.Equal(["第1章 章1", "第2章 章2", "第3章 章3", "第4章 章4", "萧玄魂天帝篇"], chapters.Select(c => c.Text));
    }

    [Fact]
    public void A_chapter_listed_twice_keeps_the_name_it_was_given_once()
    {
        const string toc = "https://www.qidian.com/book/1010868264/";
        var chapters = ChapterLinks.Pick(toc,
        [
            L("", "https://www.qidian.com/chapter/1010868264/402733549/"),
            L("第一章 绯红", "https://www.qidian.com/chapter/1010868264/402733549/"),
            L("第二章 情况", "https://www.qidian.com/chapter/1010868264/402760766/"),
            L("第三章 梅丽莎", "https://www.qidian.com/chapter/1010868264/402761081/"),
            L("", "https://www.qidian.com/chapter/1010868264/402761081/"),
        ]);

        Assert.Equal(["第一章 绯红", "第二章 情况", "第三章 梅丽莎"], chapters.Select(c => c.Text));
    }

    [Fact]
    public void A_list_written_newest_first_is_turned_around()
    {
        var links = Enumerable.Range(1, 8).Reverse().Select(i => L($"第{i}章", $"https://m.site.com/book/77123/{100 + i}.html")).ToList();

        var chapters = ChapterLinks.Pick("https://m.site.com/book/77123/", links);

        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"第{i}章"), chapters.Select(c => c.Text));
    }

    [Fact]
    public void A_long_sidebar_of_other_books_loses_to_the_books_own_few_chapters()
    {
        var links = Enumerable.Range(1, 40).Select(i => L($"别的书{i}", $"https://www.biquge.tw/book/{9000 + i}/")).ToList();
        links.AddRange(Enumerable.Range(1, 4).Select(i => L($"楔子{i}", $"https://www.biquge.tw/book/7848572/{500 + i}.html")));

        var chapters = ChapterLinks.Pick("https://www.biquge.tw/book/7848572/", links);

        Assert.Equal(4, chapters.Count);
        Assert.All(chapters, c => Assert.Contains("/book/7848572/", c.Url));
    }

    [Fact]
    public void Further_pages_of_the_list_are_found_but_not_pages_of_other_books()
    {
        var pages = ChapterLinks.ListPages("https://m.site.com/book/77123/",
        [
            L("1~100章", "https://m.site.com/book/77123/index_1.html"),
            L("101 - 200章", "https://m.site.com/book/77123/index_2.html"),
            L("第3页", "https://m.site.com/book/77123/index_3.html"),
            L("下一页", "https://m.site.com/book/77123/index_2.html"),
            L("下一页", "https://m.site.com/top/allvisit_2.html"),
            L("第1章 开始", "https://m.site.com/book/77123/101.html"),
        ]);

        Assert.Equal(["https://m.site.com/book/77123/index_1.html", "https://m.site.com/book/77123/index_2.html", "https://m.site.com/book/77123/index_3.html"], pages);
        Assert.True(ChapterLinks.IsListPage("1~100章"));
        Assert.False(ChapterLinks.IsListPage("第100章 下一页的秘密"));
    }

    [Fact]
    public void The_second_page_of_a_chapter_is_followed_but_not_the_next_chapter()
    {
        const string chapter = "https://www.biquge.tw/book/1/123.html";
        var chapters = new HashSet<string> { chapter, "https://www.biquge.tw/book/1/124.html" };
        var seen = new HashSet<string> { chapter };

        Assert.Equal("https://www.biquge.tw/book/1/123_2.html",
            ChapterLinks.Continuation(chapter, chapter, [L("下一页", "https://www.biquge.tw/book/1/123_2.html")], chapters, seen));
        Assert.Null(ChapterLinks.Continuation(chapter, chapter, [L("下一页", "https://www.biquge.tw/book/1/124.html")], chapters, seen));
        Assert.Null(ChapterLinks.Continuation(chapter, chapter, [L("下一页", "https://www.biquge.tw/book/1/1234.html")], chapters, seen));
        Assert.Null(ChapterLinks.Continuation(chapter, chapter, [L("下一章", "https://www.biquge.tw/book/1/123_2.html")], chapters, seen));
    }

    [Fact]
    public void The_sites_buttons_notices_dates_and_the_repeated_title_are_left_out()
    {
        const string page = """
            死亡作业  第3293章 杰青计划
            上一章   返回目录   下一章   返回书页

                在我成为新晋地尊之后，工作人员看向我的目光中更加的敬畏。
            關燈
            護眼
            小
            中
            大
            ……
            天才一秒记住本站地址：www.piaotia.com
            2026-09-26 21:59
            閱讀全文
            飘天文学，快乐很多
            """;

        var paragraphs = ChapterText.Clean(page, "死亡作业", "飘天文学", "第3293章 杰青计划");

        Assert.Equal(["在我成为新晋地尊之后，工作人员看向我的目光中更加的敬畏。", "……"], paragraphs);
    }

    [Fact]
    public void The_sites_address_slipped_into_a_chapters_link_is_left_out_and_a_shorter_heading_above_the_text_too()
    {
        Assert.Equal("第 53 章", ChapterLinks.CleanText("第 53 章.com"));
        Assert.Equal("現實，千露？", ChapterLinks.CleanText(".com現實，千露？"));
        Assert.Equal("第1章 开始", ChapterLinks.CleanText("第1章 开始 www.xbanxia.cc"));

        Assert.Equal(["正文。"], ChapterText.Clean("第 53 章\n正文。", "书", null, "第 53 章"));
        Assert.Equal(["正文。"], ChapterText.Clean("第53章\n正文。", "书", null, "第53章 归人"));
        // Not the title: kept.
        Assert.Equal(["第五十四章", "正文。"], ChapterText.Clean("第五十四章\n正文。", "书", null, "第53章 归人"));
    }

    [Fact]
    public void Paragraphs_a_site_runs_together_are_taken_apart_and_counts_between_them_left_out()
    {
        var paragraphs = ChapterText.Clean("痛！\n999\n　　好痛！　　头好痛！", "诡秘之主", null, "第一章 绯红");

        Assert.Equal(["痛！", "好痛！", "头好痛！"], paragraphs);
    }

    [Theory]
    [InlineData("死亡作业最新章节,死亡作业无弹窗全文阅读_飘天文学", "飘天文学")]
    [InlineData("黎闕閣牽[艾蒂晚楠], 黎闕閣牽[艾蒂晚楠]小說全文在線閱讀 - 半夏小說", "半夏小說")]
    [InlineData("www.uukanshu.net", null)]
    public void The_sites_name_is_the_last_part_of_the_page_title(string pageTitle, string? expected) =>
        Assert.Equal(expected, ScrapedBook.SiteName(pageTitle));

    [Fact]
    public void A_page_asking_to_subscribe_is_locked()
    {
        const string page = "第五十章 夜\n本章为VIP章节，订阅本章即可阅读全文。";
        var paragraphs = ChapterText.Clean("本章为VIP章节", "书", null, "第五十章 夜");

        Assert.True(ChapterText.LooksLocked(paragraphs, page));
        Assert.False(ChapterText.LooksLocked([new string('字', 400)], page));
    }

    [Theory]
    [InlineData(null, "死亡作业最新章节", "死亡作业最新章节,死亡作业无弹窗全文阅读_飘天文学", "死亡作业")]
    [InlineData(null, "", "诡秘之主(爱潜水的乌贼)小说在线阅读-首发起点中文网", "诡秘之主")]
    [InlineData(null, "半夏小說", "黎闕閣牽[艾蒂晚楠], 黎闕閣牽[艾蒂晚楠]小說全文在線閱讀 - 半夏小說", "黎闕閣牽")]
    [InlineData(null, "", "斗破苍穹－天蚕土豆｜免费线上阅读・TXT下载－爱下电子书", "斗破苍穹")]
    [InlineData(null, "十日终焉", "十日终焉完整版在线免费阅读_十日终焉小说_番茄小说官网", "十日终焉")]
    [InlineData("死亡作业", "", "随便什么", "死亡作业")]
    public void The_books_name_comes_without_the_sites_words(string? bookName, string heading, string pageTitle, string expected) =>
        Assert.Equal(expected, ScrapedBook.Title(bookName, heading, pageTitle));

    [Fact]
    public void The_written_book_splits_back_into_its_chapters_even_with_a_title_without_a_number()
    {
        var chapters = new[]
        {
            new ScrapedChapter("第1章 陨落的天才", ["“斗之力，三段！”", "望着测验魔石碑。", "少年面无表情。", "唇角有着一抹自嘲。", "紧握的手掌。", "因为大力而有些发白。"]),
            new ScrapedChapter("萧炎云韵篇", ["云韵站在山巅。", "风吹起她的衣角。", "远处传来钟声。", "她轻轻叹了口气。", "转身离去。", "山下灯火点点。"]),
        };

        var split = ChapterSplitter.Split(ScrapedBook.Text(chapters));

        Assert.Equal(["第1章 陨落的天才", "萧炎云韵篇"], split.Select(c => c.Title));
        Assert.Equal(chapters.Select(c => c.Paragraphs), split.Select(c => c.Paragraphs));
    }

    private static Chapter C(string title) => new(title, [title + "的正文。"]);

    private static ScrapeItem Got(string title) => new(L(title, "https://x.com/" + title), ScrapeOutcome.Got, new ScrapedChapter(title, [title + "的正文。"]));

    private static ScrapeItem Skipped(string title) => new(L(title, "https://x.com/" + title), ScrapeOutcome.Skipped, null);

    [Fact]
    public void A_novel_lacks_the_chapters_of_its_list_it_does_not_have_by_title_whatever_the_spaces()
    {
        Chapter[] have = [C("第1 章 山寺祸端"), C("第2 章 缠情丝")];

        var missing = BookUpdate.Missing(have,
            [L("第1章 山寺祸端", "u1"), L("第 2 章  缠情丝", "u2"), L("第3章 雨夜相逢", "u3"), L("第4章 帮帮我", "u4")]);

        Assert.Equal(["u3", "u4"], missing.Select(l => l.Url));
    }

    [Fact]
    public void New_chapters_go_where_the_list_has_them_and_every_old_chapter_says_where_it_went()
    {
        // The novel lacks chapter 2 (it failed before) and chapter 5 is new; "前言" is the novel's own and stays first.
        Chapter[] have = [C("前言"), C("第1章"), C("第3章"), C("第4章")];

        var merged = BookUpdate.Merge(have, [Skipped("第1章"), Got("第2章"), Skipped("第3章"), Skipped("第4章"), Got("第5章"),
            new ScrapeItem(L("第6章", "u6"), ScrapeOutcome.Failed, null)]);

        Assert.Equal(["前言", "第1章", "第2章", "第3章", "第4章", "第5章"], merged.Chapters.Select(c => c.Title));
        Assert.Equal([0, 1, 3, 4], merged.NewIndexOfOld);
        Assert.Equal([2, 5], merged.Added);
    }

    [Fact]
    public void Titles_ending_like_a_sentence_or_too_long_still_start_their_chapters_a_paragraph_like_a_heading_does_not_and_none_is_new_the_next_time()
    {
        // xbanxia: "第 1 章", then titles without a number that end in ？ or ！ (they were read as paragraphs once).
        string[] titles = ["第 1 章", "蓝念到底是谁？", "cp粉？！", "打起来了！", "萧炎云韵篇", "“你…很难受吗？”",
            "一个非常非常长的章节标题它超过了四十个字的长度限制所以会被截断成为更短的标题吧我想是的"];
        var chapters = titles.Select(t => new ScrapedChapter(t, ["他推开门走了进去。", "番外很早就已經寫好了，只是沒有發布出去。", "她抬头看了他一眼。", "窗外下着雨。", "灯火点点。", "夜深了。"])).ToList();

        var split = ChapterSplitter.Split(ScrapedBook.Text(chapters));

        Assert.Equal(titles.Length, split.Count);
        Assert.All(split, c => Assert.Equal(6, c.Paragraphs.Count));
        Assert.Equal("【蓝念到底是谁？】", split[1].Title);
        Assert.Equal("萧炎云韵篇", split[4].Title);
        Assert.Empty(BookUpdate.Missing(split, [.. titles.Select((t, i) => L(t, $"u{i}"))]));
    }

    [Fact]
    public void A_traditional_novel_is_stored_in_simplified_and_its_chapters_still_match_the_traditional_list()
    {
        const string traditional = "田鑫源抓了抓頭發，有些懊惱為什麽會被唐緒煜拐騙過來啊？就因為他說我在這劇裏能找到對象？";
        Assert.True(Helm.Modules.NovelReader.Text.TraditionalChinese.Looks(traditional));
        Assert.False(Helm.Modules.NovelReader.Text.TraditionalChinese.Looks("林宛撑着一柄素色油纸伞，沿着蜿蜒的山路缓步而行。"));
        Assert.Equal("田鑫源抓了抓头发，有些懊恼为什么会被唐绪煜拐骗过来啊？就因为他说我在这剧里能找到对象？",
            Helm.Modules.NovelReader.Text.TraditionalChinese.ToSimplified(traditional));

        var result = ScrapedBook.Simplified(new ScrapeResult("黎闕閣牽", "https://x.com/",
            [new ScrapeItem(L("第 1 章 藍念到底是誰？", "u1"), ScrapeOutcome.Got, new ScrapedChapter("第 1 章 藍念到底是誰？", [traditional]))]));
        Assert.Equal("黎阙阁牵", result.Title);
        Assert.Equal("第 1 章 蓝念到底是谁？", result.Items[0].Chapter!.Title);

        // Continuing: the site still lists Traditional titles; the stored chapter is Simplified.
        Chapter[] have = [new("第 1 章 蓝念到底是谁？", ["正文。"])];
        Assert.Empty(BookUpdate.Missing(have, [L("第 1 章 藍念到底是誰？", "u1")]));
    }

    [Fact]
    public void A_search_page_gives_the_novels_named_like_the_search_the_exact_one_first()
    {
        var hits = NovelSearch.Hits("斗破苍穹", "https://www.qidian.com/so/斗破苍穹.html",
        [
            L("斗破苍穹之雷霆震碎", "https://www.qidian.com/book/1042192006/"),
            L("斗破苍穹", "https://www.qidian.com/book/1209977/"),
            L("最新更新 《斗破苍穹：斗帝之路》手游", "https://www.qidian.com/chapter/1209977/427202307/"),
            L("斗破苍穹", "https://www.qidian.com/book/1209977/"),
            L("斗破苍穹搜索结果", "https://www.qidian.com/so/斗破苍穹.html"),
            L("天蚕土豆", "https://www.qidian.com/author/4362/"),
            L("斗破苍穹 同人", "https://other.com/book/1/"),
        ], "Qidian");

        Assert.Equal(["斗破苍穹", "斗破苍穹之雷霆震碎"], hits.Select(h => h.Title));
        Assert.All(hits, h => Assert.Equal("Qidian", h.Site));
        // Traditional titles answer a Simplified search.
        Assert.Single(NovelSearch.Hits("龙傲天", "https://www.xbanxia.cc/modules/article/search_t.php",
            [L("龍傲天也拒絕不了嬌妻腦", "http://www.xbanxia.cc/books/428334.html")], "xbanxia"));
        Assert.False(NovelSearch.IsBookPage("https://www.qidian.com/so/死亡作业.html"));
    }

    [Fact]
    public void A_books_information_page_leads_to_its_chapter_list()
    {
        Assert.Equal("https://www.piaotia.com/html/15/15400/", ChapterLinks.CatalogLink("https://www.piaotia.com/bookinfo/15/15400.html",
        [
            L("首页", "https://www.piaotia.com/"),
            L("目录", "https://www.piaotia.com/top/allvisit.html"),
            L("点击阅读", "https://www.piaotia.com/html/15/15400/"),
        ]));
        Assert.Null(ChapterLinks.CatalogLink("https://www.piaotia.com/bookinfo/15/15400.html", [L("目录", "https://www.piaotia.com/top/allvisit.html")]));
    }

    [Fact]
    public void Fanqies_own_characters_are_turned_back_with_the_corrected_table_and_its_paywall_is_a_locked_chapter()
    {
        Assert.Equal(372, FanqieText.Length);
        Assert.Equal(10, FanqieText.Gaps);
        // 根本 / 点头 / 给 / 一名 / 自己 / 事情 / 意思, the entries the source table had wrong.
        Assert.Equal("根本点头给一名自己事情意思", FanqieText.Decode("根头一自事思"));
        Assert.Equal("第1章 空屋", FanqieText.Decode("第1章 空屋"));
        Assert.True(FanqieText.IsScrambled(""));
        Assert.True(FanqieText.KnowsFont("https://lf6-awef.bytetos.com/obj/awesome-font/c/dc027189e0ba4cd.woff2"));
        Assert.False(FanqieText.KnowsFont("https://lf6-awef.bytetos.com/obj/awesome-font/c/0123456789abcde.woff2"));

        const string chapter11 = "齐夏座坐，既站寻找线索。\n怪怪。\n扫码下载APP免费读，SVIP网页畅读\n会员登录后，可在网页畅读全文";
        var paragraphs = ChapterText.Clean(chapter11, "十日终焉", "番茄小说官网", "第11章 继续吧");
        Assert.True(ChapterText.LooksLocked(paragraphs, chapter11));
    }

    [Fact]
    public void An_empty_novel_takes_every_chapter_that_came()
    {
        var empty = ChapterSplitter.Split("");

        var merged = BookUpdate.Merge(empty, [Got("第1章"), Got("第2章")]);

        Assert.Equal(["第1章", "第2章"], merged.Chapters.Select(c => c.Title));
        Assert.Equal([0], merged.NewIndexOfOld);
    }
}
