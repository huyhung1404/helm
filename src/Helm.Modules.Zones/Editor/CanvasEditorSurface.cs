using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones.Editor;

/// <summary>
/// Free-form zone editor: drag on empty space to draw a zone, drag a zone to move it, drag its edges/corners to
/// resize, Delete or right-click removes the selected zone. Zones are fractions of the work area.
/// </summary>
public sealed class CanvasEditorSurface : FrameworkElement
{
    public static readonly DependencyProperty ZonesProperty = DependencyProperty.Register(
        nameof(Zones), typeof(IReadOnlyList<ZoneRect>), typeof(CanvasEditorSurface),
        new FrameworkPropertyMetadata(Array.Empty<ZoneRect>(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(CanvasEditorSurface),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
        nameof(PixelSize), typeof(Size), typeof(CanvasEditorSurface), new FrameworkPropertyMetadata(new Size(1920, 1080), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(CanvasEditorSurface), new FrameworkPropertyMetadata(Color.FromRgb(0, 120, 212), FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Grip = 10;
    private const double MinSize = 0.03;

    [Flags]
    private enum Handle { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16, Draw = 32 }

    private Handle _handle;
    private Point _downPoint;
    private ZoneRect _original;
    private Point? _drawCurrent;

    public CanvasEditorSurface()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public IReadOnlyList<ZoneRect> Zones
    {
        get => (IReadOnlyList<ZoneRect>)GetValue(ZonesProperty);
        set => SetValue(ZonesProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public Size PixelSize
    {
        get => (Size)GetValue(PixelSizeProperty);
        set => SetValue(PixelSizeProperty, value);
    }

    public Color Accent
    {
        get => (Color)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _downPoint = p;
        var (index, handle) = HitTest(p);
        if (index >= 0)
        {
            SelectedIndex = index;
            _original = Zones[index];
            _handle = handle;
        }
        else
        {
            SelectedIndex = -1;
            _handle = Handle.Draw;
            _drawCurrent = p;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (e.LeftButton != MouseButtonState.Pressed || _handle == Handle.None)
        {
            var (_, h) = HitTest(p);
            Cursor = h switch
            {
                Handle.Move => Cursors.SizeAll,
                Handle.Left or Handle.Right => Cursors.SizeWE,
                Handle.Top or Handle.Bottom => Cursors.SizeNS,
                Handle.Left | Handle.Top or Handle.Right | Handle.Bottom => Cursors.SizeNWSE,
                Handle.Right | Handle.Top or Handle.Left | Handle.Bottom => Cursors.SizeNESW,
                _ => Cursors.Cross,
            };
            return;
        }

        if (_handle == Handle.Draw)
        {
            _drawCurrent = p;
            InvalidateVisual();
            return;
        }

        var dx = (p.X - _downPoint.X) / ActualWidth;
        var dy = (p.Y - _downPoint.Y) / ActualHeight;
        var r = _original;
        double left = r.X, top = r.Y, right = r.Right, bottom = r.Bottom;
        if (_handle == Handle.Move)
        {
            left += dx; right += dx; top += dy; bottom += dy;
            var shiftX = left < 0 ? -left : right > 1 ? 1 - right : 0;
            var shiftY = top < 0 ? -top : bottom > 1 ? 1 - bottom : 0;
            left += shiftX; right += shiftX; top += shiftY; bottom += shiftY;
        }
        else
        {
            if (_handle.HasFlag(Handle.Left)) left = Math.Clamp(left + dx, 0, right - MinSize);
            if (_handle.HasFlag(Handle.Right)) right = Math.Clamp(right + dx, left + MinSize, 1);
            if (_handle.HasFlag(Handle.Top)) top = Math.Clamp(top + dy, 0, bottom - MinSize);
            if (_handle.HasFlag(Handle.Bottom)) bottom = Math.Clamp(bottom + dy, top + MinSize, 1);
        }
        Replace(SelectedIndex, ZoneRect.FromEdges(left, top, right, bottom));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        if (_handle == Handle.Draw && _drawCurrent is { } end)
        {
            var r = ZoneRect.FromEdges(
                Math.Min(_downPoint.X, end.X) / ActualWidth, Math.Min(_downPoint.Y, end.Y) / ActualHeight,
                Math.Max(_downPoint.X, end.X) / ActualWidth, Math.Max(_downPoint.Y, end.Y) / ActualHeight);
            if (r.Width >= MinSize && r.Height >= MinSize)
            {
                Zones = [.. Zones, r.Clamp(MinSize)];
                SelectedIndex = Zones.Count - 1;
            }
        }
        _handle = Handle.None;
        _drawCurrent = null;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var (index, _) = HitTest(e.GetPosition(this));
        if (index >= 0) Remove(index);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Delete or Key.Back && SelectedIndex >= 0)
        {
            Remove(SelectedIndex);
            e.Handled = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, w, h));

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fill = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20));
        var selectedFill = new SolidColorBrush(Color.FromArgb(0xAA, Accent.R, Accent.G, Accent.B));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), 1.5);
        var accentPen = new Pen(new SolidColorBrush(Accent), 2);

        for (var i = 0; i < Zones.Count; i++)
        {
            var r = ToRect(Zones[i]);
            var selected = i == SelectedIndex;
            dc.DrawRectangle(selected ? selectedFill : fill, selected ? accentPen : stroke, r);
            var label = new FormattedText($"{i + 1}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"), Math.Clamp(Math.Min(r.Width, r.Height) / 4, 14, 64), Brushes.White, dpi);
            dc.DrawText(label, new Point(r.X + (r.Width - label.Width) / 2, r.Y + (r.Height - label.Height) / 2));
            var size = new FormattedText($"{Math.Round(Zones[i].Width * PixelSize.Width)} × {Math.Round(Zones[i].Height * PixelSize.Height)}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Brushes.White, dpi);
            if (size.Width < r.Width - 8) dc.DrawText(size, new Point(r.X + 8, r.Bottom - size.Height - 6));

            if (selected)
            {
                foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                    dc.DrawRectangle(Brushes.White, accentPen, new Rect(corner.X - 4, corner.Y - 4, 8, 8));
            }
        }

        if (_handle == Handle.Draw && _drawCurrent is { } end)
        {
            var r = new Rect(_downPoint, end);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B)), new Pen(new SolidColorBrush(Accent), 1.5) { DashStyle = DashStyles.Dash }, r);
        }
    }

    private Rect ToRect(ZoneRect z) => new(z.X * ActualWidth, z.Y * ActualHeight, z.Width * ActualWidth, z.Height * ActualHeight);

    /// <summary>Topmost zone under <paramref name="p"/> (last drawn wins) and which part of it was grabbed.</summary>
    private (int Index, Handle Handle) HitTest(Point p)
    {
        for (var i = Zones.Count - 1; i >= 0; i--)
        {
            var r = ToRect(Zones[i]);
            var outer = r;
            outer.Inflate(Grip / 2, Grip / 2);
            if (!outer.Contains(p)) continue;

            var handle = Handle.None;
            if (Math.Abs(p.X - r.Left) <= Grip) handle |= Handle.Left;
            else if (Math.Abs(p.X - r.Right) <= Grip) handle |= Handle.Right;
            if (Math.Abs(p.Y - r.Top) <= Grip) handle |= Handle.Top;
            else if (Math.Abs(p.Y - r.Bottom) <= Grip) handle |= Handle.Bottom;
            return (i, handle == Handle.None ? Handle.Move : handle);
        }
        return (-1, Handle.None);
    }

    private void Replace(int index, ZoneRect zone)
    {
        if (index < 0 || index >= Zones.Count) return;
        var list = Zones.ToList();
        list[index] = zone;
        Zones = list;
    }

    private void Remove(int index)
    {
        var list = Zones.ToList();
        list.RemoveAt(index);
        Zones = list;
        SelectedIndex = -1;
    }
}
