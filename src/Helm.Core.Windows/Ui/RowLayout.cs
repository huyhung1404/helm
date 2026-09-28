using System.Windows;
using System.Windows.Controls;

namespace Helm.Core.Ui;

/// <summary>
/// A settings row: the first child (title/description) takes the remaining width and wraps; every other child is a
/// control laid out on the right at its natural size. Unlike a single-cell Grid with a right-aligned control, the
/// text can never run underneath the control on narrow windows.
/// </summary>
public sealed class RowLayout : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(RowLayout),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var controls = Controls().ToList();
        double controlsWidth = 0, height = 0;
        foreach (var c in controls)
        {
            c.Measure(new Size(double.PositiveInfinity, available.Height));
            controlsWidth += c.DesiredSize.Width;
            height = Math.Max(height, c.DesiredSize.Height);
        }
        controlsWidth += Spacing * controls.Count;

        if (Text() is { } text)
        {
            var textWidth = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - controlsWidth);
            text.Measure(new Size(textWidth, available.Height));
            height = Math.Max(height, text.DesiredSize.Height);
            return new Size(text.DesiredSize.Width + controlsWidth, height);
        }
        return new Size(Math.Max(0, controlsWidth - Spacing), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var right = final.Width;
        foreach (var c in Controls().Reverse())
        {
            var w = c.DesiredSize.Width;
            right -= w;
            c.Arrange(new Rect(right, (final.Height - c.DesiredSize.Height) / 2, w, c.DesiredSize.Height));
            right -= Spacing;
        }

        if (Text() is { } text)
        {
            var width = Math.Max(0, right);
            var h = Math.Min(final.Height, text.DesiredSize.Height);
            text.Arrange(new Rect(0, (final.Height - h) / 2, width, h));
        }
        return final;
    }

    private UIElement? Text() => InternalChildren.Count > 0 && InternalChildren[0].Visibility != Visibility.Collapsed ? InternalChildren[0] : null;

    private IEnumerable<UIElement> Controls() =>
        InternalChildren.Cast<UIElement>().Skip(1).Where(c => c.Visibility != Visibility.Collapsed);
}
