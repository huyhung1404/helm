using Helm.Core.Geometry;
using Helm.Modules.Zones.Layouts;

namespace Helm.Tests;

public class ZoneMathTests
{
    private static readonly PixelRect WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void Without_spacing_neighbours_share_exact_edges()
    {
        var zones = ZoneMath.ToPixels(LayoutTemplates.Generate(LayoutKind.Columns, 3), WorkArea, spacing: 0);

        Assert.Equal(new PixelRect(0, 0, 640, 1040), zones[0]);
        Assert.Equal(zones[0].Right, zones[1].Left);
        Assert.Equal(zones[1].Right, zones[2].Left);
        Assert.Equal(1920, zones[2].Right);
    }

    [Fact]
    public void Spacing_is_exact_between_zones_and_at_borders()
    {
        const int s = 16;
        var zones = ZoneMath.ToPixels(LayoutTemplates.Generate(LayoutKind.Columns, 2), WorkArea, s);

        Assert.Equal(s, zones[0].Left - WorkArea.Left);
        Assert.Equal(s, zones[0].Top - WorkArea.Top);
        Assert.Equal(s, zones[1].Left - zones[0].Right);
        Assert.Equal(s, WorkArea.Right - zones[1].Right);
        Assert.Equal(s, WorkArea.Bottom - zones[1].Bottom);
    }

    [Fact]
    public void Odd_spacing_still_produces_exact_gaps()
    {
        var zones = ZoneMath.ToPixels(LayoutTemplates.Generate(LayoutKind.Rows, 3), WorkArea, 7);
        Assert.Equal(7, zones[1].Top - zones[0].Bottom);
        Assert.Equal(7, zones[2].Top - zones[1].Bottom);
    }

    [Fact]
    public void Work_area_offset_is_respected_for_secondary_monitors()
    {
        var secondary = new PixelRect(1920, -200, 1920 + 2560, -200 + 1400);
        var zones = ZoneMath.ToPixels([new ZoneRect(0.5, 0.5, 0.5, 0.5)], secondary, 0);
        Assert.Equal(new PixelRect(1920 + 1280, -200 + 700, 1920 + 2560, 1200), zones[0]);
    }

    [Fact]
    public void Canvas_layouts_ignore_spacing()
    {
        var zones = ZoneMath.ToPixels([new ZoneRect(0, 0, 0.5, 0.5)], WorkArea, 16, applySpacing: false);
        Assert.Equal(new PixelRect(0, 0, 960, 520), zones[0]);
    }

    [Theory]
    [InlineData(16, 96u, 16)]
    [InlineData(16, 144u, 24)]
    [InlineData(10, 120u, 13)]
    public void Spacing_scales_with_dpi(int spacing, uint dpi, int expected)
    {
        Assert.Equal(expected, ZoneMath.ScaleSpacing(spacing, dpi));
    }

    [Fact]
    public void Hit_test_prefers_containing_zone_then_nearest_within_sensitivity()
    {
        var zones = ZoneMath.ToPixels(LayoutTemplates.Generate(LayoutKind.Columns, 2), WorkArea, 20);
        // inside zone 1
        Assert.Equal(1, ZoneMath.HitTest(zones, new PixelPoint(1500, 500), 10));
        // in the 20px gap, 4px from zone 0's right edge
        var gapPoint = new PixelPoint(zones[0].Right + 3, 500);
        Assert.Equal(0, ZoneMath.HitTest(zones, gapPoint, 10));
        Assert.Equal(-1, ZoneMath.HitTest(zones, gapPoint, 1));
    }

    [Fact]
    public void Hit_test_picks_smallest_overlapping_canvas_zone()
    {
        var zones = new List<PixelRect> { new(0, 0, 1000, 1000), new(100, 100, 300, 300) };
        Assert.Equal(1, ZoneMath.HitTest(zones, new PixelPoint(150, 150), 0));
    }

    [Fact]
    public void Span_selection_grows_to_a_rectangle()
    {
        var zones = ZoneMath.ToPixels(LayoutTemplates.Generate(LayoutKind.Grid, 9), WorkArea, 8); // 3x3
        var span = ZoneMath.SelectSpan(zones, 0, 4); // top-left to center
        Assert.Equal([0, 1, 3, 4], span);
        var union = ZoneMath.UnionOf(zones, span);
        Assert.Equal(zones[0].Left, union.Left);
        Assert.Equal(zones[4].Bottom, union.Bottom);
    }

    [Theory]
    [InlineData(3, null, 1, 0)]
    [InlineData(3, null, -1, 2)]
    [InlineData(3, 2, 1, 0)]
    [InlineData(3, 0, -1, 2)]
    [InlineData(3, 1, 1, 2)]
    public void Step_wraps_and_starts_from_ends(int count, int? current, int delta, int expected)
    {
        Assert.Equal(expected, ZoneMath.Step(count, current, delta));
    }

    [Fact]
    public void Rows_template_is_a_vertical_stack()
    {
        Assert.True(ZoneMath.IsVerticalStack(LayoutTemplates.Generate(LayoutKind.Rows, 3)));
        Assert.False(ZoneMath.IsVerticalStack(LayoutTemplates.Generate(LayoutKind.Columns, 3)));
    }

    [Fact]
    public void Match_zone_tolerates_small_offsets()
    {
        var zones = ZoneMath.ToPixels(LayoutTemplates.Generate(LayoutKind.Columns, 2), WorkArea, 0);
        Assert.Equal(1, ZoneMath.MatchZone(zones, zones[1].Inflate(2, 1)));
        Assert.Equal(-1, ZoneMath.MatchZone(zones, zones[1].Inflate(40, 0)));
    }
}
