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

/// <summary>The shell's small shared pages (Helm.Shell.UI): Welcome.</summary>
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

}
