using Android.Content;
using Android.Graphics;
using Android.Views;
using Helm.Ai;

namespace Helm.Modules.QuickCapture;

/// <summary>
/// The model sheet's effort slider, like VS Code's: a line with a dot per level (<see cref="AiEfforts.Levels"/>) and a
/// knob on the chosen one. A level the model does not take is dimmed, and the knob never stops on it (it goes to the
/// nearest it takes). With none taken, the slider is dimmed and does nothing.
/// </summary>
internal sealed class EffortSlider : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly float _density;
    private readonly Color _accent, _line, _dim, _knob;
    private IReadOnlyList<string> _taken = AiEfforts.Levels;
    private int _at;

    /// <summary>The finger moved the knob to another level (said once per level, as it moves).</summary>
    public event Action<string>? Moved;

    /// <summary>The finger lifted on a level: the one to keep.</summary>
    public event Action<string>? Chosen;

    public EffortSlider(Context context, Color accent, Color line, Color dim, Color knob) : base(context)
    {
        _density = context.Resources?.DisplayMetrics?.Density ?? 2f;
        (_accent, _line, _dim, _knob) = (accent, line, dim, knob);
        Clickable = true;
        Focusable = true;
        ContentDescription = "Mức suy nghĩ";
    }

    /// <summary>Where the knob is and what the model takes; the knob goes to the nearest level taken.</summary>
    public void Show(string effort, IReadOnlyList<string> taken)
    {
        _taken = taken;
        var level = AiEfforts.Nearest(effort, taken);
        _at = Math.Max(0, AiEfforts.IndexOf(level.Length > 0 ? level : effort));
        Enabled = taken.Count > 0;
        Alpha = Enabled ? 1f : 0.45f;
        ContentDescription = Enabled ? $"Mức suy nghĩ: {AiEfforts.Name(AiEfforts.Levels[_at])}" : "Mức suy nghĩ: model này không chỉnh được";
        Invalidate();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) =>
        SetMeasuredDimension(MeasureSpec.GetSize(widthMeasureSpec), (int)(40 * _density));

    private float Inset => 13 * _density;

    private float XOf(int index) => Inset + (Width - 2 * Inset) * index / (AiEfforts.Levels.Count - 1);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var y = Height / 2f;
        _paint.StrokeCap = Paint.Cap.Round;
        _paint.StrokeWidth = 2 * _density;
        _paint.Color = _line;
        canvas.DrawLine(XOf(0), y, XOf(AiEfforts.Levels.Count - 1), y, _paint);
        if (Enabled)
        {
            _paint.Color = _accent;
            canvas.DrawLine(XOf(0), y, XOf(_at), y, _paint);
        }
        for (var i = 0; i < AiEfforts.Levels.Count; i++)
        {
            var taken = _taken.Contains(AiEfforts.Levels[i]);
            _paint.Color = !taken ? _dim : Enabled && i <= _at ? _accent : _line;
            _paint.Alpha = taken ? 255 : 110;
            canvas.DrawCircle(XOf(i), y, 3.5f * _density, _paint);
        }
        _paint.Alpha = 255;
        // The knob: a filled ring in the accent.
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = _knob;
        canvas.DrawCircle(XOf(_at), y, 11 * _density, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.Color = Enabled ? _accent : _dim;
        canvas.DrawCircle(XOf(_at), y, 10 * _density, _paint);
        _paint.SetStyle(Paint.Style.Fill);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null || !Enabled) return false;
        switch (e.Action)
        {
            case MotionEventActions.Down:
                Parent?.RequestDisallowInterceptTouchEvent(true);
                MoveTo(e.GetX());
                return true;
            case MotionEventActions.Move:
                MoveTo(e.GetX());
                return true;
            case MotionEventActions.Up:
                MoveTo(e.GetX());
                Chosen?.Invoke(AiEfforts.Levels[_at]);
                PerformClick();
                return true;
            case MotionEventActions.Cancel:
                return true;
        }
        return base.OnTouchEvent(e);
    }

    private void MoveTo(float x)
    {
        var step = (Width - 2 * Inset) / (AiEfforts.Levels.Count - 1);
        var index = (int)Math.Round(Math.Clamp((x - Inset) / step, 0, AiEfforts.Levels.Count - 1));
        var level = AiEfforts.Nearest(AiEfforts.Levels[index], _taken);
        var at = AiEfforts.IndexOf(level);
        if (at < 0 || at == _at) return;
        _at = at;
        ContentDescription = $"Mức suy nghĩ: {AiEfforts.Name(level)}";
        PerformHapticFeedback(FeedbackConstants.ClockTick);
        Invalidate();
        Moved?.Invoke(level);
    }
}
