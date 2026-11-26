using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.Missions;
using Helm.Modules.Missions.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Missions page (Helm.Modules.Missions.UI) with its real view model over the Gallery's in-memory missions.</summary>
public sealed class MissionsViewTests : IDisposable
{
    private readonly FakeModule _module = new("missions", "Missions");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private (Window Window, MissionsView View, MissionsViewModel Vm) Show(MissionsStage stage = MissionsStage.Mission, double width = 1200)
    {
        var view = (MissionsView)MissionsDemo.Create(_module, stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, (MissionsViewModel)view.DataContext!);
    }

    /// <summary>What is on screen, the page and the sheets over it.</summary>
    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Each_tab_replaces_the_whole_page_under_the_tabs()
    {
        var (window, view, _) = Show();
        var tab = (string name) => view.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Name == name);

        Assert.NotEmpty(Shown(window, "Thi Aptis B1"));
        Assert.Single(Shown(window, "Roadmap"));
        tab("InsightsTab").IsChecked = true;
        Settle();
        Assert.Single(Shown(window, "Badges"));
        Assert.Empty(Shown(window, "Roadmap"));

        tab("MissionTab").IsChecked = true;
        Settle();
        Assert.Single(Shown(window, "Roadmap"));
        Assert.Empty(Shown(window, "Badges"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_mission_card_shows_that_mission()
    {
        var (window, view, vm) = Show();
        var run = vm.Missions.Single(m => m.Title == "Chạy 10 km");
        var card = view.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext == run && b.Classes.Contains("mission"));

        card.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();

        Assert.Same(run, vm.SelectedMission);
        Assert.True(run.IsSelected);
        Assert.Contains("selected", card.Classes);
        Assert.False(vm.Missions.Single(m => m.Title == "Thi Aptis B1").IsSelected);
        window.Close();
    }

    [AvaloniaFact]
    public void New_mission_opens_one_sheet_that_Back_closes_with_the_flow()
    {
        var (window, view, vm) = Show();

        vm.OpenPromptCommand.Execute(null);
        Settle();
        Assert.True(view.NewSheet?.IsOpen);
        Assert.Single(Shown(window, "Describe your goal"));

        // The next step is the same sheet.
        var sheet = view.NewSheet;
        vm.OpenImportCommand.Execute(null);
        Settle();
        Assert.Same(sheet, view.NewSheet);
        Assert.Single(Shown(window, "2. Paste the AI's answer"));

        Assert.True(Sheet.CloseTopmost());
        Settle();
        Assert.False(vm.IsNewOpen);
        Assert.Null(view.NewSheet);
        window.Close();
    }

    [AvaloniaFact]
    public void Creating_a_mission_closes_the_sheet_and_shows_the_new_mission()
    {
        var (window, view, vm) = Show();

        vm.UseTemplateCommand.Execute(MissionTemplates.All[0]);
        Settle();
        Assert.True(view.NewSheet?.IsOpen);
        Assert.True(vm.CanCreate);

        vm.CreateMissionCommand.Execute(null);
        Settle();
        Assert.Null(view.NewSheet);
        Assert.Equal("Run 5 km", vm.Title);
        Assert.NotEmpty(Shown(window, "Run 5 km"));
        window.Close();
    }

    [AvaloniaFact]
    public void The_last_step_of_a_phase_opens_its_celebration_and_Back_dismisses_it()
    {
        var (window, view, vm) = Show(MissionsStage.PhaseDone);
        Settle();

        Assert.NotNull(vm.Celebration);
        Assert.True(view.CelebrationSheet?.IsOpen);
        Assert.Single(Shown(window, "PHASE DONE"));

        Assert.True(Sheet.CloseTopmost());
        Settle();
        Assert.Null(vm.Celebration);
        Assert.Null(view.CelebrationSheet);
        window.Close();
    }

    [AvaloniaFact]
    public void Skipping_asks_in_a_glass_sheet_and_cancelling_keeps_the_step()
    {
        var (window, _, vm) = Show();
        var before = vm.CurrentTitle;

        vm.SkipStepCommand.Execute(null);
        Settle();
        Assert.Single(Shown(window, "Skip this step?"));

        Assert.True(Sheet.CloseTopmost());
        Settle();
        Assert.Equal(before, vm.CurrentTitle);
        window.Close();
    }

    [AvaloniaFact]
    public void The_gear_opens_the_settings_sheet_whose_switch_turns_the_tool_off()
    {
        var (window, view, _) = Show();

        var sheet = view.OpenSettings();
        Settle();
        Assert.NotNull(sheet);
        Assert.Single(Shown(window, "Missions is on"));
        var toggle = OverlayLayer.GetOverlayLayer(window)!.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "EnableSwitch");
        toggle.IsChecked = false;
        Settle();

        Assert.False(_module.IsEnabled);
        Assert.Single(Shown(window, "Missions is turned off"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_stacks_the_today_cards_and_puts_the_tabs_on_their_own_line()
    {
        var (window, view, _) = Show(width: 390);
        Assert.Contains("narrow", view.Classes);
        var tabs = view.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "Tabs");
        Assert.Equal(1, Grid.GetRow(tabs));
        var today = view.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "TodayList");
        Assert.Equal(1, today.GetVisualDescendants().OfType<UniformGrid>().First().Columns);
        window.Close();
    }

    [AvaloniaFact]
    public void Every_gallery_stage_builds()
    {
        foreach (var stage in Enum.GetValues<MissionsStage>())
        {
            var (window, view, _) = Show(stage);
            Settle();
            Assert.True(view.IsEffectivelyVisible, stage.ToString());
            window.Close();
            while (Sheet.CloseTopmost()) { }
        }
    }
}
