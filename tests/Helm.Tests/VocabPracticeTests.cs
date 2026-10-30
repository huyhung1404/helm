using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Core.Settings;
using Helm.Modules.Vocab;

namespace Helm.Tests;

/// <summary>Typing the word, "I know this", hidden words, words often forgotten, and details filled in later.</summary>
public sealed class VocabPracticeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;

    public VocabPracticeTests()
    {
        _store = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>(), _time);
        _quiz = new VocabQuiz(_store, new Random(1));
    }

    private string Analyzed(string text, string phonetic = "/x/", string meaning = "nghĩa", string? hanViet = null)
    {
        var (id, _) = _store.AddWord(text);
        _store.SaveAnalysis(id, new WordAnalysis { Phonetic = phonetic, Meaning = meaning, HanViet = hanViet });
        _time.Advance(TimeSpan.FromSeconds(1));
        return id;
    }

    private static VocabWord English(string text) => new() { Language = VocabLanguage.English, Text = text, Status = VocabStatus.Analyzed };

    private static VocabWord Chinese(string text, string pinyin, string traditional = "") =>
        new() { Language = VocabLanguage.Chinese, Text = text, Phonetic = pinyin, Traditional = traditional, Status = VocabStatus.Analyzed };

    // ---- typing the word -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("relative", "relative", VocabAnswerVerdict.Correct)]
    [InlineData("relative", "  Relative ", VocabAnswerVerdict.Correct)]
    [InlineData("well-known", "well known", VocabAnswerVerdict.Correct)]
    [InlineData("relative", "relativ", VocabAnswerVerdict.Close)]
    [InlineData("ubiquitous", "ubiquitus", VocabAnswerVerdict.Close)]
    [InlineData("ubiquitous", "ubikitus", VocabAnswerVerdict.Wrong)]
    [InlineData("cat", "cut", VocabAnswerVerdict.Wrong)]
    [InlineData("relative", "", VocabAnswerVerdict.Wrong)]
    public void A_typed_english_word_is_checked_with_room_for_a_slip(string word, string typed, VocabAnswerVerdict verdict) =>
        Assert.Equal(verdict, VocabAnswer.Check(English(word), typed).Verdict);

    [Fact]
    public void A_wrong_spelling_shows_which_letters_are_off()
    {
        var pieces = VocabAnswer.Check(English("relative"), "relitave").Pieces;
        Assert.Equal("relitave", string.Concat(pieces.Where(p => p.Mark != VocabAnswerMark.Missing).Select(p => p.Text)));
        Assert.Contains(pieces, p => p.Mark == VocabAnswerMark.Wrong);
        Assert.Equal("rel", pieces[0].Text);

        // A letter left out shows where it belongs.
        var missing = VocabAnswer.Check(English("relative"), "relatve").Pieces;
        Assert.Equal([("relat", VocabAnswerMark.Ok), ("i", VocabAnswerMark.Missing), ("ve", VocabAnswerMark.Ok)],
            missing.Select(p => (p.Text, p.Mark)));
    }

    [Theory]
    [InlineData("补救", VocabAnswerVerdict.Correct)]
    [InlineData("補救", VocabAnswerVerdict.Correct)]
    [InlineData("补求", VocabAnswerVerdict.Wrong)]
    [InlineData("bǔjiù", VocabAnswerVerdict.Correct)]
    [InlineData("bu3 jiu4", VocabAnswerVerdict.Correct)]
    [InlineData("bujiu", VocabAnswerVerdict.Correct)]
    [InlineData("bu2jiu4", VocabAnswerVerdict.Close)]
    [InlineData("bùjiǔ", VocabAnswerVerdict.Close)]
    [InlineData("pujiu", VocabAnswerVerdict.Wrong)]
    public void A_typed_chinese_word_takes_characters_or_pinyin(string typed, VocabAnswerVerdict verdict) =>
        Assert.Equal(verdict, VocabAnswer.Check(Chinese("补救", "bǔjiù", "補救"), typed).Verdict);

    [Fact]
    public void Pinyin_without_tones_says_the_tones_were_not_checked()
    {
        var result = VocabAnswer.Check(Chinese("学习", "xuéxí"), "xuexi");
        Assert.Equal(VocabAnswerVerdict.Correct, result.Verdict);
        Assert.Contains("xuéxí", result.Message);
        var (letters, tones) = VocabAnswer.Syllables("lǜ");
        Assert.Equal("lv", letters);
        Assert.Equal([4], tones);
    }

    [Fact]
    public void A_type_card_checks_the_word_then_shows_the_answer()
    {
        Analyzed("relative", meaning: "họ hàng");
        var card = new VocabCardViewModel(_store, _quiz, options: () => new VocabCardOptions(VocabCardMode.Type, false));
        Assert.True(card.ShowNext());
        Assert.True(card.IsTyping);
        Assert.Equal("họ hàng", card.FrontText);
        Assert.False(card.CheckCommand.CanExecute(null));

        card.Typed = "relativ";
        card.CheckCommand.Execute(null);
        Assert.True(card.IsRevealed);
        Assert.False(card.IsTyping);
        Assert.True(card.AnswerIsClose);
        Assert.True(card.ShowsWordOnReveal);

        // The verdict stays the user's: the answer is recorded only by Forgot / Remembered.
        Assert.Empty(_store.Reviews());
        card.RememberedCommand.Execute(null);
        Assert.Single(_store.Reviews());
        Assert.False(card.HasAnswer);
        Assert.Equal("", card.Typed);
    }

    // ---- I know this, hidden words -------------------------------------------------------------------------------

    [Fact]
    public void I_know_this_sends_a_word_a_month_away()
    {
        var id = Analyzed("relative");
        _store.RecordKnown(id);
        var stats = _quiz.StatsOf(id);
        Assert.Equal(VocabQuiz.KnownLevel, stats.Level);
        Assert.Equal(T0.AddSeconds(1) + TimeSpan.FromDays(32), stats.Due);
        Assert.True(stats.IsLearned);
        // A word already higher keeps its level.
        _time.Advance(TimeSpan.FromDays(40));
        _store.Record(id, remembered: true);
        _store.RecordKnown(id);
        Assert.Equal(VocabQuiz.KnownLevel + 1, _quiz.StatsOf(id).Level);
    }

    [Fact]
    public void A_card_skips_known_and_hidden_words()
    {
        var relative = Analyzed("relative");
        var translate = Analyzed("translate");
        var card = new VocabCardViewModel(_store, _quiz);
        string? hidden = null;
        card.WordHidden += (_, text) => hidden = text;

        card.ShowNext();
        Assert.Equal(relative, card.WordId);
        card.KnowCommand.Execute(null);
        Assert.True(_store.Reviews().Single().Known);
        Assert.Equal(translate, card.WordId);

        card.HideCommand.Execute(null);
        Assert.Equal("translate", hidden);
        Assert.True(_store.GetWord(translate)!.Hidden);
        Assert.Equal(1, _quiz.Today().Total);
        for (var i = 0; i < 20; i++) Assert.NotEqual(translate, _quiz.Next()?.Id);

        _store.SetHidden(translate, false);
        Assert.Equal(2, _quiz.Today().Total);
    }

    // ---- often forgotten -----------------------------------------------------------------------------------------

    [Fact]
    public void A_word_forgotten_four_times_is_hard_until_an_agent_helps()
    {
        var id = Analyzed("ubiquitous");
        for (var i = 0; i < VocabQuiz.HardForgets - 1; i++) _store.Record(id, remembered: false);
        Assert.Empty(_quiz.HardWords());
        _store.Record(id, remembered: false);
        Assert.Equal(id, Assert.Single(_quiz.HardWords()).Id);

        _time.Advance(TimeSpan.FromMinutes(1));
        _store.SaveDetails(id, new WordAnalysis { Mnemonic = "ubi = ở đâu: ở đâu cũng thấy" }, helped: true);
        Assert.Empty(_quiz.HardWords());
        Assert.Equal("ubi = ở đâu: ở đâu cũng thấy", _store.GetWord(id)!.Mnemonic);

        // Forgetting it again after the help counts afresh.
        _time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < VocabQuiz.HardForgets; i++) _store.Record(id, remembered: false);
        Assert.Single(_quiz.HardWords());
        // Hidden words are left alone.
        _store.SetHidden(id, true);
        Assert.Empty(_quiz.HardWords());
    }

    // ---- details filled in later ---------------------------------------------------------------------------------

    private McpServer Server() =>
        new(new VocabMcpTools(_store, _quiz).Tools.Concat(new VocabFollowUpTools(_store, _quiz).Tools), "1.0", "");

    private static async Task<JsonNode> CallAsync(McpServer server, string tool, JsonObject args)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), CancellationToken.None);
        var result = reply!["result"]!;
        Assert.False(result["isError"]!.GetValue<bool>(), result["content"]![0]!["text"]!.GetValue<string>());
        return JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
    }

    [Fact]
    public async Task An_agent_fills_in_word_forms_and_helps_with_hard_words()
    {
        var server = Server();
        var relate = Analyzed("relate");
        var hard = Analyzed("ubiquitous");
        // Analyzed before Helm asked for word forms: those wait for an agent once.
        _store.UpdateWord(relate, w => w with { Details = 0 });
        _store.UpdateWord(hard, w => w with { Details = 0 });
        for (var i = 0; i < VocabQuiz.HardForgets; i++) _store.Record(hard, remembered: false);

        var follow = await CallAsync(server, "vocab_follow_ups", []);
        Assert.Equal("ubiquitous", follow["often_forgotten"]![0]!["word"]!.GetValue<string>());
        Assert.Equal(4, follow["often_forgotten"]![0]!["forgot"]!.GetValue<int>());
        Assert.Equal(2, follow["missing_details_total"]!.GetValue<int>());
        Assert.Contains("forms", follow["missing_details"]![0]!["lacks"]!.AsArray().Select(n => n!.GetValue<string>()));

        var saved = await CallAsync(server, "vocab_save_details", new JsonObject
        {
            ["words"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = "relate",
                    ["forms"] = new JsonArray(
                        new JsonObject { ["word"] = "relation", ["part_of_speech"] = "danh từ", ["meaning"] = "mối quan hệ" },
                        new JsonObject { ["word"] = "relatively", ["part_of_speech"] = "trạng từ", ["meaning"] = "tương đối" }),
                    ["synonyms"] = new JsonArray("connect", "link"),
                    ["antonyms"] = new JsonArray(),
                },
                new JsonObject { ["id"] = hard, ["mnemonic"] = "ubi = ở đâu", ["forms"] = new JsonArray(), ["synonyms"] = new JsonArray("omnipresent"), ["antonyms"] = new JsonArray("rare") },
                new JsonObject { ["id"] = "relate", ["synonyms"] = new JsonArray("a", "b", "c", "d", "e", "f", "g") }),
        });
        Assert.Equal(2, saved["saved"]!.AsArray().Count);
        Assert.Contains("at most 6 synonyms", saved["failed"]![0]!["fix"]!.GetValue<string>());
        Assert.Equal(0, saved["often_forgotten_left"]!.GetValue<int>());
        Assert.Equal(0, saved["missing_details_left"]!.GetValue<int>());

        var word = _store.GetWord(relate)!;
        Assert.Equal(["relation", "relatively"], word.Forms.Select(f => f.Word));
        Assert.Equal(["connect", "link"], word.Synonyms);
        Assert.False(word.LacksDetails);
        Assert.NotNull(_store.GetWord(hard)!.HelpedAt);
        Assert.Null(word.HelpedAt);

        var info = new VocabWordInfo(word);
        Assert.True(info.HasForms);
        Assert.Equal("connect · link", info.Synonyms);
        Assert.False(info.HasAntonyms);

        var got = await CallAsync(server, "vocab_get", new JsonObject { ["word"] = "relate" });
        Assert.Equal("relation", got["forms"]![0]!["word"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_new_analysis_brings_its_word_forms_and_needs_no_follow_up()
    {
        var server = Server();
        _store.AddWord("relate");
        await CallAsync(server, "vocab_save_analysis", new JsonObject
        {
            ["id"] = "relate", ["phonetic"] = "/rɪˈleɪt/", ["meaning"] = "liên hệ",
            ["forms"] = new JsonArray(new JsonObject { ["word"] = "relation", ["part_of_speech"] = "danh từ", ["meaning"] = "mối quan hệ" }),
            ["antonyms"] = new JsonArray(),
        });
        var word = _store.FindWord("relate")!.Value;
        Assert.Equal("relation", Assert.Single(word.Forms).Word);
        Assert.False(word.LacksDetails);
        var follow = await CallAsync(server, "vocab_follow_ups", []);
        Assert.Equal(0, follow["missing_details_total"]!.GetValue<int>());
    }

    [Fact]
    public void The_editor_reads_word_forms_one_per_line()
    {
        Assert.Equal(
            [("relation", "danh từ", "mối quan hệ"), ("relatively", "", "tương đối"), ("relate", "", "")],
            VocabWordEditor.Triples("relation | danh từ | mối quan hệ\nrelatively | tương đối\n\nrelate").ToList());
        var id = Analyzed("relate");
        var editor = new VocabWordEditor(_store, id);
        editor.BeginCommand.Execute(null);
        editor.Forms = "relation | danh từ | mối quan hệ";
        editor.Synonyms = "connect, link";
        editor.SaveCommand.Execute(null);
        Assert.Equal("mối quan hệ", _store.GetWord(id)!.Forms.Single().Meaning);
        Assert.Equal(["connect", "link"], _store.GetWord(id)!.Synonyms);
    }

    // ---- a part of the tree made a word ------------------------------------------------------------------------

    [Fact]
    public void A_character_or_inner_word_of_the_tree_becomes_a_word_of_its_own()
    {
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, new WordAnalysis
        {
            Phonetic = "/ˈrel.ə.tɪv/", Meaning = "họ hàng",
            Parts =
            [
                new AnalysisPart
                {
                    Form = "relate", Kind = VocabPartKind.Word, Meaning = "liên hệ",
                    Parts =
                    [
                        new AnalysisPart { Form = "re-", Kind = VocabPartKind.Prefix, Meaning = "lại" },
                        new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "mang" },
                    ],
                },
                new AnalysisPart { Form = "-ive", Kind = VocabPartKind.Suffix, Meaning = "có tính chất" },
            ],
        });
        var opened = new List<string>();
        var notes = new List<string>();
        var detail = new VocabWordDetailViewModel(_store, id, _store.GetWord(id)!, _quiz.StatsOf(id), false, opened.Add, notify: notes.Add);
        var relate = detail.Root.Children[0];
        Assert.False(detail.Root.CanAddAsWord);
        Assert.True(relate.CanAddAsWord);
        Assert.False(relate.Children[0].CanAddAsWord); // re- is an affix
        Assert.False(relate.Children[1].CanAddAsWord); // lat is a Latin root
        Assert.False(detail.Root.Children[1].CanAddAsWord);
        Assert.Equal("Add “relate” as a word", relate.AddAsWordText);

        relate.SelectCommand.Execute(null);
        Assert.True(detail.CanAddSelectedAsWord);
        detail.AddSelectedAsWordCommand.Execute(null);
        var added = _store.FindWord("relate")!;
        Assert.Equal(VocabStatus.Pending, added.Value.Status);
        Assert.Equal("Trong relative", added.Value.Note);
        Assert.Single(notes);

        // Rebuilt after the change, the box knows the word is there and opens it.
        detail = new VocabWordDetailViewModel(_store, id, _store.GetWord(id)!, _quiz.StatsOf(id), false, opened.Add, notify: notes.Add);
        relate = detail.Root.Children[0];
        Assert.True(relate.IsOneOfYourWords);
        Assert.Equal("Open “relate”", relate.AddAsWordText);
        relate.AddAsWordCommand.Execute(null);
        Assert.Equal([added.Id], opened);
        Assert.Equal(2, _store.Words().Count);
    }

    [Fact]
    public void Chinese_characters_and_single_character_components_can_become_words()
    {
        VocabTreeNode Node(string form, VocabPartKind kind) => new() { Form = form, Kind = kind };
        Assert.True(VocabNodeViewModel.IsWordByItself(Node("挽", VocabPartKind.Character)));
        Assert.True(VocabNodeViewModel.IsWordByItself(Node("求", VocabPartKind.Component)));
        Assert.False(VocabNodeViewModel.IsWordByItself(Node("-ive", VocabPartKind.Suffix)));
    }

    // ---- where a part sits in a word ----------------------------------------------------------------------------

    [Fact]
    public void A_part_is_highlighted_where_it_sits_in_each_word()
    {
        var (relative, _) = _store.AddWord("relative");
        _store.SaveAnalysis(relative, new WordAnalysis
        {
            Phonetic = "/x/", Meaning = "họ hàng",
            Parts =
            [
                new AnalysisPart
                {
                    Form = "relate", Kind = VocabPartKind.Word, Meaning = "liên hệ",
                    Parts =
                    [
                        new AnalysisPart { Form = "re-", Kind = VocabPartKind.Prefix, Meaning = "lại" },
                        new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "mang" },
                    ],
                },
                new AnalysisPart { Form = "-ive", Kind = VocabPartKind.Suffix, Meaning = "có tính chất" },
            ],
        });
        var (buJiu, _) = _store.AddWord("补救");
        _store.SaveAnalysis(buJiu, new WordAnalysis
        {
            Phonetic = "bǔjiù", Meaning = "cứu vãn", HanViet = "bổ cứu",
            Parts =
            [
                new AnalysisPart { Form = "补", Kind = VocabPartKind.Character, Meaning = "vá", Phonetic = "bǔ", HanViet = "bổ" },
                new AnalysisPart
                {
                    Form = "救", Kind = VocabPartKind.Character, Meaning = "cứu", Phonetic = "jiù", HanViet = "cứu",
                    Parts = [new AnalysisPart { Form = "攵", Kind = VocabPartKind.Component, Role = VocabPartRole.Meaning, Meaning = "đánh", HanViet = "phộc" }],
                },
            ],
        });
        string Part(string form) => _store.Parts().Single(p => p.Value.Form == form).Id;
        VocabWordSplit Find(string wordId, string form) => VocabHighlight.Find(_store, _store.GetWord(wordId)!, Part(form));

        Assert.Equal(new VocabWordSplit("re", "lat", "ive"), Find(relative, "lat"));
        Assert.Equal(new VocabWordSplit("", "relat", "ive"), Find(relative, "relate"));
        Assert.Equal(new VocabWordSplit("relat", "ive", ""), Find(relative, "-ive"));
        // A component is not written apart: its character is the spot.
        Assert.Equal(new VocabWordSplit("补", "救", ""), Find(buJiu, "攵"));
        Assert.Equal(VocabWordSplit.None("relative"), VocabHighlight.Find(_store, _store.GetWord(relative)!, Part("攵")));

        // The Roots tab lists each family's words with the spot marked.
        var lat = VocabRootsViewModel.Families(_store, VocabLanguage.English, _ => { }).Single(r => r.Form == "lat");
        Assert.Equal("lat", lat.Words.Single().Split.Match);
    }

    // ---- an opened word: back, previous, next --------------------------------------------------------------------

    [Fact]
    public void An_opened_word_steps_through_the_list_in_its_order()
    {
        using var dir = new TempDir();
        using var settings = new SettingsStoreFactory(new HelmPaths(dir.Path));
        var translate = Analyzed("translate");
        var apple = Analyzed("apple");
        var relative = Analyzed("relative");
        var viewModel = VocabKit.ViewModel(_store, _quiz, settings);
        viewModel.ShowWordsCommand.Execute(null);

        viewModel.Open(apple);
        Assert.Equal("1 / 3", viewModel.WordPosition);
        Assert.False(viewModel.HasPreviousWord);
        Assert.True(viewModel.HasNextWord);
        viewModel.NextWordCommand.Execute(null);
        Assert.Equal(relative, viewModel.Detail!.Id);
        viewModel.NextWordCommand.Execute(null);
        Assert.Equal(translate, viewModel.Detail!.Id);
        Assert.False(viewModel.HasNextWord);
        Assert.False(viewModel.NextWordCommand.CanExecute(null) && viewModel.HasNextWord);
        viewModel.PreviousWordCommand.Execute(null);
        Assert.Equal("2 / 3", viewModel.WordPosition);

        viewModel.CloseDetailCommand.Execute(null);
        Assert.False(viewModel.HasDetail);
        Assert.Equal("", viewModel.WordPosition);
    }

    // ---- the phone widget's look ---------------------------------------------------------------------------------

    [Fact]
    public void The_widget_look_is_saved_and_transparent_starts_see_through()
    {
        using var dir = new TempDir();
        using var factory = new SettingsStoreFactory(new HelmPaths(dir.Path));
        var settings = VocabKit.Settings(factory);
        var changed = new List<string>();
        settings.Changed += (_, name) => changed.Add(name);
        Assert.Equal(100, settings.WidgetOpacity);

        settings.WidgetBackgroundIndex = (int)Helm.Core.Settings.WidgetBackground.Transparent;
        Assert.Equal(Helm.Core.Settings.WidgetLook.TransparentOpacity, settings.WidgetOpacity);
        settings.WidgetOpacity = 60;
        settings.WidgetTextIndex = (int)Helm.Core.Settings.WidgetText.Light;
        var look = settings.Current.WidgetLook;
        Assert.Equal(Helm.Core.Settings.WidgetBackground.Transparent, look.Background);
        Assert.Equal(60, look.Opacity);
        Assert.True(look.LightText);
        Assert.Contains(nameof(VocabSettingsViewModel.WidgetOpacity), changed);
    }

    // ---- the agent working by itself -----------------------------------------------------------------------------

    [Fact]
    public async Task The_agent_does_not_start_again_on_work_it_left_behind()
    {
        using var dir = new TempDir();
        using var settings = new SettingsStoreFactory(new HelmPaths(dir.Path));
        using var vm = new VocabAgentViewModel(new IdleAgent(), _store, _quiz, VocabKit.Settings(settings), new InlineUi());
        Assert.Null(vm.Work());
        var id = Analyzed("relate");
        _store.UpdateWord(id, w => w with { Details = 0 });
        var work = vm.Work();
        Assert.NotNull(work);

        await vm.AnalyzeCommand.ExecuteAsync(null);
        // The agent did nothing: the same work is left, and it is not started again by itself...
        Assert.Equal(work, vm.Work());
        Assert.Equal(work, vm.LeftBehind);
        // ...until something changes (here: the word is forgotten again and again).
        for (var i = 0; i < VocabQuiz.HardForgets; i++) _store.Record(id, remembered: false);
        Assert.NotEqual(vm.LeftBehind, vm.Work());
    }

    private sealed class IdleAgent : IVocabAgent
    {
        public string? Unavailable() => null;

        public Task<string> AnalyzeAsync(IProgress<string>? progress, CancellationToken ct) => Task.FromResult("Không có gì để làm.");

        public Task<string> SuggestAsync(string partId, string form, string meaning, VocabLanguage language, IProgress<string>? progress, CancellationToken ct) =>
            Task.FromResult("");
    }
}
