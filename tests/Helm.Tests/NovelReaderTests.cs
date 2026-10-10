using System.Text;
using System.Text.Json;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Dictionaries;
using Helm.Modules.NovelReader.Library;
using Helm.Modules.NovelReader.Names;
using Helm.Modules.NovelReader.Speech;
using Helm.Modules.NovelReader.Text;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

public sealed class NovelReaderTests : IDisposable
{
    private const string HanVietFile = "一=nhất\n二=nhị\n林=lâm\n宛=uyển\n走=tẩu\n了=liễu\n他=tha\n说=thuyết\n好=hảo\n在=tại\n身=thân\n上=thượng\n山=sơn\n寺=tự\n" +
        "她=tha\n轻=khinh\n声=thanh\n道=đạo\n不=bất\n急=cấp\n祸=họa\n端=đoan\n雨=vũ\n青=thanh\n竹=trúc\n点=điểm\n头=đầu\n你=nhĩ\n很=ngận\n难=nan\n受=thụ\n吗=mã\n故=cố\n地=địa\n重=trọng\n游=du\n";

    // A piece of F:\Code\biquge_dl\novel_7848572.txt as its downloader writes it: "title\n\ntext\n" + "\n\n" per chapter.
    private const string Novel =
        "第1 章 山寺祸端\r\n\r\n永和十三年春，细雨如丝。\r\n\r\n林宛撑着一柄素色油纸伞。\r\n\r\n*\r\n\r\n青竹紧随其后。\r\n\r\n\r\n" +
        "第181 章 你…很难受吗？\r\n\r\n林宛点头。\r\n\r\n\r\n" +
        "番外 故地重游\r\n\r\n“不急。”她轻声道。\r\n\r\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTime _clock = new(new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
    private readonly List<IDisposable> _owned = [];
    private readonly FakeSyncServer _server = new();
    private readonly byte[] _syncKey = SyncKeyring.CreateMasterKey();

    public NovelReaderTests()
    {
        NovelReaderViewModel.ProgressQuiet = TimeSpan.FromMilliseconds(1);
        NovelReaderViewModel.ProgressInterval = TimeSpan.FromMilliseconds(1);
        _server.Now = () => _clock.GetUtcNow();
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- Reading files ---------------------------------------------------------------------------------------------

    [Fact]
    public void Files_in_any_chinese_encoding_are_read()
    {
        const string text = "林宛撑着油纸伞，细雨如丝。";
        Assert.Equal(text, TextFiles.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray()));
        Assert.Equal(text, TextFiles.Decode(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, TextFiles.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray()));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(text, TextFiles.Decode(Encoding.GetEncoding("GB18030").GetBytes(text)));
    }

    [Theory]
    [InlineData("你好，世界。", "你好, 世界.")]
    [InlineData("他说：“好！”", "他说: “好!”")]
    [InlineData("除非……”", "除非...”")]
    [InlineData("除非......”", "除非...”")]
    [InlineData("吧? ”话音", "吧?”话音")]
    [InlineData("「走」　ＡＢＣ１２３", "“走” ABC123")]
    public void Lines_are_normalized_like_the_dictionaries(string line, string expected) =>
        Assert.Equal(expected, ChineseText.NormalizeLine(line));

    [Fact]
    public void Dictionary_files_keep_the_first_entry_and_empty_meanings()
    {
        var dictionary = PhraseDictionary.Parse("\uFEFF了=\r\n走了=đi rồi/rời đi\r\n走了=bỏ đi\r\nno equals sign\r\n=nothing\r\n本章，完=hết chương\r\n");

        Assert.Equal(3, dictionary.Count);
        Assert.True(dictionary.TryGet("了", out var empty));
        Assert.Equal("", empty);
        Assert.True(dictionary.TryGet("走了", out var first));
        Assert.Equal("đi rồi/rời đi", first);
        Assert.True(dictionary.ContainsKey("本章, 完"));
        Assert.Equal(2, dictionary.MaxLengthFrom('走'));
        Assert.Equal(["đi rồi", "rời đi"], ChineseText.Meanings(first));
    }

    // ---- Converting ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_best_split_wins_and_saved_names_beat_every_phrase()
    {
        var set = Dictionaries(vietPhrase: "林=rừng\n宛走=bước khẽ\n走了=đi rồi\n了=\n");

        Assert.Equal("Rừng bước khẽ.", new Converter(set).ConvertLine("林宛走了。").Text);

        var names = PhraseDictionary.Parse("林宛=Lâm Uyển");
        Assert.Equal("Lâm Uyển đi rồi.", new Converter(set, bookNames: names).ConvertLine("林宛走了。").Text);
    }

    [Fact]
    public void A_phrase_with_a_name_starting_inside_it_is_not_used_when_names_come_first()
    {
        var set = Dictionaries(vietPhrase: "他=hắn\n他林宛=hắn rừng uyển\n", names: "林宛=Lâm Uyển\n");

        Assert.Equal("Hắn Lâm Uyển", new Converter(set, prioritizeNames: true).ConvertLine("他林宛").Text);
        Assert.Equal("Hắn rừng uyển", new Converter(set, prioritizeNames: false).ConvertLine("他林宛").Text);
    }

    [Fact]
    public void Luat_nhan_reorders_the_words_around_a_name()
    {
        var set = Dictionaries(vietPhrase: "在=tại\n", names: "林宛=Lâm Uyển\n", luatNhan: "在{0}身上=trên người {0}\n{0}点头={0} gật đầu\n");

        var converter = new Converter(set);

        Assert.Equal("Trên người Lâm Uyển.", converter.ConvertLine("在林宛身上。").Text);
        Assert.Equal("Lâm Uyển gật đầu.", converter.ConvertLine("林宛点头。").Text);
    }

    [Theory]
    [InlineData("第十二章 山寺", "Chương 12 sơn tự")]
    [InlineData("第1 章 山寺祸端", "Chương 1 sơn tự họa đoan")]
    [InlineData("第一百零五回", "Hồi 105")]
    public void Chapter_headings_read_as_chapter_numbers(string line, string expected) =>
        Assert.Equal(expected, new Converter(Dictionaries()).ConvertLine(line).Text);

    [Fact]
    public void Words_are_spaced_and_capitalized_like_vietnamese()
    {
        var set = Dictionaries(vietPhrase: "他=hắn\n说=nói\n好=tốt\n了=\n");
        var converter = new Converter(set);

        Assert.Equal("Hắn nói: “Tốt.”", converter.ConvertLine("他说：“好。”").Text);
        Assert.Equal("Hắn tốt. Hắn nói, tốt!", converter.ConvertLine("他好了。他说，好！").Text);
        Assert.Equal("Hắn... nói", converter.ConvertLine("他……说").Text);
    }

    [Fact]
    public void Every_word_knows_the_chinese_it_came_from()
    {
        var set = Dictionaries(vietPhrase: "他=hắn\n走了=đi rồi\n");
        var line = new Converter(set).ConvertLine("他走了。");

        var words = line.Segments.Where(s => s.IsWord).Select(s => (line.SourceOf(s), s.Text)).ToList();
        Assert.Equal([("他", "Hắn"), ("走了", "đi rồi")], words);
        Assert.True(new Converter(set).ConvertLine("* * *").IsSceneBreak);
    }

    // ---- Chapters and names ----------------------------------------------------------------------------------------

    [Fact]
    public void Chapters_are_found_by_heading_and_by_the_downloaders_layout()
    {
        var chapters = ChapterSplitter.Split(Novel);

        Assert.Equal(["第1 章 山寺祸端", "第181 章 你…很难受吗？", "番外 故地重游"], chapters.Select(c => c.Title));
        Assert.Equal(["永和十三年春，细雨如丝。", "林宛撑着一柄素色油纸伞。", "*", "青竹紧随其后。"], chapters[0].Paragraphs);
        Assert.Equal(["“不急。”她轻声道。"], chapters[2].Paragraphs);
    }

    [Fact]
    public void Text_before_the_first_heading_is_a_preface_and_a_file_without_headings_is_cut_into_parts()
    {
        var withPreface = ChapterSplitter.Split("简介：一个故事。\n第一章 开始\n正文。\n");
        Assert.Equal(["前言", "第一章 开始"], withPreface.Select(c => c.Title));

        var plain = string.Join("\n", Enumerable.Repeat(new string('字', 1000) + "。", 20));
        var parts = ChapterSplitter.Split(plain);
        Assert.Equal(3, parts.Count);
        Assert.Equal("第1章", parts[0].Title);
    }

