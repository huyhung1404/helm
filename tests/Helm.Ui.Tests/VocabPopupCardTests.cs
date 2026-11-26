using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.Vocab;
using Helm.Modules.Vocab.Ui;

namespace Helm.Ui.Tests;

/// <summary>The PC's corner card (VocabPopupCard, shown by VocabPopupWindow) over the Gallery's words, with a host that records what it is asked.</summary>
public sealed class VocabPopupCardTests
{
    private readonly FakeModule _module = new("vocab", "Vocabulary");

    private sealed class Host : IVocabPopupHost
    {
        public List<string> Calls { get; } = [];

        public void Close() => Calls.Add("close");

        public void OpenWord(string id) => Calls.Add("open " + id);

        public void TakeKeyboard() => Calls.Add("take");

        public void ReturnKeyboard() => Calls.Add("return");

        public void Drag(PointerPressedEventArgs e) => Calls.Add("drag");

        public void BackToCorner() => Calls.Add("corner");

        public double FullHeight => 700;
    }

    /// <summary>The demo's card view model at a stage, in a new card with the recording host, in a window.</summary>
    private (Window Window, VocabPopupCard Card, VocabCardViewModel Vm, Host Host) Show(VocabStage stage, double opacity)
    {
        var demo = (Panel)VocabDemo.Create(_module, stage);
        var vm = (VocabCardViewModel)demo.Children.OfType<VocabPopupCard>().Single().DataContext!;
        var host = new Host();
        var card = new VocabPopupCard(vm, host, opacity) { Width = 320 };
        var window = new Window { Content = card, Width = 360, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, card, vm, host);
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Button button) => button.Command?.Execute(button.CommandParameter);

    [AvaloniaFact]
    public void The_card_is_see_through_at_rest_and_solid_in_full()
    {
        var (window, card, vm, _) = Show(VocabStage.CardWord, 0.6);
        Assert.Equal(0.6, card.CardOpacity, 3);
        Assert.True(Named<TextBlock>(card, "TapHint").IsVisible);
        Assert.False(Named<StackPanel>(card, "TypePanel").IsVisible);
        Assert.False(Named<Border>(card, "Bar").IsVisible);
        Assert.True(card.CanGoAway);

        // A click on the word shows the answer and the row of icons; it no longer goes away by itself.
        Named<Button>(card, "WordButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsRevealed);
        Assert.True(Named<Border>(card, "Bar").IsVisible);
        Assert.False(Named<TextBlock>(card, "TapHint").IsVisible);
        Assert.False(Named<TextBlock>(card, "AnswersCaption").IsVisible);
        Assert.Equal("Remembered", AutomationProperties.GetName(Named<Button>(card, "RememberedButton")));
        Assert.False(card.CanGoAway);

        // Full details is read: solid, taller, with "To remember it" first; closed again, see-through.
        card.ToggleFull();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, card.CardOpacity);
        Assert.True(Named<Border>(card, "DetailsPanel").IsVisible);
        Assert.Equal(700, Named<ScrollViewer>(card, "Scroller").MaxHeight);
        Assert.Equal("Less", AutomationProperties.GetName(Named<Button>(card, "FullButton")));
        var sections = Named<ItemsControl>(card, "DetailsList").ItemsSource!.Cast<VocabPopupSection>().ToList();
        Assert.Equal("To remember it", sections[0].Title);
        Assert.Contains(sections, s => s.Title == "How the parts make it");
        card.ToggleFull();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.6, card.CardOpacity, 3);
        Assert.Equal(VocabPopupCard.CompactHeight, Named<ScrollViewer>(card, "Scroller").MaxHeight);

        // Never below the lowest opacity, whatever the setting.
        card.SetRestOpacity(0.1);
        Assert.Equal(VocabPopupCard.MinOpacity, card.CardOpacity, 3);
        window.Close();
    }

    [AvaloniaFact]
    public void Open_in_Helm_opens_the_word_and_closes_and_an_answer_is_saved()
    {
        var (window, card, vm, host) = Show(VocabStage.CardAnswer, 1);
        var id = vm.WordId!;
        Named<Button>(card, "OpenButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(["open " + id, "close"], host.Calls);
        var answered = false;
        vm.Answered += (_, _) => answered = true;
        Click(Named<Button>(card, "RememberedButton"));
        Assert.True(answered);
        window.Close();
    }

    [AvaloniaFact]
    public void The_pinyin_box_takes_the_keyboard_and_gives_it_back()
    {
        var (window, card, vm, host) = Show(VocabStage.CardAsksPinyin, 0.7);
        Assert.True(vm.AsksPinyin);
        Assert.True(Named<StackPanel>(card, "TypePanel").IsVisible);
        Assert.False(Named<TextBlock>(card, "TapHint").IsVisible);

        // A click on the word goes to the box: the card takes the keyboard and is solid; Escape gives it back.
        Named<Button>(card, "WordButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.True(card.IsTyping);
        Assert.Equal(1, card.CardOpacity);
        var box = Named<TextBox>(card, "TypeBox");
        Assert.True(box.IsFocused);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(card.IsTyping);
        Assert.Equal(["take", "return"], host.Calls);

        // Typed with the tones wrong and checked (Enter): the keyboard goes back, the answer shows, both answers are about the meaning.
        Named<Button>(card, "WordButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        box.Focus();
        window.KeyTextInput("bu3jiu2");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["take", "return", "take", "return"], host.Calls);
        Assert.True(vm.IsRevealed);
        Assert.True(vm.PinyinSlipped);
        Assert.True(Named<TextBlock>(card, "AnswersCaption").IsVisible);
        Assert.Equal("Knew the meaning", AutomationProperties.GetName(Named<Button>(card, "RememberedButton")));
        Assert.Equal("Forgot the meaning", AutomationProperties.GetName(Named<Button>(card, "ForgotButton")));
        window.Close();
    }
}
