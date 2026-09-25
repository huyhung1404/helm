using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Helm.Core.Geometry;
using Helm.Modules.Zones.Layouts;

namespace Helm.Modules.Zones.Editor;

/// <summary>
/// Interactive grid editor: click splits the zone under the cursor (vertical line; hold Shift for horizontal),
/// dragging a splitter resizes, dragging from one zone to another selects a rectangular block to merge.
/// All geometry edits go through <see cref="GridLayout"/>; this control only translates mouse input.
/// </summary>
public sealed class GridEditorSurface : FrameworkElement
{
    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        nameof(Grid), typeof(GridLayout), typeof(GridEditorSurface),
        new FrameworkPropertyMetadata(GridLayout.Single(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectedZonesProperty = DependencyProperty.Register(
        nameof(SelectedZones), typeof(IReadOnlyList<int>), typeof(GridEditorSurface),
        new FrameworkPropertyMetadata(Array.Empty<int>(), FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
        nameof(PixelSize), typeof(Size), typeof(GridEditorSurface), new FrameworkPropertyMetadata(new Size(1920, 1080), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(GridEditorSurface), new FrameworkPropertyMetadata(Color.FromRgb(0, 120, 212), FrameworkPropertyMetadataOptions.AffectsRender));

    private const double SplitterHitDistance = 6;
    private const double DragThreshold = 6;

    private Point? _hover;
    private (SplitOrientation Orientation, int Index)? _hoverSplitter;
    private (SplitOrientation Orientation, int Index)? _draggingSplitter;
    private Point _downPoint;
    private int _downZone = -1;
    private bool _selecting;
    private IReadOnlyList<int> _liveSelection = [];

    public GridEditorSurface()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public GridLayout Grid
    {
        get => (GridLayout)GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    public IReadOnlyList<int> SelectedZones
    {
        get => (IReadOnlyList<int>)GetValue(SelectedZonesProperty);
        set => SetValue(SelectedZonesProperty, value);
    }

    /// <summary>The monitor work-area size in physical pixels, for the size labels.</summary>
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        _hover = p;

        if (_draggingSplitter is { } s)
        {
            var fraction = s.Orientation == SplitOrientation.Vertical ? p.X / ActualWidth : p.Y / ActualHeight;
            Grid = Grid.MoveSplitter(s.Orientation, s.Index, fraction);
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed && _downZone >= 0)
        {
            if (!_selecting && (p - _downPoint).Length > DragThreshold) _selecting = true;
            if (_selecting)
            {
                var current = ZoneAt(p);
                _liveSelection = current < 0 ? [_downZone] : ZoneMath.SelectSpan(PixelZones(), _downZone, current);
                InvalidateVisual();
            }
            return;
        }

        _hoverSplitter = SplitterAt(p);
        Cursor = _hoverSplitter switch
        {
            { Orientation: SplitOrientation.Vertical } => Cursors.SizeWE,
            { Orientation: SplitOrientation.Horizontal } => Cursors.SizeNS,
            _ => Cursors.Cross,
        };
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        _hoverSplitter = null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        CaptureMouse();
        _downPoint = p;
        _selecting = false;
        _draggingSplitter = SplitterAt(p);
        _downZone = _draggingSplitter is null ? ZoneAt(p) : -1;
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
        var p = e.GetPosition(this);

        if (_draggingSplitter is not null)
        {
            _draggingSplitter = null;
        }
        else if (_selecting)
        {
            SelectedZones = _liveSelection.Count > 1 ? _liveSelection : [];
        }
        else if (_downZone >= 0)
        {
            var horizontal = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            Grid = horizontal
                ? Grid.Split(_downZone, SplitOrientation.Horizontal, p.Y / ActualHeight)
                : Grid.Split(_downZone, SplitOrientation.Vertical, p.X / ActualWidth);
            SelectedZones = [];
        }

        _selecting = false;
        _liveSelection = [];
        _downZone = -1;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        SelectedZones = [];
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.LeftShift or Key.RightShift) InvalidateVisual(); // flip the split preview
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.LeftShift or Key.RightShift) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(0, 0, w, h)); // hit-testable

        var zones = Grid.ToZones();
        var selection = _selecting ? _liveSelection : SelectedZones;
        var accent = new SolidColorBrush(Color.FromArgb(0x99, Accent.R, Accent.G, Accent.B));
        var fill = new SolidColorBrush(Color.FromArgb(0x88, 0x20, 0x20, 0x20));
        var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (var i = 0; i < zones.Count; i++)
        {
            var r = ToRect(zones[i]);
            dc.DrawRectangle(selection.Contains(i) ? accent : fill, stroke, r);
            DrawLabel(dc, r, (i + 1).ToString(CultureInfo.CurrentCulture), Math.Clamp(Math.Min(r.Width, r.Height) / 4, 14, 72), dpi, 0);
            var px = $"{Math.Round(zones[i].Width * PixelSize.Width)} × {Math.Round(zones[i].Height * PixelSize.Height)}";
            DrawLabel(dc, r, px, 13, dpi, Math.Clamp(Math.Min(r.Width, r.Height) / 4, 14, 72) * 0.8);
        }

        foreach (var s in Grid.Splitters())
        {
            var hot = _hoverSplitter is { } hs && hs.Orientation == s.Orientation && hs.Index == s.Index
                      || _draggingSplitter is { } ds && ds.Orientation == s.Orientation && ds.Index == s.Index;
            var pen = new Pen(hot ? new SolidColorBrush(Accent) : new SolidColorBrush(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)), hot ? 5 : 3);
            if (s.Orientation == SplitOrientation.Vertical)
                dc.DrawLine(pen, new Point(s.Position * w, s.From * h), new Point(s.Position * w, s.To * h));
            else
                dc.DrawLine(pen, new Point(s.From * w, s.Position * h), new Point(s.To * w, s.Position * h));
        }

        // Preview where a click would split.
        if (_hover is { } p && _hoverSplitter is null && _draggingSplitter is null && !_selecting)
        {
            var zone = ZoneAt(p);
            if (zone >= 0)
            {
                var r = ToRect(zones[zone]);
                var dashed = new Pen(new SolidColorBrush(Accent), 2) { DashStyle = DashStyles.Dash };
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    dc.DrawLine(dashed, new Point(r.Left, p.Y), new Point(r.Right, p.Y));
                else
                    dc.DrawLine(dashed, new Point(p.X, r.Top), new Point(p.X, r.Bottom));
            }
        }
    }

