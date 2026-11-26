using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.Vocab;
using Helm.Modules.Vocab.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Vocabulary page (Helm.Modules.Vocab.UI) with its real view models over the Gallery's in-memory data.</summary>
public sealed class VocabViewTests : IDisposable
{
    private readonly FakeModule _module = new("vocab", "Vocabulary");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private (Window Window, VocabView View, VocabViewModel Vm) Show(VocabStage stage, double width = 1200)
    {
        var view = (VocabView)VocabDemo.Create(_module, stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, (VocabViewModel)view.DataContext!);
    }

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    [AvaloniaFact]
    public void The_demo_words_stand_at_their_levels()
    {
        var (window, _, vm) = Show(VocabStage.Learn);
        Assert.Equal("relative", vm.Card.Text);
        Assert.StartsWith("Level 3/8", vm.Card.LevelText);
        Assert.True(vm.Today.Due > 0);
        Assert.Equal(3, vm.PendingCount);
        window.Close();
    }

    [AvaloniaFact]
    public void The_answer_bar_asks_then_answers_and_the_next_card_comes()
    {
        var (window, view, vm) = Show(VocabStage.Learn);
        var bar = Named<VocabAnswerBar>(view, "AnswerBar");
        Assert.True(bar.IsVisible);
        Assert.Single(Shown(window, "Show the answer"));

        vm.Card.RevealCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(Shown(window, "Show the answer"));
        Assert.Single(Shown(window, "Remembered"));
        Assert.Single(Shown(window, "How it’s built"));

        vm.Card.RememberedCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.Card.AnsweredCount);
        Assert.False(vm.Card.IsRevealed);
        Assert.NotEqual("relative", vm.Card.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Space_shows_the_answer_and_2_remembers_it()
    {
        var (window, _, vm) = Show(VocabStage.Learn);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.Card.IsRevealed);
        window.KeyPressQwerty(PhysicalKey.Digit2, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.Card.RememberedCount);
        window.Close();
    }

    [AvaloniaFact]
    public void Each_tab_replaces_the_page_and_a_word_takes_the_whole_tab()
    {
        var (window, view, vm) = Show(VocabStage.Learn);
        Named<RadioButton>(view, "WordsTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(VocabTab.Words, vm.Tab);
        Assert.True(Named<VocabWordsPanel>(view, "WordsPanel").IsVisible);
        Assert.False(Named<VocabLearnPanel>(view, "LearnPanel").IsVisible);
        Assert.False(Named<VocabAnswerBar>(view, "AnswerBar").IsVisible);

        vm.List.Words.First(w => w.Text == "relative").Open.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<VocabWordPage>(view, "WordPage").IsVisible);
        Assert.False(Named<VocabWordsPanel>(view, "WordsPanel").IsVisible);
        Assert.Single(Shown(window, "All words"));

        // Esc goes back to the list.
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Detail);
        Assert.True(Named<VocabWordsPanel>(view, "WordsPanel").IsVisible);

        Named<RadioButton>(view, "RootsTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<VocabRootsPanel>(view, "RootsPanel").IsVisible);
        Assert.Single(Shown(window, "FAMILIES"));
        window.Close();
    }

    [AvaloniaFact]
    public void Opening_a_word_from_the_card_switches_the_tabs_too()
    {
        var (window, view, vm) = Show(VocabStage.Learn);
        vm.Open(vm.Card.WordId!);
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<RadioButton>(view, "WordsTab").IsChecked);
        Assert.True(Named<VocabWordPage>(view, "WordPage").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void The_analysis_sheet_follows_the_view_model_and_Back_closes_it()
    {
        var (window, view, vm) = Show(VocabStage.Words);
        vm.OpenAgentCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Sheets!.Open[0]?.IsOpen);
        Assert.Single(Shown(window, "Waiting to be broken down (3)"));

        Assert.True(view.HandleBack());
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsAgentOpen);
        Assert.Null(view.Sheets.Open[0]);
        window.Close();
    }

    [AvaloniaFact]
    public void The_editor_stays_open_on_a_tap_outside_once_something_was_typed()
    {
        var (window, view, vm) = Show(VocabStage.Edit);
        Assert.True(view.Sheets!.Open[2]?.IsOpen);

        vm.Detail!.Editor.Meaning = "họ hàng";
        Assert.True(Sheet.CloseTopmost());
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Sheets.Open[2]?.IsOpen);
        Assert.True(vm.Detail.Editor.IsOpen);

        vm.Detail.Editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Sheets.Open[2]);
        window.Close();
    }

    [AvaloniaFact]
    public void Add_words_from_anywhere_lands_in_the_add_box_on_the_Words_tab()
    {
        var (window, view, vm) = Show(VocabStage.Learn);
        vm.OpenAddCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(VocabTab.Words, vm.Tab);
        Assert.False(vm.IsAddOpen);
        Assert.Equal("NewWordBox", (TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as Control)?.Name);

        vm.NewWord = "commute";
        vm.AddCommand.Execute(null);
        Assert.Equal("“commute” is already in your words.", vm.Message);
        window.Close();
    }

    [AvaloniaFact]
    public void First_run_shows_the_steps_and_its_button_opens_the_add_box()
    {
        var (window, view, vm) = Show(VocabStage.FirstRun);
        Assert.True(Named<Border>(view, "FirstRun").IsVisible);
        Assert.False(Named<Border>(view, "TabGroup").IsVisible);

        var button = view.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Add my first word");
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<Border>(view, "FirstRun").IsVisible);
        Assert.Equal(VocabTab.Words, vm.Tab);
        Assert.True(Named<VocabWordsPanel>(view, "WordsPanel").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Deleting_a_word_asks_in_a_glass_sheet()
    {
        var (window, _, vm) = Show(VocabStage.Word);
        vm.DeleteWordCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(window, "Delete this word?"));
        Assert.True(Sheet.CloseTopmost());
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.Detail);
        window.Close();
    }

    [AvaloniaFact]
    public void The_gear_opens_the_settings_sheet_whose_switch_turns_the_tool_off()
    {
        var (window, view, _) = Show(VocabStage.Learn);
        Assert.NotNull(view.OpenSettings());
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(window, "Vocabulary is on"));
        var toggle = OverlayLayer.GetOverlayLayer(window)!.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "EnableSwitch");
        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(_module.IsEnabled);
        Assert.Single(Shown(window, "Vocabulary is turned off"));
        _module.IsEnabled = true;
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_shows_the_outline_instead_of_the_wide_tree()
    {
        var (window, view, _) = Show(VocabStage.Word, width: 390);
        Assert.Contains("narrow", view.Classes);
        var parts = view.GetVisualDescendants().OfType<VocabPartsView>().First(p => p.IsEffectivelyVisible);
        Assert.True(Named<ItemsControl>(parts, "Outline").IsVisible);
        Assert.False(Named<ScrollViewer>(parts, "TreeScroller").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void A_typed_answer_one_letter_off_is_close_and_the_answer_shows()
    {
        var (window, _, vm) = Show(VocabStage.Typed);
        Assert.True(vm.Card.AnswerIsClose);
        Assert.True(vm.Card.IsRevealed);
        Assert.Single(Shown(window, "Almost: check the spelling."));
        window.Close();
    }
}