    [Fact]
    public void Likely_names_are_suggested_with_their_han_viet_reading()
    {
        var paragraphs = Enumerable.Repeat("林宛撑着伞，林宛点头。", 3).Append("林宛笑了。").ToList();
        var known = new HashSet<string> { "点头", "撑着" };

        var found = NameSuggester.Suggest(paragraphs, known.Contains, chinese => chinese == "林宛" ? "lâm uyển" : chinese);

        var first = Assert.Single(found);
        Assert.Equal(("林宛", 7, "Lâm Uyển"), (first.Chinese, first.Count, first.Suggested));
    }

    // ---- Finding names ---------------------------------------------------------------------------------------------

    /// <summary>A small novel: names used the way novels use them, and the traps the scan must not fall into.</summary>
    private static readonly IReadOnlyList<string> NamesNovel =
    [
        .. Enumerable.Repeat("林宛道：“走吧。”", 3),
        .. Enumerable.Repeat("林宛点头。", 3),
        .. Enumerable.Repeat("他对林宛说：“好。”", 2),
        .. Enumerable.Repeat("他转身向林宛走去。", 4),
        .. Enumerable.Repeat("一身雪白的衣裳。", 6),
        .. Enumerable.Repeat("洛景桓笑了。", 6),
        .. Enumerable.Repeat("“景桓，你来。”", 4),
        .. Enumerable.Repeat("他恼羞成怒。", 6),
        .. Enumerable.Repeat("马车到了林府。", 6),
    ];

    private static string NamesText => "第1章 开始\r\n\r\n" + string.Join("\r\n\r\n", NamesNovel) + "\r\n";

    private static readonly HashSet<string> NamesNovelWords = ["点头", "雪白", "恼羞成怒", "转身", "衣裳", "马车", "走去", "到了"];

    private static string Reading(string chinese) => string.Join(" ", chinese.Select(c => c switch
    {
        '林' => "lâm", '宛' => "uyển", '洛' => "lạc", '景' => "cảnh", '桓' => "hoàn", '府' => "phủ", _ => c.ToString(),
    }));

    [Fact]
    public void The_logic_scan_keeps_names_and_leaves_out_grammar_idioms_and_words_stuck_to_names()
    {
        var result = NameScanner.Scan(NamesNovel, NamesNovelWords.Contains, Reading);

        var sure = result.Sure.Select(f => f.Chinese).ToList();
        Assert.Contains("林宛", sure);
        Assert.Contains("洛景桓", sure);
        // The given name of a sure full name, used on its own.
        Assert.Contains("景桓", sure);
        Assert.Equal("Lâm Uyển", result.Sure.Single(f => f.Chinese == "林宛").Vietnamese);
        Assert.NotEmpty(result.Sure.Single(f => f.Chinese == "林宛").Examples);
        // "雪白的": a grammar word; "向林宛": a preposition stuck to a name; "恼羞成怒": inside an idiom.
        Assert.DoesNotContain(result.Sure.Concat(result.Maybe), f => f.Chinese is "白的" or "向林宛" or "成怒");
        // A house is only suggested.
        Assert.DoesNotContain("林府", sure);
        Assert.Contains(result.Maybe, f => f.Chinese == "林府");
    }

    [Fact]
    public async Task Found_names_never_replace_the_readers_and_a_deleted_one_is_not_added_again()
    {
        var (store, _, _) = NewStore("a");
        var id = await store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        store.SaveEntry(id, EntryKind.Name, "林宛", "Lâm Uyển Nhi");
        store.SaveEntry(null, EntryKind.Phrase, "林府", "Lâm gia");

        var added = store.SaveFoundNames(id, [("林宛", "Lâm Uyển"), ("林府", "Lâm Phủ"), ("洛景桓", "Lạc Cảnh Hoàn"), ("谢珩", "Tạ Hành")]);

        Assert.Equal(2, added);
        Assert.Equal("Lâm Uyển Nhi", store.GetEntry(id, EntryKind.Name, "林宛")!.Vietnamese);
        Assert.False(store.GetEntry(id, EntryKind.Name, "林宛")!.Auto);
        Assert.True(store.GetEntry(id, EntryKind.Name, "洛景桓")!.Auto);
        Assert.Equal(2, store.FoundNameCount(id));

        // Deleting a found name remembers it.
        store.RemoveEntry(id, "谢珩");
        Assert.Contains("谢珩", store.GetBook(id)!.IgnoredNames);
        Assert.Equal(0, store.SaveFoundNames(id, [("谢珩", "Tạ Hành")]));

        // Editing a found name makes it the reader's: undo leaves it.
        store.SaveEntry(id, EntryKind.Name, "洛景桓", "Lạc Cảnh Hoàn");
        store.SaveFoundNames(id, [("萧珩", "Tiêu Hành")]);
        Assert.Equal(1, store.RemoveFoundNames(id));
        Assert.NotNull(store.GetEntry(id, EntryKind.Name, "洛景桓"));
        Assert.Null(store.GetEntry(id, EntryKind.Name, "萧珩"));
    }

    [Fact]
    public async Task An_ai_on_this_pc_reads_the_candidates_and_adds_names_through_mcp()
    {
        var page = NewPage("a");
        page.ViewModel.AutoScanNames = false;
        var id = await page.Store.AddBookAsync("Thần y đích nữ", "n.txt", NamesText, 1, "PC");
        page.Store.SaveEntry(id, EntryKind.Name, "洛景桓", "Lạc Cảnh Hoàn Nhi");
        var tools = new NovelReaderMcpTools(page.Store, page.Dictionaries).Tools.ToDictionary(t => t.Name);
        async Task<JsonElement> Call(string tool, string args)
        {
            using var json = JsonDocument.Parse(args);
            var result = await tools[tool].Run(json.RootElement.Clone(), CancellationToken.None);
            return JsonDocument.Parse(JsonSerializer.Serialize(result)).RootElement;
        }

        Assert.True(tools["novel_name_candidates"].ReadOnly);
        Assert.False(tools["novel_add_names"].ReadOnly);
        var list = await Call("novel_list", "{}");
        Assert.Equal(id, list[0].GetProperty("id").GetString());

        var page1 = await Call("novel_name_candidates", $$"""{"novel":"{{id}}","limit":2}""");
        Assert.Equal(2, page1.GetProperty("candidates").GetArrayLength());
        var all = await Call("novel_name_candidates", """{"novel":"Thần y","offset":0,"limit":150}""");
        var lin = all.GetProperty("candidates").EnumerateArray().First(c => c.GetProperty("word").GetString() == "林宛");
        Assert.Equal("sure", lin.GetProperty("logic").GetString());
        Assert.NotEqual(0, lin.GetProperty("examples").GetArrayLength());
        // A name the reader saved is known already: not a candidate.
        Assert.DoesNotContain(all.GetProperty("candidates").EnumerateArray(), c => c.GetProperty("word").GetString() == "洛景桓");
        Assert.Equal(JsonValueKind.Null, all.GetProperty("next_offset").ValueKind);

        var added = await Call("novel_add_names", $$"""{"novel":"{{id}}","names":[{"word":"林宛","vietnamese":"Lâm Uyển"},{"word":"洛景桓","vietnamese":"Lạc Cảnh Hoàn"}]}""");
        Assert.Equal(1, added.GetProperty("added").GetInt32());
        Assert.Equal("the user saved it already", added.GetProperty("skipped")[0].GetProperty("reason").GetString());
        Assert.True(page.Store.GetEntry(id, EntryKind.Name, "林宛")!.Auto);
        Assert.Equal("Lạc Cảnh Hoàn Nhi", page.Store.GetEntry(id, EntryKind.Name, "洛景桓")!.Vietnamese);
        Assert.NotNull(page.Store.GetBook(id)!.NamesScannedAt);

        await Call("novel_ignore_names", $$"""{"novel":"{{id}}","words":["成怒"]}""");
        Assert.Contains("成怒", page.Store.GetBook(id)!.IgnoredNames);
        var names = await Call("novel_names", $$"""{"novel":"{{id}}"}""");
        Assert.Contains(names.GetProperty("names").EnumerateArray(), n => n.GetProperty("word").GetString() == "林宛" && n.GetProperty("found").GetBoolean());

        await Assert.ThrowsAsync<Helm.Core.Mcp.McpToolException>(() => Call("novel_names", """{"novel":"no such novel"}"""));
    }

