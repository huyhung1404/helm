using System.Windows;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Modules.Vocab;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The Vocabulary pages and the pop-up card load with real data, built off-screen: no window is shown. With
/// HELM_VOCAB_SNAPSHOTS set to a folder each one is rendered to a PNG there, for checking the layout by eye.
/// </summary>
public sealed class VocabViewsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_pages_and_the_card_load_with_a_word_open() => RunSta(() =>
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        using var settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var store = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>());
        Seed(store);
        var quiz = new VocabQuiz(store, new Random(1));
        var viewModel = VocabKit.ViewModel(store, quiz, settings);
        var windows = new WindowService(NullLogger<WindowService>.Instance);
        var popup = new VocabPopup(store, quiz, settings, windows, new UserActivity(), new InlineUi(), new NoServices(), new VocabVoice(null, null), NullLogger<VocabPopup>.Instance);
        var module = new VocabModule(popup, viewModel.Agent) { IsEnabled = true };

        // Words: only analyzed ones, one language at a time, alphabetical; the waiting word is on the Add tab only.
        Assert.Equal(["relative", "translate"], viewModel.List.Words.Select(w => w.Text));
        Assert.Equal("English (2)", viewModel.List.EnglishTabText);
        viewModel.List.ShowChineseCommand.Execute(null);
        Assert.Equal(["补救"], viewModel.List.Words.Select(w => w.Text));
        viewModel.List.ShowEnglishCommand.Execute(null);
        var waiting = Assert.Single(viewModel.RecentWords, w => w.IsPending);
        Assert.Equal("ubiquitous", waiting.Text);
        Assert.True(waiting.CanRemove);

        var page = new VocabContentPage(module, viewModel);
        Assert.True(viewModel.IsLearning);
        Layout(page, new Size(1100, 760), "1-learn");

        // Adding stays on the Add tab; the new word heads "Recently added", waiting for the agent.
        viewModel.ShowAddCommand.Execute(null);
        viewModel.NewWord = "serendipity";
        viewModel.AddCommand.Execute(null);
        Assert.True(viewModel.IsAdding);
        Assert.Equal("serendipity", viewModel.RecentWords[0].Text);
        Assert.True(viewModel.RecentWords[0].IsPending);
        Layout(page, new Size(1100, 760), "1b-add");
        viewModel.RecentWords.First(w => w.Text == "serendipity").Remove!.Execute(null);
        Assert.Null(store.FindWord("serendipity"));
        viewModel.ShowLearnCommand.Execute(null);
        var buJiu = store.FindWord("补救")!;
        viewModel.Card.Show(buJiu.Id, buJiu.Value);
        viewModel.Card.RevealCommand.Execute(null);
        Assert.NotNull(viewModel.Card.ExplanationTree);
        Layout(page, new Size(1100, 1150), "2-learn-revealed");

        // The rich answer: examples, collocations, mnemonic, words easy to mix up, labels.
        var relativeWord = store.FindWord("relative")!;
        viewModel.Card.Show(relativeWord.Id, relativeWord.Value);
        viewModel.Card.RevealCommand.Execute(null);
        Assert.True(viewModel.Card.Info!.HasExamples);
        Assert.True(viewModel.Card.Info.HasConfusables);
        Layout(page, new Size(1100, 1250), "2b-learn-rich");

        // Roots: the families the words share; lat holds relative and translate.
        viewModel.ShowRootsCommand.Execute(null);
        var latRow = Assert.Single(viewModel.Roots.Roots, r => r.Form == "lat");
        Assert.Equal(2, latRow.Words.Count);
        Layout(page, new Size(1100, 760), "8-roots");
        latRow.Select.Execute(null);
        Assert.True(viewModel.Roots.HasSelected);
        Layout(page, new Size(1100, 760), "8b-root-family");

        viewModel.Open(store.FindWord("补救")!.Id);
        Assert.False(viewModel.IsLearning);
        Assert.Equal(["补", "救"], viewModel.Detail!.Root.Children.Select(c => c.Node.Form));
        var jiu = viewModel.Detail.Root.Children[1].Children[0];
        jiu.SelectCommand.Execute(null);
        Assert.Equal("求", viewModel.Detail.CardTitle);
        Layout(page, new Size(1100, 900), "3-chinese-word");

        viewModel.Open(store.FindWord("relative")!.Id);
        var lat = viewModel.Detail!.Root.Children[0].Children[1];
        lat.SelectCommand.Execute(null);
        Assert.Equal(["translate"], viewModel.Detail.AlsoIn.Select(w => w.Text));
        Layout(page, new Size(1100, 900), "4-english-word");

        // A change arriving (an answer from another device) refreshes the open word but leaves the Learn tab in front.
        viewModel.ShowLearnCommand.Execute(null);
        store.Record(store.FindWord("translate")!.Id, remembered: true);
        Assert.True(viewModel.IsLearning);
        Assert.NotNull(viewModel.Detail);

        Layout(new VocabPage(module, viewModel, popup, new NoNavigation()), new Size(900, 900), "5-settings");

        var card = new VocabCardViewModel(store, quiz);
        card.ShowNext();
        var window = new VocabPopupWindow(card, windows);
        Layout(window, new Size(352, 260), "6-popup");
        card.RevealCommand.Execute(null);
        Layout(window, new Size(352, 420), "7-popup-revealed");
        window.Close();
    });

    private static void Seed(VocabStore store)
    {
        var (relative, _) = store.AddWord("relative", "Aptis reading, part 4");
        store.SaveAnalysis(relative, new WordAnalysis
        {
            Phonetic = "/ˈrel.ə.tɪv/",
            Meaning = "họ hàng; tương đối",
            Explanation = "\"mang trở lại\" → có liên quan → người có quan hệ với mình.",
            Examples = [new VocabExample { Sentence = "She is staying with relatives in London.", Translation = "Cô ấy đang ở nhà họ hàng ở London." }],
            Collocations = ["close relative", "relative to"],
            Mnemonic = "re (lại) + lat (mang): người \"mang lại\" liên hệ → họ hàng.",
            Confusables = [new VocabConfusable { Word = "relevant", Note = "relevant = liên quan; relative = họ hàng / tương đối" }],
            Level = "B1",
            PartOfSpeech = "danh từ; tính từ",
            Topic = "gia đình",
            Parts =
            [
                new AnalysisPart
                {
                    Form = "relate", Kind = VocabPartKind.Word, Meaning = "liên hệ, kể lại",
                    Parts =
                    [
                        new AnalysisPart { Form = "re-", Kind = VocabPartKind.Prefix, Meaning = "lại, trở lại" },
                        new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "mang", Origin = "Latin latus, quá khứ phân từ của ferre \"mang\" (fer = lat)" },
                    ],
                },
                new AnalysisPart { Form = "-ive", Kind = VocabPartKind.Suffix, Meaning = "có tính chất" },
            ],
        });
        var (translate, _) = store.AddWord("translate");
        store.SaveAnalysis(translate, new WordAnalysis
        {
            Phonetic = "/trænzˈleɪt/",
            Meaning = "dịch",
            Parts =
            [
                new AnalysisPart { Form = "trans-", Kind = VocabPartKind.Prefix, Meaning = "qua, sang" },
                new AnalysisPart { Form = "lat", Kind = VocabPartKind.Root, Meaning = "mang" },
                new AnalysisPart { Form = "-ate", Kind = VocabPartKind.Suffix, Meaning = "làm, khiến" },
            ],
        });
        var (word, _) = store.AddWord("补救");
        store.SaveAnalysis(word, new WordAnalysis
        {
            Phonetic = "bǔjiù",
            Meaning = "cứu vãn, bù đắp",
            HanViet = "bổ cứu",
            Traditional = "補救",
            Explanation = "vá chỗ hỏng (补) + cứu (救) → cứu vãn.",
            Parts =
            [
                new AnalysisPart
                {
                    Form = "补", Kind = VocabPartKind.Character, Meaning = "vá, bù", Phonetic = "bǔ", HanViet = "bổ", Traditional = "補",
                    Parts =
                    [
                        new AnalysisPart { Form = "衤", Kind = VocabPartKind.Component, Role = VocabPartRole.Meaning, Meaning = "áo", HanViet = "y" },
                        new AnalysisPart { Form = "卜", Kind = VocabPartKind.Component, Role = VocabPartRole.Sound, Meaning = "bói", HanViet = "bốc" },
                    ],
                },
                new AnalysisPart
                {
                    Form = "救", Kind = VocabPartKind.Character, Meaning = "cứu", Phonetic = "jiù", HanViet = "cứu",
                    Parts =
                    [
                        new AnalysisPart
                        {
                            Form = "求", Kind = VocabPartKind.Component, Role = VocabPartRole.Sound, Meaning = "cầu xin", Phonetic = "qiú", HanViet = "cầu",
                            Parts =
                            [
                                new AnalysisPart { Form = "一", Kind = VocabPartKind.Component, Meaning = "một", HanViet = "nhất" },
                                new AnalysisPart { Form = "氺", Kind = VocabPartKind.Component, Meaning = "nước", HanViet = "thủy" },
                                new AnalysisPart { Form = "丶", Kind = VocabPartKind.Component, Meaning = "chấm", HanViet = "chủ" },
                            ],
                        },
                        new AnalysisPart { Form = "攵", Kind = VocabPartKind.Component, Role = VocabPartRole.Meaning, Meaning = "đánh, thúc", HanViet = "phộc" },
                    ],
                },
            ],
        });
        store.AddWord("ubiquitous");
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoNavigation : Helm.Core.Services.IShellNavigation
    {
        public void ShowPage(Type pageType) { }
    }

    private static void Layout(FrameworkElement element, Size size, string name)
    {
        // A window that is never shown does not lay out; its content does.
        var target = element is Window { Content: FrameworkElement content } ? content : element;
        target.Measure(size);
        target.Arrange(new Rect(size));
        target.UpdateLayout();
        var idle = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => idle.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(idle);
        target.UpdateLayout();
        if (Environment.GetEnvironmentVariable("HELM_VOCAB_SNAPSHOTS") is not { Length: > 0 } folder) return;
        var host = new System.Windows.Controls.Border
        {
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("ApplicationBackgroundBrush"),
            Width = size.Width,
            Height = size.Height,
        };
        host.Measure(size);
        host.Arrange(new Rect(size));
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Render(target);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, $"{name}.png"));
        encoder.Save(file);
    }

    // Every view test shares one WPF Application (a process may have only one).
    private static void RunSta(Action body) => WpfHost.Run(body);
}
