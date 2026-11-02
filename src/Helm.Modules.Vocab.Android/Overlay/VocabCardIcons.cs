using Android.Content;
using Android.Graphics;
using AndroidColor = Android.Graphics.Color;
using AndroidView = Android.Views.View;

namespace Helm.Modules.Vocab;

internal enum CardIcon
{
    Listen,
    Video,
    Expand,
    Collapse,
    Open,
    No,
    Yes,
    Close,
}

internal enum IconStyle
{
    /// <summary>A plain icon (Listen, YouGlish, Open in Helm, ✕).</summary>
    Plain,
    /// <summary>Forgot: outlined in red.</summary>
    No,
    /// <summary>Remembered: a green disc.</summary>
    Yes,
}

/// <summary>
/// A round 44 dp icon button, drawn on a 24-unit grid like the rest of Helm's line icons (the icon 20 dp by default).
/// <see cref="On"/> marks a toggle that is on (Full details open).
/// </summary>
internal sealed class IconButton : AndroidView
{
    private readonly VocabCardOverlay.Palette _c;
    private readonly IconStyle _style;
    private readonly float _iconDp;
    private CardIcon _icon;
    private bool _highlighted, _on;

    public IconButton(Context context, CardIcon icon, IconStyle style, VocabCardOverlay.Palette palette, string name, Action click, float iconDp = 20) : base(context)
    {
        _icon = icon;
        _iconDp = iconDp;
        _style = style;
        _c = palette;
        ContentDescription = name;
        TooltipText = name;
        Clickable = true;
        Focusable = true;
        Click += (_, _) => click();
    }

    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value) return;
            _highlighted = value;
            Invalidate();
        }
    }

    public bool On
    {
        get => _on;
        set
        {
            if (_on == value) return;
            _on = value;
            Invalidate();
        }
    }

    public void Set(CardIcon icon, string name)
    {
        _icon = icon;
        ContentDescription = name;
        TooltipText = name;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var size = Math.Min(Width, Height);
        var center = size / 2f;
        var tint = _style switch
        {
            IconStyle.Yes => _c.Ok,
            IconStyle.No => _c.Bad,
            _ => _c.Ink,
        };
        // A suggested answer (the typed word was right or wrong) gets a soft halo.
        if (_highlighted)
        {
            using var ring = new Paint(PaintFlags.AntiAlias) { Color = AndroidColor.Argb(0x73, tint.R, tint.G, tint.B) };
            canvas.DrawCircle(center, center, center, ring);
        }
        var inset = _highlighted ? size * 0.07f : 0;
        var fill = _style switch
        {
            IconStyle.Yes => _c.Ok,
            _ when _on => _c.AccentFill,
            _ when Pressed => _c.Btn,
            _ => AndroidColor.Transparent,
        };
        if (fill != AndroidColor.Transparent)
        {
            using var disc = new Paint(PaintFlags.AntiAlias) { Color = fill };
            canvas.DrawCircle(center, center, center - inset, disc);
        }
        if (_style == IconStyle.No)
        {
            var edge = Dp(1.5f);
            using var outline = new Paint(PaintFlags.AntiAlias) { Color = AndroidColor.Argb(0xB3, tint.R, tint.G, tint.B), StrokeWidth = edge };
            outline.SetStyle(Paint.Style.Stroke);
            canvas.DrawCircle(center, center, center - inset - edge / 2, outline);
        }
        var ink = _style == IconStyle.Yes ? _c.OnOk : _on ? _c.AccentText : tint;
        var scale = Dp(_iconDp) / 24f;
        canvas.Save();
        canvas.Translate(center - 12 * scale, center - 12 * scale);
        canvas.Scale(scale, scale);
        using var pen = new Paint(PaintFlags.AntiAlias) { Color = ink, StrokeWidth = _style == IconStyle.Yes ? 2.4f : 1.7f, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
        pen.SetStyle(Paint.Style.Stroke);
        switch (_icon)
        {
            // The canvas's line icons, point for point.
            case CardIcon.Listen:
                canvas.DrawPath(Poly(true, 4, 9.5f, 7.5f, 9.5f, 12, 5.5f, 12, 18.5f, 7.5f, 14.5f, 4, 14.5f), pen);
                canvas.DrawArc(new RectF(8.85f, 8, 16.85f, 16), -48.6f, 97.2f, false, pen);
                canvas.DrawArc(new RectF(5.4f, 4.5f, 20.4f, 19.5f), -47.2f, 94.4f, false, pen);
                break;
            case CardIcon.Video:
                canvas.DrawRoundRect(new RectF(3.5f, 5.5f, 20.5f, 18.5f), 2.5f, 2.5f, pen);
                canvas.DrawPath(Poly(true, 10.5f, 9.5f, 14.7f, 12, 10.5f, 14.5f), pen);
                break;
            case CardIcon.Expand:
                canvas.DrawPath(Poly(false, 4, 9, 4, 4, 9, 4), pen);
                canvas.DrawPath(Poly(false, 20, 9, 20, 4, 15, 4), pen);
                canvas.DrawPath(Poly(false, 4, 15, 4, 20, 9, 20), pen);
                canvas.DrawPath(Poly(false, 20, 15, 20, 20, 15, 20), pen);
                break;
            case CardIcon.Collapse:
                canvas.DrawPath(Poly(false, 9, 4, 9, 9, 4, 9), pen);
                canvas.DrawPath(Poly(false, 15, 4, 15, 9, 20, 9), pen);
                canvas.DrawPath(Poly(false, 9, 20, 9, 15, 4, 15), pen);
                canvas.DrawPath(Poly(false, 15, 20, 15, 15, 20, 15), pen);
                break;
            case CardIcon.Open:
                canvas.DrawPath(Poly(false, 14, 4.5f, 19.5f, 4.5f, 19.5f, 10), pen);
                canvas.DrawLine(19.5f, 4.5f, 11, 13, pen);
                canvas.DrawPath(Poly(false, 17, 14, 17, 19.5f, 4.5f, 19.5f, 4.5f, 7, 10, 7), pen);
                break;
            case CardIcon.No or CardIcon.Close:
                canvas.DrawLine(6, 6, 18, 18, pen);
                canvas.DrawLine(18, 6, 6, 18, pen);
                break;
            case CardIcon.Yes:
                canvas.DrawPath(Poly(false, 5, 12.5f, 9.5f, 17, 19, 7.5f), pen);
                break;
        }
        canvas.Restore();
    }

    private float Dp(float dp) => VocabOverlayPlatform.Dp(Context!, dp);

    protected override void DrawableStateChanged()
    {
        base.DrawableStateChanged();
        Invalidate();
    }

    private static Android.Graphics.Path Poly(bool closed, params float[] points)
    {
        var path = new Android.Graphics.Path();
        path.MoveTo(points[0], points[1]);
        for (var i = 2; i < points.Length; i += 2) path.LineTo(points[i], points[i + 1]);
        if (closed) path.Close();
        return path;
    }
}