    [Fact]
    public async Task Claude_codes_stream_moves_the_progress_on_and_ends_with_its_answer()
    {
        var lines = string.Join("\n",
            """{"type":"system","subtype":"init"}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__helm__novel_name_candidates","input":{}}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Reading."},{"type":"tool_use","name":"mcp__helm__novel_add_names","input":{}}]}}""",
            "a warning line",
            """{"type":"result","subtype":"success","is_error":false,"result":"Added 42 names, set aside 17 words."}""");
        var steps = new List<string>();

        var (result, failed, calls) = await ClaudeCodeNameAgent.ReadEventsAsync(new MemoryStream(Encoding.UTF8.GetBytes(lines)), new InlineProgress(steps.Add));

        Assert.Equal(("Added 42 names, set aside 17 words.", false, 2), (result, failed, calls));
        Assert.Equal("Claude Code is adding names… (2 steps)", steps[^1]);
        Assert.All(ClaudeCodeNameAgent.AllowedTools, t => Assert.StartsWith("mcp__helm__novel_", t, StringComparison.Ordinal));
        Assert.Contains("novel_add_names", ClaudeCodeNameAgent.Instructions("b1", "Novel"), StringComparison.Ordinal);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task Opening_a_novel_the_first_time_adds_its_names_and_undo_takes_them_back()
    {
        var page = NewPage("a");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", NamesText, 1, "PC");

        await page.ViewModel.OpenBookAsync(id);
        await WaitFor(() => page.Store.GetBook(id)!.NamesScannedAt is not null && !page.ViewModel.IsFindingNames);

        Assert.True(page.Store.GetEntry(id, EntryKind.Name, "林宛")!.Auto);
        Assert.True(page.ViewModel.HasFoundNames);
        Assert.Contains(page.ViewModel.Paragraphs, p => p.Text.Contains("Lâm Uyển", StringComparison.Ordinal));
        Assert.Contains(page.ViewModel.Suggestions, s => s.Chinese == "林府");

        // Not a name: never suggested again.
        page.ViewModel.DismissSuggestionCommand.Execute(page.ViewModel.Suggestions.First(s => s.Chinese == "林府"));
        Assert.Contains("林府", page.Store.GetBook(id)!.IgnoredNames);

        await page.ViewModel.UndoFoundNamesCommand.ExecuteAsync(null);
        Assert.Null(page.Store.GetEntry(id, EntryKind.Name, "林宛"));
        Assert.False(page.ViewModel.HasFoundNames);
    }

    [Fact]
    public async Task The_ai_scan_without_an_ai_on_this_device_falls_back_to_the_logic_one()
    {
        var page = NewPage("a");
        page.ViewModel.AutoScanNames = false;
        page.ViewModel.NameScanModeIndex = (int)NameScanMode.Ai;
        var id = await page.Store.AddBookAsync("Novel", "n.txt", NamesText, 1, "PC");
        await page.ViewModel.OpenBookAsync(id);
        Assert.Null(page.Store.GetBook(id)!.NamesScannedAt);

        await page.ViewModel.ScanNamesAsync(automatic: false);

        Assert.True(page.Store.GetEntry(id, EntryKind.Name, "林宛")!.Auto);
        Assert.Contains("runs in Helm on your PC", page.ViewModel.SuggestionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_ai_scan_lets_the_ai_add_the_names_through_helms_tools()
    {
        var agent = new FakeNameAgent();
        var page = NewPage("a", agent: agent);
        page.ViewModel.AutoScanNames = false;
        page.ViewModel.NameScanModeIndex = (int)NameScanMode.Ai;
        var id = await page.Store.AddBookAsync("Novel", "n.txt", NamesText, 1, "PC");
        await page.ViewModel.OpenBookAsync(id);
        // What the AI does through novel_add_names, while Helm waits for it.
        agent.Work = () => page.Store.SaveFoundNames(id, [("林宛", "Lâm Uyển"), ("洛景桓", "Lạc Cảnh Hoàn")]);

        await page.ViewModel.ScanNamesAsync(automatic: false);

        Assert.Equal((id, "Novel"), agent.Asked);
        Assert.True(page.Store.GetEntry(id, EntryKind.Name, "洛景桓")!.Auto);
        // The AI decided: the logic scan's own sure names are not added on top.
        Assert.Null(page.Store.GetEntry(id, EntryKind.Name, "景桓"));
        Assert.StartsWith("Added 2 names", page.ViewModel.SuggestionStatus, StringComparison.Ordinal);
        Assert.Contains("Claude Code: 2 names added.", page.ViewModel.SuggestionStatus, StringComparison.Ordinal);

        agent.Problem = "Claude Code is not installed on this PC.";
        page.ViewModel.RefreshNameAgentStatusCommand.Execute(null);
        Assert.Equal("Claude Code is not installed on this PC.", page.ViewModel.NameAgentStatus);
    }

    private sealed class FakeNameAgent : INameScanAgent
    {
        public string Name => "Claude Code";

        public string? Problem { get; set; }

        public Action? Work { get; set; }

        public (string BookId, string Title)? Asked { get; private set; }

        public string? Unavailable() => Problem;

        public Task<string> FindNamesAsync(string bookId, string title, IProgress<string>? progress, CancellationToken ct)
        {
            Asked = (bookId, title);
            progress?.Report("Claude Code is reading the possible names… (1 steps)");
            Work?.Invoke();
            return Task.FromResult("2 names added.");
        }
    }

    // ---- The synced library ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_novel_is_kept_encrypted_and_read_back_as_it_was()
    {
        var (store, _, blobs) = NewStore("a");

        var id = await store.AddBookAsync("  ", @"C:\novels\novel_7848572.txt", Novel, 3, "PC");

        var book = store.GetBook(id)!;
        Assert.Equal("novel_7848572", book.Title);
        Assert.Equal(3, book.ChapterCount);
        Assert.True(store.IsUploading(book));
        Assert.Equal(Novel, await store.ReadBookTextAsync(id));
        Assert.True(blobs.IsPending(book.Blob!.Id));
    }

    [Fact]
    public async Task Saved_names_are_one_record_each_and_their_ids_hide_the_chinese()
    {
        var (store, entries, _) = NewStore("a");
        var id = await store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");

        store.SaveEntry(id, EntryKind.Name, "林宛", "Lâm Uyển");
        store.SaveEntry(null, EntryKind.Name, "林宛", "Lâm Oản");
        store.SaveEntry(null, EntryKind.Phrase, "点头", "gật đầu");
        store.SaveEntry(id, EntryKind.Phrase, "点头", "khẽ gật");

        Assert.Equal(4, entries.All().Count);
        Assert.All(entries.All(), e => Assert.DoesNotContain(e.Id, c => ChineseText.IsHan(c)));
        var layers = UserLayers.Build(store, id);
        Assert.True(layers.BookNames.TryGet("林宛", out var bookName));
        Assert.Equal("Lâm Uyển", bookName);
        Assert.True(layers.Phrases.TryGet("点头", out var phrase));
        Assert.Equal("khẽ gật", phrase);

        // Saving a meaning replaces the name saved for the same Chinese.
        store.SaveEntry(id, EntryKind.Phrase, "林宛", "rừng uyển");
        Assert.Null(store.GetEntry(id, EntryKind.Name, "林宛"));
        Assert.True(store.RemoveEntry(id, "林宛"));
        Assert.Equal("林宛=Lâm Oản\n", UserLayers.Export(store.Entries(null), EntryKind.Name));
    }

    [Fact]
    public async Task Deleting_a_novel_deletes_its_names_progress_and_text()
    {
        var (store, entries, blobs) = NewStore("a");
        var id = await store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        var blob = store.GetBook(id)!.Blob!;
        store.SaveEntry(id, EntryKind.Name, "林宛", "Lâm Uyển");
        store.SaveEntry(null, EntryKind.Name, "青竹", "Thanh Trúc");
        store.SaveProgress(id, 1, 2, "PC");

        store.DeleteBook(id);

        Assert.Empty(store.Books());
        Assert.Null(store.Progress(id));
        Assert.Equal("Thanh Trúc", Assert.Single(entries.All()).Value.Vietnamese);
        Assert.False(blobs.IsPending(blob.Id));
    }

    // ---- The page --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Opening_the_page_continues_the_novel_read_last_where_it_stopped()
    {
        var page = NewPage("a");
        var older = await page.Store.AddBookAsync("Older", "a.txt", Novel, 3, "PC");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        page.Store.SaveProgress(older, 0, 0, "PC");
        _clock.Advance(TimeSpan.FromMinutes(5));
        page.Store.SaveProgress(id, 1, 1, "Phone");
        var scrolled = new List<int>();
        page.ViewModel.ScrollRequested += (_, index) => scrolled.Add(index);

        await page.ViewModel.ActivateAsync();

        Assert.Equal(id, page.ViewModel.OpenBookId);
        Assert.Equal(1, page.ViewModel.ChapterIndex);
        Assert.Equal(1, scrolled.Last());
        Assert.False(page.ViewModel.IsBusy);
        Assert.Equal("Chương 181 nhĩ... ngận nan thụ mã?", page.ViewModel.Paragraphs[0].Text);
        Assert.Equal(["Chương 1 sơn tự họa đoan", "Chương 181 nhĩ... ngận nan thụ mã?"], page.ViewModel.Chapters.Select(c => c.Title).Take(2));
    }

    [Fact]
    public async Task Reading_further_on_another_device_is_offered_not_forced()
    {
        var page = NewPage("a");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        await page.ViewModel.OpenBookAsync(id);
        Assert.Equal(0, page.ViewModel.ChapterIndex);

        page.Progress.SetRemote(id, new NovelProgress { Chapter = 2, Paragraph = 0, UpdatedAt = _clock.GetUtcNow(), Device = "Phone" });

        Assert.Equal(0, page.ViewModel.ChapterIndex);
        Assert.Equal("Continue from chapter 3, paragraph 1 (read on Phone)", page.ViewModel.RemoteProgressOffer);
        await page.ViewModel.AcceptRemoteProgressCommand.ExecuteAsync(null);
        Assert.Equal(2, page.ViewModel.ChapterIndex);
        Assert.Null(page.ViewModel.RemoteProgressOffer);
    }

    [Fact]
    public async Task Reading_aloud_goes_on_with_the_next_chapter_and_saves_the_sentence()
    {
        var page = NewPage("a");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        page.ViewModel.SpeechRate = 1.5;
        await page.ViewModel.OpenBookAsync(id);
        page.ViewModel.ParagraphPause = 0;

        page.ViewModel.PlayPauseCommand.Execute(null);
        await WaitFor(() => !page.ViewModel.IsSpeaking && page.Engine.Spoken.Count > 0);

        Assert.Equal(2, page.ViewModel.ChapterIndex);
        Assert.Equal("Chương 1 sơn tự họa đoan", page.Engine.Spoken[0].Text);
        Assert.Contains(page.Engine.Spoken, s => s.Text == "Chương 181 nhĩ...");
        Assert.DoesNotContain(page.Engine.Spoken, s => s.Text.Contains('*'));
        Assert.All(page.Engine.Spoken, s => Assert.Equal(1.5, s.Options.Rate));
        Assert.All(page.Engine.Spoken, s => Assert.Equal("test:hoaimy", s.Voice));
        var progress = page.Store.Progress(id)!;
        Assert.Equal((2, 1, 1, 2), (progress.Chapter, progress.Paragraph, progress.Sentence, progress.ParagraphCount));
    }

    [Fact]
    public async Task A_reopened_novel_marks_where_reading_stopped_and_plays_from_that_sentence()
    {
        var page = NewPage("a");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        page.Store.SaveProgress(id, 2, 1, "Phone", sentence: 1, paragraphCount: 2);

        await page.ViewModel.OpenBookAsync(id);

        Assert.True(page.ViewModel.Paragraphs[1].IsResumePoint);
        page.ViewModel.ParagraphPause = 0;
        page.ViewModel.PlayPauseCommand.Execute(null);
        await WaitFor(() => !page.ViewModel.IsSpeaking && page.Engine.Spoken.Count > 0);
        Assert.Equal("Tha khinh thanh đạo.", page.Engine.Spoken[0].Text);
        Assert.False(page.ViewModel.Paragraphs[1].IsResumePoint);
    }

    [Fact]
    public async Task A_name_saved_from_the_word_popup_converts_the_chapter_again()
    {
        var page = NewPage("a");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        await page.ViewModel.OpenBookAsync(id);
        var paragraph = page.ViewModel.Paragraphs[2];
        Assert.StartsWith("Lâm uyển", paragraph.Text);
        var word = paragraph.Line!.Segments.First(s => s.IsWord);

        page.ViewModel.OpenWord(paragraph, word);
        page.ViewModel.Editor.ExtendRightCommand.Execute(null);
        Assert.Equal("林宛", page.ViewModel.Editor.Chinese);
        Assert.Contains("Lâm Uyển", page.ViewModel.Editor.Meanings);
        page.ViewModel.Editor.PickMeaningCommand.Execute("Lâm Uyển");
        page.ViewModel.Editor.SaveNameCommand.Execute(null);

        Assert.False(page.ViewModel.Editor.IsOpen);
        Assert.Equal("Lâm Uyển", page.Store.GetEntry(id, EntryKind.Name, "林宛")!.Vietnamese);
        await WaitFor(() => paragraph.Text.StartsWith("Lâm Uyển", StringComparison.Ordinal));
    }

    // ---- Synced dictionaries ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Dictionaries_follow_the_user_to_a_new_device()
    {
        var a = NewSyncedDevice("a");
        var source = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "ChinesePhienAmWords.txt"), HanVietFile);
        File.WriteAllText(Path.Combine(source, "VietPhrase.txt"), "他=hắn\n");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "not a dictionary");

        var skipped = await a.Dictionaries.ImportAsync(Directory.GetFiles(source));
        Assert.Equal(["readme.txt"], skipped);
        await Sync(a);

        var b = NewSyncedDevice("b");
        await Sync(b);
        Assert.True(b.Dictionaries.HasFiles);
        var set = await b.Dictionaries.EnsureLoadedAsync();

        Assert.Equal(1, set.VietPhrase.Count);
        Assert.True(set.HanViet.ContainsKey("林"));
        Assert.Equal(HanVietFile, File.ReadAllText(Path.Combine(b.Dictionaries.Folder, "ChinesePhienAmWords.txt")));

        // A file replaced on one device replaces it on the other.
        File.WriteAllText(Path.Combine(source, "VietPhrase.txt"), "他=hắn\n好=tốt\n");
        await a.Dictionaries.ImportAsync([Path.Combine(source, "VietPhrase.txt")]);
        await Sync(a);
        await Sync(b);
        await b.Dictionaries.SyncFilesAsync();
        Assert.Contains("好=tốt", File.ReadAllText(Path.Combine(b.Dictionaries.Folder, "VietPhrase.txt")));
    }

