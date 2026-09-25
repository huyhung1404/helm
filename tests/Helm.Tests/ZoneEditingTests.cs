using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Modules.Zones.Layouts;
using static Helm.Modules.Zones.Layouts.LayoutGeometry;

namespace Helm.Tests;

public class LayoutGeometryTests
{
    private static readonly MonitorInfo Left = new(1, "L", "L", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 96, true);
    private static readonly MonitorInfo Right = new(2, "R", "R", new PixelRect(1920, -200, 3000, 1720), new PixelRect(1920, -200, 3000, 1680), 96, false);

    [Fact]
    public void Monitor_layouts_use_the_target_work_area()
    {
        var layout = new ZoneLayout { Scope = LayoutScope.Monitor };
        Assert.Equal(Right.WorkArea, ReferenceRect(layout, Right, [Left, Right]));
        Assert.Equal([Right], CoveredMonitors(layout, Right, [Left, Right]));
    }

    [Fact]
    public void Span_layouts_use_the_union_of_their_connected_monitors()
    {
        var layout = new ZoneLayout { Scope = LayoutScope.Span, MonitorIds = ["L", "R", "unplugged"] };
        Assert.Equal(new PixelRect(0, -200, 3000, 1680), ReferenceRect(layout, Left, [Left, Right]));
        Assert.Null(ReferenceRect(new ZoneLayout { Scope = LayoutScope.Span, MonitorIds = ["gone"] }, Left, [Left, Right]));
    }

    [Fact]
    public void Pixels_and_fractions_round_trip()
    {
        var reference = new PixelRect(100, 50, 2020, 1130);
        var zone = new PixelRect(580, 320, 1540, 860);
        var back = ToPixels([FromPixels(zone, reference)], reference)[0];
        Assert.Equal(zone, back);
    }

    [Fact]
    public void Resize_snaps_edges_within_the_threshold()
    {
        var snapped = Snap(new PixelRect(0, 0, 955, 500), Edges.Right, xLines: [960, 1920], yLines: [], threshold: 10);
        Assert.Equal(960, snapped.Right);
        var untouched = Snap(new PixelRect(0, 0, 900, 500), Edges.Right, xLines: [960], yLines: [], threshold: 10);
        Assert.Equal(900, untouched.Right);
    }

    [Fact]
    public void Move_snaps_either_side_and_keeps_the_size()
    {
        var moved = Snap(new PixelRect(965, 7, 1465, 507), Edges.Move, xLines: [960], yLines: [0], threshold: 10);
        Assert.Equal(new PixelRect(960, 0, 1460, 500), moved);
    }

    [Fact]
    public void Split_produces_two_halves_that_tile_the_zone()
    {
        var (a, b) = Split(new PixelRect(0, 0, 1001, 600), SplitOrientation.Vertical);
        Assert.Equal(a.Right, b.Left);
        Assert.Equal(1001, a.Width + b.Width);
        var (top, bottom) = Split(new PixelRect(0, 0, 800, 601), SplitOrientation.Horizontal);
        Assert.Equal(top.Bottom, bottom.Top);
    }

    [Fact]
    public void Clamp_keeps_zones_inside_and_at_least_minimum_size()
    {
        var bounds = new PixelRect(0, 0, 1000, 800);
        var clamped = Clamp(new PixelRect(980, -30, 990, 20), bounds);
        Assert.True(bounds.Contains(clamped));
        Assert.True(clamped.Width >= MinZonePixels && clamped.Height >= MinZonePixels);
    }
}

public class ZoneEditModelTests
{
    private static readonly PixelRect LeftArea = new(0, 0, 1920, 1040);
    private static readonly PixelRect RightArea = new(1920, 0, 3840, 1040);
    private static readonly PixelRect Both = new(0, 0, 3840, 1040);

    [Fact]
    public void Drawing_on_empty_space_adds_a_zone_that_can_cross_monitors()
    {
        var model = new ZoneEditModel(Both, [LeftArea, RightArea], []);

        model.Press(new PixelPoint(1500, 100), grip: 10);
        model.Drag(new PixelPoint(2400, 900));
        model.Release();

        var zone = Assert.Single(model.Zones);
        Assert.True(zone.Left < 1920 && zone.Right > 1920, $"zone {zone} should straddle the monitor boundary");
        Assert.Equal(0, model.Selected);
    }

