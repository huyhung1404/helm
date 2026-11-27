using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Settings;
using Helm.Modules.Vocab;
using Helm.Modules.Vocab.Ui;

namespace Helm.Gallery;

/// <summary>Which Vocabulary a Gallery entry shows.</summary>
internal enum VocabStage
{
    /// <summary>The Learn tab: a word's card, its answer hidden.</summary>
    Learn,
    /// <summary>The same card with its answer: the reading, the meaning, how it is built and what helps.</summary>
    Answer,
    /// <summary>A "Type the word" card, typed one letter off and checked.</summary>
    Typed,
    /// <summary>A "Fill the gap" card.</summary>
    Gap,
    /// <summary>A Chinese card asking for the pinyin, typed with the tones wrong.</summary>
    Pinyin,
    /// <summary>The card's ⋯ menu.</summary>
    CardMenu,
    /// <summary>The Words tab: add, three words waiting for the agent, the list.</summary>
    Words,
    /// <summary>An English word's page with a part tapped (lat of relative).</summary>
    Word,
    /// <summary>A Chinese word's page with a component tapped (攵 of 补救).</summary>
    ChineseWord,
    /// <summary>The Roots tab: families and parts in one word.</summary>
    Roots,
    /// <summary>Root practice, the meaning shown.</summary>
    RootPractice,
    /// <summary>A family opened (spect).</summary>
    Family,
    /// <summary>The Analysis sheet: what waits for an AI agent.</summary>
    AnalysisSheet,
    /// <summary>Add text: the paste form and the texts pasted before.</summary>
    AddText,
    /// <summary>Add text: the words an agent proposed from a text.</summary>
    TextWords,
    /// <summary>Editing a word by hand.</summary>
    Edit,
    /// <summary>The settings sheet (the gear).</summary>
    Settings,
    /// <summary>Words added, none analyzed yet: the Learn tab says what it waits for.</summary>
    Waiting,
    /// <summary>No word at all.</summary>
    FirstRun,
    /// <summary>The PC's corner card: a word to guess.</summary>
    CardWord,
    /// <summary>The corner card with its answer.</summary>
    CardAnswer,
    /// <summary>The corner card on a Chinese word, asking for its pinyin.</summary>
    CardAsksPinyin,
    /// <summary>The corner card on a Chinese word, its pinyin typed with the tones wrong.</summary>
    CardPinyin,
    /// <summary>The corner card with Full details open.</summary>
    CardFull,
}

/// <summary>
/// The real Vocabulary view over in-memory stores: the English words a Vietnamese learner meets preparing for Aptis B1
/// (and a few Chinese ones), broken into their roots with Vietnamese meanings, answered over the last two months.
/// </summary>
internal static class VocabDemo
{
    public static Control Create(FakeModule module, VocabStage stage)
    {
        var clock = new DemoClock();
        var sources = new MemorySynced<VocabSource>();
        var store = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>(), clock, sources);
        var words = stage switch
        {
            VocabStage.FirstRun => new Dictionary<string, string>(),
            VocabStage.Waiting => SeedWaiting(store),
            _ => Seed(store, clock),
        };
        if (stage is not (VocabStage.FirstRun or VocabStage.Waiting)) SeedTexts(store);

        var dir = Path.Combine(Path.GetTempPath(), "helm-gallery", Guid.NewGuid().ToString("N"));
        var factory = new SettingsStoreFactory(new HelmPaths(dir));
        var settings = new VocabSettingsViewModel(factory.Get<VocabSettings>(VocabIds.ModuleId));
        settings.CardModeIndex = (int)(stage switch
        {
            VocabStage.Typed => VocabCardMode.Type,
            VocabStage.Gap => VocabCardMode.FillGap,
            _ => VocabCardMode.WordToMeaning,
        });
        var ui = new AvaloniaUi();
        var quiz = new VocabQuiz(store, new Random(3));
        var voice = new VocabVoice(() => null, null);
        if (stage is VocabStage.CardWord or VocabStage.CardAnswer or VocabStage.CardAsksPinyin or VocabStage.CardPinyin or VocabStage.CardFull)
            return PopupCard(store, quiz, voice, words, stage);
        var vm = new VocabViewModel(store, quiz, settings, ui, new YesDialogs(), new NoClipboard(), voice, new VocabTextsViewModel(store, ui));
        var view = new VocabView(module, vm, new Host());

