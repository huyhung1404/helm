using Helm.Modules.Zones.Layouts;

namespace Helm.Tests;

public class GridLayoutTests
{
    [Fact]
    public void Vertical_split_creates_two_zones_side_by_side()
    {
        var grid = GridLayout.Single().Split(0, SplitOrientation.Vertical, 0.4);

        Assert.True(grid.IsValid());
        var zones = grid.ToZones();
        Assert.Equal(2, zones.Count);
        Assert.Equal(0.4, zones[0].Width, 9);
        Assert.Equal(0.4, zones[1].X, 9);
        Assert.Equal(0.6, zones[1].Width, 9);
    }

    [Fact]
    public void Horizontal_split_creates_two_zones_stacked()
    {
        var grid = GridLayout.Single().Split(0, SplitOrientation.Horizontal, 0.25);

        var zones = grid.ToZones();
        Assert.Equal(2, zones.Count);
        Assert.Equal(0.25, zones[0].Height, 9);
        Assert.Equal(0.25, zones[1].Y, 9);
    }

    [Fact]
    public void Splitting_one_row_keeps_other_rows_merged()
    {
        // two rows; split the bottom zone vertically → top stays one wide zone
        var grid = GridLayout.Single()
            .Split(0, SplitOrientation.Horizontal, 0.5)
            .Split(1, SplitOrientation.Vertical, 0.5);

        Assert.True(grid.IsValid());
        Assert.Equal(3, grid.ZoneCount);
        Assert.Equal([0, 0], grid.Cells[0]);
        Assert.Equal([1, 2], grid.Cells[1]);
    }

    [Fact]
    public void Split_on_existing_boundary_reuses_the_column()
    {
        var grid = new GridLayout([0.5, 0.5], [0.5, 0.5], [[0, 0], [1, 2]]);

        var split = grid.Split(0, SplitOrientation.Vertical, 0.5);

        Assert.Equal(2, split.ColumnCount);
        Assert.Equal(4, split.ZoneCount);
    }

    [Fact]
    public void Split_too_close_to_an_edge_is_ignored()
    {
        var grid = GridLayout.Single();
        Assert.Same(grid, grid.Split(0, SplitOrientation.Vertical, 0.001));
    }

    [Fact]
    public void Merge_two_neighbours_and_collapse_redundant_columns()
    {
        var grid = TestLayouts.ColumnsGrid(3);

        var merged = grid.Merge([1, 2]);

        Assert.True(merged.IsValid());
        Assert.Equal(2, merged.ZoneCount);
        Assert.Equal(2, merged.ColumnCount); // columns 1 and 2 became identical and collapsed
        Assert.Equal(2.0 / 3, merged.ToZones()[1].Width, 6);
    }

    [Fact]
    public void Merge_diagonal_zones_expands_to_a_rectangle()
    {
        var grid = TestLayouts.SquareGrid(2); // 2x2

        var merged = grid.Merge([0, 3]);

        Assert.Equal(1, merged.ZoneCount);
        Assert.True(merged.IsValid());
    }

    [Fact]
    public void Move_splitter_resizes_neighbours_and_clamps()
    {
        var grid = TestLayouts.ColumnsGrid(2);

        var moved = grid.MoveSplitter(SplitOrientation.Vertical, 0, 0.7);
        Assert.Equal(0.7, moved.Columns[0], 9);
        Assert.Equal(0.3, moved.Columns[1], 9);

        var clamped = grid.MoveSplitter(SplitOrientation.Vertical, 0, 5);
        Assert.Equal(1 - GridLayout.MinFraction, clamped.Columns[0], 9);
    }

    [Fact]
    public void Splitters_skip_segments_inside_merged_zones()
    {
        var grid = new GridLayout([0.5, 0.5], [0.5, 0.5], [[0, 0], [1, 2]]);

        var vertical = grid.Splitters().Where(s => s.Orientation == SplitOrientation.Vertical).ToList();

        var segment = Assert.Single(vertical);
        Assert.Equal(0.5, segment.From, 9); // only the bottom row has a visible vertical line
        Assert.Equal(1.0, segment.To, 9);
    }

    [Fact]
    public void Normalize_renumbers_in_reading_order()
    {
        var grid = new GridLayout([1.0], [0.5, 0.5], [[7, 3]]).Normalize();
        Assert.Equal([0, 1], grid.Cells[0]);
    }

    [Fact]
    public void Transpose_twice_is_identity()
    {
        var grid = new GridLayout([0.5, 0.5], [0.25, 0.5, 0.25], [[0, 2, 3], [1, 2, 4]]);
        var back = grid.Transpose().Transpose();
        Assert.Equal(grid.Rows, back.Rows);
        Assert.Equal(grid.Columns, back.Columns);
        Assert.Equal(grid.Cells, back.Cells);
    }
}
