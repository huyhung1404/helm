using Helm.Modules.Zones.Layouts;

namespace Helm.Tests;

public class LayoutTemplateTests
{
    [Theory]
    [InlineData(LayoutKind.Columns, 1)]
    [InlineData(LayoutKind.Columns, 3)]
    [InlineData(LayoutKind.Rows, 4)]
    [InlineData(LayoutKind.Grid, 4)]
    [InlineData(LayoutKind.Grid, 5)]
    [InlineData(LayoutKind.Grid, 7)]
    [InlineData(LayoutKind.PriorityGrid, 1)]
    [InlineData(LayoutKind.PriorityGrid, 2)]
    [InlineData(LayoutKind.PriorityGrid, 3)]
    [InlineData(LayoutKind.PriorityGrid, 4)]
    [InlineData(LayoutKind.PriorityGrid, 7)]
    [InlineData(LayoutKind.PriorityGrid, 11)]
    [InlineData(LayoutKind.Focus, 1)]
    [InlineData(LayoutKind.Focus, 5)]
    public void Templates_generate_requested_zone_count(LayoutKind kind, int count)
    {
        Assert.Equal(count, LayoutTemplates.Generate(kind, count).Count);
    }

    [Theory]
    [InlineData(LayoutKind.Columns, 3)]
    [InlineData(LayoutKind.Rows, 5)]
    [InlineData(LayoutKind.Grid, 5)]
    [InlineData(LayoutKind.Grid, 9)]
    [InlineData(LayoutKind.PriorityGrid, 6)]
    public void Grid_templates_tile_the_work_area_exactly(LayoutKind kind, int count)
    {
        var grid = LayoutTemplates.GenerateGrid(kind, count);
        Assert.True(grid.IsValid());
        var zones = grid.ToZones();
        Assert.Equal(1.0, zones.Sum(z => z.Width * z.Height), 6);
        foreach (var z in zones)
        {
            Assert.InRange(z.X, 0, 1);
            Assert.InRange(z.Right, 0, 1 + 1e-9);
            Assert.InRange(z.Bottom, 0, 1 + 1e-9);
        }
    }

    [Fact]
    public void Columns_are_equal_width_in_reading_order()
    {
        var zones = LayoutTemplates.Generate(LayoutKind.Columns, 4);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(i * 0.25, zones[i].X, 9);
            Assert.Equal(0.25, zones[i].Width, 9);
            Assert.Equal(1.0, zones[i].Height, 9);
        }
    }

    [Fact]
    public void Grid_of_five_is_two_rows_of_three_with_last_zone_merged()
    {
        var grid = LayoutTemplates.GenerateGrid(LayoutKind.Grid, 5);
        Assert.Equal(2, grid.RowCount);
        Assert.Equal(3, grid.ColumnCount);
        Assert.Equal([3, 4, 4], grid.Cells[1]);
        var last = grid.ToZones()[4];
        Assert.Equal(1.0 / 3, last.X, 6);
        Assert.Equal(2.0 / 3, last.Width, 6);
    }

    [Fact]
    public void Priority_grid_of_three_is_quarter_half_quarter()
    {
        var zones = LayoutTemplates.Generate(LayoutKind.PriorityGrid, 3);
        Assert.Equal(0.25, zones[0].Width, 9);
        Assert.Equal(0.5, zones[1].Width, 9);
        Assert.Equal(0.25, zones[2].Width, 9);
    }

    [Fact]
    public void Priority_grid_side_columns_split_into_rows()
    {
        var zones = LayoutTemplates.Generate(LayoutKind.PriorityGrid, 5);
        // left column: 2 zones stacked; center full height; right column: 2 zones stacked
        var center = zones.Single(z => Math.Abs(z.Width - 0.5) < 1e-9);
        Assert.Equal(1.0, center.Height, 9);
        Assert.Equal(4, zones.Count(z => Math.Abs(z.Height - 0.5) < 1e-9));
    }

    [Fact]
    public void Focus_zones_cascade_and_stay_inside()
    {
        var zones = LayoutTemplates.Focus(4);
        for (var i = 1; i < zones.Count; i++)
        {
            Assert.True(zones[i].X > zones[i - 1].X);
            Assert.True(zones[i].Y > zones[i - 1].Y);
        }
        Assert.All(zones, z => Assert.True(z.Right <= 1 && z.Bottom <= 1));
    }

    [Fact]
    public void Zone_count_is_clamped()
    {
        Assert.Single(LayoutTemplates.Generate(LayoutKind.Columns, 0));
        Assert.Equal(LayoutTemplates.MaxZones, LayoutTemplates.Generate(LayoutKind.Rows, 1000).Count);
    }

    [Fact]
    public void Defaults_contain_every_template_and_samples_on_fresh_file()
    {
        var file = new LayoutsFile { Version = LayoutsFile.CurrentVersion };
        Assert.True(file.EnsureDefaults());
        foreach (var kind in LayoutTemplates.Kinds)
            Assert.NotNull(file.Find(LayoutTemplates.TemplateId(kind)));
        Assert.Contains(file.Layouts, l => !l.IsTemplate);
        Assert.All(file.Layouts.Where(l => l.Kind == LayoutKind.CustomGrid), l => Assert.True(l.Grid!.IsValid()));
        Assert.False(file.EnsureDefaults());
    }
}
