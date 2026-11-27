using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Helm.Core.Ui;

namespace Helm.Modules.Wallet.Ui;

/// <summary>
/// A fund drawn as a honey pot: the water is what is left of it (0–1), two slow waves move on its surface, and the
/// water drops smoothly when a transaction comes off the fund. The outline says the status (amber low, orange used up,
/// red over) next to the words the pages print, so colour never carries it alone. The water moves only for a few
/// seconds after the jar comes on screen or its level changes, then it rests (no frames at all, see <see cref="MotionClock"/>).
/// </summary>
public sealed class FundJar : Control
{
    // The pot in a 100 × 110 box: a small lid, then a round belly from y 10 to 108.
    private const double Bottom = 108, BodyHeight = 98;
    private static readonly Geometry Body = Geometry.Parse("M50,10 C79,10 98,23 98,53 C98,87 79,108 50,108 C21,108 2,87 2,53 C2,23 21,10 50,10 Z");
    private static readonly Geometry Wave = Geometry.Parse("M0,4 Q12.5,0 25,4 T50,4 T75,4 T100,4 T125,4 T150,4 V14 H0 Z");
    private static readonly Geometry WaveBack = Geometry.Parse("M0,4 Q12.5,8 25,4 T50,4 T75,4 T100,4 T125,4 T150,4 V14 H0 Z");

    public static readonly StyledProperty<double> LevelProperty = AvaloniaProperty.Register<FundJar, double>(nameof(Level));

    public static readonly StyledProperty<int> ColorProperty = AvaloniaProperty.Register<FundJar, int>(nameof(Color));

    public static readonly StyledProperty<FundStatus> StatusProperty = AvaloniaProperty.Register<FundJar, FundStatus>(nameof(Status));

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<FundJar, string>(nameof(Text), "");

    public static readonly StyledProperty<double> TextSizeProperty = AvaloniaProperty.Register<FundJar, double>(nameof(TextSize), 13);

    /// <summary>Draws an empty pot with a dashed outline and a plus ("Add fund").</summary>
    public static readonly StyledProperty<bool> IsAddProperty = AvaloniaProperty.Register<FundJar, bool>(nameof(IsAdd));

    /// <summary>How long the waves move after the jar shows or its level changes.</summary>
    private const double WavesFor = 2.5;

    private double _shown = -1;
    private double _phase;
    private double _wavesLeft;
    private IDisposable? _moving;

    /// <summary>The water or the waves move right now (a jar at rest asks for no frames).</summary>
    public bool IsMoving => _moving is not null;

    static FundJar()
    {
        AffectsRender<FundJar>(ColorProperty, StatusProperty, TextProperty, TextSizeProperty, IsAddProperty);
    }

    public FundJar()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public int Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public FundStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double TextSize
    {
        get => GetValue(TextSizeProperty);
        set => SetValue(TextSizeProperty, value);
    }

    public bool IsAdd
    {
        get => GetValue(IsAddProperty);
        set => SetValue(IsAddProperty, value);
    }

    private double Target => Math.Clamp(double.IsNaN(Level) ? 0 : Level, 0, 1);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Wake();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _moving?.Dispose();
        _moving = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LevelProperty) Wake();
    }

    /// <summary>The waves move (again) for a few seconds and the water eases to its level, then everything rests.</summary>
    private void Wake()
    {
        if (IsAdd || VisualRoot is null) return;
        _wavesLeft = WavesFor;
        _moving ??= MotionClock.Run(this, seconds =>
        {
            Tick(seconds);
            var moving = _wavesLeft > 0 || _shown != Target;
            if (!moving) _moving = null;
            return moving;
        });
    }

    /// <summary>One frame: the waves move on, the water eases toward its level.</summary>
    private void Tick(double seconds)
    {
        _phase = (_phase + seconds / 3.2) % 1;
        _wavesLeft -= seconds;
        var target = Target;
        if (_shown < 0) _shown = target;
        // About 0.9 s to settle, easing out.
        _shown += (target - _shown) * Math.Min(1, seconds * 5);
        if (Math.Abs(target - _shown) < 0.001) _shown = target;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var scale = Math.Min(w / 100, h / 110);
        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((w - 100 * scale) / 2, (h - 110 * scale) / 2);
        var dark = WalletTheme.IsDark(this);
        var level = _shown < 0 ? Target : _shown;

        using (context.PushTransform(matrix))
        {
            var glass = WalletTheme.Brush(dark ? 0x0FFFFFFFu : 0x08000000u);
            var lid = WalletTheme.Brush(dark ? 0x3DFFFFFFu : 0x2E000000u);
            if (IsAdd)
            {
                var accent = WalletTheme.Brush(dark ? 0xFF60CDFFu : 0xFF0067C0u);
                context.DrawRectangle(WalletTheme.Brush(dark ? 0x5960CDFFu : 0x590067C0u), null, new Rect(32, 0, 36, 9), 2, 2);
                context.DrawGeometry(null, new Pen(accent, 2.5, new ImmutableDashStyle([4, 3], 0)), Body);
                var plus = new Pen(accent, 5, lineCap: PenLineCap.Round);
                context.DrawLine(plus, new Point(50, 46), new Point(50, 74));
                context.DrawLine(plus, new Point(36, 60), new Point(64, 60));
                return;
            }
            context.DrawRectangle(lid, null, new Rect(32, 0, 36, 9), 2, 2);
            context.DrawGeometry(glass, null, Body);
            if (level > 0.001)
            {
                var water = WalletTheme.Category(Color, 0xD9, dark);
                var top = Bottom - level * BodyHeight;
                using (context.PushGeometryClip(Body))
                {
                    context.DrawRectangle(water, null, new Rect(0, top, 100, 110 - top));
                    using (context.PushOpacity(0.55))
                    using (context.PushTransform(Matrix.CreateTranslation(-50 + _phase * 50 * 0.6, top - 8)))
                        context.DrawGeometry(water, null, WaveBack);
                    using (context.PushTransform(Matrix.CreateTranslation(-_phase * 50, top - 6)))
                        context.DrawGeometry(water, null, Wave);
                }
            }
            context.DrawRectangle(WalletTheme.Brush(0x22FFFFFFu), null, new Rect(15, 30, 8, 40), 4, 4);
            var outline = Status switch
            {
                FundStatus.Over => dark ? 0xFFFF99A4u : 0xFFC42B1Cu,
                FundStatus.UsedUp => dark ? 0xFFFFB86Bu : 0xFFA8461Au,
                FundStatus.Low => dark ? 0xFFFCE100u : 0xFF9D5D00u,
                _ => dark ? 0x52FFFFFFu : 0x40000000u,
            };
            context.DrawGeometry(null, new Pen(WalletTheme.Brush(outline), 2.5), Body);
        }

        if (Text.Length == 0) return;
        // Over the water the words are white; over the empty glass, in the text colour.
        var brush = level > 0.55 ? Brushes.White : WalletTheme.Primary(this);
        var text = WalletTheme.Text(this, Text, TextSize, brush, FontWeight.Bold);
        context.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2 + 4 * scale));
    }
}
