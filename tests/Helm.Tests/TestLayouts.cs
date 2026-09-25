using Helm.Modules.Zones.Layouts;

namespace Helm.Tests;

/// <summary>Simple grids for tests (the built-in templates no longer exist).</summary>
internal static class TestLayouts
{
    public static GridLayout ColumnsGrid(int n) => GridLayout.Uniform(1, n, (_, c) => c);

    public static GridLayout RowsGrid(int n) => GridLayout.Uniform(n, 1, (r, _) => r);

    public static GridLayout SquareGrid(int side) => GridLayout.Uniform(side, side, (r, c) => r * side + c);

    public static IReadOnlyList<ZoneRect> Columns(int n) => ColumnsGrid(n).ToZones();

    public static IReadOnlyList<ZoneRect> Rows(int n) => RowsGrid(n).ToZones();

    public static IReadOnlyList<ZoneRect> Square(int side) => SquareGrid(side).ToZones();
}
