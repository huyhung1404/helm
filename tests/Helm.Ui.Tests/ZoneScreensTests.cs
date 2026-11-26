using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Gallery;
using Helm.Modules.WindowZones.Ui;

namespace Helm.Ui.Tests;

/// <summary>
/// What Window Zones shows on the screens themselves (Windows puts each in a window of its own): the full-screen editor
/// of one monitor, over the Gallery's pretend zones.
/// </summary>
public sealed class ZoneScreensTests
{
    private readonly FakeModule _module = new("window-zones", "Window Zones");

    /// <summary>The first pretend monitor (2560 × 1440) shown at half size, so one view unit is two of its pixels.</summary>
    private (Window Window, ZoneScreenEditorView View) ShowEditor(ZoneScreenStage stage = ZoneScreenStage.Toolbar, double width = 1280, double height = 720)
    {
        var view = WindowZonesDemo.ScreenEditor(_module, stage);
        var window = new Window { Content = view, Width = width, Height = height };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    [AvaloniaFact]
    public void The_editor_covers_its_monitor_and_only_the_home_monitor_has_the_toolbar()
    {
        var (window, view) = ShowEditor();
        var canvas = Named<ZoneLayoutView>(view, "Canvas");
        Assert.Equal(WindowZonesDemo.Monitors[0].Bounds, canvas.Viewport);
        Assert.Equal(new Size(1280, 720), canvas.Bounds.Size);
        Assert.True(Named<Border>(view, "Toolbar").IsVisible);
        Assert.False(Named<Border>(view, "Caption").IsVisible);
        window.Close();

        var (other, otherView) = ShowEditor(ZoneScreenStage.OtherMonitor);
        Assert.Equal(WindowZonesDemo.Monitors[1].Bounds, Named<ZoneLayoutView>(otherView, "Canvas").Viewport);
        Assert.False(Named<Border>(otherView, "Toolbar").IsVisible);
        Assert.True(Named<Border>(otherView, "Caption").IsVisible);
        Assert.Contains("The toolbar is on DISPLAY1", Named<TextBlock>(otherView, "CaptionText").Text);
        other.Close();
    }

    [AvaloniaFact]
    public void Drawing_at_full_size_makes_a_zone_in_the_monitors_own_pixels()
    {
        var (window, view) = ShowEditor();
        var session = view.Session!;
        session.DeleteAllCommand.Execute(null);
        Assert.Empty(session.Edit.Zones);

        // Below the toolbar: (200, 400) to (600, 640) here is (400, 800) to (1200, 1280) on the 2560 × 1440 monitor.
        window.MouseDown(new Point(200, 400), MouseButton.Left);
        window.MouseMove(new Point(400, 520));
        window.MouseMove(new Point(600, 640));
        window.MouseUp(new Point(600, 640), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        var zone = Assert.Single(session.Edit.Zones);
        Assert.InRange(zone.Rect.Left, 390, 410);
        Assert.InRange(zone.Rect.Top, 790, 810);
        Assert.InRange(zone.Rect.Right, 1190, 1210);
        Assert.InRange(zone.Rect.Bottom, 1270, 1290);
        window.Close();
    }

    [AvaloniaFact]
    public void Del_deletes_the_chosen_zone_Ctrl_Enter_saves_and_Esc_cancels()
    {
        var (window, view) = ShowEditor();
        var session = view.Session!;
        var closed = 0;
        session.CloseRequested += (_, _) => closed++;
        Assert.Equal(6, session.Edit.Zones.Count);

        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(5, session.Edit.Zones.Count);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal(1, closed);

        window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        // Saved, then closed: the zone deleted here is gone from the file too (the one on the screen not connected stays).
        Assert.Equal(2, closed);
        window.Close();
    }

    [AvaloniaFact]
    public void A_small_screen_puts_Cancel_and_Save_on_their_own_line()
    {
        var (window, view) = ShowEditor(width: 600, height: 900);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(1, Grid.GetRow(Named<Button>(view, "SaveButton")));
        window.Close();
    }
}