    [Fact]
    public async Task Dictionaries_already_on_a_device_are_published_when_they_load()
    {
        var a = NewSyncedDevice("a");
        Directory.CreateDirectory(a.Dictionaries.Folder);
        File.WriteAllText(Path.Combine(a.Dictionaries.Folder, "ChinesePhienAmWords.txt"), HanVietFile);

        await a.Dictionaries.EnsureLoadedAsync();

        Assert.Equal("ChinesePhienAmWords.txt", Assert.Single(a.Records.All()).Value.Name);
    }

    // ---- Reading aloud ---------------------------------------------------------------------------------------------

    [Fact]
    public void Paragraphs_are_cut_into_sentences_with_their_closing_quotes()
    {
        var set = Dictionaries(vietPhrase: "他=hắn\n说=nói\n好=tốt\n");
        var line = new Converter(set).ConvertLine("他说：“好。”他好！好……他说");

        var sentences = SentenceSplitter.Split(line);

        Assert.Equal(["Hắn nói: “Tốt.”", "Hắn tốt!", "Tốt...", "hắn nói"], sentences.Select(s => s.Text));
        Assert.Equal(line.Segments.Count, sentences.Sum(s => s.SegmentCount));
        Assert.Empty(SentenceSplitter.Split(new Converter(set).ConvertLine("***")));
    }

    [Fact]
    public void The_online_voice_signs_its_request_and_escapes_the_text()
    {
        // The token changes every five minutes, from the Windows file time.
        var at = new DateTimeOffset(2026, 10, 10, 12, 3, 0, TimeSpan.Zero);
        Assert.Equal(EdgeSpeechEngine.SecMsGec(at), EdgeSpeechEngine.SecMsGec(at.AddMinutes(1)));
        Assert.NotEqual(EdgeSpeechEngine.SecMsGec(at), EdgeSpeechEngine.SecMsGec(at.AddMinutes(3)));
        Assert.Matches("^[0-9A-F]{64}$", EdgeSpeechEngine.SecMsGec(at));

        var ssml = EdgeSpeechEngine.Ssml("Lâm <Uyển> & “bạn”", "vi-VN-HoaiMyNeural", new SpeechOptions(1.5, 1, 0.8));
        Assert.Contains("<voice name='vi-VN-HoaiMyNeural'>", ssml);
        Assert.Contains("rate='+50%'", ssml);
        Assert.Contains("pitch='+0Hz'", ssml);
        Assert.Contains("volume='-20%'", ssml);
        Assert.Contains("Lâm &lt;Uyển&gt; &amp; “bạn”", ssml);
    }

