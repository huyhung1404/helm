using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.General;
using Helm.Home;

namespace Helm.Ui.Tests;

/// <summary>The shared Home ("today") and General pages (Helm.Shell.UI) with the Gallery's made-up Helm.</summary>
public sealed class HomeViewTests
{
    private static (Window Window, T View) Show<T>(Control view, double width = 1200) where T : Control
    {
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, (T)view);
    }

    private static List<TextBlock> Shown(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public void A_busy_day_lists_problems_in_the_order_given_then_each_tools_part_most_urgent_first()
    {
        var (window, view) = Show<HomeView>(HomeDemo.Home(HomeStage.Busy));
        var vm = (HomeViewModel)view.DataContext!;

        Assert.Equal(["Ctrl+Alt+N is taken by another app", "Helm 0.88.1 is ready", "The vault has not been backed up for 9 days"], vm.Alerts.Select(a => a.Title));
        Assert.Equal("Tracker", vm.Today[0].Tool.Name);
        Assert.Single(Shown(view, "NEEDS ATTENTION"));
        Assert.Single(Shown(view, "Trả tiền điện tháng 11"));
        Assert.Empty(Shown(view, "All clear"));
        window.Close();
    }

    [AvaloniaFact]
    public void Ticking_a_task_on_home_counts_it_done()
    {
        var (window, view) = Show<HomeView>(HomeDemo.Home(HomeStage.Busy));
        var vm = (HomeViewModel)view.DataContext!;
        var before = vm.Headline;

        vm.Today[0].Items[0].TickCommand.Execute(null);
        Assert.True(vm.Today[0].Items[0].IsDone);
        Assert.NotEqual(before, vm.Headline);
        window.Close();
    }

    [AvaloniaFact]
    public void Nothing_due_and_nothing_wrong_is_all_clear()
    {
        var (window, view) = Show<HomeView>(HomeDemo.Home(HomeStage.AllClear));
        Assert.Single(Shown(view, "All clear"));
        Assert.Empty(Shown(view, "NEEDS ATTENTION"));
        window.Close();
    }

    [AvaloniaFact]
    public void Mcp_is_a_part_of_General_before_Help_where_the_platform_serves_it()
    {
        var (window, view) = Show<GeneralView>(HomeDemo.General(desktop: true));
        var model = (GeneralViewModel)view.DataContext!;
        Assert.DoesNotContain(model.Sections, s => s.Id == "mcp");

        model.AttachMcp(new TextBlock { Text = "Helm's tools for AI agents" });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["mcp", "help"], model.Sections.TakeLast(2).Select(s => s.Id));
        Assert.Empty(Shown(view, "Helm's tools for AI agents"));

        model.Selected = model.Section("mcp");
        Dispatcher.UIThread.RunJobs();
        Assert.True(model.IsMcp);
        Assert.Single(Shown(view, "Helm's tools for AI agents"));
        window.Close();
    }

    [AvaloniaFact]
    public void General_lists_its_parts_with_their_state_and_a_switch_counts_in_tools()
    {
        var (window, view) = Show<GeneralView>(HomeDemo.General(desktop: true));
        var vm = (GeneralViewModel)view.DataContext!;

        Assert.Equal("12 of 14 on", vm.Section("tools").Summary);
        Assert.True(vm.IsTools);
        vm.Tools.Single(t => t.Name == "Helm Keyboard").IsEnabled = true;
        Assert.Equal("13 of 14 on", vm.Section("tools").Summary);

        vm.Selected = vm.Section("sync");
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(view, "Devices"));
        Assert.Empty(Shown(view, "A tool that is off is hidden from the menu and Home, and does nothing in the background. Its data stays."));
        window.Close();
    }

    [AvaloniaFact]
    public void On_a_phone_General_is_the_list_and_a_part_opens_in_its_place()
    {
        var (window, view) = Show<GeneralView>(HomeDemo.General(desktop: false), width: 390);
        var row = view.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Sync");

        Assert.Empty(Shown(view, "Devices"));
        row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(view, "Devices"));
        var rail = view.FindControl<Control>("Rail")!;
        Assert.False(rail.IsVisible); // the part took the list's place

        Assert.True(view.HandleBack());
        Dispatcher.UIThread.RunJobs();
        Assert.True(rail.IsVisible);
        Assert.Empty(Shown(view, "Devices"));
        window.Close();
    }
}
