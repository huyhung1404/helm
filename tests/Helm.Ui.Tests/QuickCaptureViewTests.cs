using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.QuickCapture;
using Helm.Modules.QuickCapture.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Quick Capture views (Helm.Modules.QuickCapture.UI): the page and the capture box.</summary>
public sealed class QuickCaptureViewTests
{
    private readonly FakeModule _module = new("quick-capture", "Quick Capture");

    private static Window Show(Control view, double width = 1200)
    {
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void The_first_card_is_the_on_off_switch_and_off_greys_out_Open_now()
    {
        var (view, _, _) = QuickCaptureDemo.Build(_module, QuickCaptureStage.Page);
        var window = Show(view);
        var first = Named<Helm.Core.Ui.MasonryPanel>(view, "Cards").Children.OfType<Border>().First();
        Assert.Contains(first.GetVisualDescendants().OfType<ToggleSwitch>(), s => s.Name == "EnableSwitch");
        Assert.True(Named<Button>(view, "TryButton").IsEnabled);

        Named<ToggleSwitch>(view, "EnableSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(_module.IsEnabled);
        Assert.False(Named<Button>(view, "TryButton").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Save_to_is_stored_and_Open_now_asks_the_tool()
    {
        var (view, vm, tool) = QuickCaptureDemo.Build(_module, QuickCaptureStage.Page);
        var window = Show(view);
        Assert.Equal(["Note", "Task", "Debt"], vm.TargetNames);
        vm.DefaultTargetIndex = 1;
        Assert.Equal("task", tool.Settings.Current.DefaultTarget);

        vm.TryCommand.Execute(null);
        Assert.Equal(1, tool.Opened);
        window.Close();
    }

    [AvaloniaFact]
    public void A_taken_shortcut_shows_why_and_the_keys_show_without_a_recorder()
    {
        var (view, _, _) = QuickCaptureDemo.Build(_module, QuickCaptureStage.ShortcutTaken);
        var window = Show(view);
        Assert.True(Named<Border>(view, "ProblemCard").IsVisible);
        Assert.Contains(Named<ContentControl>(view, "ActivationHotkey").GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "N");
        window.Close();
    }

    [AvaloniaFact]
    public void The_box_routes_by_prefix_and_Enter_saves()
    {
        var (page, box, vm, saved) = QuickCaptureDemo.Box(_module, QuickCaptureStage.Box);
        var window = Show(page);
        Assert.Equal("task", vm.Targets.Single(t => t.IsActive).Target.Id);
        Assert.StartsWith("Task in Inbox", vm.PreviewText);

        var input = Named<TextBox>(box, "Input");
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.None });
        Dispatcher.UIThread.RunJobs();
        Assert.Single(saved);
        Assert.StartsWith("task:", saved[0]);
        Assert.Equal("", vm.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void The_box_with_no_tool_on_says_what_to_turn_on()
    {
        var (page, _, vm, _) = QuickCaptureDemo.Box(_module, QuickCaptureStage.BoxNoTargets);
        var window = Show(page);
        Assert.True(vm.HasNoTargets);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text?.StartsWith("Turn on Notes or Tracker") == true);
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_gets_the_narrow_layout()
    {
        var (view, _, _) = QuickCaptureDemo.Build(_module, QuickCaptureStage.Page);
        var window = Show(view, 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(new Thickness(16, 18, 16, 28), Named<StackPanel>(view, "Page").Margin);
        window.Close();
    }
}
