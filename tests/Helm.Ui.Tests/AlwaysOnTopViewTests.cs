using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.AlwaysOnTop;
using Helm.Modules.AlwaysOnTop.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Always On Top page (Helm.Modules.AlwaysOnTop.UI) with its real view model over the Gallery's pretend tool.</summary>
public sealed class AlwaysOnTopViewTests
{
    private readonly FakeModule _module = new("always-on-top", "Always On Top");

    private (Window Window, AlwaysOnTopView View, AlwaysOnTopViewModel Vm, PretendAlwaysOnTop Tool) Show(AlwaysOnTopStage stage, double width = 1200)
    {
        var (view, vm, tool) = AlwaysOnTopDemo.Build(_module, stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, tool);
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void The_first_card_is_the_on_off_switch_and_it_turns_the_tool_off()
    {
        var (window, view, _, _) = Show(AlwaysOnTopStage.Pinned);
        var cards = Named<Helm.Core.Ui.MasonryPanel>(view, "Cards");
        var first = cards.Children.OfType<Border>().First();
        Assert.Contains(first.GetVisualDescendants().OfType<ToggleSwitch>(), s => s.Name == "EnableSwitch");

        Named<ToggleSwitch>(view, "EnableSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(_module.IsEnabled);
        Assert.Equal("ANY WINDOW · TURNED OFF", Named<TextBlock>(view, "TopLine").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Kept_windows_are_listed_and_Unpin_lets_them_go()
    {
        var (window, view, vm, _) = Show(AlwaysOnTopStage.Pinned);
        Assert.Equal(3, vm.Pinned.Count);
        Assert.Equal("ANY WINDOW · 3 WINDOWS ON TOP", Named<TextBlock>(view, "TopLine").Text);
        Assert.False(Named<TextBlock>(view, "EmptyText").IsVisible);

        vm.UnpinCommand.Execute(vm.Pinned[0]);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.Pinned.Count);
        Assert.Equal("2 windows on top", vm.PinnedSummary);

        vm.UnpinAllCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Pinned);
        Assert.True(Named<TextBlock>(view, "EmptyText").IsVisible);
        Assert.Contains("press Win + Ctrl + T", Named<TextBlock>(view, "EmptyText").Text);
        Assert.False(Named<Button>(view, "UnpinAllButton").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void A_colour_is_picked_only_without_the_accent_colour_and_is_saved()
    {
        var (window, view, vm, tool) = Show(AlwaysOnTopStage.Empty);
        Assert.True(vm.UseAccentColor);
        Assert.False(vm.CanPickColor);

        vm.UseAccentColor = false;
        Assert.True(vm.CanPickColor);
        var red = Named<WrapPanel>(view, "Swatches").Children.OfType<Button>().Single(b => (string?)b.Tag == "#E81123");
        red.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#E81123", vm.BorderColor);
        Assert.Equal("#E81123", tool.Settings.Current.BorderColor);
        Assert.Contains("selected", red.Classes);

        vm.ShowBorder = false;
        Assert.False(vm.CanPickColor);
        window.Close();
    }

    [AvaloniaFact]
    public void A_shortcut_that_is_taken_shows_why_and_the_keys_show_without_a_recorder()
    {
        var (window, view, _, _) = Show(AlwaysOnTopStage.ShortcutTaken);
        Assert.True(Named<Border>(view, "ProblemCard").IsVisible);
        // The Gallery has no recorder: the keys show as keycaps.
        var picker = Named<ContentControl>(view, "HotkeyPicker");
        Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Win");
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_gets_the_narrow_layout()
    {
        var (window, view, _, _) = Show(AlwaysOnTopStage.Pinned, width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(new Thickness(16, 18, 16, 28), Named<StackPanel>(view, "Page").Margin);
        window.Close();
    }
}
