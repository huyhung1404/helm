using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Helm.Core.Desktop;
using Helm.Modules.WindowZones.Editor;
using Helm.Modules.WindowZones.Model;
using GeoPoint = Helm.Core.Geometry.PixelPoint;
using GeoRect = Helm.Core.Geometry.PixelRect;

namespace Helm.Modules.WindowZones.Ui;

/// <summary>
/// The screens to scale, with their zones: the page's overview (read only) and the zone editor's canvas (editable). It
/// draws the session's monitors and zones (<see cref="EditorSession"/>, global physical pixels) and, when editable,
/// hands the pointer to the shared <see cref="ZoneEditModel"/> as the full-screen editor does: drag on empty space to
/// draw a zone, drag a zone to move it, its edges or corners to resize it, right-click to delete it. With a
/// <see cref="Viewport"/> it is the full-screen editor's canvas instead: that part of the desktop (one monitor) fills the
/// view at full size, over the editor's dark veil.
/// </summary>
public sealed class ZoneLayoutView : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty =
        AvaloniaProperty.Register<ZoneLayoutView, EditorSession?>(nameof(Session));

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<ZoneLayoutView, bool>(nameof(IsEditable));

    public static readonly StyledProperty<double> MaxLayoutHeightProperty =
        AvaloniaProperty.Register<ZoneLayoutView, double>(nameof(MaxLayoutHeight), 360);

    public static readonly StyledProperty<GeoRect?> ViewportProperty =
        AvaloniaProperty.Register<ZoneLayoutView, GeoRect?>(nameof(Viewport));

    /// <summary>How close to an edge (on screen, in this view's units) the pointer resizes instead of moving.</summary>
    private const double GripDips = 8;

    /// <summary>The same at full size (the full-screen editor).</summary>
    private const double FullScreenGripDips = 10;

    private const double Inset = 2;

    /// <summary>Room under the screens for their names.</summary>
    private const double CaptionHeight = 16;

    private EditorSession? _subscribed;
    private bool _pressed;

    public ZoneLayoutView()
    {
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public EditorSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public bool IsEditable
    {
        get => GetValue(IsEditableProperty);
        set => SetValue(IsEditableProperty, value);
    }

    public double MaxLayoutHeight
    {
        get => GetValue(MaxLayoutHeightProperty);
        set => SetValue(MaxLayoutHeightProperty, value);
    }

    /// <summary>
    /// The part of the desktop (global physical pixels) that fills this view, for the full-screen editor: one window per
    /// monitor, each showing its monitor. Null (the page and the sheet) shows every screen to scale.
    /// </summary>
    public GeoRect? Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    private bool IsFullScreen => Viewport is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ViewportProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == SessionProperty)
        {
            if (_subscribed is not null) _subscribed.VisualsChanged -= OnVisualsChanged;
            _subscribed = Session;
            if (_subscribed is not null) _subscribed.VisualsChanged += OnVisualsChanged;
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == IsEditableProperty)
        {
            Cursor = IsEditable ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
        }
    }

    private void OnVisualsChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>All the screens side by side as Windows arranges them, in global pixels.</summary>
    private GeoRect Desktop
    {
        get
        {
            if (Viewport is { } viewport) return viewport;
            var monitors = Session?.Monitors ?? [];
            if (monitors.Count == 0) return new GeoRect(0, 0, 1920, 1080);
            var all = monitors[0].Bounds;
            foreach (var m in monitors.Skip(1)) all = all.Union(m.Bounds);
            return all;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desktop = Desktop;
        if (IsFullScreen)
            return new Size(double.IsInfinity(availableSize.Width) ? desktop.Width : availableSize.Width,
                double.IsInfinity(availableSize.Height) ? desktop.Height : availableSize.Height);
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = width * desktop.Height / Math.Max(1, desktop.Width) + 2 * Inset + CaptionHeight;
        return new Size(width, Math.Min(height, MaxLayoutHeight));
    }

    /// <summary>Pixels per global screen pixel, and where the desktop's top-left corner is drawn.</summary>
    private (double Scale, Point Origin) Transform()
    {
        var desktop = Desktop;
        // Full screen the monitor covers the view exactly, so one of its pixels is 1 / its scaling here. A pointer captured
        // past the view's edge (a zone dragged onto the next monitor) maps on linearly, to the right global pixel.
        if (IsFullScreen) return (Math.Max(0.0001, Bounds.Width / Math.Max(1, desktop.Width)), new Point(0, 0));
        var w = Bounds.Width - 2 * Inset;
        var h = Bounds.Height - 2 * Inset - CaptionHeight;
        var scale = Math.Max(0.0001, Math.Min(w / Math.Max(1, desktop.Width), h / Math.Max(1, desktop.Height)));
        var origin = new Point(Inset + (w - desktop.Width * scale) / 2, Inset + (h - desktop.Height * scale) / 2);
        return (scale, origin);
    }

    private Rect ToLocal(GeoRect r)
    {
        var (scale, origin) = Transform();
        var desktop = Desktop;
        return new Rect(origin.X + (r.Left - desktop.Left) * scale, origin.Y + (r.Top - desktop.Top) * scale, r.Width * scale, r.Height * scale);
    }

    private GeoPoint ToScreen(Point local)
    {
        var (scale, origin) = Transform();
        var desktop = Desktop;
        return new GeoPoint((int)Math.Round(desktop.Left + (local.X - origin.X) / scale), (int)Math.Round(desktop.Top + (local.Y - origin.Y) / scale));
    }

    private int Grip => (int)Math.Round((IsFullScreen ? FullScreenGripDips : GripDips) / Transform().Scale);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEditable || Session is not { } session) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        session.Edit.Press(ToScreen(point.Position), Grip);
        e.Pointer.Capture(this);
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsEditable || Session is not { } session) return;
        var p = ToScreen(e.GetPosition(this));
        if (_pressed)
        {
            session.Edit.Drag(p);
            return;
        }
        var (_, part) = session.Edit.HitTest(p, Grip);
        Cursor = new Cursor(part switch
        {
            ZoneGeometry.Edges.Move => StandardCursorType.SizeAll,
            ZoneGeometry.Edges.Left or ZoneGeometry.Edges.Right => StandardCursorType.SizeWestEast,
            ZoneGeometry.Edges.Top or ZoneGeometry.Edges.Bottom => StandardCursorType.SizeNorthSouth,
            ZoneGeometry.Edges.Left | ZoneGeometry.Edges.Top => StandardCursorType.TopLeftCorner,
            ZoneGeometry.Edges.Right | ZoneGeometry.Edges.Bottom => StandardCursorType.BottomRightCorner,
            ZoneGeometry.Edges.Right | ZoneGeometry.Edges.Top => StandardCursorType.TopRightCorner,
            ZoneGeometry.Edges.Left | ZoneGeometry.Edges.Bottom => StandardCursorType.BottomLeftCorner,
            _ => session.Edit.CanDrawAt(p) ? StandardCursorType.Cross : StandardCursorType.No,
        });
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsEditable || Session is not { } session) return;
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            var (index, _) = session.Edit.HitTest(ToScreen(e.GetPosition(this)), Grip);
            if (index >= 0)
            {
                session.Edit.Select(index);
                session.Edit.DeleteSelected();
            }
            e.Handled = true;
            return;
        }
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        session.Edit.Release();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_pressed) return;
        _pressed = false;
        Session?.Edit.Release();
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    private static Color ColorOf(IBrush brush, Color fallback) => brush is ISolidColorBrush solid ? solid.Color : fallback;

    public override void Render(DrawingContext context)
    {
        // Hit-testable everywhere (drawing starts on empty space).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Session is not { } session) return;

        var full = IsFullScreen;
        // Full screen the zones sit on the editor's dark veil in both themes: white text.
        var text = full ? Brushes.White : Brush("TextControlForeground", Brushes.White);
        var muted = Brush("HelmSecondaryText", Brushes.Gray);
        var screenFill = Brush("HelmSubtleFill", new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)));
        var screenStroke = new Pen(Brush("HelmGlassStrokeStrong", Brushes.Gray), 1);
        var accent = ColorOf(Brush("HelmEyebrow", Brushes.Teal), Colors.Teal);
        var zoneFill = new SolidColorBrush(accent, 0.16);
        var selectedFill = new SolidColorBrush(accent, 0.36);
        var zonePen = new Pen(new SolidColorBrush(accent, 0.85), 1.5);
        var selectedPen = new Pen(new SolidColorBrush(accent), 2.5);
        var strip = new SolidColorBrush(Colors.Black, full ? 0.44 : 0.18);
        var radius = full ? 8 : 5;
        var font = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var plain = new Typeface(FontFamily.Default);

        foreach (var monitor in session.Monitors)
        {
            var r = ToLocal(monitor.Bounds);
            if (full)
            {
                // The taskbar is not part of the work area: shaded, so it is clear zones stop there.
                foreach (var part in Outside(monitor.Bounds, monitor.WorkArea))
                    context.FillRectangle(strip, ToLocal(part));
                continue;
            }
            context.DrawRectangle(screenFill, screenStroke, r, 6, 6);
            // The taskbar is not part of the work area: zones stop at it.
            foreach (var part in Outside(monitor.Bounds, monitor.WorkArea))
                context.FillRectangle(strip, ToLocal(part));
            // Its name under it, where no zone covers it.
            var caption = new FormattedText(monitor.DisplayName, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 11, muted)
            {
                MaxTextWidth = Math.Max(1, r.Width - 4),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            if (r.Width > 60) context.DrawText(caption, new Point(r.X + 2, r.Bottom + 2));
        }

        var edit = session.Edit;
        for (var i = 0; i < edit.Zones.Count; i++)
        {
            var zone = edit.Zones[i];
            var r = ToLocal(zone.Rect).Deflate(1.5);
            if (r.Width <= 0 || r.Height <= 0) continue;
            var selected = IsEditable && i == edit.Selected;
            context.DrawRectangle(selected ? selectedFill : zoneFill, selected ? selectedPen : zonePen, r, radius, radius);

            // The label in the middle of the part in this view (a zone across two monitors labels both halves).
            var shown = full ? r.Intersect(new Rect(Bounds.Size)) : r;
            if (shown.Width <= 0 || shown.Height <= 0) continue;
            var (number, name) = session.LabelFor(zone);
            var size = full ? Math.Clamp(Math.Min(shown.Width, shown.Height) / 4, 14, 64) : Math.Clamp(Math.Min(r.Width, r.Height) / 3.2, 11, 40);
            var big = new FormattedText(number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, size, text);
            var center = shown.Center;
            var showName = name.Length > 0 && shown.Height > big.Height + 32 && shown.Width > 50;
            var top = center.Y - big.Height / 2 - (showName ? 8 : 0);
            context.DrawText(big, new Point(center.X - big.Width / 2, top));
            if (showName)
            {
                var caption = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, full ? 15 : 11.5, text)
                {
                    MaxTextWidth = Math.Max(1, r.Width - 10),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(caption, new Point(center.X - Math.Min(caption.Width, shown.Width - 10) / 2, top + big.Height));
            }
            if (full)
            {
                // Its size in pixels, bottom left, where there is room.
                var pixels = new FormattedText($"{zone.Rect.Width} × {zone.Rect.Height}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 12, text);
                if (pixels.Width < shown.Width - 16 && pixels.Height < shown.Height - 16)
                    context.DrawText(pixels, new Point(shown.X + 10, shown.Bottom - pixels.Height - 8));
            }

            if (!selected) continue;
            foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                context.DrawRectangle(Brushes.White, selectedPen, full ? new Rect(corner.X - 4, corner.Y - 4, 8, 8) : new Rect(corner.X - 3.5, corner.Y - 3.5, 7, 7));
        }

        if (IsEditable && edit.Drawing is { } drawing)
        {
            var dashed = new Pen(new SolidColorBrush(accent), 1.5) { DashStyle = DashStyle.Dash };
            context.DrawRectangle(new SolidColorBrush(accent, 0.25), dashed, ToLocal(drawing), radius, radius);
        }
    }

    private static IEnumerable<GeoRect> Outside(GeoRect bounds, GeoRect work)
    {
        if (work.Top > bounds.Top) yield return new GeoRect(bounds.Left, bounds.Top, bounds.Right, work.Top);
        if (work.Bottom < bounds.Bottom) yield return new GeoRect(bounds.Left, work.Bottom, bounds.Right, bounds.Bottom);
        if (work.Left > bounds.Left) yield return new GeoRect(bounds.Left, work.Top, work.Left, work.Bottom);
        if (work.Right < bounds.Right) yield return new GeoRect(work.Right, work.Top, bounds.Right, work.Bottom);
    }
}
