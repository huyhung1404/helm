using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace Helm.Modules.Wallet;

/// <summary>
/// A savings goal's photo that lights up from the bottom as money is set aside: below the line it is in full colour,
/// above it grey and dim, with a soft bright line where they meet. The light rises smoothly after money is set aside.
/// Without a photo the goal's colour and first letter stand in. Without Windows' animations the light jumps.
/// </summary>
public sealed class GoalPicture : FrameworkElement
{
    private static readonly ConditionalWeakTable<byte[], Decoded> Pictures = new();

    public static readonly DependencyProperty PhotoProperty = DependencyProperty.Register(
        nameof(Photo), typeof(object), typeof(GoalPicture), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShareProperty = DependencyProperty.Register(
        nameof(Share), typeof(double), typeof(GoalPicture), new PropertyMetadata(0.0, (d, e) => ((GoalPicture)d).Animate((double)e.NewValue)));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(int), typeof(GoalPicture), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InitialProperty = DependencyProperty.Register(
        nameof(Initial), typeof(string), typeof(GoalPicture), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(double), typeof(GoalPicture), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The light drawn now (animated toward <see cref="Share"/>).</summary>
    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(GoalPicture), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public GoalPicture()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) => Animate(Share);
    }

    /// <summary>The photo's JPEG (a byte array; typed object, as XAML cannot bind an array property in a template); null shows the colour and the first letter.</summary>
    public object? Photo
    {
        get => GetValue(PhotoProperty);
        set => SetValue(PhotoProperty, value);
    }

    /// <summary>How far it is lit, 0–1 (saved as a share of the price).</summary>
    public double Share
    {
        get => (double)GetValue(ShareProperty);
        set => SetValue(ShareProperty, value);
    }

    /// <summary>The goal's colour slot (<see cref="WalletPalette"/>), behind a goal without a photo.</summary>
    public int Color
    {
        get => (int)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public string Initial
    {
        get => (string)GetValue(InitialProperty);
        set => SetValue(InitialProperty, value);
    }

    public double CornerRadius
    {
        get => (double)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private void Animate(double share)
    {
        share = Math.Clamp(double.IsNaN(share) ? 0 : share, 0, 1);
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(ShownProperty, null);
            SetValue(ShownProperty, share);
            return;
        }
        BeginAnimation(ShownProperty, new DoubleAnimation(share, TimeSpan.FromSeconds(0.9)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var bounds = new Rect(0, 0, w, h);
        dc.PushClip(new RectangleGeometry(bounds, CornerRadius, CornerRadius));
        var lit = (double)GetValue(ShownProperty);
        var line = h * (1 - lit);
        if (Photo is byte[] { Length: > 0 } bytes && Pictures.GetValue(bytes, Decode) is { Color: { } color, Grey: { } grey })
        {
            var place = Cover(color, bounds);
            dc.DrawRectangle(Brushes.Black, null, bounds);
            dc.PushOpacity(0.5);
            dc.DrawImage(grey, place);
            dc.Pop();
            dc.PushClip(new RectangleGeometry(new Rect(0, line, w, h - line)));
            dc.DrawImage(color, place);
            dc.Pop();
        }
        else
        {
            // No photo (yet): the colour, dim above the line and bright below, with the first letter.
            dc.DrawRectangle(WalletTheme.Brush(Color, 0x40, this), null, bounds);
            dc.DrawRectangle(WalletTheme.Brush(Color, 0xFF, this), null, new Rect(0, line, w, h - line));
            var text = new FormattedText(Initial, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Display"),
                Math.Min(w, h) * 0.42, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip) { TextAlignment = TextAlignment.Center };
            text.SetFontWeight(FontWeights.SemiBold);
            dc.DrawText(text, new Point(w / 2, (h - text.Height) / 2));
        }
        if (lit is > 0.002 and < 0.998)
        {
            // The bright edge of the light: a soft glow and a thin line.
            var glow = new LinearGradientBrush(System.Windows.Media.Color.FromArgb(0, 255, 255, 255), System.Windows.Media.Color.FromArgb(0x70, 255, 255, 255), 90);
            glow.Freeze();
            dc.DrawRectangle(glow, null, new Rect(0, Math.Max(0, line - 10), w, Math.Min(10, line)));
            dc.DrawRectangle(WalletTheme.Frozen(0xE6FFFFFF), null, new Rect(0, line - 1, w, 2));
        }
        dc.Pop();
    }

    /// <summary>The picture scaled to fill the box, centred (the parts that stick out are clipped).</summary>
    private static Rect Cover(ImageSource image, Rect box)
    {
        var scale = Math.Max(box.Width / image.Width, box.Height / image.Height);
        var size = new Size(image.Width * scale, image.Height * scale);
        return new Rect(box.X + (box.Width - size.Width) / 2, box.Y + (box.Height - size.Height) / 2, size.Width, size.Height);
    }

    private sealed record Decoded(BitmapSource? Color, BitmapSource? Grey);

    private static Decoded Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            var grey = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
            grey.Freeze();
            return new Decoded(image, grey);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException)
        {
            return new Decoded(null, null);
        }
    }
}
