using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidColor = Android.Graphics.Color;
using AndroidView = Android.Views.View;

namespace Helm.Modules.Vocab;

/// <summary>
/// The floating bubble: a round Vocabulary icon over the other apps. Tapped it shows a word; dragged it moves and
/// settles against the nearer side (where it stays); dropped on the ✕ that appears at the bottom while dragging it goes
/// away until the phone is next unlocked. Plain Android views, on the main thread.
/// </summary>
internal sealed class VocabBubble
{
    private const int SizeDp = 52;
    private const int TargetDp = 64;

    /// <summary>Left alone the bubble is see-through, so it hides little of the app below; touched it is solid.</summary>
    private const float IdleAlpha = 0.45f;

    private readonly Context _context;
    private readonly IWindowManager _windows;
    private readonly Action _tapped;
    private readonly Action _dismissed;
    private readonly Action<double, double> _moved;
    private readonly AndroidView _view;
    private readonly WindowManagerLayoutParams _params;
    private readonly TextView _target;
    private readonly WindowManagerLayoutParams _targetParams;
    private bool _shown, _targetShown;

    public VocabBubble(Context context, IWindowManager windows, Action tapped, Action dismissed, Action<double, double> moved)
    {
        _context = context;
        _windows = windows;
        _tapped = tapped;
        _dismissed = dismissed;
        _moved = moved;
        var size = Dp(SizeDp);
        _view = new BubbleIcon(context, VocabOverlayPlatform.Dark(context)) { ContentDescription = "Vocabulary: show a word", Alpha = IdleAlpha };
        _view.Elevation = Dp(6);
        _params = VocabOverlay.Params(size, size, touchable: true);
        _params.Gravity = GravityFlags.Top | GravityFlags.Left;
        _view.Touch += OnTouch;

        var target = Dp(TargetDp);
        var ring = new GradientDrawable();
        ring.SetShape(ShapeType.Oval);
        ring.SetColor(AndroidColor.Argb(0xCC, 0x20, 0x20, 0x20));
        _target = new TextView(context) { Text = "✕", Gravity = GravityFlags.Center, Background = ring };
        _target.SetTextColor(AndroidColor.White);
        _target.SetTextSize(Android.Util.ComplexUnitType.Sp, 24);
        _targetParams = VocabOverlay.Params(target, target, touchable: false);
        _targetParams.Gravity = GravityFlags.Bottom | GravityFlags.CenterHorizontal;
        _targetParams.Y = Dp(56);
    }

    public bool IsShown => _shown;

    /// <summary>Where the bubble is (screen pixels), so the card can open next to it.</summary>
    public (int X, int Y, int Size) Bounds => (_params.X, _params.Y, Dp(SizeDp));

    /// <summary>Puts the bubble on screen at the saved place (fractions of the screen; null: right edge, a third down).</summary>
    public void Show(double? x, double? y)
    {
        var (width, height) = Screen();
        var size = Dp(SizeDp);
        _params.X = (x ?? 1) >= 0.5 ? width - size - Dp(6) : Dp(6);
        _params.Y = (int)Math.Clamp((y ?? 0.33) * height, Dp(24), Math.Max(Dp(24), height - size - Dp(24)));
        if (_shown)
        {
            _windows.UpdateViewLayout(_view, _params);
            return;
        }
        _windows.AddView(_view, _params);
        _shown = true;
    }

    public void Hide()
    {
        HideTarget();
        if (!_shown) return;
        _shown = false;
        _windows.RemoveView(_view);
    }

    private float _downX, _downY;
    private int _startX, _startY;
    private bool _dragging;

