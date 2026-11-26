using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.BugReports.Ui;
using Helm.Core.Diagnostics;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Shell.BugReports;

namespace Helm.Ui.Tests;

/// <summary>The shared Bug reports page and report form (Helm.Shell.UI) with their real view models over in-memory stores.</summary>
public sealed class BugReportsViewTests : IDisposable
{
    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private static (Window Window, BugReportsView View, BugReportsViewModel Vm, BugStore Store) Show(double width = 1200, bool seed = true)
    {
        var (store, time) = BugReportsDemo.SeededStore(seed);
        var vm = new BugReportsViewModel(store, new BlackBox(), new NoClipboard(), null, time);
        var view = new BugReportsView(vm, new PageHost());
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, store);
    }

    private static string Id(BugStore store, string tool) => store.All().First(b => b.Value.Tool == tool).Id;

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    private static T InSheet<T>(Window window) where T : Control =>
        (OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? []).OfType<T>().Single();

    [AvaloniaFact]
    public void A_wide_page_shows_the_list_and_the_opened_bug_side_by_side()
    {
        var (window, view, vm, store) = Show();
        Assert.Single(Shown(window, "Pick a bug on the left to see its conversation, its pictures and its black box."));

        vm.Open(Id(store, "Vault"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.FindControl<Grid>("ListPane")!.IsEffectivelyVisible);
        Assert.NotEmpty(Shown(window, "Tìm trong Vault không ra gì sau khi mở khoá bằng vân tay"));
        // The agent's question waits on top, with its answer box.
        Assert.Single(Shown(window, "Claude Code asks"));
        Assert.Empty(Shown(window, "Pick a bug on the left to see its conversation, its pictures and its black box."));
        Assert.False(view.HandleBack());
    }

    [AvaloniaFact]
    public void A_phone_shows_the_list_or_the_bug_and_back_returns_to_the_list()
    {
        var (window, view, vm, store) = Show(width: 390);
        Assert.Contains("narrow", view.Classes);
        var list = view.FindControl<Grid>("ListPane")!;
        var detail = view.FindControl<Grid>("DetailPane")!;
        Assert.True(list.IsVisible);
        Assert.False(detail.IsVisible);

        vm.SelectCommand.Execute(vm.Bugs.First(b => b.Tool == "Wallet"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(list.IsVisible);
        Assert.True(detail.IsVisible);
        Assert.Single(Shown(window, "Is it fixed?"));

        Assert.True(view.HandleBack());
        Dispatcher.UIThread.RunJobs();
        Assert.True(list.IsVisible);
        Assert.False(detail.IsVisible);
        Assert.Null(vm.Selected);
    }

    [AvaloniaFact]
    public void Closing_asks_why_in_a_sheet_and_closes_the_bug()
    {
        var (window, view, vm, store) = Show();
        var id = Id(store, "Tracker");
        vm.Open(id);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.Detail.AskClose());
        Dispatcher.UIThread.RunJobs();
        var sheet = InSheet<BugCloseView>(window);
        Assert.Single(Shown(window, $"Close #{store.Get(id)!.Code}?"));

        sheet.Choose(BugCloseReason.Duplicate);
        sheet.FindControl<TextBox>("Original")!.Text = "#B2";
        sheet.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(BugStatus.Closed, store.Get(id)!.Status);
        Assert.Equal(BugCloseReason.Duplicate, store.Get(id)!.CloseReason);
        Assert.Equal("#B2", store.Get(id)!.DuplicateOf);
        Assert.Null(view.Detail.OpenSheet);
    }

    [AvaloniaFact]
    public void Still_broken_needs_a_reason_then_reopens_the_bug()
    {
        var (window, view, vm, store) = Show();
        var id = Id(store, "Wallet");
        vm.Open(id);
        Dispatcher.UIThread.RunJobs();

        view.Detail.AskReopen(stillBroken: true);
        Dispatcher.UIThread.RunJobs();
        var sheet = InSheet<BugReopenView>(window);
        var reopen = sheet.FindControl<Button>("ReopenButton")!;
        Assert.False(reopen.IsEnabled);

        sheet.Text = "Vẫn bị cắt khi xoay ngang màn hình.";
        Dispatcher.UIThread.RunJobs();
        Assert.True(reopen.IsEnabled);
        reopen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(BugStatus.Open, store.Get(id)!.Status);
        Assert.Contains(store.Entries(id), e => e.Value.Text.Contains("Vẫn bị cắt", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void The_black_box_sheet_filters_its_events()
    {
        var (window, view, vm, store) = Show();
        vm.Open(Id(store, "Notes"));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.Detail.ShowBlackBox());
        Dispatcher.UIThread.RunJobs();
        var sheet = InSheet<BlackBoxView>(window);
        var model = (BlackBoxSheetModel)sheet.DataContext!;
        Assert.Equal(8, model.Events.Count);

        sheet.GetVisualDescendants().OfType<RadioButton>().Single(r => (string?)r.Tag == "Errors").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, model.Events.Count);
        Assert.All(model.Events, e => Assert.Equal(BlackBoxKind.Error, e.Kind));
        Assert.NotEmpty(Shown(window, "Environment"));
    }

    [AvaloniaFact]
    public void Nothing_reported_yet_says_how_to_report_and_hides_the_filters()
    {
        var (window, view, _, _) = Show(seed: false);
        Assert.Single(Shown(window, "No bugs yet"));
        Assert.Empty(Shown(window, "Not closed (0)"));
        Assert.Empty(Shown(window, "Pick a bug on the left to see its conversation, its pictures and its black box."));
    }

    // ---- the report form ----

    private static (Window Window, BugReportFormView View, BugReportFormViewModel Form, FormHost Host, BugStore Store) ShowForm(double width = 680)
    {
        var (store, _) = BugReportsDemo.SeededStore(seed: false);
        var box = new BlackBox();
        box.Freeze();
        var form = new BugReportFormViewModel(store, box, BugReportsDemo.SampleBox(), "Tracker", BugReportsDemo.ToolNames);
        var host = new FormHost(box);
        var view = new BugReportFormView(form, host);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, form, host, store);
    }

    private static void Press(Control view, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Escape_closes_an_empty_form_at_once_and_a_written_one_at_the_second_press()
    {
        var (_, view, form, host, _) = ShowForm();
        Press(view, Key.Escape);
        Assert.Equal(1, host.Closed);
        Assert.True(host.Thawed);

        (_, view, form, host, _) = ShowForm();
        form.Text = "Kéo thả việc thì việc biến mất";
        Press(view, Key.Escape);
        Assert.Equal(0, host.Closed);
        Assert.Equal("Press Esc again to discard", view.FindControl<TextBlock>("KeyHint")!.Text);
        Press(view, Key.Escape);
        Assert.Equal(1, host.Closed);
    }

    [AvaloniaFact]
    public void Ctrl_enter_sends_and_the_form_offers_to_open_the_bug()
    {
        var (window, view, form, host, store) = ShowForm();
        form.Text = "Nhắc việc hiện hai lần\nBấm Xong ở cái thứ nhất thì cái thứ hai vẫn còn.";
        Press(view, Key.Enter, KeyModifiers.Control);

        Assert.True(form.IsDone);
        var bug = Assert.Single(store.All());
        Assert.Equal("Tracker", bug.Value.Tool);
        Assert.Single(Shown(window, $"Bug #{bug.Value.Code} saved"));

        view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Open the bug").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(bug.Id, host.Opened);
        Assert.Equal(1, host.Closed);
    }

    [AvaloniaFact]
    public void A_phone_form_stacks_its_fields_and_offers_the_camera()
    {
        var (_, view, _, _, _) = ShowForm(width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(1, Grid.GetRow(view.FindControl<StackPanel>("ToolField")!));
        // The test host has a camera: three picture buttons share the row.
        Assert.True(view.FindControl<Button>("CameraButton")!.IsVisible);
        Assert.Equal(3, view.FindControl<Grid>("PictureButtons")!.ColumnDefinitions.Count);
    }

    private sealed class PageHost : IBugReportsPageHost
    {
        public void Report() { }

        public string ReportHint => "Ctrl+Alt+F12, anywhere";

        public Task<IReadOnlyList<BugImageDraft>> PickAsync(int max) => Task.FromResult<IReadOnlyList<BugImageDraft>>([]);

        public Task<IReadOnlyList<BugImageDraft>> PasteAsync() => Task.FromResult<IReadOnlyList<BugImageDraft>>([]);
    }

    private sealed class FormHost(BlackBox box) : IBugReportFormHost
    {
        public int Closed { get; private set; }

        public string? Opened { get; private set; }

        public bool Thawed => !box.IsFrozen;

        public void Close() => Closed++;

        public void OpenBug(string id) => Opened = id;

        public bool HasKeyboard => true;

        public bool CanTakePhoto => true;

        public Task<IReadOnlyList<BugImageDraft>> PickAsync(int max) => Task.FromResult<IReadOnlyList<BugImageDraft>>([]);

        public Task<IReadOnlyList<BugImageDraft>> PasteAsync() => Task.FromResult<IReadOnlyList<BugImageDraft>>([]);
    }
}
