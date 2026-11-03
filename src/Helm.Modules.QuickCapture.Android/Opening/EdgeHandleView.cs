using Android.Content;
using Android.Graphics;
using Android.Views;
using Helm.Ai.Assistant;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The edge handle's window content: a 4dp line in Helm's accent, 3dp from the screen's edge, in a 20dp strip that takes
/// the touches (<see cref="EdgeHandleGesture"/>: 40dp inward or a tap opens Ask Helm). The strip keeps itself from the
/// Back gesture while it is shown. It also tells when the app in front goes full screen (the status bar hides).
/// </summary>
internal sealed class EdgeHandleView : View
{
    private readonly float _density;
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly RectF _line = new();
    private readonly Action _open;
    private readonly Action<bool> _fullScreen;
    private EdgeHandleGesture _gesture;
    private bool _hidden;

    public EdgeHandleView(Context context, EdgeSide side, Action open, Action<bool> fullScreen) : base(context)
    {
        _density = context.Resources?.DisplayMetrics?.Density ?? 2f;
        _gesture = new EdgeHandleGesture(side, _density);
        _open = open;
        _fullScreen = fullScreen;
        ContentDescription = "Ask Helm: drag inward or tap";
        Clickable = true;
        UpdateColour();
    }

    public EdgeSide Side
    {
        get => _gesture.Side;
        set
        {
            if (value == _gesture.Side) return;
            _gesture = new EdgeHandleGesture(value, _density);
            Invalidate();
        }
    }

    /// <summary>Drawn and keeping the edge, or invisible (over a full-screen app).</summary>
    public bool Hidden
    {
        get => _hidden;
        set
        {
            if (value == _hidden) return;
            _hidden = value;
            _gesture.Cancel();
            KeepEdge();
            Invalidate();
        }
    }

    /// <summary>The theme changed: the accent of the other mode.</summary>
    public void UpdateColour()
    {
        var dark = ((Context?.Resources?.Configuration?.UiMode ?? Android.Content.Res.UiMode.NightNo) & Android.Content.Res.UiMode.NightMask)
            == Android.Content.Res.UiMode.NightYes;
        _paint.Color = Color.ParseColor(dark ? "#4CC2FF" : "#0067C0");
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_hidden) return;
        var width = Dp(EdgeHandleLayout.LineDp + 4 * _gesture.Progress);
        var inset = Dp(EdgeHandleLayout.LineInsetDp);
        // Pulled inward with the finger, as far as the strip allows.
        var pull = Math.Clamp(_gesture.Pull, 0, Math.Max(0, Width - width - 2 * inset));
        var left = Side == EdgeSide.Right ? Width - inset - width - pull : inset + pull;
        _line.Set(left, 0, left + width, Height);
        _paint.Alpha = (int)(255 * (0.8f + 0.2f * _gesture.Progress));
        canvas.DrawRoundRect(_line, Dp(3), Dp(3), _paint);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null || _hidden) return false;
        var action = EdgeHandleAction.None;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _gesture.Down(e.RawX, e.RawY);
                break;
            case MotionEventActions.Move:
                action = _gesture.Move(e.RawX, e.RawY);
                break;
            case MotionEventActions.Up:
                action = _gesture.Up(e.RawX, e.RawY);
                break;
            case MotionEventActions.Cancel:
                _gesture.Cancel();
                break;
        }
        Invalidate();
        if (action == EdgeHandleAction.Open) Open();
        return true;
    }

    /// <summary>A screen reader's double tap.</summary>
    public override bool PerformClick()
    {
        base.PerformClick();
        Open();
        return true;
    }

    private void Open()
    {
        PerformHapticFeedback(FeedbackConstants.VirtualKey);
        _open();
    }

    protected override void OnLayout(bool changed, int left, int top, int right, int bottom)
    {
        base.OnLayout(changed, left, top, right, bottom);
        KeepEdge();
    }

    /// <summary>The Back gesture leaves the shown strip to the handle (Android allows up to 200dp of each edge).</summary>
    private void KeepEdge()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29)) return;
        SystemGestureExclusionRects = _hidden || Width == 0 ? [] : [new Rect(0, 0, Width, Height)];
    }

    public override WindowInsets OnApplyWindowInsets(WindowInsets? insets)
    {
        if (insets is not null && OperatingSystem.IsAndroidVersionAtLeast(30))
            _fullScreen(!insets.IsVisible(WindowInsets.Type.StatusBars()));
        return base.OnApplyWindowInsets(insets!) ?? insets!;
    }

    // Before Android 11 the status bar's state comes as the old system UI flags, sent to every window.
#pragma warning disable CA1422, CS0618, CS0672
    public override void OnWindowSystemUiVisibilityChanged(SystemUiFlags visible)
    {
        base.OnWindowSystemUiVisibilityChanged(visible);
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) _fullScreen((visible & SystemUiFlags.Fullscreen) != 0);
    }
#pragma warning restore CA1422, CS0618, CS0672

    private float Dp(double dp) => (float)(dp * _density);
}