    private void OnTouch(object? sender, AndroidView.TouchEventArgs e)
    {
        e.Handled = true;
        if (e.Event is not { } ev || !_shown) return;
        var slop = ViewConfiguration.Get(_context)!.ScaledTouchSlop;
        switch (ev.ActionMasked)
        {
            case MotionEventActions.Down:
                (_downX, _downY, _startX, _startY, _dragging) = (ev.RawX, ev.RawY, _params.X, _params.Y, false);
                _view.Alpha = 1f;
                break;
            case MotionEventActions.Move:
                var dx = ev.RawX - _downX;
                var dy = ev.RawY - _downY;
                if (!_dragging && Math.Abs(dx) <= slop && Math.Abs(dy) <= slop) break;
                _dragging = true;
                ShowTarget();
                _params.X = _startX + (int)dx;
                _params.Y = _startY + (int)dy;
                _windows.UpdateViewLayout(_view, _params);
                _target.Alpha = OverTarget(ev.RawX, ev.RawY) ? 1f : 0.7f;
                _target.ScaleX = _target.ScaleY = OverTarget(ev.RawX, ev.RawY) ? 1.2f : 1f;
                break;
            case MotionEventActions.Up:
                HideTarget();
                _view.Alpha = IdleAlpha;
                if (!_dragging)
                {
                    _tapped();
                    break;
                }
                if (OverTarget(ev.RawX, ev.RawY))
                {
                    Hide();
                    _dismissed();
                    break;
                }
                Settle();
                break;
            case MotionEventActions.Cancel:
                HideTarget();
                _view.Alpha = IdleAlpha;
                if (_dragging) Settle();
                break;
        }
    }

    /// <summary>Against the nearer side, and remembered there.</summary>
    private void Settle()
    {
        var (width, height) = Screen();
        var size = Dp(SizeDp);
        var right = _params.X + size / 2 > width / 2;
        var y = Math.Clamp(_params.Y, Dp(24), Math.Max(Dp(24), height - size - Dp(24)));
        _params.X = right ? width - size - Dp(6) : Dp(6);
        _params.Y = y;
        _windows.UpdateViewLayout(_view, _params);
        _moved(right ? 1 : 0, (double)y / Math.Max(1, height));
    }

    /// <summary>The finger is near the ✕ at the bottom.</summary>
    private bool OverTarget(float rawX, float rawY)
    {
        var (width, height) = Screen();
        var centerX = width / 2f;
        var centerY = height - _targetParams.Y - Dp(TargetDp) / 2f;
        var dx = rawX - centerX;
        var dy = rawY - centerY;
        return dx * dx + dy * dy <= Dp(72) * (float)Dp(72);
    }

    private void ShowTarget()
    {
        if (_targetShown) return;
        _windows.AddView(_target, _targetParams);
        _targetShown = true;
    }

    private void HideTarget()
    {
        if (!_targetShown) return;
        _targetShown = false;
        _windows.RemoveView(_target);
    }

    private (int Width, int Height) Screen()
    {
        var metrics = _context.Resources!.DisplayMetrics!;
        return (metrics.WidthPixels, metrics.HeightPixels);
    }

    private int Dp(double dp) => VocabOverlayPlatform.Dp(_context, dp);

    /// <summary>A disc with the Vocabulary icon (<see cref="VocabIconShape"/>: three dots on two branches, amber to rose).</summary>
    private sealed class BubbleIcon(Context context, bool dark) : AndroidView(context)
    {
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var size = Math.Min(Width, Height);
            using var disc = new Paint(PaintFlags.AntiAlias) { Color = dark ? AndroidColor.Rgb(0x2B, 0x2B, 0x2B) : AndroidColor.White };
            canvas.DrawCircle(size / 2f, size / 2f, size / 2f, disc);
            using var rim = new Paint(PaintFlags.AntiAlias) { Color = dark ? AndroidColor.Rgb(0x45, 0x45, 0x45) : AndroidColor.Rgb(0xDD, 0xDD, 0xDD), StrokeWidth = 2 };
            rim.SetStyle(Paint.Style.Stroke);
            canvas.DrawCircle(size / 2f, size / 2f, size / 2f - 1, rim);

            // The icon in the middle 56 % of the disc, in its 100 × 100 box.
            var box = size * 0.56f;
            var scale = box / (float)VocabIconShape.Size;
            canvas.Save();
            canvas.Translate((size - box) / 2f, (size - box) / 2f);
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
    }
}
