using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Core.Hotkeys;
using Helm.Core.Settings;
using Helm.Modules.WindowZones;
using Helm.Modules.WindowZones.Model;

namespace Helm.Tests;

public class WindowZonesTests
{
    private static readonly MonitorInfo Left = new(1, "DISPLAY1_1920x1080", @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, true);
    private static readonly MonitorInfo Right = new(2, "DISPLAY2_2560x1440", @"\\.\DISPLAY2", new PixelRect(1920, -200, 4480, 1240), new PixelRect(1920, -200, 4480, 1240), 144, false);

    // ───────────── geometry ─────────────

    [Fact]
    public void Fractions_follow_the_work_area_of_a_secondary_monitor_and_round_trip()
    {
        var zone = new ZoneRect(0.5, 0.5, 0.5, 0.5);
        var pixels = ZoneGeometry.ToPixels(zone, Right.WorkArea);

        Assert.Equal(new PixelRect(1920 + 1280, -200 + 720, 4480, 1240), pixels);
        var back = ZoneGeometry.FromPixels(pixels, Right.WorkArea);
        Assert.Equal(zone.X, back.X, 6);
        Assert.Equal(zone.Height, back.Height, 6);
    }

    [Fact]
    public void Hit_test_prefers_the_smallest_containing_zone_then_the_nearest_within_reach()
    {
        var zones = new List<PixelRect> { new(0, 0, 1000, 1000), new(100, 100, 300, 300), new(1020, 0, 1500, 1000) };

        Assert.Equal(1, ZoneGeometry.HitTest(zones, new PixelPoint(150, 150), 0));
        Assert.Equal(0, ZoneGeometry.HitTest(zones, new PixelPoint(900, 900), 0));
        Assert.Equal(2, ZoneGeometry.HitTest(zones, new PixelPoint(1012, 500), 10)); // in the gap, 8 px from zone 2
        Assert.Equal(-1, ZoneGeometry.HitTest(zones, new PixelPoint(1600, 500), 10));
    }

    [Theory]
    [InlineData(new long[] { 10, 20, 30 }, 10, 1, 20)]
    [InlineData(new long[] { 10, 20, 30 }, 30, 1, 10)]
    [InlineData(new long[] { 10, 20, 30 }, 10, -1, 30)]
    [InlineData(new long[] { 10, 20, 30 }, 99, 1, 10)]
    [InlineData(new long[] { 10 }, 10, 1, 0)]
    public void Switching_wraps_through_the_windows_of_a_zone(long[] windows, long current, int delta, long expected)
    {
        Assert.Equal((nint)expected, ZoneGeometry.CycleTarget(windows.Select(w => (nint)w).ToList(), (nint)current, delta));
    }

    [Fact]
    public void A_window_counts_as_in_its_zone_until_it_is_moved_away()
    {
        var zone = new PixelRect(0, 0, 960, 1040);

        Assert.True(ZoneGeometry.IsInZone(new PixelRect(4, 2, 1100, 1040), zone)); // an app that will not be narrower
        Assert.False(ZoneGeometry.IsInZone(new PixelRect(300, 0, 1260, 1040), zone));
    }

    // ───────────── drawers ─────────────

    [Theory]
    [InlineData(0, 0, false, ZoneToggleAction.Empty)]
    [InlineData(3, 3, false, ZoneToggleAction.Open)]
    [InlineData(3, 3, true, ZoneToggleAction.Open)]
    [InlineData(3, 1, false, ZoneToggleAction.BringToFront)]
    [InlineData(3, 0, true, ZoneToggleAction.Close)]
    [InlineData(1, 0, true, ZoneToggleAction.Close)]
    public void The_zone_shortcut_opens_brings_forward_or_closes(int windows, int minimized, bool inFront, ZoneToggleAction expected)
    {
        Assert.Equal(expected, ZoneWindows.Decide(windows, minimized, inFront));
    }

    [Fact]
    public void A_window_lives_in_one_zone_and_the_last_one_in_front_comes_back_on_top()
    {
        var zones = new ZoneWindows();
        zones.Add("a", Member(1));
        zones.Add("a", Member(2));
        zones.Add("b", Member(3));

        Assert.Equal((nint)2, zones.Top("a"));
        Assert.True(zones.Touch(1));
        Assert.False(zones.Touch(1));
        Assert.Equal((nint)1, zones.Top("a"));

        zones.Add("b", Member(1)); // dropped into another zone: it leaves the first
        Assert.Equal("b", zones.ZoneOf(1));
        Assert.Equal([2], zones.Members("a").Select(m => (long)m.Handle));
        Assert.Equal((nint)2, zones.Top("a")); // the last one in front left: the newest one is on top
        Assert.Equal([3, 1], zones.Members("b").Select(m => (long)m.Handle));

        Assert.True(zones.Remove(2));
        Assert.Empty(zones.Members("a"));
        Assert.Equal((nint)0, zones.Top("a"));
        Assert.False(zones.Touch(2));
    }