        void Card(string word) => vm.Card.Show(words[word], store.GetWord(words[word]));
        void Later(Action action) => view.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
        switch (stage)
        {
            case VocabStage.Learn:
                Card("relative");
                break;
            case VocabStage.Answer:
                Card("relative");
                vm.Card.RevealCommand.Execute(null);
                break;
            case VocabStage.Typed:
                Card("environment");
                vm.Card.Typed = "enviroment";
                vm.Card.CheckCommand.Execute(null);
                break;
            case VocabStage.Gap:
                Card("reliable");
                break;
            case VocabStage.Pinyin:
                Card("认真");
                vm.Card.Typed = "ren2zhen1";
                vm.Card.CheckCommand.Execute(null);
                break;
            case VocabStage.CardMenu:
                Card("relative");
                Later(() => view.OpenCardMenu());
                break;
            case VocabStage.Words:
                vm.Tab = VocabTab.Words;
                break;
            case VocabStage.Word:
                vm.Open(words["relative"]);
                Pick(vm, "lat");
                break;
            case VocabStage.ChineseWord:
                vm.Open(words["补救"]);
                Pick(vm, "攵");
                break;
            case VocabStage.Roots:
                vm.Tab = VocabTab.Roots;
                break;
            case VocabStage.RootPractice:
                vm.Tab = VocabTab.Roots;
                vm.Roots.NextRootCommand.Execute(null);
                vm.Roots.RevealRootCommand.Execute(null);
                break;
            case VocabStage.Family:
                vm.Tab = VocabTab.Roots;
                vm.Roots.Roots.First(r => r.Form == "spect").Select.Execute(null);
                break;
            case VocabStage.AnalysisSheet:
                vm.Tab = VocabTab.Words;
                vm.OpenAgentCommand.Execute(null);
                break;
            case VocabStage.AddText:
                vm.Tab = VocabTab.Words;
                vm.Texts.OpenCommand.Execute(null);
                break;
            case VocabStage.TextWords:
                vm.Tab = VocabTab.Words;
                vm.Texts.OpenCommand.Execute(null);
                vm.Texts.Texts.First(t => !t.IsWaiting).Open.Execute(null);
                break;
            case VocabStage.Edit:
                vm.Open(words["relative"]);
                vm.Detail?.Editor.BeginCommand.Execute(null);
                break;
            case VocabStage.Settings:
                Later(() => view.OpenSettings());
                break;
        }
        return view;
    }

    /// <summary>The PC's corner card (the real view the pop-up window shows), in the bottom-right corner of a screen.</summary>
    private static Control PopupCard(VocabStore store, VocabQuiz quiz, VocabVoice voice, Dictionary<string, string> words, VocabStage stage)
    {
        var card = new VocabCardViewModel(store, quiz, voice: voice, options: () => new VocabCardOptions(VocabCardMode.WordToMeaning, false, TypePinyin: true))
        {
            ShowYouGlish = true,
        };
        var word = stage is VocabStage.CardAsksPinyin or VocabStage.CardPinyin ? "补救" : "relative";
        card.Show(words[word], store.GetWord(words[word]));
        if (stage == VocabStage.CardPinyin)
        {
            card.Typed = "bu3jiu2";
            card.CheckCommand.Execute(null);
        }
        else if (stage is VocabStage.CardAnswer or VocabStage.CardFull)
        {
            card.RevealCommand.Execute(null);
        }
        var view = new VocabPopupCard(card, new PopupHost(), stage is VocabStage.CardWord or VocabStage.CardAsksPinyin ? 0.85 : 1)
        {
            Width = 320,
            Margin = new Avalonia.Thickness(24),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
        };
        if (stage == VocabStage.CardFull) view.ToggleFull();
        return new Panel { Children = { new Helm.Core.Ui.Backdrop(), view } };
    }

    private sealed class PopupHost : IVocabPopupHost
    {
        public void Close() { }

        public void OpenWord(string id) { }

        public void TakeKeyboard() { }

        public void ReturnKeyboard() { }

        public void Drag(Avalonia.Input.PointerPressedEventArgs e) { }

        public void BackToCorner() { }

        public double FullHeight => 460;
    }

    private static void Pick(VocabViewModel vm, string form) =>
        vm.Detail?.Root.SelfAndDescendants().First(n => n.Node.Form == form).SelectCommand.Execute(null);

    // ---- the words -------------------------------------------------------------------------------------------------

    private static AnalysisPart P(string form, VocabPartKind kind, string meaning, string? origin = null, params AnalysisPart[] parts) =>
        new() { Form = form, Kind = kind, Meaning = meaning, Origin = origin, Parts = parts.Length == 0 ? null : parts };

    private static AnalysisPart Pre(string form, string meaning, string? origin = null) => P(form, VocabPartKind.Prefix, meaning, origin);

    private static AnalysisPart Root(string form, string meaning, string? origin = null) => P(form, VocabPartKind.Root, meaning, origin);

    private static AnalysisPart Suf(string form, string meaning) => P(form, VocabPartKind.Suffix, meaning);

    private static AnalysisPart Inner(string form, string meaning, params AnalysisPart[] parts) => P(form, VocabPartKind.Word, meaning, null, parts);

    private static AnalysisPart Han(string form, string pinyin, string hanViet, string meaning, params AnalysisPart[] parts) =>
        new() { Form = form, Kind = VocabPartKind.Character, Phonetic = pinyin, HanViet = hanViet, Meaning = meaning, Parts = parts.Length == 0 ? null : parts };

    private static AnalysisPart Comp(string form, VocabPartRole role, string meaning, string? pinyin = null) =>
        new() { Form = form, Kind = VocabPartKind.Component, Role = role, Meaning = meaning, Phonetic = pinyin };

    private static VocabExample Ex(string sentence, string translation) => new() { Sentence = sentence, Translation = translation };

    /// <summary>
    /// Twenty-odd English words of an Aptis B1 learner and three Chinese ones, added over two months and answered since:
    /// some due now, some new, some learned, one forgotten again and again, a Chinese one whose pinyin slips, one hidden;
    /// three more just added, waiting for the agent.
    /// </summary>
    private static Dictionary<string, string> Seed(VocabStore store, DemoClock clock)
    {
        var ids = new Dictionary<string, string>();
        var day = 62;
        string Add(string text, string note, WordAnalysis analysis, params string[] tags)
        {
            clock.DaysAgo(day);
            day -= 2;
            var (id, _) = store.AddWord(text, note);
            store.SaveAnalysis(id, analysis);
            if (tags.Length > 0) store.UpdateWord(id, w => w with { Tags = tags });
            ids[text] = id;
            return id;
        }

        var lat = Root("lat", "mang, đưa", "Latin lātus, dạng quá khứ của ferre “mang” (cùng gốc với fer trong transfer)");
        Add("relative", "Aptis reading, part 2", new WordAnalysis
        {
            Phonetic = "/ˈrel.ə.tɪv/",
            Meaning = "họ hàng, người thân; tương đối",
            Explanation = "relate (đưa lại gần, liên hệ) + -ive (người / tính chất): người có liên hệ với mình → họ hàng.",
            Parts = [Inner("relate", "liên hệ, kể lại", Pre("re-", "lại, trở lại"), lat), Suf("-ive", "có tính chất, người")],
            Examples = [Ex("Most of my relatives live in Hanoi.", "Phần lớn họ hàng của tôi sống ở Hà Nội."),
                Ex("The test was relatively easy.", "Bài thi tương đối dễ."), Ex("Prices are high relative to salaries.", "Giá cả cao so với lương.")],
            Collocations = ["a close relative", "a distant relative", "relative to", "relatively easy"],
            Mnemonic = "re- (lại) + lat (mang): người mà Tết nào cũng “mang lại” nhà mình chơi → họ hàng.",
            Confusables = [new() { Word = "relevant", Note = "liên quan, đúng trọng tâm (a relevant answer), không phải họ hàng" },
                new() { Word = "relation", Note = "mối quan hệ nói chung (international relations)" }],
            Level = "B1",
            PartOfSpeech = "danh từ · tính từ",
            Topic = "gia đình",
            Forms = [new() { Word = "relate", PartOfSpeech = "động từ", Meaning = "liên hệ, kể" }, new() { Word = "relation", PartOfSpeech = "danh từ", Meaning = "mối quan hệ" },
                new() { Word = "relationship", PartOfSpeech = "danh từ", Meaning = "mối quan hệ (tình cảm)" }, new() { Word = "relatively", PartOfSpeech = "trạng từ", Meaning = "tương đối" }],
            Synonyms = ["family member", "kin"],
            Antonyms = ["absolute"],
        }, "Aptis");
        Add("translate", "Lớp tiếng Anh", new WordAnalysis
        {
            Phonetic = "/trænsˈleɪt/",
            Meaning = "dịch (từ ngôn ngữ này sang ngôn ngữ khác)",
            Explanation = "trans- (sang) + late (mang): mang ý từ bên này sang bên kia.",
            Parts = [Pre("trans-", "qua, sang"), lat],
            Examples = [Ex("Can you translate this email into English?", "Bạn dịch giúp email này sang tiếng Anh được không?")],
            Collocations = ["translate into", "translate from"],
            Level = "B1",
            PartOfSpeech = "động từ",
            Topic = "ngôn ngữ",
            Forms = [new() { Word = "translation", PartOfSpeech = "danh từ", Meaning = "bản dịch" }, new() { Word = "translator", PartOfSpeech = "danh từ", Meaning = "người dịch" }],
        }, "Aptis");
        Add("relation", "", new WordAnalysis
        {
            Phonetic = "/rɪˈleɪ.ʃən/",
            Meaning = "mối quan hệ, sự liên quan",
            Parts = [Inner("relate", "liên hệ, kể lại"), Suf("-ion", "sự, việc")],
            Level = "B1",
            PartOfSpeech = "danh từ",
            Topic = "xã hội",
        });

        var spect = Root("spect", "nhìn", "Latin specere “nhìn”; cũng gặp ở spec-, spic- (special, conspicuous)");
        Add("inspect", "Aptis listening, part 3", new WordAnalysis
        {
            Phonetic = "/ɪnˈspekt/",
            Meaning = "kiểm tra kỹ, thanh tra",
            Explanation = "in- (vào trong) + spect (nhìn): nhìn vào tận bên trong.",
            Parts = [Pre("in-", "vào, trong"), spect],
            Examples = [Ex("The inspector inspected the kitchen carefully.", "Thanh tra kiểm tra nhà bếp rất kỹ.")],
            Level = "B2",
            PartOfSpeech = "động từ",
            Topic = "công việc",
        });
        Add("respect", "", new WordAnalysis
        {
            Phonetic = "/rɪˈspekt/",
            Meaning = "tôn trọng, kính trọng",
            Explanation = "re- (lại) + spect (nhìn): ngoái nhìn lại một người vì nể → tôn trọng.",
            Parts = [Pre("re-", "lại, trở lại"), spect],
            Examples = [Ex("Students should respect their teachers.", "Học sinh nên tôn trọng thầy cô.")],
            Collocations = ["show respect", "with respect to"],
            Level = "B1",
            PartOfSpeech = "động từ · danh từ",
            Topic = "xã hội",
        });
        Add("perspective", "Bài Writing task 4", new WordAnalysis
        {
            Phonetic = "/pəˈspek.tɪv/",
            Meaning = "góc nhìn, quan điểm",
            Explanation = "per- (xuyên qua) + spect (nhìn) + -ive: cách nhìn xuyên qua sự việc.",
            Parts = [Pre("per-", "xuyên qua, hoàn toàn"), spect, Suf("-ive", "có tính chất, người")],
            Examples = [Ex("From my perspective, online learning saves time.", "Theo góc nhìn của tôi, học trực tuyến tiết kiệm thời gian.")],
            Collocations = ["from my perspective", "a different perspective"],
            Level = "B2",
            PartOfSpeech = "danh từ",
            Topic = "ý kiến",
        }, "Aptis", "Writing");

        var dict = Root("dict", "nói", "Latin dīcere “nói”, dictum “điều đã nói”");
        Add("predict", "", new WordAnalysis
        {
            Phonetic = "/prɪˈdɪkt/",
            Meaning = "dự đoán, đoán trước",
            Explanation = "pre- (trước) + dict (nói): nói trước khi chuyện xảy ra.",
            Parts = [Pre("pre-", "trước"), dict],
            Examples = [Ex("Experts predict that prices will rise next year.", "Các chuyên gia dự đoán năm sau giá sẽ tăng.")],
            Level = "B1",
            PartOfSpeech = "động từ",
            Topic = "tương lai",
            Forms = [new() { Word = "prediction", PartOfSpeech = "danh từ", Meaning = "lời dự đoán" }, new() { Word = "predictable", PartOfSpeech = "tính từ", Meaning = "đoán trước được" }],
        }, "Aptis");
        Add("contradict", "", new WordAnalysis
        {
            Phonetic = "/ˌkɒn.trəˈdɪkt/",
            Meaning = "nói ngược lại, mâu thuẫn với",
            Parts = [Pre("contra-", "ngược lại, chống"), dict],
            Level = "B2",
            PartOfSpeech = "động từ",
            Topic = "ý kiến",
        });
        Add("dictionary", "", new WordAnalysis
        {
            Phonetic = "/ˈdɪk.ʃən.ər.i/",
            Meaning = "từ điển",
            Parts = [dict, Suf("-ion", "sự, việc"), Suf("-ary", "nơi chứa, thuộc về")],
            Level = "A2",
            PartOfSpeech = "danh từ",
            Topic = "học tập",
        });

        var port = Root("port", "mang, chở", "Latin portāre “mang, chở”; port cũng là bến cảng, nơi hàng được chở tới");
        Add("transport", "", new WordAnalysis
        {
            Phonetic = "/ˈtræn.spɔːt/",
            Meaning = "giao thông, vận chuyển",
            Parts = [Pre("trans-", "qua, sang"), port],
            Examples = [Ex("Public transport in my city is cheap but crowded.", "Giao thông công cộng ở thành phố tôi rẻ nhưng đông.")],
            Collocations = ["public transport", "means of transport"],
            Level = "A2",
            PartOfSpeech = "danh từ · động từ",
            Topic = "đi lại",
        }, "Aptis");
        Add("export", "Báo VnExpress", new WordAnalysis
        {
            Phonetic = "/ɪkˈspɔːt/",
            Meaning = "xuất khẩu",
            Parts = [Pre("ex-", "ra ngoài"), port],
            Level = "B1",
            PartOfSpeech = "động từ · danh từ",
            Topic = "kinh tế",
        });
        Add("portable", "", new WordAnalysis
        {
            Phonetic = "/ˈpɔː.tə.bəl/",
            Meaning = "xách tay, mang theo được",
            Parts = [port, Suf("-able", "có thể")],
            Level = "B1",
            PartOfSpeech = "tính từ",
            Topic = "đồ dùng",
        });

        Add("environment", "Aptis speaking, part 3", new WordAnalysis
        {
            Phonetic = "/ɪnˈvaɪ.rən.mənt/",
            Meaning = "môi trường",
            Explanation = "environ (vây quanh) + -ment (sự, cái): cái vây quanh ta.",
            Parts = [Inner("environ", "vây quanh", Pre("en-", "trong, làm cho"), Root("viron", "vòng tròn", "tiếng Pháp cổ virer “quay vòng”")), Suf("-ment", "sự, cái")],
            Examples = [Ex("We must protect the environment for our children.", "Chúng ta phải bảo vệ môi trường cho con cháu.")],
            Collocations = ["protect the environment", "a friendly environment", "environmental pollution"],
            Mnemonic = "Nhớ “environ” = vòng quanh: môi trường là mọi thứ quây quanh mình. Đừng quên chữ n trước m: enviro-N-ment.",
            Level = "B1",
            PartOfSpeech = "danh từ",
            Topic = "môi trường",
        }, "Aptis");
        Add("reliable", "", new WordAnalysis
        {
            Phonetic = "/rɪˈlaɪ.ə.bəl/",
            Meaning = "đáng tin cậy",
            Parts = [Inner("rely", "dựa vào, tin cậy"), Suf("-able", "có thể")],
            Examples = [Ex("My old motorbike is still very reliable.", "Chiếc xe máy cũ của tôi vẫn rất đáng tin cậy."),
                Ex("We need reliable information before we decide.", "Chúng ta cần thông tin đáng tin cậy trước khi quyết định.")],
            Collocations = ["reliable information", "a reliable friend"],
            Level = "B1",
            PartOfSpeech = "tính từ",
            Topic = "tính cách",
            Antonyms = ["unreliable"],
        }, "Aptis");
        Add("visible", "", new WordAnalysis
        {
            Phonetic = "/ˈvɪz.ə.bəl/",
            Meaning = "nhìn thấy được",
            Parts = [Root("vis", "nhìn, thấy", "Latin vidēre, vīsus “nhìn”"), Suf("-ible", "có thể")],
            Level = "B1",
            PartOfSpeech = "tính từ",
            Topic = "mô tả",
        });
        Add("independent", "", new WordAnalysis
        {
            Phonetic = "/ˌɪn.dɪˈpen.dənt/",
            Meaning = "độc lập, không phụ thuộc",
            Parts = [Pre("in-", "không"), Inner("depend", "phụ thuộc", Pre("de-", "xuống"), Root("pend", "treo", "Latin pendēre “treo”")), Suf("-ent", "có tính chất")],
            Level = "B1",
            PartOfSpeech = "tính từ",
            Topic = "tính cách",
        });
        Add("comfortable", "", new WordAnalysis
        {
            Phonetic = "/ˈkʌm.fə.tə.bəl/",
            Meaning = "thoải mái, dễ chịu",
            Parts = [Inner("comfort", "sự an ủi, dễ chịu"), Suf("-able", "có thể")],
            Level = "A2",
            PartOfSpeech = "tính từ",
            Topic = "cảm xúc",
        });
        Add("audience", "", new WordAnalysis
        {
            Phonetic = "/ˈɔː.di.əns/",
            Meaning = "khán giả, thính giả",
            Parts = [Root("aud", "nghe", "Latin audīre “nghe” (audio, audition)"), Suf("-ience", "sự, những người")],
            Level = "B1",
            PartOfSpeech = "danh từ",
            Topic = "giải trí",
        });

        // Chinese, from a novel: tones in colour, Hán Việt, components that give the meaning or the sound.
        Add("补救", "Truyện: chương 12", new WordAnalysis
        {
            Phonetic = "bǔjiù",
            Meaning = "cứu vãn, khắc phục",
            HanViet = "bổ cứu",
            Traditional = "補救",
            Explanation = "补 (vá) + 救 (cứu): vá chỗ hỏng để cứu lấy → khắc phục, cứu vãn.",
            Parts = [Han("补", "bǔ", "bổ", "vá, bù vào", Comp("衤", VocabPartRole.Meaning, "áo"), Comp("卜", VocabPartRole.Sound, "bói", "bǔ")),
                Han("救", "jiù", "cứu", "cứu, giúp", Comp("求", VocabPartRole.Sound, "cầu, xin", "qiú"), Comp("攵", VocabPartRole.Meaning, "đánh, tay cầm gậy"))],
            Examples = [Ex("现在补救还来得及。", "Bây giờ khắc phục vẫn còn kịp.")],
            Collocations = ["补救措施", "无法补救"],
            Mnemonic = "Áo (衤) rách thì vá (补); người gặp nạn thì cứu (救): vá lại để cứu lấy.",
            Level = "HSK6",
            PartOfSpeech = "động từ",
        }, "Truyện");
        Add("认真", "Truyện: chương 3", new WordAnalysis
        {
            Phonetic = "rènzhēn",
            Meaning = "nghiêm túc, chăm chỉ",
            HanViet = "nhận chân",
            Explanation = "认 (nhận) + 真 (thật): nhận là thật → coi là chuyện thật, làm nghiêm túc.",
            Parts = [Han("认", "rèn", "nhận", "nhận ra, thừa nhận", Comp("讠", VocabPartRole.Meaning, "lời nói"), Comp("人", VocabPartRole.Sound, "người", "rén")),
                Han("真", "zhēn", "chân", "thật")],
            Examples = [Ex("他工作很认真。", "Anh ấy làm việc rất nghiêm túc.")],
            PinyinMnemonic = "认 đọc như 人 (rén) nhưng thanh 4, đi xuống: rèn. 真 thanh 1, giữ ngang: zhēn.",
            Level = "HSK3",
            PartOfSpeech = "tính từ",
        });
        Add("故事", "", new WordAnalysis
        {
            Phonetic = "gùshi",
            Meaning = "câu chuyện",
            HanViet = "cố sự",
            Parts = [Han("故", "gù", "cố", "cũ, xưa"), Han("事", "shì", "sự", "việc")],
            Level = "HSK2",
            PartOfSpeech = "danh từ",
        });

        // How each is known now: level and when it is due (negative: due already), forgets, pinyin slips.
        Learn(store, clock, ids["relative"], level: 3, dueInDays: -0.5);
        Learn(store, clock, ids["translate"], level: 5, dueInDays: 9);
        Learn(store, clock, ids["inspect"], level: 2, dueInDays: -1);
        Learn(store, clock, ids["respect"], level: 6, dueInDays: 20);
        Learn(store, clock, ids["perspective"], level: 1, dueInDays: -0.2);
        Learn(store, clock, ids["predict"], level: 4, dueInDays: 3);
        Learn(store, clock, ids["dictionary"], level: 7, dueInDays: 40);
        Learn(store, clock, ids["transport"], level: 5, dueInDays: 6);
        Learn(store, clock, ids["export"], level: 2, dueInDays: 1);
        Learn(store, clock, ids["environment"], level: 1, dueInDays: -0.1, forgets: 4);
        Learn(store, clock, ids["reliable"], level: 3, dueInDays: -2);
        Learn(store, clock, ids["visible"], level: 4, dueInDays: 5);
        Learn(store, clock, ids["comfortable"], level: 6, dueInDays: 25);
        Learn(store, clock, ids["补救"], level: 2, dueInDays: 1);
        Learn(store, clock, ids["认真"], level: 1, dueInDays: -0.3, slips: 3);
        Learn(store, clock, ids["故事"], level: 5, dueInDays: 12);
        store.SetHidden(ids["comfortable"], true);

        // Just added, waiting for the agent.
        clock.Now();
        clock.DaysAgo(0.1);
        store.AddWord("reluctant", "Aptis reading, part 4");
        store.AddWord("sufficient", "Bài nghe Aptis");
        store.AddWord("commute", "Podcast BBC 6 Minute English");
        clock.Now();
        return ids;
    }

    /// <summary>
    /// The word's reviews, so it stands at <paramref name="level"/>, due <paramref name="dueInDays"/> from now: answered on
    /// time at every level up to it, after <paramref name="forgets"/> forgets and <paramref name="slips"/> pinyin slips.
    /// </summary>
    private static void Learn(VocabStore store, DemoClock clock, string id, int level, double dueInDays, int forgets = 0, int slips = 0)
    {
        var at = DateTimeOffset.UtcNow.AddDays(dueInDays);
        var times = new List<DateTimeOffset>();
        for (var l = level; l >= 1; l--)
        {
            // A minute more than the gap: each answer comes on time, whatever the clock's jitter while seeding.
            at -= VocabQuiz.Interval(l) + TimeSpan.FromMinutes(1);
            times.Insert(0, at);
        }
        var start = times[0];
        for (var i = forgets + slips; i >= 1; i--)
        {
            clock.At(start.AddDays(-1.5 * i));
            if (i > slips) store.Record(id, remembered: false);
            else store.Record(id, remembered: true, pinyinSlip: true);
        }
        foreach (var time in times)
        {
            clock.At(time);
            store.Record(id, remembered: true);
        }
        clock.Now();
    }

    /// <summary>Three words added, none broken down yet.</summary>
    private static Dictionary<string, string> SeedWaiting(VocabStore store)
    {
        store.AddWord("reluctant", "Aptis reading, part 4");
        store.AddWord("sufficient", "Bài nghe Aptis");
        store.AddWord("commute", "Podcast BBC 6 Minute English");
        return [];
    }

    /// <summary>A reading text whose words an agent proposed, and one still waiting for it.</summary>
    private static void SeedTexts(VocabStore store)
    {
        var read = store.AddSource("Aptis reading: Getting to work",
            "Every morning, millions of people commute to work in big cities. In Hanoi, many choose motorbikes because buses are slow and crowded.\n" +
            "Traffic congestion is now a serious problem. The city plans to build more metro lines as an alternative to private vehicles.\n" +
            "Experts predict that a reliable public transport system could reduce pollution and make the environment healthier.");
        store.Propose(read,
        [
            new VocabProposal { Word = "commute", Phonetic = "/kəˈmjuːt/", Meaning = "đi lại hằng ngày (giữa nhà và nơi làm)",
                Sentence = "Every morning, millions of people commute to work in big cities.", Reason = "Gặp nhiều trong Aptis phần đọc về đời sống đô thị." },
            new VocabProposal { Word = "congestion", Phonetic = "/kənˈdʒes.tʃən/", Meaning = "sự tắc nghẽn",
                Sentence = "Traffic congestion is now a serious problem.", Reason = "con- (cùng) + gest (mang): xe cùng dồn lại." },
            new VocabProposal { Word = "alternative", Phonetic = "/ɒlˈtɜː.nə.tɪv/", Meaning = "sự lựa chọn khác, thay thế",
                Sentence = "The city plans to build more metro lines as an alternative to private vehicles.", Reason = "Từ B1 hay dùng trong Writing." },
            new VocabProposal { Word = "vehicle", Phonetic = "/ˈvɪə.kəl/", Meaning = "xe cộ, phương tiện", Sentence = "…an alternative to private vehicles.", Chosen = false },
            new VocabProposal { Word = "reliable", Phonetic = "/rɪˈlaɪ.ə.bəl/", Meaning = "đáng tin cậy", Sentence = "…a reliable public transport system…" },
        ]);
        store.AddSource("Bài đọc Aptis: Working from home",
            "More companies now let their staff work from home two days a week. Some workers say they feel more productive; others miss their colleagues.");
    }

    /// <summary>A clock the seed can move back in time (the store stamps words and answers with it).</summary>
    private sealed class DemoClock : TimeProvider
    {
        private TimeSpan _offset;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + _offset;

        public void DaysAgo(double days) => _offset = TimeSpan.FromDays(-days);

        public void At(DateTimeOffset time) => _offset = time - DateTimeOffset.UtcNow;

        public void Now() => _offset = TimeSpan.Zero;
    }

    private sealed class Host : IVocabPageHost
    {
        public void Opened() { }

        public string AddHint => "Several at once with commas. From anywhere: Quick Capture (Win+Alt+N), then /v relative.";

        public Action? ShowWordNow => () => { };
    }
}
