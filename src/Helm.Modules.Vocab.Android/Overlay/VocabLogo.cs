using Android.Content;
using Android.Graphics;
using AndroidColor = Android.Graphics.Color;
using AndroidView = Android.Views.View;

namespace Helm.Modules.Vocab;

/// <summary>
/// The Vocabulary icon (<see cref="VocabIconShape"/>: three dots on two branches, amber to rose) drawn on a canvas: the
/// bubble's middle and the card's header.
/// </summary>
internal static class VocabLogo
{
    /// <summary>Draws the icon in a square of <paramref name="box"/> pixels whose top left is the canvas origin.</summary>
    public static void Draw(Canvas canvas, float box)
    {
        var scale = box / (float)VocabIconShape.Size;
        canvas.Save();
        canvas.Scale(scale, scale);
        static AndroidColor Argb(uint c) => AndroidColor.Argb((int)(c >> 24), (int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));
        using var gradient = new LinearGradient(0, 100, 100, 0, Argb(VocabIconShape.GradientStart), Argb(VocabIconShape.GradientEnd), Shader.TileMode.Clamp!);
        using var stroke = new Paint(PaintFlags.AntiAlias) { StrokeWidth = (float)VocabIconShape.Stroke, StrokeCap = Paint.Cap.Round };
        stroke.SetStyle(Paint.Style.Stroke);
        stroke.SetShader(gradient);
        canvas.DrawLine(50, 15, 16, 84, stroke);
        canvas.DrawLine(50, 15, 84, 84, stroke);
        using var fill = new Paint(PaintFlags.AntiAlias);
        fill.SetShader(gradient);
        canvas.DrawCircle(50, 15, 14, fill);
        canvas.DrawCircle(16, 84, 14, fill);
        canvas.DrawCircle(84, 84, 14, fill);
        canvas.Restore();
    }

    /// <summary>The icon alone, filling its view (the card's header).</summary>
    public sealed class View(Context context) : AndroidView(context)
    {
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var size = Math.Min(Width, Height);
            canvas.Save();
            canvas.Translate((Width - size) / 2f, (Height - size) / 2f);
            VocabLogo.Draw(canvas, size);
            canvas.Restore();
        }
    }
}