    [Fact]
    public void Windows_of_deleted_zones_are_let_go()
    {
        var zones = new ZoneWindows();
        zones.Add("a", Member(1));
        zones.Add("b", Member(2));

        Assert.True(zones.RetainZones(new HashSet<string> { "b" }));
        Assert.Null(zones.ZoneOf(1));
        Assert.Equal("b", zones.ZoneOf(2));
        Assert.False(zones.RetainZones(new HashSet<string> { "b" }));
    }

    [Fact]
    public void Saved_windows_come_back_only_when_the_same_window_still_exists()
    {
        var zones = new ZoneWindows();
        zones.Add("a", new ZoneMember(1, 100, 800, 600));
        zones.Add("a", new ZoneMember(2, 200, 0, 0));
        zones.Add("a", new ZoneMember(3, 300, 0, 0));
        zones.Touch(1);
        zones.Add("gone", new ZoneMember(4, 400, 0, 0));
        var file = zones.Export();

        // Window 2 closed; window 3's handle now belongs to another program; zone "gone" was deleted.
        var alive = new Dictionary<nint, int> { [1] = 100, [3] = 999, [4] = 400 };
        var back = ZoneWindows.Import(file, new HashSet<string> { "a" }, (h, pid) => alive.TryGetValue(h, out var p) && p == pid);

        Assert.Equal([1], back.Members("a").Select(m => (long)m.Handle));
        Assert.Equal(new ZoneMember(1, 100, 800, 600), back.Find(1));
        Assert.Equal((nint)1, back.Top("a"));
        Assert.Null(back.ZoneOf(4));
    }

    // ───────────── numbers ─────────────

    [Fact]
    public void Kept_zones_keep_their_numbers_and_new_ones_take_the_lowest_free_in_reading_order()
    {
        var zones = new List<ZoneDefinition>
        {
            Zone("kept", Left, 2, new ZoneRect(0.5, 0, 0.5, 1)),
            Zone("right-monitor", Right, null, new ZoneRect(0, 0, 1, 1)),
            Zone("bottom-left", Left, null, new ZoneRect(0, 0.5, 0.5, 0.5)),
            Zone("top-left", Left, null, new ZoneRect(0, 0, 0.5, 0.5)),
            Zone("bad", Left, 12, new ZoneRect(0, 0, 0.1, 0.1)),
        };

        ZoneNumbers.Assign(zones, [Left.Id, Right.Id]);

        Assert.Equal(2, zones[0].Number);
        Assert.Equal(1, zones.Single(z => z.Id == "top-left").Number);
        Assert.Equal(3, zones.Single(z => z.Id == "bad").Number); // y 0 before y 0.5
        Assert.Equal(4, zones.Single(z => z.Id == "bottom-left").Number);
        Assert.Equal(5, zones.Single(z => z.Id == "right-monitor").Number);
    }

    [Fact]
    public void Only_nine_zones_get_a_shortcut()
    {
        var zones = Enumerable.Range(0, 11).Select(i => Zone($"z{i}", Left, null, new ZoneRect(i / 11.0, 0, 1 / 11.0, 1))).ToList();
        ZoneNumbers.Assign(zones, [Left.Id]);
        Assert.Equal(Enumerable.Range(1, 9).Cast<int?>().Append(null).Append(null), zones.Select(z => z.Number));
    }

    [Fact]
    public void Taking_a_number_in_use_swaps_it()
    {
        var zones = new List<ZoneDefinition> { Zone("a", Left, 1, default), Zone("b", Left, 2, default) };

        ZoneNumbers.Set(zones, "b", 1);
        Assert.Equal((2, 1), (zones[0].Number, zones[1].Number));

        ZoneNumbers.Set(zones, "a", null);
        Assert.Null(zones[0].Number);
        ZoneNumbers.Set(zones, "a", 10); // out of range: ignored
        Assert.Null(zones[0].Number);
    }

    [Fact]
    public void Labels_show_the_number_and_the_name()
    {
        Assert.Equal("Zone 3", Zone("a", Left, 3, default).Label);
        Assert.Equal("3 · Browser", new ZoneDefinition { Number = 3, Name = " Browser " }.Label);
        Assert.Equal("Notes", new ZoneDefinition { Name = "Notes" }.Label);
    }

    // ───────────── editor ─────────────

    [Fact]
    public void A_drawn_zone_stays_on_the_monitor_it_was_started_on()
    {
        var edit = Editor();
        edit.Press(new PixelPoint(1500, 100), grip: 10);
        edit.Drag(new PixelPoint(2600, 600)); // past the edge into the right monitor
        edit.Release();

        var zone = Assert.Single(edit.Zones);
        Assert.Equal(new PixelRect(1500, 100, 1920, 600), zone.Rect);
    }

