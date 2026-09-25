namespace Helm.Modules.Zones.Layouts;

public enum SplitOrientation
{
    /// <summary>A vertical line: the zone becomes a left and a right part.</summary>
    Vertical,

    /// <summary>A horizontal line: the zone becomes a top and a bottom part.</summary>
    Horizontal,
}

/// <summary>
/// A grid layout: row heights and column widths as fractions (each summing to 1) plus a cell → zone map.
/// Merged cells share a zone index; every zone must occupy a full rectangle of cells. Instances are treated as
/// immutable — every edit returns a new, normalized grid.
/// </summary>
public sealed class GridLayout
{
    public const double MinFraction = 0.02;

    public GridLayout() { }

    public GridLayout(IEnumerable<double> rows, IEnumerable<double> columns, int[][] cells)
    {
        Rows = rows.ToList();
        Columns = columns.ToList();
        Cells = cells;
    }

    public List<double> Rows { get; set; } = [1.0];

    public List<double> Columns { get; set; } = [1.0];

    /// <summary>[row][column] → zone index.</summary>
    public int[][] Cells { get; set; } = [[0]];

    public int RowCount => Rows.Count;
    public int ColumnCount => Columns.Count;
    public int ZoneCount => Cells.SelectMany(r => r).Distinct().Count();

    public static GridLayout Single() => new([1.0], [1.0], [[0]]);

    /// <summary>Evenly sized grid where cell k (row-major) belongs to zone <paramref name="map"/>(row, col).</summary>
    public static GridLayout Uniform(int rows, int columns, Func<int, int, int> map)
    {
        var cells = new int[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new int[columns];
            for (var c = 0; c < columns; c++) cells[r][c] = map(r, c);
        }
        return new GridLayout(Enumerable.Repeat(1.0 / rows, rows), Enumerable.Repeat(1.0 / columns, columns), cells).Normalize();
    }

    public GridLayout Clone() => new(Rows, Columns, Cells.Select(r => (int[])r.Clone()).ToArray());

    public double ColumnStart(int c) => Columns.Take(c).Sum();
    public double ColumnEnd(int c) => Columns.Take(c + 1).Sum();
    public double RowStart(int r) => Rows.Take(r).Sum();
    public double RowEnd(int r) => Rows.Take(r + 1).Sum();

    /// <summary>Zones in index order as fractional rectangles.</summary>
    public IReadOnlyList<ZoneRect> ToZones()
    {
        var bounds = CellBounds();
        return bounds.OrderBy(kv => kv.Key)
            .Select(kv => ZoneRect.FromEdges(ColumnStart(kv.Value.C0), RowStart(kv.Value.R0), ColumnEnd(kv.Value.C1), RowEnd(kv.Value.R1)))
            .ToList();
    }

    /// <summary>True when every zone's cells form a solid rectangle and fractions are sane.</summary>
    public bool IsValid()
    {
        if (Rows.Count == 0 || Columns.Count == 0 || Cells.Length != Rows.Count) return false;
        if (Cells.Any(r => r.Length != Columns.Count)) return false;
        if (Math.Abs(Rows.Sum() - 1) > 1e-3 || Math.Abs(Columns.Sum() - 1) > 1e-3) return false;
        foreach (var (zone, b) in CellBounds())
        {
            for (var r = b.R0; r <= b.R1; r++)
                for (var c = b.C0; c <= b.C1; c++)
                    if (Cells[r][c] != zone) return false;
        }
        return true;
    }

    /// <summary>Splits <paramref name="zone"/> at absolute fraction <paramref name="position"/> (x for vertical, y for horizontal).</summary>
    public GridLayout Split(int zone, SplitOrientation orientation, double position)
    {
        if (orientation == SplitOrientation.Horizontal)
            return Transpose().Split(zone, SplitOrientation.Vertical, position).Transpose();

        var bounds = CellBounds();
        if (!bounds.TryGetValue(zone, out var b)) return this;
        var x0 = ColumnStart(b.C0);
        var x1 = ColumnEnd(b.C1);
        if (position < x0 + MinFraction || position > x1 - MinFraction) return this;

        var g = Clone();
        // Find the column containing the split; reuse an existing boundary when the split lands on it.
        var boundary = -1; // index of the first column right of the split
        for (var c = b.C0; c <= b.C1; c++)
        {
            var start = g.ColumnStart(c);
            var end = g.ColumnEnd(c);
            if (Math.Abs(position - start) < 1e-4 && c > b.C0) { boundary = c; break; }
            if (position > start && position < end)
            {
                if (position - start < MinFraction / 2 || end - position < MinFraction / 2) return this;
                g.Columns[c] = position - start;
                g.Columns.Insert(c + 1, end - position);
                foreach (var row in Enumerable.Range(0, g.RowCount))
                {
                    var list = g.Cells[row].ToList();
                    list.Insert(c + 1, list[c]);
                    g.Cells[row] = list.ToArray();
                }
                boundary = c + 1;
                b = b with { C1 = b.C1 + 1 };
                break;
            }
        }
        if (boundary < 0) return this;

        var newZone = g.Cells.SelectMany(r => r).Max() + 1;
        for (var r = b.R0; r <= b.R1; r++)
            for (var c = boundary; c <= b.C1; c++)
                g.Cells[r][c] = newZone;
        return g.Normalize();
    }