    [Fact]
    public void Tiny_drags_do_not_create_zones()
    {
        var model = new ZoneEditModel(Both, [LeftArea, RightArea], []);
        model.Press(new PixelPoint(500, 500), 10);
        model.Drag(new PixelPoint(510, 505));
        model.Release();
        Assert.Empty(model.Zones);
    }

    [Fact]
    public void Drawing_snaps_to_the_monitor_boundary()
    {
        var model = new ZoneEditModel(Both, [LeftArea, RightArea], []);
        model.Press(new PixelPoint(3, 4), 10);
        model.Drag(new PixelPoint(1914, 1036));
        model.Release();
        Assert.Equal(LeftArea, model.Zones[0]);
    }

    [Fact]
    public void Moving_a_zone_snaps_it_next_to_another_and_stays_inside()
    {
        var model = new ZoneEditModel(LeftArea, [LeftArea], [new PixelRect(0, 0, 960, 1040), new PixelRect(1200, 100, 1600, 500)]);

        model.Press(new PixelPoint(1400, 300), 10);   // body of the second zone
        model.Drag(new PixelPoint(1165, 300));        // its left edge lands 5 px from x = 960
        model.Release();

        Assert.Equal(960, model.Zones[1].Left);
        Assert.Equal(400, model.Zones[1].Width);

        model.Press(new PixelPoint(1100, 300), 10);
        model.Drag(new PixelPoint(9000, 300));        // far off-screen
        model.Release();
        Assert.True(LeftArea.Contains(model.Zones[1]));
    }

    [Fact]
    public void Resizing_respects_the_minimum_size()
    {
        var model = new ZoneEditModel(LeftArea, [LeftArea], [new PixelRect(100, 100, 600, 600)]);
        model.Press(new PixelPoint(600, 350), 10);    // right edge
        model.Drag(new PixelPoint(-500, 350));
        model.Release();
        Assert.Equal(MinZonePixels, model.Zones[0].Width);
        Assert.Equal(100, model.Zones[0].Left);
    }

    [Fact]
    public void Split_and_delete_the_selected_zone()
    {
        var model = new ZoneEditModel(LeftArea, [LeftArea], [LeftArea]);
        model.Select(0);

        Assert.True(model.SplitSelected(SplitOrientation.Vertical));
        Assert.Equal(2, model.Zones.Count);
        Assert.Equal(model.Zones[0].Right, model.Zones[1].Left);

        Assert.True(model.DeleteSelected());
        Assert.Single(model.Zones);
        Assert.False(model.DeleteSelected()); // nothing selected any more
    }

    [Fact]
    public void Saved_zones_are_fractions_numbered_left_to_right()
    {
        var model = new ZoneEditModel(Both, [LeftArea, RightArea], [RightArea, LeftArea]);
        var zones = model.ToLayoutZones();
        Assert.Equal(0, zones[0].X, 9);
        Assert.Equal(0.5, zones[1].X, 9);
        Assert.Equal(0.5, zones[0].Width, 9);
    }

    [Fact]
    public void Reset_gives_one_zone_per_monitor()
    {
        var model = new ZoneEditModel(Both, [LeftArea, RightArea], [new PixelRect(10, 10, 200, 200)]);
        model.ResetToWorkAreas();
        Assert.Equal([LeftArea, RightArea], model.Zones);
    }
}

public class ZoneCycleTests
{
    [Theory]
    [InlineData(new long[] { 10, 20, 30 }, 20, 1, 30)]
    [InlineData(new long[] { 10, 20, 30 }, 30, 1, 10)]
    [InlineData(new long[] { 10, 20, 30 }, 10, -1, 30)]
    [InlineData(new long[] { 10, 20, 30 }, 99, 1, 10)]
    [InlineData(new long[] { 10 }, 10, 1, 0)]
    public void Cycle_wraps_through_windows_sharing_a_zone(long[] group, long current, int delta, long expected)
    {
        Assert.Equal((nint)expected, ZoneMath.CycleTarget(group.Select(g => (nint)g).ToList(), (nint)current, delta));
    }
}
