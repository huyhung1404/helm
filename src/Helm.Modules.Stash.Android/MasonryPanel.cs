using Avalonia;
using Avalonia.Controls;

namespace Helm.Modules.Stash;

/// <summary>
/// Cards of different heights in equal columns, each card going into the shortest column so the gaps fill up (like a
/// photo wall). As many columns as fit at <see cref="MinColumnWidth"/> (two on a phone); the columns share the width.
/// </summary>
public sealed class MasonryPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<MasonryPanel, double>(nameof(MinColumnWidth), 150);

    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<MasonryPanel, double>(nameof(Spacing), 8);

    static MasonryPanel() => AffectsMeasure<MasonryPanel>(MinColumnWidthProperty, SpacingProperty);

    public double MinColumnWidth
    {
        get => GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinColumnWidth : availableSize.Width;
        return new Size(width, Layout(width, arrange: false));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <returns>The height of the tallest column.</returns>
    private double Layout(double width, bool arrange)
    {
        var (columns, columnWidth) = MasonryLayout.Columns(width, MinColumnWidth, Spacing);
        var heights = new double[columns];
        foreach (var child in Children)
        {
            if (!arrange) child.Measure(new Size(columnWidth, double.PositiveInfinity));
            if (!child.IsVisible) continue;
            var column = MasonryLayout.Shortest(heights);
            if (arrange) child.Arrange(new Rect(column * (columnWidth + Spacing), heights[column], columnWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Spacing;
        }
        return Math.Max(0, heights.Max() - Spacing);
    }
}
