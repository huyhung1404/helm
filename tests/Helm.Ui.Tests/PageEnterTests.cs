using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Ui;

namespace Helm.Ui.Tests;

/// <summary>A page opened in the shell fades in over a backdrop that stays put (no scale, no black edges).</summary>
public sealed class PageEnterTests
{
    [AvaloniaFact]
    public void The_backdrop_stays_and_the_content_fades_in()
    {
        var backdrop = new Backdrop();
        var content = new Border { Width = 200, Height = 100, Background = Brushes.Teal }; // something to draw: the clock follows frames
        var page = new UserControl { Content = new Panel { Children = { backdrop, content } } };
        var window = new Window { Content = page, Width = 400, Height = 300 };
        window.Show();

        var animated = Motion.EnterPage(page);
        Tick(1);

        Assert.Equal([content], animated);
        Assert.True(page.RenderTransform is null || page.RenderTransform.Value.IsIdentity);
        Assert.Equal(1, backdrop.Opacity);
        Assert.Null(backdrop.Effect);
        Assert.Null(content.Effect);

        // The headless clock follows real time: tick well past the end of the animation (it takes 150 ms; its first frame
        // can come a tick late, so the opacity may still read 1 here).
        var until = DateTime.UtcNow.AddSeconds(1);
        while (DateTime.UtcNow < until)
        {
            Tick(1);
            Thread.Sleep(5);
        }
        Tick(5);

        Assert.Equal(1, content.Opacity);
        Assert.Null(content.Effect);
        window.Close();
    }

    [AvaloniaFact]
    public void A_page_without_a_backdrop_fades_in_as_a_whole()
    {
        var page = new UserControl { Content = new Border() };

        Assert.Equal([page], Motion.EnterPage(page));
    }

    private static void Tick(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