    [Fact]
    public async Task Reading_aloud_synthesizes_ahead_and_skips_by_sentence_and_paragraph()
    {
        var engine = new FakeEngine();
        var output = new FakeOutput { Hold = true };
        var host = new FakeHost([["a1. a2.", "b1. b2. b3.", "c1."]]);
        var reader = NewReader(engine, output, host);

        reader.Play(0);
        await WaitFor(() => output.Played.Count == 1);

        Assert.Equal("a1.", output.Played[0]);
        // Everything to the end of the chapter is downloaded ahead.
        await WaitFor(() => engine.Spoken.Count == 6);
        Assert.Equal(["a1.", "a2.", "b1.", "b2.", "b3.", "c1."], engine.Spoken.Select(s => s.Text).Order());

        reader.NextParagraph();
        await WaitFor(() => output.Played.Count == 2);
        Assert.Equal("b1.", output.Played[1]);
        reader.NextSentence();
        await WaitFor(() => output.Played.Count == 3);
        Assert.Equal(new ReadingPosition(0, 1, 1), reader.Position);
        reader.PreviousParagraph();
        await WaitFor(() => output.Played.Count == 4);
        Assert.Equal("b1.", output.Played[3]);
        reader.PreviousSentence();
        await WaitFor(() => output.Played.Count == 5);
        Assert.Equal("a2.", output.Played[4]);
        // Going back plays the kept sound: no new synthesis.
        Assert.Equal(1, engine.Spoken.Count(s => s.Text == "a2."));
        reader.Stop();
        Assert.Equal(ReadAloudState.Stopped, reader.State);
    }

    [Fact]
    public async Task Pausing_stops_mid_sentence_and_resuming_goes_on_from_there()
    {
        var output = new FakeOutput { Hold = true };
        var reader = NewReader(new FakeEngine(), output, new FakeHost([["a1. a2."]]));
        reader.Play(0);
        await WaitFor(() => reader.State == ReadAloudState.Playing);

        reader.PlayPause();
        Assert.Equal(ReadAloudState.Paused, reader.State);
        Assert.True(output.Paused);
        reader.PlayPause();
        Assert.Equal(ReadAloudState.Playing, reader.State);
        Assert.False(output.Paused);
        Assert.Single(output.Played);

        output.Finish();
        await WaitFor(() => output.Played.Count == 2);
        Assert.Equal("a2.", output.Played[1]);
    }

    [Fact]
    public async Task Reading_aloud_opens_the_next_chapter_and_skips_empty_paragraphs()
    {
        var output = new FakeOutput();
        var host = new FakeHost([["a1.", "", "a2."], ["b1."]]);
        var reader = NewReader(new FakeEngine(), output, host);

        reader.Play(0);
        await WaitFor(() => reader.State == ReadAloudState.Stopped && output.Played.Count == 3);

        Assert.Equal(["a1.", "a2.", "b1."], output.Played);
        Assert.Equal(1, host.ChapterIndex);
    }

    [Fact]
    public async Task The_sleep_timer_fades_out_and_stops_after_its_time()
    {
        var output = new FakeOutput();
        var reader = NewReader(new FakeEngine(), output, new FakeHost([["a1. a2. a3. a4."]]));
        var notices = new List<string>();
        reader.Notice += (_, text) => notices.Add(text);
        reader.SetSleepTimer(TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(2));

        reader.Play(0);
        await WaitFor(() => reader.State == ReadAloudState.Stopped && output.Played.Count > 0);

        Assert.Equal(["a1."], output.Played);
        Assert.Equal(1, output.Volume);
        Assert.Null(reader.SleepAt);
        Assert.Equal(new ReadingPosition(0, 0, 1), reader.Position);
        Assert.Contains("Sleep timer: reading stopped.", notices);
    }

