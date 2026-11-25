using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Helm.Core.Ui;

/// <summary>
/// Softens the cut where a scrolling list meets what stays fixed above or below it: while there is more to scroll to,
/// that edge fades out over a few pixels instead of ending in a hard line through a card. Nothing fades at the very
/// top or bottom of the content, so the first and last card keep their edges and shadows.
/// <c>core:ScrollFade.Enabled="True"</c> on a ScrollViewer.
/// </summary>
public static class ScrollFade
{
    /// <summary>How far the edge fades, in pixels.</summary>
    public const double Length = 22;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, ScrollViewer, bool>("Enabled");

    static ScrollFade() => EnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, e) =>
    {
        if (e.NewValue is not true) return;
        viewer.ScrollChanged += (_, _) => Update(viewer);
        viewer.PropertyChanged += (_, args) =>
        {
            if (args.Property == Visual.BoundsProperty) Update(viewer);
        };
    });

    public static bool GetEnabled(ScrollViewer viewer) => viewer.GetValue(EnabledProperty);

    public static void SetEnabled(ScrollViewer viewer, bool value) => viewer.SetValue(EnabledProperty, value);

    private static void Update(ScrollViewer viewer)
    {
        var height = viewer.Bounds.Height;
        if (height <= 0) return;
        var top = viewer.Offset.Y > 1;
        var bottom = viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y > 1;
        if (!top && !bottom)
        {
            if (viewer.OpacityMask is not null) viewer.OpacityMask = null;
            return;
        }
        var edge = Math.Min(Length / height, 0.25);
        viewer.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(top ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, top ? edge : 0),
                new GradientStop(Colors.Black, bottom ? 1 - edge : 1),
                new GradientStop(bottom ? Colors.Transparent : Colors.Black, 1),
            },
        };
    }
}
