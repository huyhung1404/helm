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
/// draw a zone, drag a zone to move it, its edges or corners to resize it, right-click to delete it.
/// </summary>
public sealed class ZoneLayoutView : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty =
        AvaloniaProperty.Register<ZoneLayoutView, EditorSession?>(nameof(Session));

    public static readonly StyledProperty<bool> IsEditableProperty =
        AvaloniaProperty.Register<ZoneLayoutView, bool>(nameof(IsEditable));

    public static readonly StyledProperty<double> MaxLayoutHeightProperty =
        AvaloniaProperty.Register<ZoneLayoutView, double>(nameof(MaxLayoutHeight), 360);

    /// <summary>How close to an edge (on screen, in this view's units) the pointer resizes instead of moving.</summary>
    private const double GripDips = 8;

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SessionProperty)
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
        var width = double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width;
        var height = width * desktop.Height / Math.Max(1, desktop.Width) + 2 * Inset + CaptionHeight;
        return new Size(width, Math.Min(height, MaxLayoutHeight));
    }

    /// <summary>Pixels per global screen pixel, and where the desktop's top-left corner is drawn.</summary>
    private (double Scale, Point Origin) Transform()
    {
        var desktop = Desktop;
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

    private int Grip => (int)Math.Round(GripDips / Transform().Scale);

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

        var text = Brush("TextControlForeground", Brushes.White);
        var muted = Brush("HelmSecondaryText", Brushes.Gray);
        var screenFill = Brush("HelmSubtleFill", new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)));
        var screenStroke = new Pen(Brush("HelmGlassStrokeStrong", Brushes.Gray), 1);
        var accent = ColorOf(Brush("HelmEyebrow", Brushes.Teal), Colors.Teal);
        var zoneFill = new SolidColorBrush(accent, 0.16);
        var selectedFill = new SolidColorBrush(accent, 0.36);
        var zonePen = new Pen(new SolidColorBrush(accent, 0.85), 1.5);
        var selectedPen = new Pen(new SolidColorBrush(accent), 2.5);
        var strip = new SolidColorBrush(Colors.Black, 0.18);
        var font = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        var plain = new Typeface(FontFamily.Default);

        foreach (var monitor in session.Monitors)
        {
            var r = ToLocal(monitor.Bounds);
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
            context.DrawRectangle(selected ? selectedFill : zoneFill, selected ? selectedPen : zonePen, r, 5, 5);

            var (number, name) = session.LabelFor(zone);
            var size = Math.Clamp(Math.Min(r.Width, r.Height) / 3.2, 11, 40);
            var big = new FormattedText(number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, size, text);
            var center = r.Center;
            var showName = name.Length > 0 && r.Height > big.Height + 32 && r.Width > 50;
            var top = center.Y - big.Height / 2 - (showName ? 8 : 0);
            context.DrawText(big, new Point(center.X - big.Width / 2, top));
            if (showName)
            {
                var caption = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, plain, 11.5, text)
                {
                    MaxTextWidth = Math.Max(1, r.Width - 10),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(caption, new Point(center.X - Math.Min(caption.Width, r.Width - 10) / 2, top + big.Height));
            }

            if (!selected) continue;
            foreach (var corner in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
                context.DrawRectangle(Brushes.White, selectedPen, new Rect(corner.X - 3.5, corner.Y - 3.5, 7, 7));
        }

        if (IsEditable && edit.Drawing is { } drawing)
        {
            var dashed = new Pen(new SolidColorBrush(accent), 1.5) { DashStyle = DashStyle.Dash };
            context.DrawRectangle(new SolidColorBrush(accent, 0.25), dashed, ToLocal(drawing), 5, 5);
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
