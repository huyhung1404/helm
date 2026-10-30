namespace Helm.Modules.Habits;

public enum HabitCellState
{
    /// <summary>After today: still to come.</summary>
    Future,

    /// <summary>Before the habit started: greyed out, never counted.</summary>
    BeforeStart,

    Missed,
    Done,
}

/// <summary>One day of the grid: its week (column), weekday (row, Monday first) and what happened.</summary>
public readonly record struct HabitGridCell(DateOnly Day, int Column, int Row, HabitCellState State, int Level, bool IsToday);

/// <summary>
/// A habit's year as a contribution grid like GitHub's: one column per week (Monday on top), January 1 in the first
/// column and December 31 in the last. Pure data; each app draws it (and hit-tests it) with <see cref="HabitGridGeometry"/>.
/// The grid starts over each January; earlier years stay in the data and the report can show them.
/// </summary>
public sealed class HabitYearGrid
{
    private readonly Dictionary<(int, int), HabitGridCell> _byPosition;

    private HabitYearGrid(string habitId, int year, IReadOnlyList<HabitGridCell> cells, IReadOnlyList<HabitMonthLabel> months)
    {
        HabitId = habitId;
        Year = year;
        Cells = cells;
        Months = months;
        Columns = cells.Max(c => c.Column) + 1;
        _byPosition = cells.ToDictionary(c => (c.Column, c.Row));
    }

    public string HabitId { get; }

    public int Year { get; }

    /// <summary>Weeks (53 or 54).</summary>
    public int Columns { get; }

    public const int Rows = 7;

    public IReadOnlyList<HabitGridCell> Cells { get; }

    public IReadOnlyList<HabitMonthLabel> Months { get; }

    /// <summary>The column of today, or of the last day shown for a past year (where a sideways scroll should land).</summary>
    public int FocusColumn => Cells.LastOrDefault(c => c.State != HabitCellState.Future).Column;

    /// <summary>Monday = 0 … Sunday = 6.</summary>
    public static int Row(DateOnly day) => ((int)day.DayOfWeek + 6) % 7;

    public static int Column(DateOnly day) => (day.DayOfYear - 1 + Row(new DateOnly(day.Year, 1, 1))) / 7;

    public HabitGridCell? At(int column, int row) => _byPosition.TryGetValue((column, row), out var cell) ? cell : null;

    public static HabitYearGrid Build(string habitId, Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, int year)
    {
        var cells = new List<HabitGridCell>(366);
        var last = new DateOnly(year, 12, 31);
        for (var day = new DateOnly(year, 1, 1); day <= last; day = day.AddDays(1))
        {
            var state = day > today ? HabitCellState.Future
                : day < habit.StartDay ? HabitCellState.BeforeStart
                : done.Contains(day) ? HabitCellState.Done
                : HabitCellState.Missed;
            var level = state == HabitCellState.Done ? HabitColors.Level(HabitStats.RunEndingOn(done, day)) : 0;
            cells.Add(new HabitGridCell(day, Column(day), Row(day), state, level, day == today));
        }
        var months = Enumerable.Range(1, 12)
            .Select(m => new HabitMonthLabel(Column(new DateOnly(year, m, 1)), new DateOnly(year, m, 1).ToString("MMM", System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        return new HabitYearGrid(habitId, year, cells, months);
    }
}

public readonly record struct HabitMonthLabel(int Column, string Name);

/// <summary>Where the cells and labels of a <see cref="HabitYearGrid"/> go, in device-independent units.</summary>
public readonly record struct HabitGridGeometry(double Cell, double Gap, double Left, double Top)
{
    public const double LabelWidth = 30;
    public const double LabelHeight = 16;

    /// <summary>The largest cell (within limits) that fits the grid into <paramref name="width"/>.</summary>
    public static HabitGridGeometry Fit(double width, int columns, double minCell, double maxCell, double gap = 3)
    {
        var cell = (width - LabelWidth - gap * (columns - 1)) / columns;
        return new HabitGridGeometry(Math.Clamp(double.IsFinite(cell) ? cell : minCell, minCell, maxCell), gap, LabelWidth, LabelHeight);
    }

    public double X(int column) => Left + column * (Cell + Gap);

    public double Y(int row) => Top + row * (Cell + Gap);

    public double Width(int columns) => Left + columns * Cell + (columns - 1) * Gap;

    public double Height => Top + HabitYearGrid.Rows * Cell + (HabitYearGrid.Rows - 1) * Gap;

    /// <summary>The cell under a point (gaps count with the cell before them, so a tap never falls through).</summary>
    public (int Column, int Row)? Hit(double x, double y, int columns)
    {
        if (x < Left || y < Top) return null;
        var column = (int)((x - Left) / (Cell + Gap));
        var row = (int)((y - Top) / (Cell + Gap));
        return column < columns && row < HabitYearGrid.Rows ? (column, row) : null;
    }
}
