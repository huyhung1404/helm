using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ai;
using Helm.Core.Settings;
using Helm.Gallery;
using Helm.Modules.Tracker;
using Helm.Modules.Tracker.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ui.Tests;

/// <summary>The shared Tracker page (Helm.Modules.Tracker.UI) with its real view model over in-memory stores.</summary>
public sealed class TrackerViewTests : IDisposable
{
    private const string FiveSteps = """
        {"tasks": [{"title": "Học 10 câu mẫu part 1"}, {"title": "Mô tả 3 bức ảnh part 2"}, {"title": "So sánh 2 ảnh part 3"},
                   {"title": "Nói 2 phút part 4"}, {"title": "Ghi âm, nghe lại, sửa lỗi"}]}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStoreFactory _settings;
    private readonly TrackerStore _store = new(new MemorySynced<TrackerWorkspace>(), new MemorySynced<TrackerItem>(), new MemorySyncedLog<TrackerEvent>());

    public TrackerViewTests() => _settings = new SettingsStoreFactory(new HelmPaths(_dir));

    public void Dispose()
    {
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (Window Window, TrackerView View, TrackerViewModel Vm) Show(TrackerProposalViewModel? proposals = null, double width = 1200)
    {
        var vm = new TrackerViewModel(_store, _settings, new AvaloniaUi(), new YesDialogs(), new NoClipboard(),
            new TrackerReminderService(_store, _settings), NullLogger<TrackerViewModel>.Instance, proposals: proposals);
        var view = new TrackerView(new FakeModule("tracker", "Tracker"), vm, new NoHost());
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static List<TextBlock> Shown(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public async Task The_steps_panel_shows_only_under_the_task_whose_steps_are_open()
    {
        var list = _store.AddWorkspace("TODO");
        var first = _store.AddItem(list, new TrackerItemDraft("Đổi mật khẩu WeChat"));
        _store.AddItem(list, new TrackerItemDraft("Đòi 3 khoản tiền"));
        var proposals = new TrackerProposalViewModel(_store, new OneAnswer(FiveSteps), TimeProvider.System);
        var (window, view, vm) = Show(proposals);

        Assert.Equal(2, vm.OpenItems.Count);
        // No task has its steps open: no panel at all (it once showed under every task, every part at once).
        Assert.Empty(Shown(view, "Proposed steps"));

        await proposals.ForTaskAsync(first);
        Dispatcher.UIThread.RunJobs();

        var heading = Assert.Single(Shown(view, "Proposed steps"));
        Assert.Same(proposals, heading.DataContext);
        window.Close();
    }

    [AvaloniaFact]
    public void Each_tab_replaces_the_whole_page_under_the_tabs()
    {
        _store.AddItem(_store.AddWorkspace("Hôm nay"), new TrackerItemDraft("Gọi điện cho mẹ"));
        var (window, view, _) = Show();
        var tab = (string name) => view.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == name);

        Assert.Single(Shown(view, "Gọi điện cho mẹ"));
        tab("CalendarTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        // The calendar comes right under the tabs: the list, its title and the quick add are gone.
        Assert.Empty(Shown(view, "Gọi điện cho mẹ"));
        Assert.Empty(Shown(view, "Hôm nay"));

        tab("InsightsTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(view, "Completed per day"));
        Assert.Empty(Shown(view, "Gọi điện cho mẹ"));

        tab("TasksTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(view, "Gọi điện cho mẹ"));
        Assert.Empty(Shown(view, "Completed per day"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_gear_opens_the_settings_as_a_sheet_over_the_page()
    {
        _store.AddWorkspace("Hôm nay");
        var (window, view, _) = Show();
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(view)!;

        var gear = view.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Tracker settings");
        gear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var settings = layer.GetVisualDescendants().OfType<TrackerSettingsView>().Single();
        Assert.Contains(settings.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Settings");
        // Done closes it.
        settings.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DoneButton").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(layer.GetVisualDescendants().OfType<TrackerSettingsView>());
        window.Close();
    }

    [AvaloniaFact]
    public void The_settings_sheet_turns_the_tool_on_and_off()
    {
        // The on/off switch used to be on a WPF settings page; with that gone it lives in the sheet.
        _store.AddWorkspace("Hôm nay");
        var module = new FakeModule("tracker", "Tracker");
        var vm = new TrackerViewModel(_store, _settings, new AvaloniaUi(), new YesDialogs(), new NoClipboard(),
            new TrackerReminderService(_store, _settings), NullLogger<TrackerViewModel>.Instance);
        var view = new TrackerView(module, vm, new NoHost());
        var window = new Window { Content = view, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(view)!;
        view.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Tracker settings")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var toggle = layer.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "EnableSwitch");
        Assert.True(toggle.IsChecked);
        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(module.IsEnabled);
        // Turned off elsewhere, the switch follows.
        module.IsEnabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(toggle.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Lists_are_managed_in_the_settings_renamed_in_place_and_removed_after_asking()
    {
        _store.AddWorkspace("Hôm nay");
        _store.AddWorkspace("Helm");
        var (window, view, vm) = Show();
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(view)!;
        view.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Tracker settings")
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // Rename Helm in its row.
        var box = layer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == "Helm" && Avalonia.Automation.AutomationProperties.GetName(t) == "List name");
        box.Text = "Công việc";
        box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.Workspaces, w => w.Name == "Công việc");

        // Remove it: asks first; Delete removes it, and its row goes.
        var asking = layer.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Delete Công việc");
        asking.Command!.Execute(asking.CommandParameter);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(layer.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is { } s && s.StartsWith("Delete “Công việc”"));
        layer.GetVisualDescendants().OfType<Button>().Last(b => b.Content as string == "Delete").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(vm.Workspaces, w => w.Name == "Công việc");
        Assert.Single(vm.Workspaces);

        // Add one.
        vm.NewWorkspaceName = "Nhà";
        vm.CreateWorkspaceCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Hôm nay", "Nhà"], vm.Workspaces.Select(w => w.Name).Order(StringComparer.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void New_list_is_only_on_a_phone_lists_are_added_in_the_settings_on_a_wide_screen()
    {
        _store.AddWorkspace("Hôm nay");
        var wide = Show(width: 1200);
        Assert.DoesNotContain(wide.View.GetVisualDescendants().OfType<Button>(), b => b.Name == "NewListButton" && b.IsEffectivelyVisible);
        wide.Window.Close();
        var phone = Show(width: 390);
        Assert.Contains(phone.View.GetVisualDescendants().OfType<Button>(), b => b.Name == "NewListButton" && b.IsEffectivelyVisible);
        phone.Window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_a_task_asks_in_the_glass_sheet_and_Cancel_keeps_it()
    {
        _store.AddItem(_store.AddWorkspace("Hôm nay"), new TrackerItemDraft("Gọi điện cho mẹ"));
        var (window, view, vm) = Show();
        var layer = Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(view)!;

        var asking = vm.OpenItems[0].DeleteCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(layer.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Delete this task?");

        layer.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        await asking;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.OpenItems);
        Assert.DoesNotContain(layer.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Delete this task?");
        window.Close();
    }
    [AvaloniaFact]
    public void An_opened_task_turns_its_chevron_and_on_a_phone_its_actions_are_icons()
    {
        _store.AddItem(_store.AddWorkspace("Hôm nay"), new TrackerItemDraft("Gọi điện cho mẹ"));
        var (window, view, vm) = Show(width: 390);
        vm.OpenItems[0].IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        var chevron = view.GetVisualDescendants().OfType<Panel>().First(i => i.Classes.Contains("turn"));
        Assert.Contains("open", chevron.Classes);
        // It turns by a rotation (Avalonia keeps it in its standard transform group once animated).
        Assert.True(chevron.RenderTransform is Avalonia.Media.RotateTransform
            || (chevron.RenderTransform as Avalonia.Media.TransformGroup)?.Children.OfType<Avalonia.Media.RotateTransform>().Any() == true);
        var edit = view.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Edit");
        Assert.All(edit.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("label")), t => Assert.False(t.IsVisible));
        window.Close();
    }

    private sealed class NoHost : ITrackerPageHost
    {
        public void ExportCalendar(string ics) { }

        public void Opened() { }

    }

    private sealed class OneAnswer(string answer) : IAiText
    {
        public string? Unavailable(string feature) => null;

        public Task<AiTextAnswer> AskAsync(AiTextRequest request, CancellationToken ct) => Task.FromResult(new AiTextAnswer(answer, null));
    }
}
