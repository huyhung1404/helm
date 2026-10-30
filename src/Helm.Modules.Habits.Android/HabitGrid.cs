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
/// A habit's year as a GitHub-style contribution grid (<see cref="HabitYearGrid"/>), drawn like the Windows one: month
/// names on top, Mon/Wed/Fri on the left, one rounded square per day (green when done, deeper as the run grows; empty
/// when missed; faded when it does not count; today outlined). Cells keep a size a finger can hit, so the grid is wider
/// than a phone: put it in a sideways <see cref="ScrollViewer"/>, which it scrolls to today. Tapping a day runs
/// <see cref="Command"/> with a <see cref="HabitDayTap"/> and stops there, so the card around it does not take the tap.
/// </summary>
public sealed class HabitGrid : Control
{
    public static readonly StyledProperty<HabitYearGrid?> GridProperty = AvaloniaProperty.Register<HabitGrid, HabitYearGrid?>(nameof(Grid));

    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<HabitGrid, ICommand?>(nameof(Command));

    public static readonly StyledProperty<double> CellSizeProperty = AvaloniaProperty.Register<HabitGrid, double>(nameof(CellSize), 12);

    private bool _scrolled;

    static HabitGrid()
    {
        AffectsRender<HabitGrid>(GridProperty, CellSizeProperty);
        AffectsMeasure<HabitGrid>(GridProperty, CellSizeProperty);
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

    private HabitGridGeometry Geometry => new(CellSize, 3, HabitGridGeometry.LabelWidth, HabitGridGeometry.LabelHeight);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Another year (the report) lands on its own last week again; today's refresh keeps the scroll.
        if (change.Property == GridProperty && change.OldValue is HabitYearGrid old && change.NewValue is HabitYearGrid now && old.Year != now.Year) _scrolled = false;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var g = Geometry;
        return new Size(g.Width(Grid?.Columns ?? 53), g.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (!_scrolled && Grid is not null) Dispatcher.UIThread.Post(ScrollToFocus, DispatcherPriority.Loaded);
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
        var g = Geometry;
        var dark = (ActualThemeVariant ?? Application.Current?.ActualThemeVariant) == ThemeVariant.Dark;
        var secondary = this.TryFindResource("HelmSecondaryText", ActualThemeVariant, out var s) && s is IBrush b ? b : Brushes.Gray;
        var accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var a) && a is Color c ? new ImmutableSolidColorBrush(c) : new ImmutableSolidColorBrush(Colors.DodgerBlue);
        var typeface = new Typeface(TextElement.GetFontFamily(this));
        FormattedText Label(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, secondary);

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

        var levels = (dark ? HabitColors.DarkLevels : HabitColors.LightLevels).Select(Brush).ToArray();
        var empty = Brush(dark ? HabitColors.DarkEmpty : HabitColors.LightEmpty);
        var faded = Brush(dark ? HabitColors.DarkFaded : HabitColors.LightFaded);
        var todayPen = new ImmutablePen(accent, 1.5);
        var radius = Math.Max(1.5, g.Cell / 5);
        foreach (var cell in grid.Cells)
        {
            var fill = cell.State switch
            {
                HabitCellState.Done => levels[Math.Clamp(cell.Level, 1, 4) - 1],
                HabitCellState.Missed => empty,
                _ => faded,
            };
            context.DrawRectangle(fill, cell.IsToday ? todayPen : null, new RoundedRect(new Rect(g.X(cell.Column), g.Y(cell.Row), g.Cell, g.Cell), radius));
        }
    }

    private void OnTapped(object? sender, TappedEventArgs e)
    {
        if (Grid is not { } grid || Geometry.Hit(e.GetPosition(this).X, e.GetPosition(this).Y, grid.Columns) is not { } hit
            || grid.At(hit.Column, hit.Row) is not { } cell) return;
        // A day was hit: the card around the grid must not tick today as well.
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
