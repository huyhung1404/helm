using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Settings;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.Habits;
using Helm.Modules.Habits.Ui;
using Microsoft.Extensions.Logging.Abstractions;

namespace Helm.Ui.Tests;

/// <summary>The shared Habits page (Helm.Modules.Habits.UI) with its real view model over in-memory stores.</summary>
public sealed class HabitsViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "helm-ui-tests", Guid.NewGuid().ToString("N"));
    private readonly SettingsStoreFactory _settings;
    private readonly MemorySynced<Habit> _habits = new();
    private readonly HabitsStore _store;
    private readonly FakeModule _module = new("habits", "Habits");

    public HabitsViewTests()
    {
        _settings = new SettingsStoreFactory(new HelmPaths(_dir));
        _store = new HabitsStore(_habits, new MemorySynced<HabitCheck>(), new MemorySynced<HabitLapse>());
    }

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
        _settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A habit started <paramref name="daysAgo"/> days ago, done every day before today except <paramref name="skip"/>.</summary>
    private string Habit(string title, int daysAgo, params int[] skip)
    {
        var today = _store.Today;
        var id = _habits.Add(new Habit { Title = title, StartDay = today.AddDays(-daysAgo), CreatedAt = DateTimeOffset.Now.AddDays(-daysAgo) });
        for (var d = daysAgo; d >= 1; d--)
        {
            if (!skip.Contains(d)) _store.Check(id, today.AddDays(-d));
        }
        return id;
    }

    private (Window Window, HabitsView View, HabitsViewModel Vm) Show(double width = 1200)
    {
        var vm = new HabitsViewModel(_store, _settings, new AvaloniaUi(), new YesDialogs(), new HabitReminderService(_store, _settings),
            NullLogger<HabitsViewModel>.Instance);
        var view = new HabitsView(_module, vm, new NoHost());
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    [AvaloniaFact]
    public void Each_tab_replaces_the_whole_page_under_the_tabs()
    {
        Habit("Đọc 20 trang sách", 30);
        var (window, view, vm) = Show();
        var tab = (string name) => view.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == name);

        Assert.Single(Shown(window, "Today"));
        tab("ReportTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsReportTab);
        Assert.Single(Shown(window, "Month by month"));
        Assert.Empty(Shown(window, "Today"));

        tab("HabitsTab").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(window, "Today"));
        Assert.Empty(Shown(window, "Month by month"));
        window.Close();
    }

    [AvaloniaFact]
    public void Tapping_a_card_opens_a_sheet_to_mark_the_day_and_Back_closes_it()
    {
        Habit("Đọc 20 trang sách", 30);
        var (window, view, vm) = Show();

        vm.TapCardCommand.Execute(vm.ToDo.Single());
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(view.Popup);
        Assert.Single(Shown(window, "How much of it did you do today?"));

        // Back (or a tap outside, Escape) closes the sheet, and the day is not marked.
        Assert.True(Sheet.CloseTopmost());
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Mark);
        Assert.Null(view.Popup);
        Assert.False(_store.IsDone(vm.ToDo.Single().Id, _store.Today));
        window.Close();
    }

    [AvaloniaFact]
    public void Saving_the_day_turns_the_same_sheet_into_the_celebration()
    {
        Habit("Đọc 20 trang sách", 30);
        var (window, view, vm) = Show();
        vm.TapCardCommand.Execute(vm.ToDo.Single());
        Dispatcher.UIThread.RunJobs();
        var sheet = view.Popup;

        vm.SaveMarkCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Celebration);
        Assert.Same(sheet, view.Popup);
        Assert.Single(Shown(window, "“Đọc 20 trang sách” done!"));
        Assert.Empty(Shown(window, "How much of it did you do today?"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_reason_for_skipped_days_cannot_be_closed_without_an_answer()
    {
        Habit("Học 15 từ vựng", 30, skip: [1, 2]);
        var (window, view, vm) = Show();

        // Opening the page asks why the last two days were skipped.
        Assert.NotNull(vm.PendingLapse);
        Assert.NotNull(view.Popup);
        Assert.True(Sheet.CloseTopmost(), "Back stays on the sheet");
        Dispatcher.UIThread.RunJobs();
        Assert.True(view.Popup?.IsOpen);
        Assert.NotNull(vm.PendingLapse);

        vm.LapseReason = "Ốm hai hôm";
        vm.SaveReasonCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.PendingLapse);
        Assert.Null(view.Popup);
        window.Close();
    }

    [AvaloniaFact]
    public void The_gear_opens_the_settings_sheet_whose_switch_turns_the_tool_off()
    {
        Habit("Đọc 20 trang sách", 30);
        var (window, view, _) = Show();

        var sheet = view.OpenSettings(focusAdd: false);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(sheet);
        Assert.Single(Shown(window, "Habits is on"));
        var toggle = OverlayLayer.GetOverlayLayer(window)!.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "EnableSwitch");
        toggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(_module.IsEnabled);
        Assert.Single(Shown(window, "Habits is turned off"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_puts_the_numbers_two_by_two()
    {
        Habit("Đọc 20 trang sách", 30);
        var (window, view, _) = Show(width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(2, view.GetVisualDescendants().OfType<UniformGrid>().Single(g => g.Name == "StatGrid").Columns);
        window.Close();
    }

    private sealed class NoHost : IHabitsPageHost
    {
        public void Opened() { }
    }
}
