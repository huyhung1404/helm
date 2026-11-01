namespace Helm.Modules.Habits;

public enum HabitCellState
{
    /// <summary>After today: still to come.</summary>
    Future,

    /// <summary>Before the habit started: greyed out, never counted.</summary>
    BeforeStart,

    Missed,
    Done,

    /// <summary>A habit to quit slipped that day: missed, with a reason written for it.</summary>
    Slipped,
}

/// <summary>
/// One day of the grid: its week (column), weekday (row, Monday first), what happened and, for a day done, how much of it
/// was done (1–100; 0 otherwise).
/// </summary>
public readonly record struct HabitGridCell(DateOnly Day, int Column, int Row, HabitCellState State, int Percent, bool IsToday);

/// <summary>
/// A habit's year as a contribution grid like GitHub's: one column per week (Monday on top), January 1 in the first
/// column and December 31 in the last. Pure data; each app draws it (and hit-tests it) with <see cref="HabitGridGeometry"/>,
/// or in two half-years with <see cref="HabitGridBands"/>.
/// The grid starts over each January; earlier years stay in the data and the report can show them.
/// </summary>
public sealed class HabitYearGrid
{
    private readonly Dictionary<(int, int), HabitGridCell> _byPosition;

    private HabitYearGrid(string habitId, HabitKind kind, int year, IReadOnlyList<HabitGridCell> cells, IReadOnlyList<HabitMonthLabel> months)
    {
        HabitId = habitId;
        Kind = kind;
        Year = year;
        Cells = cells;
        Months = months;
        Columns = cells.Max(c => c.Column) + 1;
        _byPosition = cells.ToDictionary(c => (c.Column, c.Row));
    }

    public string HabitId { get; }

    /// <summary>A habit to quit: its done days are clean days, and its slips are shown.</summary>
    public HabitKind Kind { get; }

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

    /// <summary>
    /// The grid of one year. Days after the habit's action day are still to come (so today, for a habit to quit), and
    /// for a habit to quit a missed day covered by one of <paramref name="lapses"/> is a slip. A done day carries its
    /// share from <paramref name="percents"/> (all of it when missing).
    /// </summary>
    public static HabitYearGrid Build(string habitId, Habit habit, IReadOnlySet<DateOnly> done, DateOnly today, int year, IEnumerable<HabitLapse>? lapses = null,
        IReadOnlyDictionary<DateOnly, int>? percents = null)
    {
        var cells = new List<HabitGridCell>(366);
        var last = new DateOnly(year, 12, 31);
        var actionDay = HabitDays.ActionDay(habit, today);
        var slipped = new HashSet<DateOnly>();
        if (habit.Kind == HabitKind.Quit)
        {
            foreach (var l in lapses ?? [])
            {
                for (var d = l.From; d <= l.To; d = d.AddDays(1)) slipped.Add(d);
            }
        }
        for (var day = new DateOnly(year, 1, 1); day <= last; day = day.AddDays(1))
        {
            var state = day > actionDay ? HabitCellState.Future
                : day < habit.StartDay ? HabitCellState.BeforeStart
                : done.Contains(day) ? HabitCellState.Done
                : slipped.Contains(day) ? HabitCellState.Slipped
                : HabitCellState.Missed;
            var percent = state == HabitCellState.Done ? (int)Math.Round(HabitStats.Share(percents, day) * 100) : 0;
            cells.Add(new HabitGridCell(day, Column(day), Row(day), state, percent, day == today));
        }
        var months = Enumerable.Range(1, 12)
            .Select(m => new HabitMonthLabel(Column(new DateOnly(year, m, 1)), new DateOnly(year, m, 1).ToString("MMM", System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        return new HabitYearGrid(habitId, habit.Kind, year, cells, months);
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

    /// <summary>
    /// Whether the whole year fits <paramref name="width"/> in one band with cells of at least <paramref name="minCell"/>
    /// (a wide screen); otherwise it goes in two (<see cref="HabitGridBands"/>).
    /// </summary>
    public static bool FitsOneBand(double width, int columns, double minCell, double gap = 3) =>
        double.IsFinite(width) && (width - LabelWidth - gap * (columns - 1)) / columns >= minCell;

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

/// <summary>
/// Where the cells of a <see cref="HabitYearGrid"/> go when the year is drawn in two bands, January–June above
/// July–December, so all of it fits a phone's width without scrolling. Device-independent units; month names above
/// each band, no weekday names. The week of July 1 opens the second band.
/// </summary>
public readonly record struct HabitGridBands(double Cell, double Gap, int Split, int Columns)
{
    public const double LabelHeight = 16;
    public const double BandGap = 10;

    /// <summary>The largest cell (within limits) that fits the wider band into <paramref name="width"/>.</summary>
    public static HabitGridBands Fit(double width, HabitYearGrid grid, double minCell, double maxCell, double gap = 2)
    {
        var split = HabitYearGrid.Column(new DateOnly(grid.Year, 7, 1));
        var widest = Math.Max(split, grid.Columns - split);
        var cell = (width - gap * (widest - 1)) / widest;
        return new HabitGridBands(Math.Clamp(double.IsFinite(cell) ? cell : minCell, minCell, maxCell), gap, split, grid.Columns);
    }

    /// <summary>Weeks in the wider band.</summary>
    public int Widest => Math.Max(Split, Columns - Split);

    public double BandHeight => LabelHeight + HabitYearGrid.Rows * Cell + (HabitYearGrid.Rows - 1) * Gap;

    public double Width => Widest * Cell + (Widest - 1) * Gap;

    public double Height => 2 * BandHeight + BandGap;

    /// <summary>The second band holds the weeks from <see cref="Split"/> on.</summary>
    public int Band(int column) => column >= Split ? 1 : 0;

    public double X(int column) => (column - Band(column) * Split) * (Cell + Gap);

    /// <summary>The top of a band's month names.</summary>
    public double LabelY(int column) => Band(column) * (BandHeight + BandGap);

    public double Y(int column, int row) => LabelY(column) + LabelHeight + row * (Cell + Gap);

    /// <summary>The cell under a point (gaps count with the cell before them, the gap between bands with the nearer band).</summary>
    public (int Column, int Row)? Hit(double x, double y)
    {
        var band = y >= BandHeight + BandGap / 2 ? 1 : 0;
        var top = band * (BandHeight + BandGap) + LabelHeight;
        if (x < 0 || y < top) return null;
        var week = (int)(x / (Cell + Gap));
        var row = (int)((y - top) / (Cell + Gap));
        var column = week + band * Split;
        if (row >= HabitYearGrid.Rows || (band == 0 && week >= Split) || column >= Columns) return null;
        return (column, row);
    }
}