    [Fact]
    public async Task An_unreachable_online_voice_falls_back_to_an_offline_vietnamese_voice()
    {
        var online = new FakeEngine("edge") { Fail = true };
        var offline = new FakeEngine("win", online: false);
        var output = new FakeOutput();
        var reader = new ReadAloudController(new SpeechCatalog([online, offline]), output, _clock)
        {
            VoiceId = () => "edge:hoaimy",
            ParagraphPause = () => 0,
        };
        var notices = new List<string>();
        reader.Notice += (_, text) => notices.Add(text);
        reader.Attach(new FakeHost([["a1. a2."]]));

        reader.Play(0);
        await WaitFor(() => reader.State == ReadAloudState.Stopped && output.Played.Count == 2);

        Assert.Equal(["a1.", "a2."], offline.Spoken.Select(s => s.Text));
        Assert.Contains(notices, n => n.Contains("HoaiMy reads for now", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_online_voice_that_does_not_answer_is_asked_again_before_reading_stops()
    {
        var online = new FakeEngine("edge") { Fail = true };
        var output = new FakeOutput();
        var reader = new ReadAloudController(new SpeechCatalog([online]), output, _clock)
        {
            VoiceId = () => "edge:hoaimy",
            ParagraphPause = () => 0,
            OnlineRetryWaits = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(30)],
        };
        var notices = new List<string>();
        reader.Notice += (_, text) => { lock (notices) notices.Add(text); };
        reader.Attach(new FakeHost([["a1."]]));

        reader.Play(0);
        // Asked again after the first wait, still no answer: a second wait.
        await WaitFor(() => { lock (notices) return notices.Count == 2; });
        Assert.All(notices, n => Assert.Contains("HoaiMy is not answering. Trying again", n, StringComparison.Ordinal));
        Assert.True(reader.IsActive);

        // The connection is back.
        online.Fail = false;
        await WaitFor(() => output.Played.Count == 1);
        Assert.Equal("a1.", output.Played[0]);
        lock (notices) Assert.Equal("", notices[^1]);
    }

    [Fact]
    public void Why_the_online_voice_failed_is_said_in_words_not_resource_keys()
    {
        Assert.Equal("it did not answer in time.", EdgeSpeechEngine.Describe(new TimeoutException()));
        Assert.Equal("the connection was cut before any sound.",
            EdgeSpeechEngine.Describe(new System.Net.WebSockets.WebSocketException("net_WebSockets_ConnectionClosedPrematurely_Generic")));
        Assert.Equal("there is no connection to the service.", EdgeSpeechEngine.Describe(new HttpRequestException("net_http_error")));
        Assert.Equal("the service did not answer.", EdgeSpeechEngine.Describe(null));
    }

    [Fact]
    public void Downloaded_sentences_are_kept_per_voice_speed_and_novel()
    {
        var cache = new AudioCache(Path.Combine(_dir, "audio"));
        var voice = new SpeechVoice("edge:hoaimy", "HoaiMy", "vi-VN", true);
        var normal = AudioCache.Profile(voice, new SpeechOptions(1, 1, 1));
        var faster = AudioCache.Profile(voice, new SpeechOptions(1.5, 1, 1));
        var key = AudioCache.Key(normal, "Lâm Uyển gật đầu.");

        cache.Put("book", key, new SpeechAudio([1, 2, 3], "audio/mpeg"));

        Assert.True(cache.Contains("book", key));
        Assert.False(cache.Contains("other", key));
        Assert.NotEqual(key, AudioCache.Key(faster, "Lâm Uyển gật đầu."));
        Assert.Equal([1, 2, 3], cache.TryGet("book", key)!.Data);
        Assert.Equal(3, cache.SizeOf("book"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "audio", "book"), "*.part"));
        cache.MarkChapter("book", 4, normal);
        Assert.Equal([4], cache.DownloadedChapters("book", normal));
        Assert.Empty(cache.DownloadedChapters("book", faster));

        cache.Clear("book");
        Assert.False(cache.Contains("book", key));
        Assert.Equal(0, cache.SizeOf("book"));
    }

    [Fact]
    public async Task Sound_downloaded_once_plays_again_without_the_voice()
    {
        var cache = new AudioCache(Path.Combine(_dir, "audio"));
        var engine = new FakeEngine();
        var first = NewReader(engine, new FakeOutput(), new FakeHost([["a1. a2.", "b1."]]), cache);
        first.Play(0);
        await WaitFor(() => first.State == ReadAloudState.Stopped && engine.Spoken.Count == 3);

        // Another session (Helm restarted): everything plays from the disk.
        var output = new FakeOutput();
        var again = NewReader(engine, output, new FakeHost([["a1. a2.", "b1."]]), cache);
        again.Play(0);
        await WaitFor(() => again.State == ReadAloudState.Stopped && output.Played.Count == 3);

        Assert.Equal(3, engine.Spoken.Count);
        Assert.Equal(["a1.", "a2.", "b1."], output.Played);
    }

    [Fact]
    public async Task Downloading_ahead_goes_to_the_end_of_the_chapter_and_through_the_next_one()
    {
        var engine = new FakeEngine();
        var output = new FakeOutput { Hold = true };
        var host = new FakeHost([["a1. a2.", "b1."], ["c1. c2. c3.", "d1. d2. d3."]]);
        var cache = new AudioCache(Path.Combine(_dir, "audio"));
        var reader = NewReader(engine, output, host, cache);

        reader.Play(0);

        // Reading goes on by itself (the screen may be off): the open chapter, then all of the next one.
        await WaitFor(() => engine.Spoken.Count == 9);
        Assert.Equal(["a1.", "a2.", "b1.", "c1.", "c2.", "c3.", "d1.", "d2.", "d3."], engine.Spoken.Select(s => s.Text).Order());
        await WaitFor(() => cache.DownloadedChapters("book", reader.Profile!).Contains(1));
        reader.RefreshReady();
        Assert.Equal(1, reader.ReadyThrough);
        reader.Stop();
    }

    [Fact]
    public async Task Without_going_on_to_the_next_chapter_only_its_start_is_downloaded_ahead()
    {
        var engine = new FakeEngine();
        var host = new FakeHost([["a1. a2.", "b1."], ["c1. c2. c3.", "d1. d2. d3."]]);
        var reader = NewReader(engine, new FakeOutput { Hold = true }, host);
        reader.ContinueToNextChapter = () => false;

        reader.Play(0);

        await WaitFor(() => engine.Spoken.Count == 3 + ReadAloudController.NextChapterLead);
        await Task.Delay(100);
        Assert.Equal(["a1.", "a2.", "b1.", "c1.", "c2.", "c3.", "d1.", "d2."], engine.Spoken.Select(s => s.Text).Order());
        reader.Stop();
    }

    [Fact]
    public async Task Downloading_ahead_stays_the_set_number_of_chapters_ahead_and_follows_reading()
    {
        var engine = new FakeEngine();
        var output = new FakeOutput { Hold = true };
        var host = new FakeHost([["a1. a2."], ["b1. b2."], ["c1. c2."], ["d1. d2."], ["e1. e2."]]);
        var cache = new AudioCache(Path.Combine(_dir, "audio"));
        var reader = NewReader(engine, output, host, cache);
        reader.PrefetchChapters = () => 2;

        reader.Play(0);

        // The chapter being read and the two after it, with no stop between them; the rest waits.
        await WaitFor(() => engine.Spoken.Count == 6);
        await Task.Delay(100);
        Assert.Equal(["a1.", "a2.", "b1.", "b2.", "c1.", "c2."], engine.Spoken.Select(s => s.Text).Order());
        await WaitFor(() => cache.DownloadedChapters("book", reader.Profile!).SetEquals([0, 1, 2]));

        // Reading moves into the next chapter: one more chapter is downloaded, and nothing twice.
        await WaitFor(() => output.Played.Count == 1);
        output.Finish();
        await WaitFor(() => output.Played.Count == 2);
        output.Finish();
        await WaitFor(() => engine.Spoken.Count == 8);
        await Task.Delay(100);
        Assert.Equal(8, engine.Spoken.Count);
        Assert.Equal(["d1.", "d2."], engine.Spoken.Skip(6).Select(s => s.Text).Order());
        reader.Stop();
    }

    [Fact]
    public void The_silence_a_voice_leaves_around_a_sentence_is_cut()
    {
        // Half a second of silence, 0.2 s of sound, half a second of silence (24 kHz, 16-bit mono).
        const int rate = 24000;
        var samples = new short[(int)(rate * 1.2)];
        for (var i = rate / 2; i < rate * 7 / 10; i++) samples[i] = (short)(8000 * Math.Sin(i * 2 * Math.PI * 220 / rate));
        var audio = new SpeechAudio(TimeStretch.Wav(samples, rate), "audio/wav");

        var cut = SpeechSilence.Trim(audio);

        var expected = 0.2 + SpeechSilence.KeepBefore.TotalSeconds + SpeechSilence.KeepAfter.TotalSeconds;
        Assert.Equal(expected, ReadAloudController.EstimateLength(cut).TotalSeconds, tolerance: 0.005);
        Assert.Same(cut, SpeechSilence.Trim(cut));
        // The sound itself is all there.
        var kept = Enumerable.Range(0, (cut.Data.Length - 44) / 2).Select(i => BitConverter.ToInt16(cut.Data, 44 + 2 * i)).ToArray();
        Assert.Equal(samples.Sum(s => Math.Abs((int)s)), kept.Sum(s => Math.Abs((int)s)));
        // Only WAV is cut; silence alone stays as it is.
        var mp3 = new SpeechAudio([1, 2, 3], "audio/mpeg");
        Assert.Same(mp3, SpeechSilence.Trim(mp3));
        var quiet = new SpeechAudio(TimeStretch.Wav(new short[rate], rate), "audio/wav");
        Assert.Same(quiet, SpeechSilence.Trim(quiet));
    }

    [Fact]
    public void A_phones_own_female_vietnamese_voice_comes_before_the_online_one()
    {
        var online = new FakeEngine("edge");
        var phone = new FakeEngine("android", online: false, voices:
        [
            new SpeechVoice("android:vi-vn-x-vid-local", "Vietnamese 2", "vi-VN", false),
            new SpeechVoice("android:vi-vn-x-gft-local", "Vietnamese 1", "vi-VN", false) { IsFemale = true },
            new SpeechVoice("android:en-us-x-iol-local", "English", "en-US", false) { IsFemale = true },
        ]);

        var voices = new SpeechCatalog([online, phone]).Voices;

        Assert.Equal(["android:vi-vn-x-gft-local", "android:vi-vn-x-vid-local", "edge:hoaimy", "android:en-us-x-iol-local"], voices.Select(v => v.Id));
        Assert.Equal("Vietnamese 1 (on this phone)", voices[0].Label);
    }

    [Fact]
    public void The_saved_voice_is_picked_again_once_the_phone_has_loaded_its_voices()
    {
        // The phone's text-to-speech starts after Helm: its voices are missing at first.
        var phone = new FakeEngine("android", online: false, voices: []);
        var page = NewPage("a", "android:vi-vn-x-gft-local", null, phone);
        Assert.Equal("test:hoaimy", page.ViewModel.SelectedVoice?.Id);
        // Filling the list again meanwhile does not make the stand-in the saved choice.
        page.ViewModel.RefreshVoicesCommand.Execute(null);

        phone.Voices = [new SpeechVoice("android:vi-vn-x-gft-local", "Vietnamese 1", "vi-VN", false) { IsFemale = true }];
        page.ViewModel.RefreshVoicesCommand.Execute(null);

        Assert.Equal("android:vi-vn-x-gft-local", page.ViewModel.SelectedVoice?.Id);
    }

    [Fact]
    public async Task Play_and_pause_from_a_headset_never_do_the_other_and_stop_stops_reading()
    {
        var page = NewPage("a");
        var id = await page.Store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");
        await page.ViewModel.OpenBookAsync(id);
        page.Output.Hold = true;
        page.Output.Press(MediaButton.Play);
        await WaitFor(() => page.ViewModel.IsSpeaking && page.Output.Played.Count == 1);

        page.Output.Press(MediaButton.Pause);
        page.Output.Press(MediaButton.Pause);
        Assert.Equal(ReadAloudState.Paused, page.ViewModel.Reader.State);
        page.Output.Press(MediaButton.Play);
        page.Output.Press(MediaButton.Play);
        Assert.Equal(ReadAloudState.Playing, page.ViewModel.Reader.State);

        page.Output.Press(MediaButton.Stop);

        await WaitFor(() => !page.ViewModel.IsSpeaking);
    }

    [Fact]
    public void How_long_a_wav_lasts_comes_from_its_header()
    {
        // One second at 24 kHz (16-bit mono: 48,000 bytes a second), as Android and VieNeu make them.
        var wav = TimeStretch.Wav(new short[24000], 24000);

        Assert.Equal(1.0, ReadAloudController.EstimateLength(new SpeechAudio(wav, "audio/wav")).TotalSeconds, 3);
    }

    [Fact]
    public async Task Chapters_download_for_listening_offline_and_only_what_is_missing()
    {
        var cache = new AudioCache(Path.Combine(_dir, "audio"));
        var engine = new FakeEngine();
        var host = new FakeHost([["a1. a2."], ["b1. b2."], ["c1."]]);
        var reader = NewReader(engine, new FakeOutput(), host, cache);
        var seen = new List<AudioDownloadProgress>();
        reader.DownloadChanged += (_, _) => { lock (seen) seen.Add(reader.Download); };

        reader.DownloadChapters(0, 1);
        await WaitFor(() => !reader.Download.Active && engine.Spoken.Count == 4);

        lock (seen) Assert.Contains(seen, p => p is { Active: true, Chapter: 1, Done: 2, Total: 2 });
        Assert.Equal([0, 1], cache.DownloadedChapters("book", reader.Profile!).Order());

        reader.DownloadChapters(0, 2);
        await WaitFor(() => !reader.Download.Active && engine.Spoken.Count == 5);
        Assert.Equal("c1.", engine.Spoken[^1].Text);
    }

    private ReadAloudController NewReader(FakeEngine engine, FakeOutput output, FakeHost host, AudioCache? cache = null)
    {
        var reader = new ReadAloudController(new SpeechCatalog([engine]), output, _clock, disk: cache)
        {
            VoiceId = () => "test:hoaimy",
            ParagraphPause = () => 0,
        };
        reader.Attach(host);
        return reader;
    }

    /// <summary>Chapters of paragraphs; a paragraph is cut into sentences at ". ".</summary>
    private sealed class FakeHost(string[][] chapters) : IReadAloudHost
    {
        public string? BookId => "book";

        public int ChapterIndex { get; private set; }

        public Task<IReadOnlyList<IReadOnlyList<Sentence>>> ChapterSentencesAsync(int chapter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IReadOnlyList<Sentence>>>(chapters[chapter].Select(p => Split(p)).ToList());

        public int ChapterCount => chapters.Length;

        public int ParagraphCount => chapters[ChapterIndex].Length;

        public IReadOnlyList<Sentence> SentencesOf(int paragraph) => Split(chapters[ChapterIndex][paragraph]);

        private static IReadOnlyList<Sentence> Split(string text) =>
            text.Length == 0 ? [] : text.Split(". ").Select((s, i) => new Sentence(i, i, 1, s.EndsWith('.') ? s : s + ".")).ToList();

        public Task<bool> OpenChapterForReadingAsync(int chapter)
        {
            ChapterIndex = chapter;
            return Task.FromResult(true);
        }

        public (string Title, string Subtitle) NowPlaying => ("Novel", $"Chapter {ChapterIndex + 1}");
    }

    // ---- Voices on this PC and covers --------------------------------------------------------------------------

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(0.75)]
    public void Speeding_up_shortens_the_sound_and_keeps_its_pitch(double rate)
    {
        const int sampleRate = 24000;
        const double frequency = 220;
        var input = Enumerable.Range(0, sampleRate * 2).Select(i => (short)(8000 * Math.Sin(2 * Math.PI * frequency * i / sampleRate))).ToArray();

        var output = TimeStretch.Stretch(input, sampleRate, rate);

        Assert.InRange(output.Length, (int)(input.Length / rate) - 10, (int)(input.Length / rate) + 10);
        // Zero crossings per second tell the pitch: 2 per period.
        var middle = output.Skip(output.Length / 4).Take(output.Length / 2).ToArray();
        var crossings = 0;
        for (var i = 1; i < middle.Length; i++) if ((middle[i - 1] < 0) != (middle[i] < 0)) crossings++;
        var measured = crossings / 2.0 / (middle.Length / (double)sampleRate);
        Assert.InRange(measured, frequency * 0.95, frequency * 1.05);
        Assert.Same(input, TimeStretch.Stretch(input, sampleRate, 1));
    }

    [Fact]
    public void Local_voice_sound_becomes_a_complete_wav_file()
    {
        var wav = TimeStretch.Wav([1, -1, 300], 24000);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + 6, BitConverter.ToInt32(wav, 4));
        Assert.Equal(24000, BitConverter.ToInt32(wav, 24));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(6, BitConverter.ToInt32(wav, 40));
        Assert.Equal(300, BitConverter.ToInt16(wav, 48));
        Assert.Equal([50, -50], TimeStretch.Scale([100, -100], 0.5));
    }

