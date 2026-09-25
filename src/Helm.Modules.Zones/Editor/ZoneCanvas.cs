using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones.Editor;

/// <summary>
/// Draws the session's zones on one monitor and forwards mouse input to the shared <see cref="ZoneEditModel"/>.
/// All geometry is in global physical pixels: <see cref="Visual.PointToScreen"/> / <see cref="Visual.PointFromScreen"/>
/// convert, so a zone dragged from one monitor onto another continues seamlessly (the mouse is captured).
/// </summary>
public sealed class ZoneCanvas : FrameworkElement
{
    private const double GripDips = 10;

    private readonly EditorSession _session;
    private readonly MonitorInfo _monitor;
    private readonly Color _accent;

    public ZoneCanvas(EditorSession session, MonitorInfo monitor, Color accent)
    {
        _session = session;
        _monitor = monitor;
        _accent = accent;
        Focusable = true;
        _session.VisualsChanged += (_, _) => InvalidateVisual();
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private int Grip => (int)Math.Round(GripDips * Scale);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_session.Edit is not { } edit) return;
        Focus();
        edit.Press(ToScreen(e.GetPosition(this)), Grip);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_session.Edit is not { } edit) return;
        var p = ToScreen(e.GetPosition(this));
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
        {
            edit.Drag(p);
            return;
        }

        var (_, part) = edit.HitTest(p, Grip);
        Cursor = part switch
        {
            LayoutGeometry.Edges.Move => Cursors.SizeAll,
            LayoutGeometry.Edges.Left or LayoutGeometry.Edges.Right => Cursors.SizeWE,
            LayoutGeometry.Edges.Top or LayoutGeometry.Edges.Bottom => Cursors.SizeNS,
            LayoutGeometry.Edges.Left | LayoutGeometry.Edges.Top or LayoutGeometry.Edges.Right | LayoutGeometry.Edges.Bottom => Cursors.SizeNWSE,
            LayoutGeometry.Edges.Right | LayoutGeometry.Edges.Top or LayoutGeometry.Edges.Left | LayoutGeometry.Edges.Bottom => Cursors.SizeNESW,
            _ => edit.Reference.Contains(p) ? Cursors.Cross : Cursors.No,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_session.Edit is not { } edit) return;
        ReleaseMouseCapture();
        edit.Release();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (_session.Edit is not { } edit) return;
        var (index, _) = edit.HitTest(ToScreen(e.GetPosition(this)), Grip);
        if (index >= 0)
        {
            edit.Select(index);
            edit.DeleteSelected();
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0 || PresentationSource.FromVisual(this) is null) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, w, h)); // hit-testable

        var (zones, selected, drawing, reference) = _session.VisualsFor(_monitor);
        var editing = _session.IsEditing;

        // Outside the editable area (monitors not in a spanning layout, or other monitors for a one-monitor layout).
        if (editing && reference is { } refRect && !refRect.IntersectsWith(_monitor.Bounds))
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), null, new Rect(0, 0, w, h));
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fill = new SolidColorBrush(Color.FromArgb(editing ? (byte)0x99 : (byte)0x77, 0x22, 0x22, 0x22));
        var selectedFill = new SolidColorBrush(Color.FromArgb(0xAA, _accent.R, _accent.G, _accent.B));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), 1.5);
        var accentPen = new Pen(new SolidColorBrush(_accent), 2);

        for (var i = 0; i < zones.Count; i++)
        {
            var r = ToLocal(zones[i]);
            if (!r.IntersectsWith(new Rect(0, 0, w, h))) continue;
            var isSelected = i == selected;
            dc.DrawRectangle(isSelected ? selectedFill : fill, isSelected ? accentPen : stroke, r);

            var visible = Rect.Intersect(r, new Rect(0, 0, w, h)); // label where this monitor sees the zone
            var number = new FormattedText($"{i + 1}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), Math.Clamp(Math.Min(visible.Width, visible.Height) / 4, 14, 64), Brushes.White, dpi);
            dc.DrawText(number, new Point(visible.X + (visible.Width - number.Width) / 2, visible.Y + (visible.Height - number.Height) / 2));
            var size = new FormattedText($"{zones[i].Width} × {zones[i].Height}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, dpi);
            if (size.Width < visible.Width - 12) dc.DrawText(size, new Point(visible.X + 8, visible.Bottom - size.Height - 6));

            if (isSelected && editing)
                foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    dc.DrawRectangle(Brushes.White, accentPen, new Rect(corner.X - 4, corner.Y - 4, 8, 8));
        }

        if (drawing is { } d)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, _accent.R, _accent.G, _accent.B)),
                new Pen(new SolidColorBrush(_accent), 1.5) { DashStyle = DashStyles.Dash }, ToLocal(d));
        }
    }

    private PixelPoint ToScreen(Point local)
    {
        var p = PointToScreen(local);
        return new PixelPoint((int)Math.Round(p.X), (int)Math.Round(p.Y));
    }

    private Rect ToLocal(PixelRect r)
    {
        var a = PointFromScreen(new Point(r.Left, r.Top));
        var b = PointFromScreen(new Point(r.Right, r.Bottom));
        return new Rect(a, b);
    }
}