    private void DrawLabel(DrawingContext dc, Rect r, string text, double size, double dpi, double offsetY)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), size, Brushes.White, dpi);
        if (ft.Width > r.Width - 4) return;
        dc.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2 + offsetY));
    }

    private Rect ToRect(ZoneRect z) => new(z.X * ActualWidth, z.Y * ActualHeight, z.Width * ActualWidth, z.Height * ActualHeight);

    private IReadOnlyList<PixelRect> PixelZones() =>
        Grid.ToZones().Select(z => ToRect(z)).Select(r => new PixelRect((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom)).ToList();

    private int ZoneAt(Point p)
    {
        var zones = Grid.ToZones();
        for (var i = 0; i < zones.Count; i++)
            if (ToRect(zones[i]).Contains(p)) return i;
        return -1;
    }

    private (SplitOrientation Orientation, int Index)? SplitterAt(Point p)
    {
        foreach (var s in Grid.Splitters())
        {
            if (s.Orientation == SplitOrientation.Vertical)
            {
                var x = s.Position * ActualWidth;
                if (Math.Abs(p.X - x) <= SplitterHitDistance && p.Y >= s.From * ActualHeight && p.Y <= s.To * ActualHeight) return (s.Orientation, s.Index);
            }
            else
            {
                var y = s.Position * ActualHeight;
                if (Math.Abs(p.Y - y) <= SplitterHitDistance && p.X >= s.From * ActualWidth && p.X <= s.To * ActualWidth) return (s.Orientation, s.Index);
            }
        }
        return null;
    }
}