    [Fact]
    public async Task The_local_voice_starts_its_server_and_asks_for_raw_sound()
    {
        var server = new FakeLocalServer();
        var http = new FakeHttp(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/voices" => Json("{\"object\":\"list\",\"data\":[{\"id\":\"Hải Đăng\",\"name\":\"Hải Đăng\",\"gender\":\"male\"},{\"id\":\"Ngọc Huyền\",\"name\":\"Ngọc Huyền\",\"gender\":\"female\",\"description\":\"Bắc\"}]}"),
            "/v1/audio/speech" => Pcm(4800),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });
        var engine = new LocalSpeechEngine(server, Path.Combine(_dir, "local-voices.json"), NullLogger<LocalSpeechEngine>.Instance, http);

        await engine.RefreshVoicesAsync(CancellationToken.None);
        var voice = engine.Voices[0];
        var audio = await engine.SynthesizeAsync("Lâm Uyển gật đầu.", voice, new SpeechOptions(2, 1, 1), CancellationToken.None);

        Assert.Equal(("local:Ngọc Huyền", true), (voice.Id, voice.IsFemale));
        Assert.False(voice.SupportsPitch);
        Assert.Equal(1, server.Started);
        Assert.Equal("audio/wav", audio.ContentType);
        // 4800 samples at twice the speed: about 2400 samples after the 44-byte header.
        Assert.InRange((audio.Data.Length - 44) / 2, 2390, 2410);
        var sent = JsonDocument.Parse(http.Bodies.Last()).RootElement;
        Assert.Equal(("Ngọc Huyền", "pcm", 24000), (sent.GetProperty("voice").GetString(), sent.GetProperty("response_format").GetString(), sent.GetProperty("sample_rate").GetInt32()));
        // The list is kept for the next start of Helm, before the server runs.
        var again = new LocalSpeechEngine(server, Path.Combine(_dir, "local-voices.json"), NullLogger<LocalSpeechEngine>.Instance, http);
        Assert.Equal("Ngọc Huyền", again.Voices[0].Name);
    }

    [Fact]
    public async Task A_local_voice_that_cannot_start_is_unavailable()
    {
        var server = new FakeLocalServer { CanStart = false };
        var engine = new LocalSpeechEngine(server, Path.Combine(_dir, "v.json"), NullLogger<LocalSpeechEngine>.Instance, new FakeHttp(_ => Pcm(10)));

        await Assert.ThrowsAsync<SpeechUnavailableException>(() =>
            engine.SynthesizeAsync("a", engine.Voices[0], new SpeechOptions(1, 1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task A_cover_is_synced_with_its_novel_and_replaced_or_removed()
    {
        var (store, _, blobs) = NewStore("a");
        var id = await store.AddBookAsync("Novel", "n.txt", Novel, 3, "PC");

        await store.SetCoverAsync(id, [1, 2, 3]);
        var first = store.GetBook(id)!.Cover!;
        Assert.Equal([1, 2, 3], await store.ReadCoverAsync(id));
        Assert.Contains(first.Id, NovelStore.BookOptions.BlobReferences!(store.GetBook(id)!));
        Assert.Contains(store.GetBook(id)!.Blob!.Id, NovelStore.BookOptions.BlobReferences!(store.GetBook(id)!));

        await store.SetCoverAsync(id, [4, 5]);
        Assert.False(blobs.IsPending(first.Id));
        Assert.Equal([4, 5], await store.ReadCoverAsync(id));

        store.RemoveCover(id);
        Assert.Null(store.GetBook(id)!.Cover);
        Assert.Null(await store.ReadCoverAsync(id));
    }

    [Fact]
    public void A_novel_without_a_picture_gets_the_same_drawn_cover_every_time()
    {
        Assert.Equal(CoverArt.Colors("01ABC"), CoverArt.Colors("01ABC"));
        Assert.Equal("LU", CoverArt.Initials("lâm uyển truyện"));
        Assert.Equal("N", CoverArt.Initials("novel_7848572"));
        Assert.Equal("?", CoverArt.Initials("  "));
    }

    private static HttpResponseMessage Json(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Pcm(int samples)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++) BitConverter.TryWriteBytes(bytes.AsSpan(2 * i), (short)(4000 * Math.Sin(i / 10.0)));
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Headers.Add("X-Sample-Rate", "24000");
        return response;
    }

    private sealed class FakeLocalServer : ILocalVoiceServer
    {
        public bool CanStart { get; init; } = true;
        public int Started { get; private set; }
        public Uri BaseAddress { get; } = new("http://127.0.0.1:8765/");
        public bool IsInstalled => true;
        public bool IsRunning => Started > 0;
        public string Status => IsRunning ? "Running" : "Stopped";
        public event EventHandler? StatusChanged { add { } remove { } }

        public Task<bool> EnsureRunningAsync(CancellationToken ct)
        {
            if (CanStart && Started == 0) Started++;
            return Task.FromResult(CanStart);
        }

        public void Touch() { }

        public void Stop() { }
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(ct));
            return answer(request);
        }
    }

    [Fact]
    public async Task A_voice_that_plays_by_itself_reads_in_chunks_and_follows_the_spoken_words()
    {
        var engine = new FakeDirectEngine();
        var output = new FakeOutput();
        var cache = new AudioCache(Path.Combine(_dir, "audio"));
        var reader = new ReadAloudController(new SpeechCatalog([engine]), output, _clock, disk: cache)
        {
            VoiceId = () => "edgeapp:hoaimy",
            ParagraphPause = () => 0,
        };
        var notices = new List<string>();
        reader.Notice += (_, text) => notices.Add(text);
        reader.Attach(new FakeHost([["a1. a2.", "b1."]]));

        var positions = new List<ReadingPosition>();
        reader.PositionChanged += (_, p) => { lock (positions) positions.Add(p); };

        reader.Play(0);
        await WaitFor(() => engine.Queued.Count == 1);
        // The whole chapter fits one chunk: one text, paragraphs on their own lines.
        Assert.Equal("a1. a2.\nb1.", engine.Queued[0]);
        engine.Report(4);   // "a2."
        engine.Report(8);   // "b1."
        lock (positions) Assert.Equal([new ReadingPosition(0, 0, 0), new ReadingPosition(0, 0, 1), new ReadingPosition(0, 1, 0)], positions.Take(3));
        Assert.Equal("b1.", reader.CurrentText);
        reader.Pause();
        Assert.Equal(1, engine.Paused);
        reader.Resume();
        Assert.Equal(1, engine.Resumed);
        engine.FinishAll();
        await WaitFor(() => reader.State == ReadAloudState.Stopped);

        Assert.Single(engine.Queued);
        Assert.Empty(output.Played);
        Assert.Equal(0, cache.SizeOf(null));
        reader.DownloadChapters(0, 0);
        Assert.Contains(notices, n => n.Contains("cannot be downloaded", StringComparison.Ordinal));
    }

    /// <summary>Plays "by itself": each sentence ends when the test says so (or at once once <see cref="FinishAll"/> ran).</summary>
    private sealed class FakeDirectEngine : ISpeechEngine, IDirectSpeechEngine
    {
        private readonly List<TaskCompletionSource> _playing = [];
        private bool _finishAll;
        private Action<int>? _progress;

        public List<string> Queued { get; } = [];
        public int Paused { get; private set; }
        public int Resumed { get; private set; }
        public string Prefix => "edgeapp";
        public IReadOnlyList<SpeechVoice> Voices { get; } = [new SpeechVoice("edgeapp:hoaimy", "HoaiMy", "vi-VN", true)];
        public void RefreshVoices() { }

        public Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct) =>
            throw new SpeechUnavailableException("plays by itself");

        public Task SpeakAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct, Action<int>? progress = null)
        {
            _progress = progress;
            lock (Queued) Queued.Add(text);
            if (_finishAll) return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled());
            lock (_playing) _playing.Add(done);
            return done.Task;
        }

        public void Report(int at) => _progress?.Invoke(at);

        public void FinishAll()
        {
            _finishAll = true;
            lock (_playing) foreach (var p in _playing) p.TrySetResult();
        }

        public void Pause() => Paused++;
        public void Resume() => Resumed++;
        public void Cancel() { }
        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static DictionarySet Dictionaries(string vietPhrase = "", string names = "", string luatNhan = "") =>
        new(PhraseDictionary.Parse(HanVietFile), PhraseDictionary.Parse(vietPhrase), PhraseDictionary.Parse(names), new PhraseDictionary(),
            LuatNhanRules.Parse(luatNhan));

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private (NovelStore Store, MemorySynced<NovelEntry> Entries, BlobStore Blobs) NewStore(string name)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory, _clock);
        var entries = new MemorySynced<NovelEntry>();
        return (new NovelStore(new MemorySynced<NovelBook>(), entries, new MemorySynced<NovelProgress>(), blobs, _clock), entries, blobs);
    }

    private sealed record Page(NovelReaderViewModel ViewModel, NovelStore Store, MemorySynced<NovelProgress> Progress, FakeEngine Engine, FakeOutput Output,
        DictionaryLibrary Dictionaries);

    private Page NewPage(string name, string voiceId = "test:hoaimy", INameScanAgent? agent = null, params ISpeechEngine[] moreEngines)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var blobs = new BlobStore(db, new NullBlobTransport(), paths.SyncBlobsDirectory, _clock);
        var progress = new MemorySynced<NovelProgress>();
        var store = new NovelStore(new MemorySynced<NovelBook>(), new MemorySynced<NovelEntry>(), progress, blobs, _clock);
        var settings = Own(new SettingsStoreFactory(paths));
        var dictionaries = new DictionaryLibrary(settings, new MemorySynced<NovelDictionary>(), blobs, new Device(), NullLogger<DictionaryLibrary>.Instance);
        Directory.CreateDirectory(dictionaries.Folder);
        File.WriteAllText(Path.Combine(dictionaries.Folder, "ChinesePhienAmWords.txt"), HanVietFile);
        settings.Get<NovelReaderSettings>(NovelReaderIds.ModuleId).Update(s => s.VoiceId = voiceId);
        var engine = new FakeEngine();
        var output = new FakeOutput();
        var viewModel = new NovelReaderViewModel(store, dictionaries, settings, new InlineUi(), new AcceptDialogs(), new Device(),
            new SpeechCatalog([engine, .. moreEngines]), output, new AudioCache(Path.Combine(paths.Root, "audio")), new Launcher(), new MemoryClipboard(),
            NullLogger<NovelReaderViewModel>.Instance, nameAgent: agent);
        return new Page(viewModel, store, progress, engine, output, dictionaries);
    }

    private sealed record SyncedDevice(DictionaryLibrary Dictionaries, SyncedCollection<NovelDictionary> Records, SyncEngine Engine);

    private SyncedDevice NewSyncedDevice(string name)
    {
        var paths = new HelmPaths(Path.Combine(_dir, name));
        var db = Own(new SyncDatabase(paths.SyncDatabaseFile, TestKeys.Local));
        var transport = _server.Connect();
        var blobs = new BlobStore(db, transport, paths.SyncBlobsDirectory, _clock);
        var engine = Own(new SyncEngine(db, transport, new InMemoryMasterKeyStore(_syncKey), [], time: _clock,
            debounce: TimeSpan.FromHours(1), blobs: blobs));
        var records = Own(new SyncedCollection<NovelDictionary>(engine, DictionaryLibrary.Options));
        var settings = Own(new SettingsStoreFactory(paths));
        return new SyncedDevice(new DictionaryLibrary(settings, records, blobs, new Device(), NullLogger<DictionaryLibrary>.Instance), records, engine);
    }

    private static async Task Sync(SyncedDevice device) =>
        Assert.Equal(SyncRunOutcome.Completed, (await device.Engine.SyncNowAsync()).Outcome);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    /// <summary>An engine that "speaks" by recording the text; it can fail like an unreachable online voice.</summary>
    private sealed class FakeEngine(string prefix = "test", bool online = true, IReadOnlyList<SpeechVoice>? voices = null) : ISpeechEngine
    {
        public List<(string Text, SpeechOptions Options, string Voice)> Spoken { get; } = [];

        public bool Fail { get; set; }

        public string Prefix => prefix;

        public IReadOnlyList<SpeechVoice> Voices { get; set; } = voices ?? [new SpeechVoice($"{prefix}:hoaimy", "HoaiMy", "vi-VN", online)];

        public void RefreshVoices() { }

        public Task<SpeechAudio> SynthesizeAsync(string text, SpeechVoice voice, SpeechOptions options, CancellationToken ct)
        {
            if (Fail) return Task.FromException<SpeechAudio>(new SpeechUnavailableException("offline"));
            lock (Spoken) Spoken.Add((text, options, voice.Id));
            return Task.FromResult(new SpeechAudio(Encoding.UTF8.GetBytes(text), "audio/mpeg"));
        }
    }

    /// <summary>Plays at once, or holds each sound until <see cref="Finish"/> when <see cref="Hold"/> is set.</summary>
    private sealed class FakeOutput : IAudioOutput
    {
        private TaskCompletionSource? _playing;

        public List<string> Played { get; } = [];

        public bool Hold { get; set; }

        public bool Paused { get; private set; }

        public double Volume { get; set; } = 1;

        public event EventHandler<MediaButton>? MediaButton;

        public Task PlayAsync(SpeechAudio audio, CancellationToken ct)
        {
            lock (Played) Played.Add(Encoding.UTF8.GetString(audio.Data));
            if (!Hold) return Task.CompletedTask;
            _playing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => _playing.TrySetCanceled());
            return _playing.Task;
        }

        public void Finish() => _playing?.TrySetResult();

        public void Pause() => Paused = true;

        public void Resume() => Paused = false;

        public void ShowNowPlaying(string title, string subtitle) { }

        public void ClearNowPlaying() { }

        public void Press(MediaButton button) => MediaButton?.Invoke(this, button);
    }

    private sealed class Device : IDeviceInfo
    {
        public string DeviceName => "PC";
    }

    private sealed class Launcher : IProcessLauncher
    {
        public string ExecutablePath => "";
        public bool IsElevated => false;
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public void StartNewInstance(string? arguments = null) { }
    }
}
