using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Ui;

namespace Helm.Ui.Tests;

/// <summary>A page opened in the shell comes into focus over a backdrop that stays put (no scale, no black edges).</summary>
public sealed class PageEnterTests
{
    [AvaloniaFact]
    public void The_backdrop_stays_and_the_content_comes_into_focus()
    {
        var backdrop = new Backdrop();
        var content = new Border { Width = 200, Height = 100 };
        var page = new UserControl { Content = new Panel { Children = { backdrop, content } } };
        var window = new Window { Content = page, Width = 400, Height = 300 };
        window.Show();

        var animated = Motion.EnterPage(page);
        Tick(1);

        Assert.Equal([content], animated);
        Assert.True(page.RenderTransform is null || page.RenderTransform.Value.IsIdentity);
        Assert.Equal(1, backdrop.Opacity);
        Assert.Null(backdrop.Effect);
        // Still coming in: the headless clock follows real time, so the first tick can be ~100 ms in (opacity ~0.65).
        Assert.True(content.Opacity < 1);
        Assert.IsAssignableFrom<IBlurEffect>(content.Effect);

        // The headless clock follows real time: tick until the animation is over (it takes 260 ms), at most 3 s.
        var until = DateTime.UtcNow.AddSeconds(3);
        while (content.Opacity < 1 && DateTime.UtcNow < until)
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
    public void A_page_without_a_backdrop_comes_into_focus_as_a_whole()
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
