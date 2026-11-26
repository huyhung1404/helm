using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.Keyboard;
using Helm.Modules.Keyboard.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Helm Keyboard page (Helm.Modules.Keyboard.UI) with its real view model over the Gallery's pretend tool.</summary>
public sealed class KeyboardViewTests
{
    private readonly FakeModule _module = new("keyboard", "Helm Keyboard");

    private (Window Window, KeyboardView View, KeyboardViewModel Vm, PretendKeyboard Tool) Show(KeyboardStage stage, double width = 1200)
    {
        var (view, vm, tool) = KeyboardDemo.Build(_module, stage);
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
        var (window, view, _, _) = Show(KeyboardStage.Installed);
        var first = Named<Helm.Core.Ui.MasonryPanel>(view, "Cards").Children.OfType<Border>().First();
        Assert.Contains(first.GetVisualDescendants().OfType<ToggleSwitch>(), s => s.Name == "EnableSwitch");

        Named<ToggleSwitch>(view, "EnableSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(_module.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void Install_and_Remove_follow_the_input_method()
    {
        var (window, _, vm, tool) = Show(KeyboardStage.NotInstalled);
        Assert.True(vm.CanInstall);
        Assert.False(vm.IsInstalled);
        Assert.StartsWith("Not installed.", vm.InstallDescription);

        vm.InstallCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsInstalled);
        Assert.False(vm.CanInstall);
        Assert.Equal(KeyboardInstallState.Installed, tool.InstallStatus().State);
        window.Close();
    }

    [AvaloniaFact]
    public void Apps_with_their_own_keyboard_are_listed_and_Forget_removes_one()
    {
        var (window, _, vm, tool) = Show(KeyboardStage.Installed);
        Assert.Equal(4, vm.Apps.Count);
        var facebook = vm.Apps.Single(a => a.Display == "facebook.com");
        Assert.True(facebook.IsSite);

        facebook.Mode = KeyboardMode.Chinese;
        Assert.Equal(KeyboardMode.Chinese, tool.Apps.ModeOf(KeyboardApps.SiteKeyOf("facebook.com")));

        vm.ForgetCommand.Execute(facebook);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Apps.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void A_problem_shows_and_the_own_shortcut_shows_its_keys_without_a_recorder()
    {
        var (window, view, vm, _) = Show(KeyboardStage.Problem);
        Assert.True(Named<Border>(view, "ProblemCard").IsVisible);
        Assert.True(vm.IsOwnHotkey);
        var picker = Named<ContentControl>(view, "OwnHotkey");
        Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "K");

        vm.Hotkey = new Helm.Core.Hotkeys.HotkeyGesture(Helm.Core.Hotkeys.HotkeyModifiers.Ctrl | Helm.Core.Hotkeys.HotkeyModifiers.Alt, 'J');
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "J");
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_gets_the_narrow_layout()
    {
        var (window, view, _, _) = Show(KeyboardStage.Installed, width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(new Thickness(16, 18, 16, 28), Named<StackPanel>(view, "Page").Margin);
        window.Close();
    }
}
