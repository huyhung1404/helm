using System.Windows;
using System.Windows.Controls;

namespace Helm.Modules.Stash;

/// <summary>
/// Cards of different heights in equal columns, each card going into the shortest column so the gaps fill up (like a
/// photo wall). As many columns as fit at <see cref="MinColumnWidth"/>; the columns share the width.
/// </summary>
public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(240.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
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
        foreach (UIElement child in InternalChildren)
        {
            if (!arrange) child.Measure(new Size(columnWidth, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) continue;
            var column = MasonryLayout.Shortest(heights);
            if (arrange) child.Arrange(new Rect(column * (columnWidth + Spacing), heights[column], columnWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Spacing;
        }
        return Math.Max(0, heights.Max() - Spacing);
    }
}
