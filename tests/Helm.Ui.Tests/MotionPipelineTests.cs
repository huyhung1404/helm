using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.Wallet.Ui;

namespace Helm.Ui.Tests;

/// <summary>
/// Motion never costs the whole page: loops run on the compositor (no layout, no UI-thread property changes) and stop
/// when hidden; drawn motion asks for frames only while something moves; a page at rest asks for none.
/// </summary>
[Collection("Motion")]
public sealed class MotionPipelineTests
{
    private static Border Marker = new();

    private static Window Window(Control content, double width = 300, double height = 200)
    {
        Marker = new Border { Width = 1, Height = 1, Background = Avalonia.Media.Brushes.Gray, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Window { Content = new Panel { Children = { content, Marker } }, Width = width, Height = height };
        window.Show();
        return window;
    }

    /// <summary>
    /// Real time passes with a frame drawn every few ms, as on a screen (headless draws a frame only when something
    /// changed, so a marker in the corner changes each time).
    /// </summary>
    private static void Frames(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Marker.Opacity = Marker.Opacity > 0.5 ? 0.4 : 0.6;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
    }

    [AvaloniaFact]
    public void A_spinning_glyph_turns_on_the_compositor_without_any_layout_and_stops_when_hidden()
    {
        var glyph = new Border { Width = 20, Height = 20, Background = Avalonia.Media.Brushes.Teal };
        var window = Window(new StackPanel { Children = { glyph, new TextBlock { Text = "Something else on the page" } } });
        Frames(0.05);

        Motion.SetSpin(glyph, true);
        Frames(0.05);
        Assert.True(Motion.IsLooping(glyph));
        Assert.NotNull(ElementComposition.GetElementVisual(glyph));

        var layouts = 0;
        glyph.LayoutUpdated += (_, _) => layouts++;
        var changes = 0;
        glyph.PropertyChanged += (_, _) => changes++;
        Frames(0.3);
        Assert.Equal(0, layouts); // the compositor turns it: the page is never laid out again
        Assert.Equal(0, changes); // and nothing is set on the UI thread

        glyph.IsVisible = false; // hidden: the loop stops
        Frames(0.05);
        Assert.False(Motion.IsLooping(glyph));
        glyph.IsVisible = true;
        Frames(0.05);
        Assert.True(Motion.IsLooping(glyph));
        Motion.SetSpin(glyph, false);
        Assert.False(Motion.IsLooping(glyph));
        window.Close();
    }

    [AvaloniaFact]
    public void A_drawn_animation_asks_for_frames_only_until_it_rests()
    {
        var window = Window(new Panel());
        var left = 3;

        using var loop = MotionClock.Run(window, _ => --left > 0);
        Assert.True(loop.IsRunning);
        Frames(0.5);

        Assert.Equal(0, left); // called once per frame until it said it rests, then never again
        Assert.False(loop.IsRunning);
        window.Close();
    }

    [AvaloniaFact]
    public void The_wallet_with_its_fund_jars_comes_to_rest()
    {
        var view = WalletDemo.Create(new FakeModule("wallet", "Wallet"), WalletStage.Money);
        var window = Window(view, 1200, 900);
        Frames(0.2);
        var jars = view.GetVisualDescendants().OfType<FundJar>().Where(j => !j.IsAdd && j.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(jars);
        Assert.Contains(jars, j => j.IsMoving); // the water moves when the jars come on screen

        Frames(3.5); // the waves move for 2.5 s

        Assert.All(jars, j => Assert.False(j.IsMoving));
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Control>(), Motion.IsLooping);
        window.Close();
        while (Sheet.CloseTopmost()) { }
    }
}
