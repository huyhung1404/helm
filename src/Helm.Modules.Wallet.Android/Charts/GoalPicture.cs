using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Helm.Modules.Wallet;

/// <summary>
/// A savings goal's photo that lights up from the bottom as money is set aside: below the line it is in full colour,
/// above it grey and dim, with a soft bright line where they meet. The light rises smoothly after money is set aside
/// (a short timer runs only while it moves). Without a photo the goal's colour and first letter stand in.
/// </summary>
public sealed class GoalPicture : Control
{
    private static readonly ConditionalWeakTable<byte[], Decoded> Pictures = new();

    /// <summary>The photo's JPEG (a byte array); null shows the colour and the first letter.</summary>
    public static readonly StyledProperty<object?> PhotoProperty = AvaloniaProperty.Register<GoalPicture, object?>(nameof(Photo));

    public static readonly StyledProperty<double> ShareProperty = AvaloniaProperty.Register<GoalPicture, double>(nameof(Share));

    public static readonly StyledProperty<int> ColorProperty = AvaloniaProperty.Register<GoalPicture, int>(nameof(Color));

    public static readonly StyledProperty<string> InitialProperty = AvaloniaProperty.Register<GoalPicture, string>(nameof(Initial), "");

    public static readonly StyledProperty<double> CornerRadiusProperty = AvaloniaProperty.Register<GoalPicture, double>(nameof(CornerRadius));

    private double _shown = -1;
    private DispatcherTimer? _timer;
    private DateTime _last;

    static GoalPicture()
    {
        AffectsRender<GoalPicture>(PhotoProperty, ColorProperty, InitialProperty, CornerRadiusProperty);
    }

    public GoalPicture()
    {
        IsHitTestVisible = false;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public object? Photo
    {
        get => GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    /// <summary>How far it is lit, 0–1 (saved as a share of the price).</summary>
    public double Share
    {
        get => GetValue(ShareProperty);
        set => SetValue(ShareProperty, value);
    }

    /// <summary>The goal's colour slot (<see cref="WalletPalette"/>), behind a goal without a photo.</summary>
    public int Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string Initial
    {
        get => GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private double Target => Math.Clamp(double.IsNaN(Share) ? 0 : Share, 0, 1);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ShareProperty) return;
        // The first value shows at once; later ones rise to it.
        if (_shown < 0 || VisualRoot is null) _shown = Target;
        else StartMoving();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopMoving();
        _shown = Target;
        base.OnDetachedFromVisualTree(e);
    }

    private void StartMoving()
    {
        if (_timer is not null) return;
        _last = DateTime.UtcNow;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            var now = DateTime.UtcNow;
            var seconds = Math.Min(0.1, (now - _last).TotalSeconds);
            _last = now;
            var target = Target;
            _shown += (target - _shown) * Math.Min(1, seconds * 4.5);
            if (Math.Abs(target - _shown) < 0.002)
            {
                _shown = target;
                StopMoving();
            }
            InvalidateVisual();
        });
        _timer.Start();
    }

    private void StopMoving()
    {
        _timer?.Stop();
        _timer = null;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var bounds = new Rect(0, 0, w, h);
        var lit = _shown < 0 ? Target : _shown;
        var line = h * (1 - lit);
        using (context.PushClip(new RoundedRect(bounds, CornerRadius)))
        {
            if (Photo is byte[] { Length: > 0 } bytes && Pictures.GetValue(bytes, Decode) is { Color: { } color, Grey: { } grey })
            {
                var source = Cover(color.Size, bounds.Size);
                context.FillRectangle(Brushes.Black, bounds);
                using (context.PushOpacity(0.5)) context.DrawImage(grey, source, bounds);
                using (context.PushClip(new Rect(0, line, w, h - line))) context.DrawImage(color, source, bounds);
            }
            else
            {
                // No photo (yet): the colour, dim above the line and bright below, with the first letter.
                var dark = WalletTheme.IsDark(this);
                context.FillRectangle(WalletTheme.Category(Color, 0x40, dark), bounds);
                context.FillRectangle(WalletTheme.Category(Color, 0xFF, dark), new Rect(0, line, w, h - line));
                var text = WalletTheme.Text(this, Initial, Math.Min(w, h) * 0.42, Brushes.White, FontWeight.SemiBold);
                context.DrawText(text, new Point((w - text.Width) / 2, (h - text.Height) / 2));
            }
            if (lit is > 0.002 and < 0.998)
            {
                // The bright edge of the light: a soft glow and a thin line.
                var glow = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Avalonia.Media.Color.FromArgb(0, 255, 255, 255), 0), new GradientStop(Avalonia.Media.Color.FromArgb(0x70, 255, 255, 255), 1) },
                };
                context.FillRectangle(glow, new Rect(0, Math.Max(0, line - 10), w, Math.Min(10, line)));
                context.FillRectangle(WalletTheme.Brush(0xE6FFFFFFu), new Rect(0, line - 1, w, 2));
            }
        }
    }

    /// <summary>The part of the picture that fills the box, centred (the rest is cut off).</summary>
    private static Rect Cover(Size picture, Size box)
    {
        var scale = Math.Max(box.Width / picture.Width, box.Height / picture.Height);
        var width = box.Width / scale;
        var height = box.Height / scale;
        return new Rect((picture.Width - width) / 2, (picture.Height - height) / 2, width, height);
    }

    private sealed record Decoded(Bitmap? Color, Bitmap? Grey);

    /// <summary>The photo, and a grey copy of it (Avalonia has no grey filter: the pixels are turned once here).</summary>
    private static Decoded Decode(byte[] bytes)
    {
        try
        {
            using var first = new MemoryStream(bytes);
            var color = Bitmap.DecodeToWidth(first, 480);
            using var second = new MemoryStream(bytes);
            var grey = WriteableBitmap.DecodeToWidth(second, 480);
            using (var buffer = grey.Lock())
            {
                var rgba = buffer.Format == PixelFormat.Rgba8888;
                var length = buffer.RowBytes * buffer.Size.Height;
                var pixels = new byte[length];
                Marshal.Copy(buffer.Address, pixels, 0, length);
                for (var y = 0; y < buffer.Size.Height; y++)
                {
                    var row = y * buffer.RowBytes;
                    for (var x = 0; x < buffer.Size.Width; x++)
                    {
                        var i = row + x * 4;
                        int r = rgba ? pixels[i] : pixels[i + 2], g = pixels[i + 1], b = rgba ? pixels[i + 2] : pixels[i];
                        var l = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = l;
                    }
                }
                Marshal.Copy(pixels, 0, buffer.Address, length);
            }
            return new Decoded(color, grey);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException or NullReferenceException)
        {
            return new Decoded(null, null);
        }
    }
}
