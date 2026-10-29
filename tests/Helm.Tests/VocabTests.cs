using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;
using Helm.Modules.Vocab;

namespace Helm.Tests;

public sealed class VocabTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualTime _time = new(T0);
    private readonly MemorySynced<VocabWord> _words = new();
    private readonly MemorySynced<VocabPart> _parts = new();
    private readonly MemorySyncedLog<VocabReview> _reviews = new();
    private readonly VocabStore _store;

    public VocabTests()
    {
        _store = new VocabStore(_words, _parts, _reviews, _time);
    }

    private static WordAnalysis Relative() => new()
    {
        Phonetic = "/ˈrel.ə.tɪv/",
        Meaning = "họ hàng; tương đối",
        Explanation = "mang trở lại → liên quan → người có liên quan",
        Parts =
        [
            new AnalysisPart
            {
                Form = "relate", Kind = VocabPartKind.Word, Meaning = "liên hệ",
                Parts =
                [
                    new AnalysisPart { Form = "re-", Kind = VocabPartKind.Prefix, Meaning = "lại, trở lại" },
                    new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "mang", Origin = "Latin latus, từ ferre" },
                ],
            },
            new AnalysisPart { Form = "-ive", Kind = VocabPartKind.Suffix, Meaning = "có tính chất" },
        ],
    };

    private static WordAnalysis Jiu() => new()
    {
        Phonetic = "jiù",
        Meaning = "cứu",
        HanViet = "cứu",
        Parts =
        [
            new AnalysisPart { Form = "求", Kind = VocabPartKind.Component, Role = VocabPartRole.Sound, Meaning = "cầu xin", Phonetic = "qiú", HanViet = "cầu" },
            new AnalysisPart { Form = "攵", Kind = VocabPartKind.Component, Role = VocabPartRole.Meaning, Meaning = "đánh, thúc", HanViet = "phộc" },
        ],
    };

    [Fact]
    public void Words_are_added_once_and_their_language_is_recognized()
    {
        var (en, added) = _store.AddWord("  Relative ", "Aptis reading");
        Assert.True(added);
        Assert.Equal(VocabLanguage.English, _store.GetWord(en)!.Language);
        Assert.Equal("Relative", _store.GetWord(en)!.Text);
        Assert.Equal((en, false), _store.AddWord("relative"));

        var (zh, _) = _store.AddWord("补 救");
        Assert.Equal(VocabLanguage.Chinese, _store.GetWord(zh)!.Language);
        Assert.Equal("补救", _store.GetWord(zh)!.Text);
        Assert.Equal([en, zh], _store.Pending().Select(w => w.Id));
    }

    [Fact]
    public void An_analysis_builds_the_tree_and_leaves_the_pending_list()
    {
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, Relative());

        var word = _store.GetWord(id)!;
        Assert.True(word.IsAnalyzed);
        Assert.Empty(_store.Pending());
        var tree = VocabTree.Build(_store, word);
        Assert.Equal(["relate", "-ive"], tree.Children.Select(c => c.Form));
        Assert.Equal(["re-", "lat"], tree.Children[0].Children.Select(c => c.Form));
        Assert.Equal("Latin latus, từ ferre", tree.Children[0].Children[1].Origin);
    }

    [Fact]
    public void The_same_piece_with_the_same_meaning_is_shared_and_makes_a_family()
    {
        var (relative, _) = _store.AddWord("relative");
        _store.SaveAnalysis(relative, Relative());
        var (translate, _) = _store.AddWord("translate");
        _store.SaveAnalysis(translate, new WordAnalysis
        {
            Phonetic = "/trænzˈleɪt/",
            Meaning = "dịch",
            Parts =
            [
                new AnalysisPart { Form = "trans-", Kind = VocabPartKind.Prefix, Meaning = "qua, sang" },
                // Written without the agent's lookup: still the same "lat".
                new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "Mang" },
                new AnalysisPart { Form = "-ate", Kind = VocabPartKind.Suffix, Meaning = "làm, khiến" },
            ],
        });

        var lat = Assert.Single(_store.PartsWritten("lat", VocabLanguage.English));
        Assert.Equal(["translate", "relative"], _store.WordsUsing(lat.Id).Select(w => w.Value.Text));
        Assert.Equal("Latin latus, từ ferre", lat.Value.Origin); // the first analysis's origin is kept

        // "lat" meaning "side" (lateral) is another part.
        var (lateral, _) = _store.AddWord("lateral");
        _store.SaveAnalysis(lateral, new WordAnalysis
        {
            Phonetic = "/ˈlæt.ər.əl/",
            Meaning = "bên cạnh",
            Parts = [new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "bên, cạnh" }, new AnalysisPart { Form = "-al", Kind = VocabPartKind.Suffix, Meaning = "thuộc về" }],
        });
        Assert.Equal(2, _store.PartsWritten("lat", VocabLanguage.English).Count);
    }

    [Fact]
    public void A_chinese_word_breaks_into_its_characters_in_order()
    {
        var (id, _) = _store.AddWord("补救");
        var wrong = new WordAnalysis
        {
            Phonetic = "bǔjiù", Meaning = "cứu vãn", HanViet = "bổ cứu",
            Parts = [new AnalysisPart { Form = "救", Kind = VocabPartKind.Character, Meaning = "cứu", Phonetic = "jiù", HanViet = "cứu" }],
        };
        var ex = Assert.Throws<VocabAnalysisException>(() => _store.SaveAnalysis(id, wrong));
        Assert.Contains("补 + 救", ex.Message);

        _store.SaveAnalysis(id, wrong with
        {
            Traditional = "補救",
            Parts =
            [
                new AnalysisPart { Form = "补", Kind = VocabPartKind.Character, Meaning = "vá, bù", Phonetic = "bǔ", HanViet = "bổ", Traditional = "補" },
                new AnalysisPart { Form = "救", Kind = VocabPartKind.Character, Meaning = "cứu", Phonetic = "jiù", HanViet = "cứu", Parts = Jiu().Parts },
            ],
        });
        var tree = VocabTree.Build(_store, _store.GetWord(id)!);
        Assert.Equal("補救", tree.Traditional);
        Assert.Equal([VocabPartRole.Sound, VocabPartRole.Meaning], tree.Children[1].Children.Select(c => c.Role));
    }

    [Theory]
    [InlineData("丿", "is not")]       // fine on its own...
    [InlineData("求求", "not one character")]
    public void Chinese_parts_are_single_characters(string form, string expected)
    {
        var (id, _) = _store.AddWord("救");
        var analysis = Jiu() with { Parts = [new AnalysisPart { Form = form, Kind = VocabPartKind.Component, Meaning = "nét phẩy" }] };
        if (form.Length == 1)
        {
            _store.SaveAnalysis(id, analysis); // ...a one-character component passes
            return;
        }
        Assert.Contains(expected, Assert.Throws<VocabAnalysisException>(() => _store.SaveAnalysis(id, analysis)).Message);
    }

    [Fact]
    public void A_rejected_analysis_writes_nothing()
    {
        var (id, _) = _store.AddWord("relative");
        var bad = Relative() with
        {
            Parts = [Relative().Parts[0], new AnalysisPart { Form = "-ive", Kind = VocabPartKind.Suffix }], // no meaning
        };
        Assert.Throws<VocabAnalysisException>(() => _store.SaveAnalysis(id, bad));
        Assert.Empty(_store.Parts());
        Assert.False(_store.GetWord(id)!.IsAnalyzed);
    }

    [Fact]
    public void A_part_cannot_contain_itself()
    {
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, Relative());
        var relate = Assert.Single(_store.PartsWritten("relate", VocabLanguage.English));
        var (other, _) = _store.AddWord("relation");
        var loop = new WordAnalysis
        {
            Phonetic = "/rɪˈleɪ.ʃən/", Meaning = "mối quan hệ",
            Parts = [new AnalysisPart { Id = relate.Id, Parts = [new AnalysisPart { Id = relate.Id }] }],
        };
        Assert.Throws<VocabAnalysisException>(() => _store.SaveAnalysis(other, loop));
    }

    [Fact]
    public void A_word_sent_back_waits_again_with_the_remark()
    {
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, Relative());
        _store.RequestRedo(id, "tách lat thành ferre");

        var word = _store.GetWord(id)!;
        Assert.Equal(VocabStatus.Pending, word.Status);
        Assert.Equal("tách lat thành ferre", word.RedoNote);
        Assert.NotEmpty(word.Parts); // the old analysis stays until a new one
        _store.SaveAnalysis(id, Relative());
        Assert.Equal("", _store.GetWord(id)!.RedoNote);
    }

    [Fact]
    public void Forgotten_words_come_up_more_often_than_known_ones()
    {
        var (known, _) = _store.AddWord("cat");
        var (forgotten, _) = _store.AddWord("dog");
        _store.AddWord("pending");
        foreach (var id in new[] { known, forgotten }) _store.SaveAnalysis(id, new WordAnalysis { Phonetic = "/x/", Meaning = "x" });
        for (var i = 0; i < 5; i++) _store.Record(known, remembered: true);
        _store.Record(forgotten, remembered: false);
        _time.Advance(TimeSpan.FromHours(1));

        var quiz = new VocabQuiz(_store, new Random(7));
        Assert.Equal(VocabQuiz.TopLevel, quiz.StatsOf(known).Level);
        Assert.Equal(0, quiz.StatsOf(forgotten).Level);
        var picks = Enumerable.Range(0, 1000).Select(_ => quiz.Next()!.Id).ToList();
        Assert.DoesNotContain(picks, p => _store.GetWord(p)!.Text == "pending");
        Assert.InRange(picks.Count(p => p == forgotten), 900, 970); // 8 : 0.5
        Assert.Equal(forgotten, quiz.Next(except: known)!.Id);
    }

    [Fact]
    public void The_card_hides_the_meaning_until_tapped_and_records_the_answer()
    {
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, Relative());
        var card = new VocabCardViewModel(_store, new VocabQuiz(_store));
        Assert.True(card.ShowNext());
        Assert.Equal("relate (re- + lat) + -ive", card.PartsText);
        Assert.Equal(["relate · liên hệ ← re- lại + lat mang", "-ive · có tính chất"], card.PartLines);
        card.RememberedCommand.Execute(null); // not revealed: nothing recorded
        Assert.Empty(_store.Reviews());

        card.RevealCommand.Execute(null);
        card.ForgotCommand.Execute(null);
        var review = Assert.Single(_store.Reviews());
        Assert.False(review.Remembered);
        Assert.False(card.IsRevealed);
    }

    [Theory]
    [InlineData("relative", VocabLanguage.English, "https://youglish.com/pronounce/relative/english/uk")]
    [InlineData("补救", VocabLanguage.Chinese, "https://youglish.com/pronounce/%E8%A1%A5%E6%95%91/chinese")]
    [InlineData("look up", VocabLanguage.English, "https://youglish.com/pronounce/look%20up/english/uk")]
    public void YouGlish_opens_the_word_in_its_language(string word, VocabLanguage language, string url) =>
        Assert.Equal(url, VocabVoice.YouGlishUrl(word, language));

    [Fact]
    public void A_custom_pop_up_range_is_typed_in_seconds_and_saved_only_when_it_makes_sense()
    {
        using var dir = new TempDir();
        using var settings = new Helm.Core.Settings.SettingsStoreFactory(new Helm.Core.Settings.HelmPaths(dir.Path));
        var vm = new VocabViewModel(_store, new VocabQuiz(_store), settings, new InlineUi(), new AcceptDialogs(), new MemoryClipboard());
        var store = settings.Get<VocabSettings>(VocabIds.ModuleId);

        vm.PopupFrequencyIndex = VocabViewModel.CustomFrequencyIndex;
        Assert.True(vm.IsCustomFrequency);
        vm.CustomToText = "10";
        vm.CustomFromText = "30";
        Assert.True(vm.HasCustomError); // longest shorter than shortest: not saved
        Assert.Equal(20 * 60, store.Current.PopupMinSeconds);

        vm.CustomToText = "90";
        Assert.False(vm.HasCustomError);
        Assert.Equal((30, 90, true), (store.Current.PopupMinSeconds, store.Current.PopupMaxSeconds, store.Current.PopupCustom));

        vm.CustomFromText = "2";
        Assert.Contains("At least", vm.CustomError);

        vm.PopupFrequencyIndex = 0; // a fixed choice again
        Assert.Equal((600, 1800, false), (store.Current.PopupMinSeconds, store.Current.PopupMaxSeconds, store.Current.PopupCustom));
    }

    // ---- MCP -----------------------------------------------------------------------------------------------------

    private McpServer Server() => new(new VocabMcpTools(_store, new VocabQuiz(_store)).Tools, "1.0", "");

    private static async Task<JsonNode> CallAsync(McpServer server, string tool, JsonObject args, bool expectError = false)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
        };
        var reply = await server.HandleAsync(request.ToJsonString(), CancellationToken.None);
        var result = reply!["result"]!;
        Assert.Equal(expectError, result["isError"]!.GetValue<bool>());
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        return expectError ? JsonValue.Create(text) : JsonNode.Parse(text)!;
    }

    [Fact]
    public async Task An_agent_reads_pending_words_and_saves_a_nested_analysis()
    {
        var server = Server();
        _store.AddWord("救", "đọc trong truyện");
        var pending = await CallAsync(server, "vocab_pending", []);
        var item = pending["words"]![0]!;
        Assert.Equal("chinese", item["language"]!.GetValue<string>());
        Assert.Equal("đọc trong truyện", item["note"]!.GetValue<string>());

        var saved = await CallAsync(server, "vocab_save_analysis", new JsonObject
        {
            ["id"] = "救",
            ["phonetic"] = "jiù",
            ["meaning"] = "cứu",
            ["han_viet"] = "cứu",
            ["parts"] = new JsonArray(
                new JsonObject
                {
                    ["form"] = "求", ["kind"] = "component", ["role"] = "sound", ["meaning"] = "cầu xin", ["han_viet"] = "cầu",
                    ["parts"] = new JsonArray(new JsonObject { ["form"] = "氺", ["kind"] = "component", ["meaning"] = "nước", ["han_viet"] = "thủy" }),
                },
                new JsonObject { ["form"] = "攵", ["kind"] = "component", ["role"] = "meaning", ["meaning"] = "đánh, thúc", ["han_viet"] = "phộc" }),
        });
        Assert.Equal("氺", saved["tree"]!["parts"]![0]!["parts"]![0]!["form"]!.GetValue<string>());
        Assert.Equal(0, (await CallAsync(server, "vocab_pending", []))["total"]!.GetValue<int>());

        var found = await CallAsync(server, "vocab_find_parts", new JsonObject { ["forms"] = new JsonArray("求"), ["language"] = "chinese" });
        Assert.Equal("救", found[0]!["parts"]![0]!["used_by"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Many_words_are_analyzed_in_one_call_and_each_one_is_checked_on_its_own()
    {
        var server = Server();
        _store.AddWord("cat");
        _store.AddWord("补救");
        var result = await CallAsync(server, "vocab_save_analyses", new JsonObject
        {
            ["analyses"] = new JsonArray(
                new JsonObject { ["id"] = "cat", ["phonetic"] = "/kæt/", ["meaning"] = "con mèo" },
                new JsonObject { ["id"] = "补救", ["phonetic"] = "bǔjiù", ["meaning"] = "cứu vãn" }), // no Hán Việt
        });
        Assert.Equal("cat", result["saved"]![0]!.GetValue<string>());
        Assert.Contains("han_viet", result["failed"]![0]!["fix"]!.GetValue<string>());
        Assert.Equal(1, result["pending_left"]!.GetValue<int>());
    }

    [Fact]
    public async Task Words_and_parts_are_reworded_without_touching_their_trees()
    {
        var server = Server();
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, Relative());
        var lat = Assert.Single(_store.PartsWritten("lat", VocabLanguage.English));
        var result = await CallAsync(server, "vocab_edit", new JsonObject
        {
            ["words"] = new JsonArray(new JsonObject { ["id"] = "relative", ["meaning"] = "người thân; tương đối" }),
            ["parts"] = new JsonArray(new JsonObject { ["id"] = lat.Id, ["meaning"] = "mang theo" }, new JsonObject { ["id"] = "nope", ["meaning"] = "x" }),
        });
        Assert.Equal(["relative", "lat"], result["edited"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("người thân; tương đối", _store.GetWord(id)!.Meaning);
        Assert.Equal("mang theo", _store.GetPart(lat.Id)!.Meaning);
        Assert.Equal("/ˈrel.ə.tɪv/", _store.GetWord(id)!.Phonetic); // untouched
        Assert.Equal(2, _store.GetWord(id)!.Parts.Count);
    }

    [Fact]
    public void The_user_rewords_a_word_and_a_part_by_hand()
    {
        var (id, _) = _store.AddWord("relative");
        _store.SaveAnalysis(id, Relative());
        var detail = new VocabWordDetailViewModel(_store, id, _store.GetWord(id)!, VocabWordStats.New, showTraditional: true, open: _ => { });
        detail.BeginEditWordCommand.Execute(null);
        Assert.Equal("họ hàng; tương đối", detail.EditMeaning);
        detail.EditMeaning = "  người thân ";
        detail.SaveWordCommand.Execute(null);
        Assert.Equal("người thân", _store.GetWord(id)!.Meaning);

        detail.Root.Children[0].Children[1].SelectCommand.Execute(null); // lat
        detail.BeginEditPartCommand.Execute(null);
        detail.EditPartMeaning = "mang, chở";
        detail.SavePartCommand.Execute(null);
        Assert.Equal("mang, chở", _store.PartsWritten("lat", VocabLanguage.English)[0].Value.Meaning);
    }

    [Fact]
    public async Task An_incomplete_analysis_is_refused_with_what_to_fix()
    {
        var server = Server();
        _store.AddWord("补救");
        var error = await CallAsync(server, "vocab_save_analysis", new JsonObject { ["id"] = "补救", ["phonetic"] = "bǔjiù", ["meaning"] = "cứu vãn" }, expectError: true);
        Assert.Contains("han_viet", error.GetValue<string>());
    }
}
