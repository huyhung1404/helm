using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.NovelReader;
using Helm.Modules.NovelReader.Conversion;
using Helm.Modules.NovelReader.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Novel Reader page (Helm.Modules.NovelReader.UI) with its real view model over the Gallery's library.</summary>
public sealed class NovelReaderViewTests : IDisposable
{
    private readonly FakeModule _module = new("novel-reader", "Novel Reader");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private async Task<(Window Window, NovelReaderView View, NovelReaderViewModel Vm)> Show(NovelStage stage, double width = 1200)
    {
        var window = new Window { Width = width, Height = 860 };
        var (view, vm) = await NovelDemo.CreateAsync(_module, stage, window);
        await view.Activation;
        await Settle();
        return (window, view, vm);
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 30; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<T> Shown<T>(Window window) where T : Control =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<T>().Where(c => c.IsEffectivelyVisible).Distinct().ToList();

    private static bool Says(Window window, string text) => Shown<TextBlock>(window).Any(t => t.Text == text);

    [AvaloniaFact]
    public async Task The_novel_read_last_opens_converted_with_its_names_in_colour()
    {
        var (window, _, vm) = await Show(NovelStage.Reading);

        Assert.True(vm.IsBookOpen);
        Assert.Equal("Sơn môn dạ vũ", vm.BookTitle);
        var text = Shown<SegmentText>(window).Select(t => t.Line?.Text ?? "").ToList();
        Assert.Contains(text, t => t.StartsWith("Lâm Uyển đứng ở sơn môn", StringComparison.Ordinal));
        // Where reading stopped is marked.
        Assert.True(Says(window, "Continue reading here"));
    }

    [AvaloniaFact]
    public async Task First_run_shows_the_steps_and_asks_for_the_dictionaries_first_when_there_are_none()
    {
        var (window, _, _) = await Show(NovelStage.NoDictionaries);
        Assert.True(Says(window, "START HERE"));
        Assert.True(Says(window, "Get the dictionaries"));
        Assert.True(Says(window, "Download the dictionaries"));
        // The library's own card does not ask a second time.
        Assert.False(Says(window, "FIRST, THE DICTIONARIES"));
        window.Close();

        (window, _, _) = await Show(NovelStage.FirstRun);
        Assert.True(Says(window, "Add my first novel"));
    }

    [AvaloniaFact]
    public async Task The_library_shows_a_card_per_novel_and_Edit_opens_its_sheet()
    {
        var (window, view, vm) = await Show(NovelStage.Library);
        Assert.False(vm.IsBookOpen);
        Assert.Equal(4, vm.Books.Count);
        Assert.True(Says(window, "Library"));

        var card = vm.Books.First(b => b.HasSource);
        view.OpenBook(card);
        await Settle();
        Assert.True(card.IsRenaming);
        Assert.True(Says(window, "Web source"));
        Sheet.CloseTopmost();
        await Settle();
        // Closed without Save: the drafts are dropped.
        Assert.False(card.IsRenaming);
        Assert.False(card.IsEditingSource);
    }

    [AvaloniaFact]
    public async Task Names_sit_beside_the_text_on_a_wide_screen_and_in_a_sheet_on_a_phone()
    {
        var (window, view, vm) = await Show(NovelStage.Reading);
        view.OpenNames();
        await Settle();
        Assert.True(vm.ShowNamesPanel);
        var panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");
        Assert.True(panel.IsVisible);
        Assert.Contains(panel.GetVisualDescendants(), v => v is NovelNamesView);
        view.OpenNames();
        await Settle();
        Assert.False(panel.IsVisible);
        window.Close();

        (window, view, vm) = await Show(NovelStage.Reading, width: 390);
        Assert.Contains("narrow", view.Classes);
        view.OpenNames();
        await Settle();
        panel = view.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SidePanel");
        Assert.False(panel.IsVisible);
        Assert.Contains(OverlayLayer.GetOverlayLayer(window)!.GetVisualDescendants(), v => v is NovelNamesView);
        Sheet.CloseTopmost();
        await Settle();
        Assert.False(vm.ShowNamesPanel);
    }

    [AvaloniaFact]
    public async Task A_tapped_word_opens_its_sheet_and_closing_the_sheet_closes_the_word()
    {
        var (_, view, vm) = await Show(NovelStage.Reading);
        var paragraph = vm.Paragraphs.First(p => p.Line?.Segments.Any(s => s.Kind == SegmentKind.Name) == true);
        vm.OpenWord(paragraph, paragraph.Line!.Segments.First(s => s.Kind == SegmentKind.Name));
        await Settle();
        Assert.True(view.WordSheet?.IsOpen);

        view.WordSheet!.Close();
        await Settle();
        Assert.False(vm.Editor.IsOpen);

        // Closed by the view model (Read from here): the sheet goes too.
        vm.OpenWord(paragraph, paragraph.Line!.Segments.First(s => s.Kind == SegmentKind.Name));
        await Settle();
        vm.Editor.CloseCommand.Execute(null);
        await Settle();
        Assert.Null(view.WordSheet);
    }

    [AvaloniaFact]
    public async Task A_held_sentence_shows_in_a_sheet_with_its_word_for_Vocabulary()
    {
        var (window, view, _) = await Show(NovelStage.Sentence);
        await Settle();
        Assert.True(view.SentenceSheet?.IsOpen);
        Assert.True(Says(window, "CÂU NÀY"));
        Assert.True(Says(window, "THÊM TỪ TRONG CÂU"));
        Assert.False(Says(window, "DỊCH TỰ NHIÊN"));
        Assert.Single(Shown<ToggleButton>(window), b => b.Classes.Contains("chip"));
    }

    [AvaloniaFact]
    public async Task Keys_play_change_the_speed_and_the_text_size_but_not_while_typing()
    {
        var (_, view, vm) = await Show(NovelStage.Reading);
        var speed = vm.SpeechRate;
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemCloseBrackets });
        Assert.Equal(speed + 0.1, vm.SpeechRate, 3);
        var size = vm.FontSize;
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.OemPlus, KeyModifiers = KeyModifiers.Control });
        Assert.Equal(size + 1, vm.FontSize);
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
        await Settle();
        Assert.True(vm.IsSpeaking);
    }

    [AvaloniaFact]
    public async Task The_settings_sheet_has_the_switch_first_and_only_what_the_platform_has()
    {
        var (window, _, _) = await Show(NovelStage.Settings);
        Assert.True(Says(window, "Novel Reader is on"));
        Assert.True(Says(window, "Chapters downloaded at once"));
        Assert.False(Says(window, "With the screen off"));
        Assert.False(Says(window, "Novel Lens"));
        Sheet.CloseTopmost();
        window.Close();

        (window, _, _) = await Show(NovelStage.SettingsPhone, width: 390);
        Assert.True(Says(window, "With the screen off"));
        Assert.True(Says(window, "Novel Lens"));
        Assert.False(Says(window, "Chapters downloaded at once"));
        Assert.False(Says(window, "Mark the paragraph under the mouse"));
    }

    [AvaloniaFact]
    public async Task Turning_the_tool_off_keeps_the_library_but_read_only()
    {
        var (window, view, _) = await Show(NovelStage.Library);
        _module.IsEnabled = false;
        await Settle();
        Assert.True(Says(window, "Novel Reader is turned off"));
        Assert.False(view.GetVisualDescendants().OfType<NovelLibraryView>().Single().IsEnabled);
        _module.IsEnabled = true;
    }
}
