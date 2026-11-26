using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Helm.Core.Ui;
using Helm.Gallery;
using Helm.Modules.WindowZones;
using Helm.Modules.WindowZones.Ui;

namespace Helm.Ui.Tests;

/// <summary>The shared Window Zones page (Helm.Modules.WindowZones.UI) with its real view model over the Gallery's pretend engine.</summary>
public sealed class WindowZonesViewTests : IDisposable
{
    private readonly FakeModule _module = new("window-zones", "Window Zones");

    public void Dispose()
    {
        while (Sheet.CloseTopmost()) { }
    }

    private (Window Window, WindowZonesView View, WindowZonesViewModel Vm, PretendZones Zones) Show(WindowZonesStage stage, double width = 1200)
    {
        var (view, vm, zones) = WindowZonesDemo.Build(_module, stage);
        var window = new Window { Content = view, Width = width, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm, zones);
    }

    private static List<TextBlock> Shown(Window window, string text) =>
        window.GetVisualDescendants().Concat(OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants() ?? [])
            .OfType<TextBlock>().Where(t => t.Text == text && t.IsEffectivelyVisible).Distinct().ToList();

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static Visual Sheets(Window window) => OverlayLayer.GetOverlayLayer(window)!;

    [AvaloniaFact]
    public void Every_zone_has_a_card_and_the_screens_are_drawn_to_scale()
    {
        var (window, view, vm, _) = Show(WindowZonesStage.Zones);
        Assert.Equal(7, vm.Zones.Count);
        Assert.Contains("7 ZONES · 7 WINDOWS", Named<TextBlock>(view, "TodayLine").Text);
        var overview = Named<ZoneLayoutView>(view, "Overview");
        Assert.Equal(2, overview.Session?.Monitors.Count);
        // The zone on the laptop screen not connected now has a card but is not drawn.
        Assert.Equal(6, overview.Session?.Edit.Zones.Count);
        Assert.Equal("Its monitor is not connected", vm.Zones.Single(z => z.Id == "notes").StateText);
        Assert.Equal("1", vm.Zones[0].NumberText);
        window.Close();
    }

    [AvaloniaFact]
    public void Closing_a_zone_minimizes_its_windows_and_Open_brings_them_back()
    {
        var (window, _, vm, zones) = Show(WindowZonesStage.Zones);
        var code = vm.Zones.Single(z => z.Id == "code");
        Assert.False(code.IsClosed);

        code.CloseCommand.Execute(null);
        WaitFor(() => code.IsClosed);
        Assert.Contains("close code", zones.Asked);

        code.OpenCommand.Execute(null);
        WaitFor(() => !code.IsClosed);

        // Take a window out: the card loses it, the window stays where it is.
        code.Windows[1].ReleaseCommand.Execute(null);
        WaitFor(() => code.Windows.Count == 1);
        window.Close();
    }

    [AvaloniaFact]
    public void The_editor_sheet_splits_a_zone_and_Save_keeps_the_zone_on_the_screen_not_connected()
    {
        var (window, view, vm, zones) = Show(WindowZonesStage.Zones);
        Assert.NotNull(view.OpenEditor());
        Dispatcher.UIThread.RunJobs();
        var editor = view.Editor;
        Assert.NotNull(editor);
        var session = editor!.Session!;

        session.Edit.Select(0);
        session.SplitVerticalCommand.Execute(null);
        Assert.Equal(7, session.Edit.Zones.Count);
        session.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(view.OpenSheet);
        WaitFor(() => vm.Zones.Count == 8);
        Assert.Contains(zones.Data.GetZones(), z => z.Id == "notes");
        window.Close();
    }

    [AvaloniaFact]
    public void Drawing_on_the_editor_canvas_adds_a_zone_and_Cancel_forgets_it()
    {
        var (window, view, vm, _) = Show(WindowZonesStage.FirstRun);
        Assert.Empty(vm.Zones);
        Assert.Single(Shown(window, "Give every window a place"));
        view.OpenEditor();
        Dispatcher.UIThread.RunJobs();
        var session = view.Editor!.Session!;
        var layout = Named<ZoneLayoutView>(Sheets(window), "Layout");

        // A drag across the middle of the first screen draws a zone there.
        var from = layout.TranslatePoint(new Point(layout.Bounds.Width * 0.1, layout.Bounds.Height * 0.2), window)!.Value;
        var to = layout.TranslatePoint(new Point(layout.Bounds.Width * 0.4, layout.Bounds.Height * 0.6), window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(session.Edit.Zones);
        Assert.Equal("DISPLAY1_2560x1440", session.Monitors[Helm.Modules.WindowZones.Model.ZoneGeometry.MostOverlapping(session.Edit.WorkAreas, session.Edit.Zones[0].Rect)].Id);

        session.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.OpenSheet);
        Assert.Empty(vm.Zones);
        window.Close();
    }

    [AvaloniaFact]
    public void The_settings_sheet_shows_the_keys_resets_a_shortcut_and_turns_the_tool_off()
    {
        var (window, view, vm, _) = Show(WindowZonesStage.Zones);
        vm.NextWindowHotkey = default;
        Assert.NotNull(view.OpenSettings());
        Dispatcher.UIThread.RunJobs();
        Assert.Single(Shown(window, "No shortcut"));

        Named<Button>(Sheets(window), "NextReset").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(vm.DefaultNextWindowHotkey, vm.NextWindowHotkey);
        Assert.Empty(Shown(window, "No shortcut"));
        Assert.NotEmpty(Shown(window, "PgDn"));

        Named<ToggleSwitch>(Sheets(window), "EnableSwitch").IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(_module.IsEnabled);
        Assert.Single(Shown(window, "Window Zones is turned off"));
        // Off: the zones stay, without their windows.
        Assert.All(vm.Zones, z => Assert.Empty(z.Windows));
        window.Close();
    }

    [AvaloniaFact]
    public void A_shortcut_another_app_took_shows_on_the_page()
    {
        var (window, view, _, _) = Show(WindowZonesStage.ShortcutTaken);
        Assert.True(Named<Border>(view, "ProblemBar").IsVisible);
        Assert.StartsWith("Ctrl+Alt+4, 5 could not be registered", Named<TextBlock>(view, "ProblemText").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void A_phone_stacks_the_two_ways_to_edit()
    {
        var (window, view, _, _) = Show(WindowZonesStage.Zones, width: 390);
        Assert.Contains("narrow", view.Classes);
        Assert.Equal(1, Grid.GetRow(Named<Button>(view, "ScreensButton")));
        window.Close();
    }

    private static void WaitFor(Func<bool> done)
    {
        // The view model follows the engine through the UI thread.
        for (var i = 0; i < 100 && !done(); i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }
}
