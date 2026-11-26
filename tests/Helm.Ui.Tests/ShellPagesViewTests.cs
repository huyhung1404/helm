using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Shell.Pages;

namespace Helm.Ui.Tests;

/// <summary>The shell's small shared pages (Helm.Shell.UI): Welcome and Diagnostics.</summary>
public sealed class ShellPagesViewTests
{
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
    public void Welcome_shows_three_steps_and_its_one_button_starts()
    {
        var started = 0;
        var view = new WelcomeView(() => started++);
        var window = Show(view);
        Assert.Equal(3, Named<UniformGrid>(view, "Steps").Columns);
        Named<Button>(view, "StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, started);
        window.Close();
    }

    [AvaloniaFact]
    public void Welcome_on_a_phone_puts_the_steps_one_under_the_other()
    {
        var view = new WelcomeView(() => { });
        var window = Show(view, 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(1, Named<UniformGrid>(view, "Steps").Columns);
        window.Close();
    }

    [AvaloniaFact]
    public void Diagnostics_lists_this_Helm_the_monitors_and_the_windows_and_copies_them()
    {
        var (view, vm, source, clipboard) = ShellPagesDemo.BuildDiagnostics(DiagnosticsStage.ThisPc);
        var window = Show(view);
        Assert.Equal(2, vm.Monitors.Count);
        Assert.Equal(5, vm.Windows.Count);
        Assert.False(vm.HasConflicts);
        Assert.StartsWith("2 monitors, 5 app windows.", vm.Summary);
        Assert.Contains("on top", vm.Windows.Single(w => w.Process == "ApplicationFrameHost").Flags);

        var logs = vm.Facts.Single(f => f.Name == "Logs");
        vm.CopyCommand.Execute(logs);
        Assert.Equal(logs.Value, clipboard.Text);
        Assert.Equal("Copied: Logs", vm.Copied);
        vm.OpenCommand.Execute(logs);
        Assert.Equal([logs.Value], source.Opened);
        // A fact that is not a folder does not open.
        vm.OpenCommand.Execute(vm.Facts.Single(f => f.Name == "Version"));
        Assert.Single(source.Opened);

        vm.CopyAllCommand.Execute(null);
        Assert.Contains("Monitors:", clipboard.Text);
        Assert.Contains("0x2C0F4", clipboard.Text);
        Assert.Contains(logs.Value, clipboard.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void The_hooks_test_shows_keys_and_stops_when_the_page_closes()
    {
        var (view, vm, source, _) = ShellPagesDemo.BuildDiagnostics(DiagnosticsStage.Watching);
        var window = Show(view);
        Assert.True(source.Watching);
        Assert.True(vm.HasConflicts);
        Assert.Equal("#1 K down", vm.LastKey);

        // Moves show one in ten.
        for (var i = 0; i < 9; i++) source.Mouse($"Move at ({i}, 0)");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("#10 Move at (8, 0)", vm.LastMouse);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsHookTestEnabled);
        Assert.False(source.Watching);
    }
}