    /// <summary>
    /// Merges the given zones plus whatever else is needed to keep the result rectangular (the smallest enclosing
    /// block of whole zones).
    /// </summary>
    public GridLayout Merge(IEnumerable<int> zones)
    {
        var selected = zones.ToHashSet();
        if (selected.Count < 2) return this;
        var bounds = CellBounds();
        if (!selected.All(bounds.ContainsKey)) return this;

        var (r0, c0, r1, c1) = (int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);
        bool grew;
        do
        {
            grew = false;
            foreach (var z in selected)
            {
                var b = bounds[z];
                r0 = Math.Min(r0, b.R0); c0 = Math.Min(c0, b.C0);
                r1 = Math.Max(r1, b.R1); c1 = Math.Max(c1, b.C1);
            }
            for (var r = r0; r <= r1; r++)
                for (var c = c0; c <= c1; c++)
                    if (selected.Add(Cells[r][c])) grew = true;
        }
        while (grew);

        var g = Clone();
        var target = selected.Min();
        for (var r = r0; r <= r1; r++)
            for (var c = c0; c <= c1; c++)
                g.Cells[r][c] = target;
        return g.Normalize();
    }

    /// <summary>Moves the line between column <paramref name="index"/> and <paramref name="index"/>+1 (or rows) to <paramref name="position"/>.</summary>
    public GridLayout MoveSplitter(SplitOrientation orientation, int index, double position)
    {
        var sizes = orientation == SplitOrientation.Vertical ? Columns : Rows;
        if (index < 0 || index >= sizes.Count - 1) return this;
        var start = sizes.Take(index).Sum();
        var end = sizes.Take(index + 2).Sum();
        var p = Math.Clamp(position, start + MinFraction, end - MinFraction);

        var g = Clone();
        var target = orientation == SplitOrientation.Vertical ? g.Columns : g.Rows;
        target[index] = p - start;
        target[index + 1] = end - p;
        return g;
    }

    /// <summary>
    /// Segments where a splitter line is visible: the boundary after column (or row) <c>Index</c>, between
    /// <c>From</c> and <c>To</c> along the other axis (fractions). Used by the editor to draw draggable splitters.
    /// </summary>
    public IReadOnlyList<(SplitOrientation Orientation, int Index, double Position, double From, double To)> Splitters()
    {
        var result = new List<(SplitOrientation, int, double, double, double)>();
        for (var c = 0; c < ColumnCount - 1; c++)
        {
            var x = ColumnEnd(c);
            foreach (var (from, to) in Runs(RowCount, r => Cells[r][c] != Cells[r][c + 1], RowStart, RowEnd))
                result.Add((SplitOrientation.Vertical, c, x, from, to));
        }
        for (var r = 0; r < RowCount - 1; r++)
        {
            var y = RowEnd(r);
            foreach (var (from, to) in Runs(ColumnCount, c => Cells[r][c] != Cells[r + 1][c], ColumnStart, ColumnEnd))
                result.Add((SplitOrientation.Horizontal, r, y, from, to));
        }
        return result;
    }

    public GridLayout Transpose()
    {
        var cells = new int[ColumnCount][];
        for (var c = 0; c < ColumnCount; c++)
        {
            cells[c] = new int[RowCount];
            for (var r = 0; r < RowCount; r++) cells[c][r] = Cells[r][c];
        }
        return new GridLayout(Columns, Rows, cells);
    }

    /// <summary>Collapses redundant rows/columns and renumbers zones in reading order (row-major first appearance).</summary>
    public GridLayout Normalize()
    {
        var rows = Rows.ToList();
        var columns = Columns.ToList();
        var cells = Cells.Select(r => r.ToList()).ToList();

        for (var c = columns.Count - 1; c > 0; c--)
        {
            if (cells.All(row => row[c] == row[c - 1]))
            {
                columns[c - 1] += columns[c];
                columns.RemoveAt(c);
                foreach (var row in cells) row.RemoveAt(c);
            }
        }
        for (var r = rows.Count - 1; r > 0; r--)
        {
            if (cells[r].SequenceEqual(cells[r - 1]))
            {
                rows[r - 1] += rows[r];
                rows.RemoveAt(r);
                cells.RemoveAt(r);
            }
        }

        var renumber = new Dictionary<int, int>();
        foreach (var id in cells.SelectMany(r => r))
            if (!renumber.ContainsKey(id)) renumber[id] = renumber.Count;

        return new GridLayout(rows, columns, cells.Select(r => r.Select(id => renumber[id]).ToArray()).ToArray());
    }

    private Dictionary<int, (int R0, int C0, int R1, int C1)> CellBounds()
    {
        var bounds = new Dictionary<int, (int R0, int C0, int R1, int C1)>();
        for (var r = 0; r < Cells.Length; r++)
        {
            for (var c = 0; c < Cells[r].Length; c++)
            {
                var z = Cells[r][c];
                bounds[z] = bounds.TryGetValue(z, out var b)
                    ? (Math.Min(b.R0, r), Math.Min(b.C0, c), Math.Max(b.R1, r), Math.Max(b.C1, c))
                    : (r, c, r, c);
            }
        }
        return bounds;
    }

    private static IEnumerable<(double From, double To)> Runs(int count, Func<int, bool> visible, Func<int, double> start, Func<int, double> end)
    {
        var runStart = -1;
        for (var i = 0; i <= count; i++)
        {
            var on = i < count && visible(i);
            if (on && runStart < 0) runStart = i;
            if (!on && runStart >= 0)
            {
                yield return (start(runStart), end(i - 1));
                runStart = -1;
            }
        }
    }
}
