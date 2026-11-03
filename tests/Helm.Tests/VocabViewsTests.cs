using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    public void The_page_shows_every_tab_and_dialog() => RunSta(() =>
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
        var size = new Size(1280, 900);

        // Words: only analyzed ones, one language at a time, alphabetical; the waiting word is in Add words only.
        Assert.Equal(["relative", "translate"], viewModel.List.Words.Select(w => w.Text));
        viewModel.List.ShowChineseCommand.Execute(null);
        Assert.Equal(["补救"], viewModel.List.Words.Select(w => w.Text));
        viewModel.List.ShowEnglishCommand.Execute(null);
        var waiting = Assert.Single(viewModel.RecentWords, w => w.IsPending);
        Assert.Equal("ubiquitous", waiting.Text);
        Assert.True(waiting.CanRemove);

        // Learn: the card, Today, the kind of card and the keys; the tabs show their counts.
        var page = new VocabContentPage(module, viewModel, new NoNavigation());
        Assert.True(viewModel.IsLearning);
        var relative = store.FindWord("relative")!;
        viewModel.Card.Show(relative.Id, relative.Value);
        Layout(page, size, "01-learn");
        Assert.True(Shows(page, "Today"));
        Assert.True(Shows(page, "Show the answer"));
        Assert.True(Shows(page, "Word → meaning"));
        Assert.False(Shows(page, "Knew the meaning"));

        // Add words: a dialog over the page; the new word heads "Added recently", waiting, with ✕.
        viewModel.OpenAddCommand.Execute(null);
        viewModel.NewWord = "serendipity";
        viewModel.NewNote = "a novel, chapter 3";
        viewModel.AddCommand.Execute(null);
        Assert.True(viewModel.IsAddOpen);
        Assert.Equal("serendipity", viewModel.RecentWords[0].Text);
        Assert.True(viewModel.RecentWords[0].IsPending);
        Layout(page, size, "02-add");
        Assert.True(Shows(page, "Added recently"));
        Assert.True(Shows(page, "Waiting"));
        viewModel.RecentWords.First(w => w.Text == "serendipity").Remove!.Execute(null);
        Assert.Null(store.FindWord("serendipity"));
        viewModel.CloseAddCommand.Execute(null);

        // Analysis: what waits for Claude Code; the chip says how many.
        Assert.Equal("1 waiting for analysis", viewModel.AgentChipText);
        Assert.True(viewModel.IsAgentWaiting);
        viewModel.OpenAgentCommand.Execute(null);
        Layout(page, size, "03-analysis");
        // The last run's Activity: each step, result, Claude's own words and how it ended.
        var at = DateTimeOffset.Now;
        foreach (var (text, kind) in new[]
        {
            ("Analyzing with Sonnet (recommended) · 3 words waiting", VocabLogKind.Start),
            ("“I'll break down the 3 words, then the follow-ups.”", VocabLogKind.Note),
            ("Reading the words waiting to be broken down", VocabLogKind.Step),
            ("✓ 3 waiting", VocabLogKind.Result),
            ("Looking up parts: re, lat, -ive, luct and 2 more", VocabLogKind.Step),
            ("Saving 3 analyses: relative, 补救, reluctant", VocabLogKind.Step),
            ("✓ 2 saved · 1 to fix: reluctant (The parts of reluctant must be re- + luct + -ant.)", VocabLogKind.Result),
            ("✗ words must be a list.", VocabLogKind.Problem),
            ("Done in 1 min 12 s · 4 steps. Broke down 3 words.", VocabLogKind.End),
        })
        {
            viewModel.Agent.Log.Add(new VocabAgentLogLine(at, text, kind));
            at = at.AddSeconds(9);
        }
        Assert.True(viewModel.Agent.HasLog);
        Assert.True(Shows(page, "Activity"));
        Layout(page, size, "03b-analysis-log");
        Assert.True(Shows(page, "Waiting to be broken down (1)"));
        Assert.True(Shows(page, "ubiquitous"));
        viewModel.CloseAgentCommand.Execute(null);

        // The rich answer: the tree beside examples, collocations, mnemonic, words easy to mix up; Forgot / Remembered below.
        viewModel.Card.RevealCommand.Execute(null);
        Assert.NotNull(viewModel.Card.ExplanationTree);
        Layout(page, size, "04-learn-revealed");
        Assert.True(Shows(page, "How it's built"));
        // A narrower window: the side panel folds into a line above the card, the tree goes above what helps.
        Layout(page, new Size(640, 900), "04b-learn-revealed-narrow");
        Assert.Equal(Visibility.Visible, page.LearnView.NarrowBar.Visibility);
        Assert.Equal(Visibility.Collapsed, page.LearnView.SidePanel.Visibility);
        Assert.Equal(1, Grid.GetRow(page.LearnView.InfoPart));
        Layout(page, size, "04-learn-revealed");
        Assert.Equal(Visibility.Collapsed, page.LearnView.NarrowBar.Visibility);
        Assert.Equal(0, Grid.GetRow(page.LearnView.InfoPart));
        // The side sections fold away and stay so: the keys start folded; Today folded keeps its numbers in its title.
        Assert.Equal(Visibility.Collapsed, page.LearnView.KeysBody.Visibility);
        page.LearnView.TodayFold.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(Visibility.Collapsed, page.LearnView.TodayBody.Visibility);
        Assert.Equal(Visibility.Visible, page.LearnView.TodayLine.Visibility);
        Assert.True(viewModel.Settings.IsFolded("today"));
        Layout(page, size, "04c-learn-folded");
        page.LearnView.TodayFold.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(Visibility.Visible, page.LearnView.TodayBody.Visibility);
        Assert.True(Shows(page, "Remembered"));
        Assert.True(Shows(page, viewModel.Card.RememberedHint));

        // Typing the word: checked letter by letter.
        viewModel.Settings.CardModeIndex = (int)VocabCardMode.Type;
        viewModel.Card.Show(relative.Id, relative.Value);
        Assert.True(viewModel.Card.IsTyping);
        viewModel.Card.Typed = "relitive";
        viewModel.Card.CheckCommand.Execute(null);
        Assert.True(viewModel.Card.AnswerIsClose);
        Layout(page, size, "05-type-checked");
        viewModel.Settings.CardModeIndex = 0;

        // A Chinese word asks for its pinyin; the tones slipped, the meaning was known: the buttons ask about the
        // meaning, Remembered keeps the level ("pinyin slip · back tomorrow"), the pinyin hook shows.
        var buJiu = store.FindWord("补救")!;
        for (var i = 0; i < VocabQuiz.PinyinSlips; i++) store.Record(buJiu.Id, remembered: true, pinyinSlip: true);
        store.UpdateWord(buJiu.Id, w => w with { PinyinMnemonic = "补 mượn âm của 卜 bǔ, 救 mượn vần của 求 qiú → bǔ jiù." });
        buJiu = store.FindWord("补救")!;
        viewModel.Card.Show(buJiu.Id, buJiu.Value);
        Assert.True(viewModel.Card.IsTyping);
        Assert.True(viewModel.Card.IsSlipping);
        Assert.Equal("Type its pinyin", viewModel.Card.KindText);
        Layout(page, size, "06-pinyin");
        viewModel.Card.Typed = "bu3jiu2";
        viewModel.Card.CheckCommand.Execute(null);
        Assert.True(viewModel.Card.PinyinSlipped);
        Assert.Equal("Knew the meaning", viewModel.Card.RememberedText);
        Assert.StartsWith("pinyin slip", viewModel.Card.RememberedHint);
        Layout(page, size, "07-pinyin-slipped");
        Assert.True(Shows(page, "Pinyin slips"));
        Assert.True(Shows(page, "Remembering the pinyin"));
        Assert.True(Shows(page, viewModel.Card.VerdictHint));
        Assert.True(Shows(page, "Knew the meaning"));
        Assert.True(Shows(page, "Forgot the meaning"));

        // Words: the list with its chips (each with its count); a chip narrows it.
        viewModel.ShowWordsCommand.Execute(null);
        Assert.Equal("All words", viewModel.List.Chips[0].Name);
        Assert.True(viewModel.List.Chips[0].IsOn);
        Layout(page, size, "08-words");
        Assert.True(Shows(page, "How well you know it"));
        Assert.True(Shows(page, "relative"));
        var newChip = viewModel.List.Chips.First(c => c.Name == "New");
        newChip.Pick.Execute(null);
        Assert.True(viewModel.List.Chips.First(c => c.Name == "New").IsOn);
        Assert.Equal(newChip.Count, viewModel.List.Words.Count);
        viewModel.List.Chips[0].Pick.Execute(null);

        // A Chinese word's page: its labels (Pinyin slips among them), dots, the pinyin hook first, how it is built.
        viewModel.Open(buJiu.Id);
        Assert.True(viewModel.IsWordsTab);
        Assert.Equal(["补", "救"], viewModel.Detail!.Root.Children.Select(c => c.Node.Form));
        var jiu = viewModel.Detail.Root.Children[1].Children[0];
        jiu.SelectCommand.Execute(null);
        Assert.Equal("求", viewModel.Detail.CardTitle);
        Layout(page, size, "09-chinese-word");
        Assert.True(viewModel.Detail.IsSlipping);
        Assert.Equal(VocabQuiz.TopLevel, viewModel.Detail.Dots.Count);
        Assert.True(Shows(page, "Remembering the pinyin"));
        Assert.True(Shows(page, "Pinyin slips"));

        // An English word's page: labels, the family of a part.
        store.UpdateWord(relative.Id, w => w with { Tags = ["Aptis"] });
        viewModel.Open(relative.Id);
        var lat = viewModel.Detail!.Root.Children[0].Children[1];
        lat.SelectCommand.Execute(null);
        Assert.Equal(["translate"], viewModel.Detail.AlsoIn.Select(w => w.Text));
        Assert.True(viewModel.HasNextWord);
        Assert.Equal("1 / 2", viewModel.WordPosition);
        Layout(page, size, "10-english-word");
        Assert.False(viewModel.Detail!.IsSlipping);
        Assert.True(Shows(page, "B1"));
        Assert.True(Shows(page, "#Aptis"));
        Assert.True(Shows(page, "Also in your words"));

        // Edit: a dialog with the word's fields; the pinyin hook only for a Chinese word.
        viewModel.Detail.Editor.BeginCommand.Execute(null);
        Assert.False(viewModel.Detail.Editor.IsChinese);
        Layout(page, size, "11-edit");
        Assert.True(Shows(page, "Help to remember and use it"));
        Assert.False(Shows(page, "To remember the pinyin"));
        viewModel.Detail.Editor.CancelCommand.Execute(null);
        viewModel.Open(buJiu.Id);
        viewModel.Detail!.Editor.BeginCommand.Execute(null);
        Assert.True(viewModel.Detail.Editor.IsChinese);
        Layout(page, size, "11b-edit-chinese");
        Assert.True(Shows(page, "To remember the pinyin"));
        viewModel.Detail.Editor.PinyinMnemonic = "Thanh 3 rồi 4.";
        viewModel.Detail.Editor.SaveCommand.Execute(null);
        Assert.Equal("Thanh 3 rồi 4.", store.FindWord("补救")!.Value.PinyinMnemonic);

        // Ask for a new analysis: a dialog with the note.
        viewModel.Detail!.BeginRedoCommand.Execute(null);
        Assert.True(viewModel.Detail.IsAskingRedo);
        Layout(page, size, "12-redo");
        Assert.True(Shows(page, "What should change?"));
        viewModel.Detail.BeginRedoCommand.Execute(null);

        // Roots: family cards in groups; a family takes the tab and walks the list; a root practised alone.
        viewModel.ShowRootsCommand.Execute(null);
        var latRow = Assert.Single(viewModel.Roots.Roots, r => r.Form == "lat");
        Assert.Equal(2, latRow.Words.Count);
        Layout(page, size, "13-roots");
        Assert.True(Shows(page, VocabRootGroups.Families));
        Assert.True(Shows(page, VocabRootGroups.Single));
        viewModel.Roots.NextRootCommand.Execute(null);
        viewModel.Roots.RevealRootCommand.Execute(null);
        Layout(page, size, "14-root-practice");
        Assert.True(Shows(page, "Its family in your words"));
        Assert.False(Shows(page, VocabRootGroups.Families));
        viewModel.Roots.ClosePracticeCommand.Execute(null);
        latRow.Select.Execute(null);
        Assert.False(viewModel.Roots.ShowsList);
        Assert.Equal($"1 / {viewModel.Roots.Roots.Count}", viewModel.Roots.RootPosition);
        viewModel.Roots.NextFamilyCommand.Execute(null);
        Assert.NotEqual("lat", viewModel.Roots.Selected!.Form);
        viewModel.Roots.PreviousFamilyCommand.Execute(null);
        Assert.Equal("lat", viewModel.Roots.Selected!.Form);
        Layout(page, size, "15-root-family");
        Assert.True(Shows(page, "Your words with it"));

        // A change arriving (an answer from another device) refreshes the open word but leaves the tab in front.
        viewModel.ShowLearnCommand.Execute(null);
        store.Record(store.FindWord("translate")!.Id, remembered: true);
        Assert.True(viewModel.IsLearning);
        Assert.NotNull(viewModel.Detail);

        // Delete… asks first, over the page; Delete there, and the word goes.
        viewModel.Open(relative.Id);
        page.DeleteDialog.Open();
        Layout(page, size, "12b-delete");
        Assert.True(Shows(page, "Delete “relative”?"));
        Descendants(page.DeleteDialog).OfType<Button>().Single(b => Equals(b.Content, "Delete"))
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.False(page.DeleteDialog.IsOpen);
        Assert.Null(store.FindWord("relative"));
        Assert.False(viewModel.HasDetail);

        // The settings page: the pop-up card's opacity.
        Layout(new VocabPage(module, viewModel, popup, new NoNavigation()), new Size(900, 1100), "16-settings");
        viewModel.Settings.CardOpacity = 60;
        Assert.Equal(60, settings.Get<VocabSettings>(VocabIds.ModuleId).Current.CardOpacity);
    });

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

    /// <summary>
    /// The design canvas's states (Main.dc.html at 1280 × 960, the word page also at 1060 and 1010, the width the page
    /// gets in a 1060 px window, and in the light theme): nothing on the word page is wider than the page.
    /// </summary>
    [Fact]
    public void The_page_lays_out_the_canvas_states_without_clipping() => RunSta(() =>
    {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        using var settings = new SettingsStoreFactory(new HelmPaths(_dir.Path));
        var store = new VocabStore(new MemorySynced<VocabWord>(), new MemorySynced<VocabPart>(), new MemorySyncedLog<VocabReview>());
        Seed(store);
        SeedCanvas(store);
        var quiz = new VocabQuiz(store, new Random(1));
        var viewModel = VocabKit.ViewModel(store, quiz, settings);
        var windows = new WindowService(NullLogger<WindowService>.Instance);
        var popup = new VocabPopup(store, quiz, settings, windows, new UserActivity(), new InlineUi(), new NoServices(), new VocabVoice(null, null), NullLogger<VocabPopup>.Instance);
        var page = new VocabContentPage(new VocabModule(popup, viewModel.Agent) { IsEnabled = true }, viewModel, new NoNavigation());
        var size = new Size(1280, 960);
        var relative = store.FindWord("relative")!;
        var buJiu = store.FindWord("补救")!;

        // Learn: the front, the answer with a part picked, a pinyin card typed with its tones slipped.
        viewModel.Card.Show(relative.Id, relative.Value);
        Layout(page, size, "c01-learn");
        viewModel.Card.RevealCommand.Execute(null);
        viewModel.Card.ExplanationTree!.Root.Children[0].Children[1].SelectCommand.Execute(null);
        Layout(page, size, "c02-revealed");
        Layout(page, new Size(1060, 960), "c02b-revealed-1060");
        Assert.Equal(Visibility.Visible, page.LearnView.SidePanel.Visibility);
        viewModel.Settings.TypePinyin = true;
        viewModel.Card.Show(buJiu.Id, buJiu.Value);
        Layout(page, size, "c03a-pinyin-front");
        Assert.True(Shows(page, "No tones is fine; tones typed are checked."));
        viewModel.Card.Typed = "bu3jiu2";
        viewModel.Card.CheckCommand.Execute(null);
        Layout(page, size, "c03-pinyin");
        Assert.True(Shows(page, "Pinyin “bu3jiu2”: " + viewModel.Card.PinyinVerdictText));

        // Words, a word's page (at the canvas's width, Anh's window and the page it leaves), the Chinese word's.
        viewModel.ShowWordsCommand.Execute(null);
        Layout(page, size, "c04-words");
        viewModel.Open(relative.Id);
        viewModel.Detail!.Root.Children[0].Children[1].SelectCommand.Execute(null);
        foreach (var (width, name) in new[] { (1280.0, "c05-word"), (1060.0, "c05b-word-1060"), (1010.0, "c05c-word-1010") })
        {
            Layout(page, new Size(width, 960), name);
            Assert.True(page.WordView.DetailPanel.ActualWidth <= width - 64, $"{name}: the word page is wider than the page");
        }
        Assert.True(Shows(page, "To remember it"));
        viewModel.Open(buJiu.Id);
        Layout(page, size, "c06-word-chinese");

        // The dialogs: Edit, Add words, Analysis, Ask for a new analysis, Delete.
        viewModel.Open(relative.Id);
        viewModel.Detail!.Editor.BeginCommand.Execute(null);
        Layout(page, size, "c07-edit");
        Assert.True(Shows(page, "Aptis"));
        viewModel.Detail.Editor.CancelCommand.Execute(null);
        viewModel.OpenAddCommand.Execute(null);
        viewModel.NewWord = "vulnerable";
        Layout(page, size, "c08-add");
        viewModel.CloseAddCommand.Execute(null);
        viewModel.OpenAgentCommand.Execute(null);
        Layout(page, size, "c09-agent");
        viewModel.CloseAgentCommand.Execute(null);
        viewModel.Detail!.BeginRedoCommand.Execute(null);
        Layout(page, size, "c10-redo");
        viewModel.Detail.BeginRedoCommand.Execute(null);
        page.DeleteDialog.Open();
        Layout(page, size, "c11-delete");
        page.DeleteDialog.Close();

        // Roots, a family, a root practised.
        viewModel.ShowRootsCommand.Execute(null);
        Layout(page, size, "c12-roots");
        viewModel.Roots.Roots.First(r => r.Form == "lat").Select.Execute(null);
        Layout(page, size, "c13-family");
        viewModel.Roots.CloseSelectedCommand.Execute(null);
        viewModel.Roots.NextRootCommand.Execute(null);
        viewModel.Roots.RevealRootCommand.Execute(null);
        Layout(page, size, "c14-practice");
        viewModel.Roots.ClosePracticeCommand.Execute(null);

        // The light theme: the palette follows (a page built in it: one off-screen is not told of the change).
        viewModel.Open(relative.Id);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light, updateAccent: false);
        try
        {
            VocabPalette.Ensure();
            Assert.Equal(System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF5), ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("VocabInset")).Color);
            var light = new VocabContentPage(new VocabModule(popup, viewModel.Agent) { IsEnabled = true }, viewModel, new NoNavigation());
            Layout(light, size, "c15-word-light");
        }
        finally
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark, updateAccent: false);
            VocabPalette.Ensure();
        }
        Assert.Equal(System.Windows.Media.Color.FromRgb(0x32, 0x32, 0x32), ((System.Windows.Media.SolidColorBrush)Application.Current.FindResource("VocabInset")).Color);

        // The pop-up card: the pinyin asked, typed (tones slipped), an English word, Full details.
        var zh = new VocabCardViewModel(store, quiz, options: () => new VocabCardOptions(VocabCardMode.WordToMeaning, false, TypePinyin: true));
        zh.Show(buJiu.Id, buJiu.Value);
        var zhWindow = new VocabPopupWindow(zh, windows, opacity: 1);
        Layout(zhWindow, new Size(352, 320), "c20-popup-pinyin");
        zh.Typed = "bu3jiu2";
        zh.CheckCommand.Execute(null);
        Layout(zhWindow, new Size(352, 560), "c21-popup-typed");
        zhWindow.ToggleFull();
        Layout(zhWindow, new Size(352, 900), "c23-popup-full");
        Assert.True(Shows(zhWindow, "To remember it"));
        zhWindow.Close();
        var card = new VocabCardViewModel(store, quiz);
        card.Show(relative.Id, relative.Value);
        var window = new VocabPopupWindow(card, windows, opacity: 1);
        Layout(window, new Size(352, 240), "c22-popup-word");
        window.Close();
    });

    /// <summary>The canvas's own data on top of <see cref="Seed"/>: relative's whole page, 补救's pinyin hook and slips, more words and tags.</summary>
    private static void SeedCanvas(VocabStore store)
    {
        var relative = store.FindWord("relative")!;
        store.UpdateWord(relative.Id, w => w with
        {
            Meaning = "người thân, họ hàng; tương đối",
            Explanation = "relate (liên quan) + -ive (có tính chất) → “có liên quan”: người có quan hệ ruột thịt với mình, hoặc xét trong tương quan.",
            Note = "Aptis reading, part 2",
            PartOfSpeech = "noun, adjective",
            Topic = "Family",
            Tags = ["Aptis"],
            Examples =
            [
                new VocabExample { Sentence = "All my relatives came to the wedding.", Translation = "Tất cả họ hàng của tôi đều đến dự đám cưới." },
                new VocabExample { Sentence = "Prices here are relatively low.", Translation = "Giá ở đây tương đối thấp." },
            ],
            Collocations = ["close relative", "distant relative", "relative to"],
            Mnemonic = "RE + LAT: những người “mang” (lat) chung dòng máu, Tết nào cũng gặp lại (re-).",
            Confusables = [new VocabConfusable { Word = "relevant", Note = "liên quan đến việc đang bàn, không phải người thân" }],
            Synonyms = ["kin", "family member"],
            Antonyms = ["absolute (adj.)"],
        });
        for (var i = 0; i < 3; i++) store.Record(relative.Id, remembered: true);
        var buJiu = store.FindWord("补救")!;
        store.UpdateWord(buJiu.Id, w => w with
        {
            Meaning = "cứu vãn, bù đắp (sai sót)",
            Level = "HSK6",
            PartOfSpeech = "verb",
            Topic = "Work",
            Examples =
            [
                new VocabExample { Sentence = "现在补救还来得及。", Translation = "Bây giờ cứu vãn vẫn còn kịp." },
                new VocabExample { Sentence = "我们必须采取补救措施。", Translation = "Chúng ta phải có biện pháp khắc phục." },
            ],
            Collocations = ["补救措施", "无法补救"],
            Mnemonic = "补: áo (衤) rách thì vá. 救: cầm gậy (攵) đi cứu.",
            PinyinMnemonic = "补 mượn âm của 卜 bǔ, 救 mượn vần của 求 qiú → bǔ jiù. Thanh 3 rồi 4: “bủ?” hỏi xuống rồi lên, rồi “cứu!” dứt khoát đi xuống.",
            Confusables = [new VocabConfusable { Word = "挽回", Note = "lấy lại thứ đã mất (danh dự, khách hàng); 补救 là sửa sai sót" }],
            Synonyms = ["挽回", "弥补"],
        });
        for (var i = 0; i < VocabQuiz.PinyinSlips; i++) store.Record(buJiu.Id, remembered: true, pinyinSlip: true);
        var (reluctant, _) = store.AddWord("reluctant");
        store.SaveAnalysis(reluctant, new WordAnalysis
        {
            Phonetic = "/rɪˈlʌk.tənt/",
            Meaning = "miễn cưỡng, ngần ngại",
            Level = "B2",
            Examples = [new VocabExample { Sentence = "She was reluctant to admit her mistake.", Translation = "Cô ấy ngần ngại thừa nhận lỗi của mình." }],
            Parts =
            [
                new AnalysisPart { Form = "re-", Kind = VocabPartKind.Prefix, Meaning = "lại, trở lại" },
                new AnalysisPart { Form = "luct", Kind = VocabPartKind.Root, Meaning = "vật lộn" },
                new AnalysisPart { Form = "-ant", Kind = VocabPartKind.Suffix, Meaning = "đang…" },
            ],
        });
        for (var i = 0; i < VocabQuiz.HardForgets; i++) store.Record(reluctant, remembered: false);
        store.AddWord("vulnerable", "Novel, chapter 12");
    }

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
