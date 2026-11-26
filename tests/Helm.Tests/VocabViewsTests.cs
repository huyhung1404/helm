using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Helm.Core.Desktop;
using Helm.Core.Settings;
using Helm.Modules.Vocab;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Tests;

/// <summary>
/// The PC corner pop-up card loads with real data, built off-screen: no window is shown. With
/// HELM_VOCAB_SNAPSHOTS set to a folder each one is rendered to a PNG there, for checking the layout by eye. The page itself
/// is the shared Avalonia view, tested in Helm.Ui.Tests (VocabViewTests).
/// </summary>
public sealed class VocabViewsTests
{
    [Fact]
    public void The_pop_up_card_is_see_through_at_rest() => RunSta(() =>
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        var store = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>());
        Seed(store);
        var quiz = new VocabQuiz(store, new Random(1));
        var windows = new WindowService(NullLogger<WindowService>.Instance);

        var card = new VocabCardViewModel(store, quiz);
        var relative = store.FindWord("relative")!;
        card.Show(relative.Id, relative.Value);
        var window = new VocabPopupWindow(card, windows, opacity: 0.6);
        Layout(window, new Size(392, 260), "20-popup");
        Assert.Equal(0.6, window.CardBorder.Opacity, 3);
        card.RevealCommand.Execute(null);
        Layout(window, new Size(392, 460), "21-popup-revealed");
        Assert.Equal("Remembered", System.Windows.Automation.AutomationProperties.GetName(window.RememberedButton));
        Assert.False(Shows(window, "✗ / ✓ answer for the meaning"));
        // Full details is read: solid; closed again, see-through.
        window.ToggleFull();
        Assert.Equal(1, window.CardBorder.Opacity);
        window.ToggleFull();
        Assert.Equal(0.6, window.CardBorder.Opacity, 3);
        // Never below the lowest opacity, whatever the setting.
        window.SetRestOpacity(0.1);
        Assert.Equal(VocabSettingsViewModel.MinCardOpacity / 100.0, window.CardBorder.Opacity, 3);
        window.Close();

        // A Chinese word asks for its pinyin first; the tones slipped: the verdict hint, the pinyin hook, and ✗ / ✓
        // about the meaning.
        var buJiu = store.FindWord("补救")!;
        store.UpdateWord(buJiu.Id, w => w with { PinyinMnemonic = "补 mượn âm của 卜 bǔ, 救 mượn vần của 求 qiú → bǔ jiù." });
        buJiu = store.FindWord("补救")!;
        var zh = new VocabCardViewModel(store, quiz, options: () => new VocabCardOptions(VocabCardMode.WordToMeaning, false, TypePinyin: true));
        zh.Show(buJiu.Id, buJiu.Value);
        Assert.True(zh.IsTyping);
        var zhWindow = new VocabPopupWindow(zh, windows, opacity: 0.85);
        Layout(zhWindow, new Size(392, 300), "22-popup-pinyin");
        zh.Typed = "bu3jiu2";
        zh.CheckCommand.Execute(null);
        Assert.True(zh.PinyinSlipped);
        Layout(zhWindow, new Size(392, 620), "23-popup-slipped");
        Assert.True(Shows(zhWindow, zh.VerdictHint));
        Assert.True(Shows(zhWindow, "Remembering the pinyin"));
        Assert.True(Shows(zhWindow, "✗ / ✓ answer for the meaning"));
        Assert.Equal("Knew the meaning", System.Windows.Automation.AutomationProperties.GetName(zhWindow.RememberedButton));
        Assert.Equal("Forgot the meaning", System.Windows.Automation.AutomationProperties.GetName(zhWindow.ForgotButton));
        zhWindow.ToggleFull();
        Layout(zhWindow, new Size(392, 900), "24-popup-full");
        zhWindow.Close();
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
            Forms =
            [
                new VocabForm { Word = "relation", PartOfSpeech = "danh từ", Meaning = "mối quan hệ" },
                new VocabForm { Word = "relatively", PartOfSpeech = "trạng từ", Meaning = "tương đối" },
            ],
            Synonyms = ["kin", "family member"],
            Antonyms = ["absolute"],
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

    /// <summary>
    /// Some text block under <paramref name="root"/> shows this text (alone, or followed by more runs: "Your words with
    /// it (2)"), and nothing above it is collapsed.
    /// </summary>
    private static bool Shows(DependencyObject root, string text)
    {
        if (root is Window { Content: DependencyObject content }) root = content;
        return Descendants(root).OfType<TextBlock>().Any(t => Says(TextOf(t), text) && Visible(t, root));
    }

    /// <summary>All of a text block's runs ("Text" alone may hold only the first).</summary>
    private static string TextOf(TextBlock block) =>
        block.Inlines.Count > 0 ? new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text : block.Text;

    private static bool Says(string shown, string text)
    {
        shown = string.Join(' ', shown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return shown == text || shown.StartsWith(text + " ", StringComparison.Ordinal);
    }

    private static bool Visible(DependencyObject element, DependencyObject root)
    {
        for (var at = element; at is not null && at != root; at = VisualTreeHelper.GetParent(at))
            if (at is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
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
