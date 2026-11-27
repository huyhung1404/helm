using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Helm.Core.Ui;

/// <summary>
/// A burst of confetti from the top centre of the control, falling past its edges for about three seconds. Purely
/// decorative: it never takes touches, and it clears itself when done or when the control leaves the screen.
/// </summary>
public sealed class ConfettiView : Canvas
{
    // Festive colours on both themes; the pieces are small and move, so they need no theme brush.
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0xA8, 0x55, 0xF7), Color.FromRgb(0xEC, 0x48, 0x99),
    ];

    private static readonly TimeSpan Length = TimeSpan.FromSeconds(3.2);

    private readonly List<Piece> _pieces = [];
    private IDisposable? _moving;
    private double _age;

    public ConfettiView()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
    }

    /// <summary>Starts a burst (more pieces for <paramref name="big"/>).</summary>
    public void Play(bool big)
    {
        Stop();
        var random = new Random();
        var width = Math.Max(Bounds.Width, 200);
        var count = big ? 110 : 50;
        for (var i = 0; i < count; i++)
        {
            var shape = new Rectangle
            {
                Width = 5 + random.NextDouble() * 6,
                Height = 8 + random.NextDouble() * 8,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(Palette[random.Next(Palette.Length)]),
                RenderTransformOrigin = RelativePoint.Center,
                // Moved by its transform only (never Canvas.Left/Top): no layout while it falls.
                RenderTransform = new TransformGroup { Children = { new RotateTransform(random.Next(360)), new TranslateTransform(width / 2, 10) } },
            };
            var angle = (-90 + (random.NextDouble() - 0.5) * 140) * Math.PI / 180;
            var speed = 260 + random.NextDouble() * 420;
            _pieces.Add(new Piece(shape, width / 2, 10, Math.Cos(angle) * speed, Math.Sin(angle) * speed, (random.NextDouble() - 0.5) * 720));
            Children.Add(shape);
        }
        _age = 0;
        _moving = MotionClock.Run(this, Tick);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>One frame; false once the burst is over.</summary>
    private bool Tick(double dt)
    {
        dt = Math.Min(dt, 0.05);
        _age += dt;
        if (_age > Length.TotalSeconds)
        {
            Stop();
            return false;
        }
        var fade = _age > Length.TotalSeconds - 0.8 ? (Length.TotalSeconds - _age) / 0.8 : 1;
        foreach (var p in _pieces)
        {
            p.VelocityY += 900 * dt;
            p.VelocityX *= 1 - 1.2 * dt;
            p.X += p.VelocityX * dt;
            p.Y += p.VelocityY * dt;
            var moves = ((TransformGroup)p.Shape.RenderTransform!).Children;
            ((RotateTransform)moves[0]).Angle += p.Spin * dt;
            ((TranslateTransform)moves[1]).X = p.X;
            ((TranslateTransform)moves[1]).Y = p.Y;
            p.Shape.Opacity = fade;
        }
        return true;
    }

    private void Stop()
    {
        _moving?.Dispose();
        _moving = null;
        _pieces.Clear();
        Children.Clear();
    }

    private sealed class Piece(Rectangle shape, double x, double y, double vx, double vy, double spin)
    {
        public Rectangle Shape { get; } = shape;
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public double VelocityX { get; set; } = vx;
        public double VelocityY { get; set; } = vy;
        public double Spin { get; } = spin;
    }
}
