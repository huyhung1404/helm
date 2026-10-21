using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Core.Desktop;
using Helm.Core.Geometry;
using Helm.Modules.WindowZones.Model;

namespace Helm.Modules.WindowZones.Editor;

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
        Focus();
        _session.Edit.Press(ToScreen(e.GetPosition(this)), Grip);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var edit = _session.Edit;
        var p = ToScreen(e.GetPosition(this));
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
        {
            edit.Drag(p);
            return;
        }

        var (_, part) = edit.HitTest(p, Grip);
        Cursor = part switch
        {
            ZoneGeometry.Edges.Move => Cursors.SizeAll,
            ZoneGeometry.Edges.Left or ZoneGeometry.Edges.Right => Cursors.SizeWE,
            ZoneGeometry.Edges.Top or ZoneGeometry.Edges.Bottom => Cursors.SizeNS,
            ZoneGeometry.Edges.Left | ZoneGeometry.Edges.Top or ZoneGeometry.Edges.Right | ZoneGeometry.Edges.Bottom => Cursors.SizeNWSE,
            ZoneGeometry.Edges.Right | ZoneGeometry.Edges.Top or ZoneGeometry.Edges.Left | ZoneGeometry.Edges.Bottom => Cursors.SizeNESW,
            _ => edit.CanDrawAt(p) ? Cursors.Cross : Cursors.No,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        _session.Edit.Release();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var edit = _session.Edit;
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

        var edit = _session.Edit;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fill = new SolidColorBrush(Color.FromArgb(0x99, 0x22, 0x22, 0x22));
        var selectedFill = new SolidColorBrush(Color.FromArgb(0xAA, _accent.R, _accent.G, _accent.B));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), 1.5);
        var accentPen = new Pen(new SolidColorBrush(_accent), 2);
        var screen = new Rect(0, 0, w, h);

        for (var i = 0; i < edit.Zones.Count; i++)
        {
            var zone = edit.Zones[i];
            var r = ToLocal(zone.Rect);
            if (!r.IntersectsWith(screen)) continue;
            var isSelected = i == edit.Selected;
            dc.DrawRectangle(isSelected ? selectedFill : fill, isSelected ? accentPen : stroke, r);

            var visible = Rect.Intersect(r, screen);
            var (number, name) = _session.LabelFor(zone);
            var big = new FormattedText(number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), Math.Clamp(Math.Min(visible.Width, visible.Height) / 4, 14, 64), Brushes.White, dpi);
            var center = new Point(visible.X + visible.Width / 2, visible.Y + visible.Height / 2);
            dc.DrawText(big, new Point(center.X - big.Width / 2, center.Y - big.Height / 2));
            if (name.Length > 0)
            {
                var caption = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 14, Brushes.White, dpi) { MaxTextWidth = Math.Max(1, visible.Width - 16), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                dc.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y + big.Height / 2));
            }
            var size = new FormattedText($"{zone.Rect.Width} × {zone.Rect.Height}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, dpi);
            if (size.Width < visible.Width - 12) dc.DrawText(size, new Point(visible.X + 8, visible.Bottom - size.Height - 6));

            if (isSelected)
                foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    dc.DrawRectangle(Brushes.White, accentPen, new Rect(corner.X - 4, corner.Y - 4, 8, 8));
        }

        if (edit.Drawing is { } d)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, _accent.R, _accent.G, _accent.B)),
                new Pen(new SolidColorBrush(_accent), 1.5) { DashStyle = DashStyles.Dash }, ToLocal(d));
        }

        // The taskbar strip is not part of the work area: shade it so it is clear zones stop there.
        foreach (var strip in Outside(_monitor.Bounds, _monitor.WorkArea))
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), null, ToLocal(strip));
    }

    private static IEnumerable<PixelRect> Outside(PixelRect bounds, PixelRect work)
    {
        if (work.Top > bounds.Top) yield return new PixelRect(bounds.Left, bounds.Top, bounds.Right, work.Top);
        if (work.Bottom < bounds.Bottom) yield return new PixelRect(bounds.Left, work.Bottom, bounds.Right, bounds.Bottom);
        if (work.Left > bounds.Left) yield return new PixelRect(bounds.Left, work.Top, work.Left, work.Bottom);
        if (work.Right < bounds.Right) yield return new PixelRect(work.Right, work.Top, bounds.Right, work.Bottom);
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
