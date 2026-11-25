using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Helm.Core.Ui;

namespace Helm.Ui.Tests;

public sealed class ScrollFadeTests
{
    [AvaloniaFact]
    public void A_scrolled_list_fades_the_edge_it_has_more_behind_and_not_the_ends()
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 2000 }, Height = 300, Width = 300 };
        ScrollFade.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // At the top: more below, so only the bottom fades.
        Assert.NotNull(viewer.OpacityMask);
        Assert.Equal(Colors.Black, ((LinearGradientBrush)viewer.OpacityMask!).GradientStops[0].Color);

        // In the middle: both edges fade.
        viewer.Offset = new Vector(0, 500);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Colors.Transparent, ((LinearGradientBrush)viewer.OpacityMask!).GradientStops[0].Color);
        Assert.Equal(Colors.Transparent, ((LinearGradientBrush)viewer.OpacityMask!).GradientStops[3].Color);

        // At the very end: nothing below, only the top fades.
        viewer.Offset = new Vector(0, 5000);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Colors.Transparent, ((LinearGradientBrush)viewer.OpacityMask!).GradientStops[0].Color);
        Assert.Equal(Colors.Black, ((LinearGradientBrush)viewer.OpacityMask!).GradientStops[3].Color);
        window.Close();
    }

    [AvaloniaFact]
    public void Content_that_fits_has_no_fade()
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 50 }, Height = 300, Width = 300 };
        ScrollFade.SetEnabled(viewer, true);
        var window = new Window { Content = viewer, Width = 400, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(viewer.OpacityMask);
        window.Close();
    }
}
