using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Helm.Modules.Habits;

/// <summary>
/// A habit's year as a GitHub-style contribution grid (<see cref="HabitYearGrid"/>): month names on top, Mon/Wed/Fri on
/// the left, one rounded square per day. Done days are green (deeper as the run grows), missed days empty, days that do
/// not count faded, today outlined. The cells grow with the width up to <see cref="MaxCell"/>. Clicking a day runs
/// <see cref="Command"/> with a <see cref="HabitDayTap"/>; the click stops here, so a card around the grid does not
/// take it too.
/// </summary>
public sealed class HabitGrid : FrameworkElement
{
    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        nameof(Grid), typeof(HabitYearGrid), typeof(HabitGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(HabitGrid));

    public static readonly DependencyProperty MaxCellProperty = DependencyProperty.Register(
        nameof(MaxCell), typeof(double), typeof(HabitGrid), new FrameworkPropertyMetadata(13.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const double MinCell = 5;
    private HabitGridGeometry _geometry;
    private (int Column, int Row)? _pressed;

    public HabitGrid()
    {
        Cursor = Cursors.Hand;
        Loaded += (_, _) => ApplicationThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => ApplicationThemeManager.Changed -= OnThemeChanged;
    }

    public HabitYearGrid? Grid
    {
        get => (HabitYearGrid?)GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public double MaxCell
    {
        get => (double)GetValue(MaxCellProperty);
        set => SetValue(MaxCellProperty, value);
    }

    private void OnThemeChanged(ApplicationTheme theme, Color accent) => InvalidateVisual();

    protected override Size MeasureOverride(Size available)
    {
        var columns = Grid?.Columns ?? 53;
        var width = double.IsInfinity(available.Width) ? HabitGridGeometry.LabelWidth + columns * (MaxCell + 3) : available.Width;
        _geometry = HabitGridGeometry.Fit(width, columns, MinCell, MaxCell);
        return new Size(Math.Min(_geometry.Width(columns), width), _geometry.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _geometry = HabitGridGeometry.Fit(finalSize.Width, Grid?.Columns ?? 53, MinCell, MaxCell);
        return finalSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Grid is not { } grid) return;
        var g = _geometry;
        var dark = IsDark();
        var secondary = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;
        var accent = TryFindResource("AccentTextFillColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(System.Windows.Documents.TextElement.GetFontFamily(this) ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText Label(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, secondary, dpi);

        foreach (var month in grid.Months)
        {
            // The label of a month that starts in the last column would run off the grid.
            if (month.Column >= grid.Columns - 1) continue;
            dc.DrawText(Label(month.Name), new Point(g.X(month.Column), 0));
        }
        foreach (var (row, name) in new[] { (0, "Mon"), (2, "Wed"), (4, "Fri") })
        {
            var text = Label(name);
            dc.DrawText(text, new Point(0, g.Y(row) + (g.Cell - text.Height) / 2));
        }

        var levels = (dark ? HabitColors.DarkLevels : HabitColors.LightLevels).Select(Frozen).ToArray();
        var empty = Frozen(dark ? HabitColors.DarkEmpty : HabitColors.LightEmpty);
        var faded = Frozen(dark ? HabitColors.DarkFaded : HabitColors.LightFaded);
        var todayPen = new Pen(accent, 1.5);
        todayPen.Freeze();
        var radius = Math.Max(1.5, g.Cell / 5);
        foreach (var cell in grid.Cells)
        {
            var fill = cell.State switch
            {
                HabitCellState.Done => levels[Math.Clamp(cell.Level, 1, 4) - 1],
                HabitCellState.Missed => empty,
                _ => faded,
            };
            var rect = new Rect(g.X(cell.Column), g.Y(cell.Row), g.Cell, g.Cell);
            dc.DrawRoundedRectangle(fill, cell.IsToday ? todayPen : null, rect, radius, radius);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var cell = CellAt(e.GetPosition(this));
        ToolTip = cell is { } c ? Describe(c) : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // Taken here, so the card around the grid does not tick today instead.
        e.Handled = true;
        _pressed = CellAt(e.GetPosition(this)) is { } c ? (c.Column, c.Row) : null;
        if (_pressed is not null) CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        e.Handled = true;
        var pressed = _pressed;
        _pressed = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (pressed is null || CellAt(e.GetPosition(this)) is not { } cell || (cell.Column, cell.Row) != pressed.Value || Grid is not { } grid) return;
        if (cell.State == HabitCellState.Future) return;
        var tap = new HabitDayTap(grid.HabitId, cell.Day);
        if (Command is { } command && command.CanExecute(tap)) command.Execute(tap);
    }

    private HabitGridCell? CellAt(Point p) =>
        Grid is { } grid && _geometry.Hit(p.X, p.Y, grid.Columns) is { } hit ? grid.At(hit.Column, hit.Row) : null;

    private static string Describe(HabitGridCell cell)
    {
        var day = cell.Day.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        return cell.State switch
        {
            HabitCellState.Done => $"{day}: done",
            HabitCellState.Missed => cell.IsToday ? $"{day}: not done yet" : $"{day}: missed",
            HabitCellState.BeforeStart => $"{day}: before the habit started",
            _ => day,
        };
    }

    private bool IsDark()
    {
        if (TryFindResource("TextFillColorPrimaryBrush") is not SolidColorBrush brush) return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var c = brush.Color;
        return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 128;
    }

    private static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }
}
