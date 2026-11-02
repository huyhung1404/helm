using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace Helm.Modules.Wallet;

/// <summary>
/// A fund drawn as a honey pot: the water is what is left of it (0–1), two slow waves move on its surface, and the
/// water drops smoothly when a transaction comes off the fund. The outline says the status (amber low, orange used up,
/// red over) next to the words the pages print, so colour never carries it alone. Every jar on screen shares one
/// clock (about 30 frames a second) that runs only while a jar is shown.
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

    private double _shown = -1;
    private double _phase;

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
        if (!IsAdd) Clock.Add(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clock.Remove(this);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>One frame: the waves move on, the water eases toward its level.</summary>
    private void Tick(double seconds)
    {
        _phase = (_phase + seconds / 3.2) % 1;
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

    /// <summary>The one clock all shown jars move by; it stops when none is shown.</summary>
    private static class Clock
    {
        private static readonly List<FundJar> s_jars = [];
        private static DispatcherTimer? s_timer;
        private static DateTime s_last;

        public static void Add(FundJar jar)
        {
            if (!s_jars.Contains(jar)) s_jars.Add(jar);
            if (s_timer is not null) return;
            s_last = DateTime.UtcNow;
            s_timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                var now = DateTime.UtcNow;
                var seconds = Math.Min(0.1, (now - s_last).TotalSeconds);
                s_last = now;
                foreach (var j in s_jars.ToList()) j.Tick(seconds);
            });
            s_timer.Start();
        }

        public static void Remove(FundJar jar)
        {
            s_jars.Remove(jar);
            if (s_jars.Count > 0 || s_timer is null) return;
            s_timer.Stop();
            s_timer = null;
        }
    }
}
