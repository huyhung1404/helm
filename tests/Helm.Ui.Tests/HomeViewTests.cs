using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Home;

namespace Helm.Ui.Tests;

/// <summary>The shared Home page (Helm.Home.UI) with the Gallery's made-up Helm.</summary>
public sealed class HomeViewTests
{
    private static (Window Window, HomeView View, HomeViewModel Vm) Show(HomeStage stage, double width = 1200)
    {
        var view = (HomeView)HomeDemo.Create(stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, (HomeViewModel)view.DataContext!);
    }

    private static List<TextBlock> Shown(Control root, string text) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public void Tools_that_are_on_are_tiles_and_a_switch_adds_or_removes_one()
    {
        var (window, view, vm) = Show(HomeStage.Desktop);

        Assert.Equal(14, vm.QuickAccess.Count); // three of the seventeen are off
        Assert.Single(Shown(view, "Tracker"));
        Assert.Single(Shown(view, "3 open · 1 overdue"));

        var keyboard = vm.Tools.Single(t => t.Id == "keyboard");
        keyboard.IsEnabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(keyboard, vm.QuickAccess);
        keyboard.IsEnabled = false;
        Assert.DoesNotContain(keyboard, vm.QuickAccess);
        window.Close();
    }

    [AvaloniaFact]
    public void A_desktop_shows_shortcuts_and_conflicts_a_phone_does_not()
    {
        var (desktop, desktopView, _) = Show(HomeStage.Desktop);
        Assert.Single(Shown(desktopView, "Shortcut conflicts"));
        Assert.Single(Shown(desktopView, "Snap the window to a zone"));
        desktop.Close();

        var (phone, phoneView, _) = Show(HomeStage.Phone, width: 390);
        Assert.Contains("narrow", (IEnumerable<string>)phoneView.Classes);
        Assert.Empty(Shown(phoneView, "Shortcut conflicts"));
        Assert.Empty(Shown(phoneView, "Snap the window to a zone"));
        phone.Close();
    }

    [AvaloniaFact]
    public void With_nothing_on_it_says_what_to_do()
    {
        var (window, view, vm) = Show(HomeStage.Empty);
        Assert.False(vm.HasQuickAccess);
        Assert.Single(Shown(view, "No tool is on yet. Turn one on below."));
        window.Close();
    }

    [AvaloniaFact]
    public void The_tools_list_orders_by_name_or_by_category()
    {
        var (window, _, vm) = Show(HomeStage.Desktop);
        Assert.Equal(vm.Tools.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => t.Id), vm.Tools.Select(t => t.Id));
        vm.ToggleSortCommand.Execute(null);
        Assert.Equal(vm.Tools.OrderBy(t => t.Group, StringComparer.CurrentCulture).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).Select(t => t.Id), vm.Tools.Select(t => t.Id));
        window.Close();
    }
}
