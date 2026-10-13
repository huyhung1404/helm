using System.Windows;
using System.Windows.Media;

namespace Helm.Modules.Workspace;

/// <summary>The Workspace Controller icon as a WPF vector image (<see cref="WorkspaceIconShape"/>).</summary>
public static class WorkspaceLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Create();

    private static DrawingImage Create()
    {
        static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
        const double size = WorkspaceIconShape.Size;
        var gradient = new LinearGradientBrush(Argb(WorkspaceIconShape.GradientStart), Argb(WorkspaceIconShape.GradientEnd),
            new Point(0, size), new Point(size, 0)) { MappingMode = BrushMappingMode.Absolute };
        var frame = new Pen(gradient, WorkspaceIconShape.Stroke) { LineJoin = PenLineJoin.Round };
        var chevron = new Pen(gradient, WorkspaceIconShape.Stroke + 1)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var group = new DrawingGroup();
        // A transparent box keeps the icon's own margins at any size.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))));
        group.Children.Add(new GeometryDrawing(null, frame, Geometry.Parse(WorkspaceIconShape.Frame)));
        group.Children.Add(new GeometryDrawing(null, chevron, Geometry.Parse(WorkspaceIconShape.Chevron)));
        group.Children.Add(new GeometryDrawing(gradient, null, Geometry.Parse(WorkspaceIconShape.Cursor)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
