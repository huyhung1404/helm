using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Helm.Modules.Habits;

/// <summary>
/// A habit's year as a GitHub-style contribution grid (<see cref="HabitYearGrid"/>), drawn like the Windows one: one
/// rounded square per day (green when done, deeper as more of the day was done; empty when missed; soft red on a slip of
/// a habit to quit; faded when it does not count; today outlined). Two layouts:
/// <list type="bullet">
/// <item><see cref="Fit"/> (the cards): the whole year at a glance without scrolling: in one band as wide as the control
/// when its cells can be at least <see cref="MinWideCell"/> (a tablet, a phone turned sideways), otherwise in two
/// half-year bands (<see cref="HabitGridBands"/>). Turning the phone switches between the two.</item>
/// <item>Otherwise (the report): one band of <see cref="CellSize"/> cells with Mon/Wed/Fri on the left, wider than a
/// phone; put it in a sideways <see cref="ScrollViewer"/>, which it scrolls to today.</item>
/// </list>
/// Tapping a day runs <see cref="Command"/> with a <see cref="HabitDayTap"/> and stops there, so the card around it does
/// not take the tap.
/// </summary>
public sealed class HabitGrid : Control
{
    public static readonly StyledProperty<HabitYearGrid?> GridProperty = AvaloniaProperty.Register<HabitGrid, HabitYearGrid?>(nameof(Grid));

    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<HabitGrid, ICommand?>(nameof(Command));

    public static readonly StyledProperty<double> CellSizeProperty = AvaloniaProperty.Register<HabitGrid, double>(nameof(CellSize), 12);

    public static readonly StyledProperty<bool> FitProperty = AvaloniaProperty.Register<HabitGrid, bool>(nameof(Fit));

    private const double MinBandCell = 4;
    private const double MaxBandCell = 16;

    /// <summary>The smallest cell of a fitted grid in one band; below it the year goes in two.</summary>
    public const double MinWideCell = 9;

    /// <summary>The largest cell of a fitted grid in one band, so it fills a tablet without turning into big tiles.</summary>
    private const double MaxWideCell = 24;

    private bool _scrolled;
    private HabitGridBands _bands;

    /// <summary>A fitted grid that is wide enough for one band: its geometry (null: two bands).</summary>
    private HabitGridGeometry? _wide;

    static HabitGrid()
    {
        AffectsRender<HabitGrid>(GridProperty, CellSizeProperty, FitProperty);
        AffectsMeasure<HabitGrid>(GridProperty, CellSizeProperty, FitProperty);
    }