    [Fact]
    public void Tiny_drags_draw_nothing_and_drawing_outside_every_monitor_is_refused()
    {
        var edit = Editor();
        edit.Press(new PixelPoint(100, 100), 10);
        edit.Drag(new PixelPoint(110, 110));
        edit.Release();
        Assert.Empty(edit.Zones);

        Assert.False(edit.CanDrawAt(new PixelPoint(100, 1060))); // the taskbar, outside the work area
        edit.Press(new PixelPoint(100, 1060), 10);
        Assert.Null(edit.Drawing);
    }

    [Fact]
    public void A_zone_moved_onto_another_monitor_goes_there_whole()
    {
        var edit = Editor(new EditZone("a", new PixelRect(100, 100, 500, 500)));
        edit.Press(new PixelPoint(300, 300), 10);
        edit.Drag(new PixelPoint(2300, 300));
        edit.Release();

        var moved = edit.Zones[0].Rect;
        Assert.Equal("a", edit.Zones[0].Id);
        Assert.True(Right.WorkArea.Contains(moved));
        Assert.Equal((400, 400), (moved.Width, moved.Height));
    }

    [Fact]
    public void Splitting_keeps_the_zone_id_on_the_first_half()
    {
        var edit = Editor(new EditZone("a", new PixelRect(0, 0, 1000, 1000)));
        edit.Select(0);

        Assert.True(edit.SplitSelected(SplitOrientation.Vertical));
        Assert.Equal("a", edit.Zones[0].Id);
        Assert.Equal(new PixelRect(0, 0, 500, 1000), edit.Zones[0].Rect);
        Assert.NotEqual("a", edit.Zones[1].Id);
        Assert.Equal(new PixelRect(500, 0, 1000, 1000), edit.Zones[1].Rect);
    }

    [Fact]
    public void Zones_of_a_monitor_that_is_not_connected_stay_out_of_the_editor()
    {
        var edit = new ZoneEditModel([Left.WorkArea], [new EditZone("on", new PixelRect(0, 0, 500, 500)), new EditZone("off", new PixelRect(3000, 0, 3500, 500))]);
        Assert.Equal(["on"], edit.Zones.Select(z => z.Id));
    }

    [Fact]
    public void Resizing_respects_the_minimum_size_and_the_monitor()
    {
        var edit = Editor(new EditZone("a", new PixelRect(100, 100, 600, 600)));
        edit.Press(new PixelPoint(600, 350), 10); // right edge
        edit.Drag(new PixelPoint(0, 350));
        Assert.Equal(100 + ZoneGeometry.MinZonePixels, edit.Zones[0].Rect.Right);
        edit.Drag(new PixelPoint(5000, 350));
        Assert.Equal(1920, edit.Zones[0].Rect.Right);
        edit.Release();
    }

    // ───────────── storage ─────────────

    [Fact]
    public void Saved_zones_keep_offline_monitors_and_read_back_the_same()
    {
        using var dir = new TempDir();
        using (var settings = new SettingsStoreFactory(new HelmPaths(dir.Path)))
        {
            var data = new WindowZonesData(settings);
            var raised = 0;
            data.Changed += (_, _) => raised++;
            data.SaveZones([Zone("right", Right, null, new ZoneRect(0, 0, 0.5, 1))], [Right]);
            // The right monitor is unplugged; the editor only showed the left one.
            data.SaveZones([Zone("left", Left, null, new ZoneRect(0, 0, 0.25, 1))], [Left]);
            data.SetNumber("left", 7);
            data.Rename("left", "  Chat ");
            Assert.Equal(4, raised);
            settings.FlushAll();
        }

        using (var settings = new SettingsStoreFactory(new HelmPaths(dir.Path)))
        {
            var data = new WindowZonesData(settings);
            var zones = data.GetZones();
            Assert.Equal(["left", "right"], zones.Select(z => z.Id));
            var left = zones.Single(z => z.Id == "left");
            Assert.Equal((7, "Chat", Left.Id), (left.Number, left.Name, left.MonitorId));
            Assert.Equal(0.25, left.Rect.Width, 6);
            Assert.Equal(1, zones.Single(z => z.Id == "right").Number);

            var placed = data.Place([Left]);
            Assert.Equal(new PixelRect(0, 0, 480, 1040), Assert.Single(placed).Rect);
        }
    }

    [Fact]
    public void Zone_shortcuts_use_the_chosen_keys()
    {
        var s = new WindowZonesSettings();
        Assert.Equal(new HotkeyGesture(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, '2'), s.ZoneHotkey(2));
        Assert.Equal(VirtualKeyNames.Shift, s.DragVirtualKey);
        Assert.Contains(s.ZoneModifiers, WindowZonesSettings.ZoneModifierChoices);
    }

    private static ZoneEditModel Editor(params EditZone[] zones) => new([Left.WorkArea, Right.WorkArea], zones);

    private static ZoneMember Member(long handle) => new((nint)handle, 1, 0, 0);

    private static ZoneDefinition Zone(string id, MonitorInfo monitor, int? number, ZoneRect rect) =>
        new() { Id = id, MonitorId = monitor.Id, Number = number, Rect = rect };
}
