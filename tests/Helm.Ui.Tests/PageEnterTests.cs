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
        var content = new Border { Width = 200, Height = 100, Background = Brushes.Teal };
        var page = new UserControl { Content = new Panel { Children = { backdrop, content } } };
        // A frame every few ms, as on a screen: headless draws only when something changed, so a marker changes each tick.
        var window = new Window { Content = new Panel { Children = { page, Marker } }, Width = 400, Height = 300 };
        window.Show();

        var animated = Motion.EnterPage(page);
        Tick(1);

        Assert.Equal([content], animated);
        Assert.True(page.RenderTransform is null || page.RenderTransform.Value.IsIdentity);
        Assert.Equal(1, backdrop.Opacity);
        Assert.Null(backdrop.Effect);
        Assert.Null(content.Effect);
        Assert.IsType<BitmapCache>(content.CacheMode); // blended as one bitmap while it fades
        Assert.Null(backdrop.CacheMode);

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
        Assert.Null(content.CacheMode); // a cached page would be redrawn whole on every change inside it
        window.Close();
    }

    [AvaloniaFact]
    public void A_page_without_a_backdrop_fades_in_as_a_whole()
    {
        var page = new UserControl { Content = new Border() };

        Assert.Equal([page], Motion.EnterPage(page));
    }

    private static readonly Border Marker = new() { Width = 1, Height = 1, Background = Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };

    private static void Tick(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Marker.Opacity = Marker.Opacity > 0.5 ? 0.4 : 0.6;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