    public HabitGrid()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        Tapped += OnTapped;
    }

    public HabitYearGrid? Grid
    {
        get => GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public double CellSize
    {
        get => GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    /// <summary>The whole year in two bands as wide as the control is given, instead of one band of <see cref="CellSize"/>.</summary>
    public bool Fit
    {
        get => GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    /// <summary>The geometry of a grid in one band: fitted to a wide control, or of <see cref="CellSize"/> cells.</summary>
    private HabitGridGeometry Geometry => _wide ?? new(CellSize, 3, HabitGridGeometry.LabelWidth, HabitGridGeometry.LabelHeight);

    private bool InBands => Fit && _wide is null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Another year (the report) lands on its own last week again; today's refresh keeps the scroll.
        if (change.Property == GridProperty && change.OldValue is HabitYearGrid old && change.NewValue is HabitYearGrid now && old.Year != now.Year) _scrolled = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _wide = null;
        if (Fit && Grid is { } grid)
        {
            var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 360;
            if (HabitGridGeometry.FitsOneBand(width, grid.Columns, MinWideCell))
            {
                _wide = HabitGridGeometry.Fit(width, grid.Columns, MinWideCell, MaxWideCell);
            }
            else
            {
                _bands = HabitGridBands.Fit(width, grid, MinBandCell, MaxBandCell);
                return new Size(_bands.Width, _bands.Height);
            }
        }
        var g = Geometry;
        return new Size(g.Width(Grid?.Columns ?? 53), g.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (!Fit && !_scrolled && Grid is not null) Dispatcher.UIThread.Post(ScrollToFocus, DispatcherPriority.Loaded);
        return size;
    }

    /// <summary>Shows the latest weeks (today near the right edge) in the scroll viewer around the grid.</summary>
    private void ScrollToFocus()
    {
        if (_scrolled || Grid is not { } grid || this.FindAncestorOfType<ScrollViewer>() is not { } scroller || scroller.Viewport.Width <= 0) return;
        _scrolled = true;
        var g = Geometry;
        var right = g.X(grid.FocusColumn) + g.Cell + 4 * (g.Cell + g.Gap);
        scroller.Offset = new Vector(Math.Clamp(right - scroller.Viewport.Width, 0, Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width)), 0);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Grid is not { } grid) return;
        var dark = (ActualThemeVariant ?? Application.Current?.ActualThemeVariant) == ThemeVariant.Dark;
        var secondary = this.TryFindResource("HelmSecondaryText", ActualThemeVariant, out var s) && s is IBrush b ? b : Brushes.Gray;
        var accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var a) && a is Color c ? new ImmutableSolidColorBrush(c) : new ImmutableSolidColorBrush(Colors.DodgerBlue);
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        FormattedText Label(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, secondary);

        Func<int, int, Rect> place;
        if (InBands)
        {
            var bands = _bands;
            foreach (var month in grid.Months)
            {
                // A month that starts in the last week of its band would run off it.
                var lastOfBand = bands.Band(month.Column) == 0 ? bands.Split - 1 : grid.Columns - 1;
                if (month.Column >= lastOfBand) continue;
                context.DrawText(Label(month.Name), new Point(bands.X(month.Column), bands.LabelY(month.Column)));
            }
            place = (column, row) => new Rect(bands.X(column), bands.Y(column, row), bands.Cell, bands.Cell);
        }
        else
        {
            var g = Geometry;
            foreach (var month in grid.Months)
            {
                if (month.Column >= grid.Columns - 1) continue;
                context.DrawText(Label(month.Name), new Point(g.X(month.Column), 0));
            }
            foreach (var (row, name) in new[] { (0, "Mon"), (2, "Wed"), (4, "Fri") })
            {
                var text = Label(name);
                context.DrawText(text, new Point(0, g.Y(row) + (g.Cell - text.Height) / 2));
            }
            place = (column, row) => new Rect(g.X(column), g.Y(row), g.Cell, g.Cell);
        }

        var empty = Brush(dark ? HabitColors.DarkEmpty : HabitColors.LightEmpty);
        var faded = Brush(dark ? HabitColors.DarkFaded : HabitColors.LightFaded);
        var slipped = Brush(dark ? HabitColors.DarkSlipped : HabitColors.LightSlipped);
        var done = new Dictionary<int, IBrush>();
        var todayPen = new ImmutablePen(accent, 1.5);
        foreach (var cell in grid.Cells)
        {
            var fill = cell.State switch
            {
                HabitCellState.Done => done.TryGetValue(cell.Percent, out var f) ? f : done[cell.Percent] = Brush(HabitColors.ForPercent(cell.Percent, dark)),
                HabitCellState.Missed => empty,
                HabitCellState.Slipped => slipped,
                _ => faded,
            };
            var rect = place(cell.Column, cell.Row);
            context.DrawRectangle(fill, cell.IsToday ? todayPen : null, new RoundedRect(rect, Math.Max(1, rect.Width / 5)));
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (Grid is not { } grid) return;
        var p = e.GetPosition(this);
        var hit = InBands ? _bands.Hit(p.X, p.Y) : Geometry.Hit(p.X, p.Y, grid.Columns);
        if (hit is null || grid.At(hit.Value.Column, hit.Value.Row) is not { } cell) return;
        // A day was hit: the card around the grid must not open today as well.
        e.Handled = true;
        if (cell.State == HabitCellState.Future) return;
        var tap = new HabitDayTap(grid.HabitId, cell.Day);
        if (Command is { } command && command.CanExecute(tap)) command.Execute(tap);
    }

    private static IBrush Brush(uint argb) => new ImmutableSolidColorBrush(Color.FromUInt32(argb));
}

/// <summary>Colours from the view model ("#AARRGGBB") as brushes, without reflection (the Release build is trimmed).</summary>
public static class HabitBrushes
{
    public static IValueConverter Hex { get; } = new FuncValueConverter<string?, IBrush?>(v =>
        v is not null && Color.TryParse(v, out var c) ? new ImmutableSolidColorBrush(c) : null);
}
