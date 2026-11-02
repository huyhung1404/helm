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
}

internal enum IconStyle
{
    Plain,
    No,
    Yes,
}

/// <summary>A round 44 dp icon button, drawn on a 24-unit grid like the rest of Helm's line icons.</summary>
internal sealed class IconButton : AndroidView
{
    private readonly VocabCardOverlay.Palette _c;
    private readonly IconStyle _style;
    private CardIcon _icon;
    private bool _highlighted;

    public IconButton(Context context, CardIcon icon, IconStyle style, VocabCardOverlay.Palette palette, string name, Action click) : base(context)
    {
        _icon = icon;
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
            IconStyle.Yes => _c.Accent,
            IconStyle.No => _c.Bad,
            _ => _c.Sub,
        };
        if (_highlighted)
        {
            using var ring = new Paint(PaintFlags.AntiAlias) { Color = AndroidColor.Argb(0x73, tint.R, tint.G, tint.B) };
            canvas.DrawCircle(center, center, center, ring);
        }
        if (_style != IconStyle.Plain || Pressed)
        {
            var fill = _style switch
            {
                IconStyle.Yes => _c.Accent,
                IconStyle.No => AndroidColor.Argb(0x24, _c.Bad.R, _c.Bad.G, _c.Bad.B),
                _ => _c.Btn,
            };
            using var disc = new Paint(PaintFlags.AntiAlias) { Color = fill };
            canvas.DrawCircle(center, center, center - (_highlighted ? size * 0.07f : 0), disc);
        }
        var ink = _style == IconStyle.Yes ? _c.OnAccent : tint;
        var scale = size * 0.5f / 24f;
        canvas.Save();
        canvas.Translate(center - 12 * scale, center - 12 * scale);
        canvas.Scale(scale, scale);
        using var pen = new Paint(PaintFlags.AntiAlias) { Color = ink, StrokeWidth = 2, StrokeCap = Paint.Cap.Round, StrokeJoin = Paint.Join.Round };
        pen.SetStyle(Paint.Style.Stroke);
        using var fillPen = new Paint(PaintFlags.AntiAlias) { Color = ink };
        switch (_icon)
        {
            case CardIcon.Listen:
                canvas.DrawPath(Poly(true, 4, 9.5f, 4, 14.5f, 7.5f, 14.5f, 12, 18.5f, 12, 5.5f, 7.5f, 9.5f), fillPen);
                canvas.DrawArc(new RectF(11.5f, 8.5f, 18.5f, 15.5f), -55, 110, false, pen);
                canvas.DrawArc(new RectF(9, 4.5f, 24, 19.5f), -50, 100, false, pen);
                break;
            case CardIcon.Video:
                canvas.DrawRoundRect(new RectF(3, 6.5f, 15, 17.5f), 2, 2, pen);
                canvas.DrawPath(Poly(true, 15, 10.5f, 21, 7.5f, 21, 16.5f, 15, 13.5f), pen);
                break;
            case CardIcon.Expand:
                canvas.DrawPath(Poly(false, 14, 4, 20, 4, 20, 10), pen);
                canvas.DrawLine(20, 4, 13, 11, pen);
                canvas.DrawPath(Poly(false, 10, 20, 4, 20, 4, 14), pen);
                canvas.DrawLine(4, 20, 11, 13, pen);
                break;
            case CardIcon.Collapse:
                canvas.DrawPath(Poly(false, 4, 14, 10, 14, 10, 20), pen);
                canvas.DrawLine(10, 14, 3, 21, pen);
                canvas.DrawPath(Poly(false, 20, 10, 14, 10, 14, 4), pen);
                canvas.DrawLine(14, 10, 21, 3, pen);
                break;
            case CardIcon.Open:
                canvas.DrawPath(Poly(false, 14, 4, 20, 4, 20, 10), pen);
                canvas.DrawLine(20, 4, 11, 13, pen);
                canvas.DrawPath(Poly(false, 18, 14, 18, 20, 4, 20, 4, 6, 10, 6), pen);
                break;
            case CardIcon.No:
                canvas.DrawLine(7, 7, 17, 17, pen);
                canvas.DrawLine(17, 7, 7, 17, pen);
                break;
            case CardIcon.Yes:
                canvas.DrawPath(Poly(false, 5, 12.5f, 9.5f, 17, 19, 7.5f), pen);
                break;
        }
        canvas.Restore();
    }

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
