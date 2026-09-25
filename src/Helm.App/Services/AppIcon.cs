using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helm.App.Services;

/// <summary>
/// <c>services:AppIcon.Size="20"</c> on an <see cref="Image"/> shows the Helm icon using the helm.ico frame drawn for
/// that many physical pixels (size × DPI scale). Downscaling the 256 px image to title-bar size blurs the 1 px gap
/// between the two windows and the thin bar; the per-size frames are drawn natively and stay crisp.
/// </summary>
internal static class AppIcon
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(double), typeof(AppIcon), new PropertyMetadata(0.0, OnSizeChanged));

    private static readonly Lazy<IReadOnlyList<BitmapFrame>> s_frames = new(LoadFrames);

    public static double GetSize(DependencyObject d) => (double)d.GetValue(SizeProperty);

    public static void SetSize(DependencyObject d, double value) => d.SetValue(SizeProperty, value);

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        var size = (double)e.NewValue;
        image.Width = size;
        image.Height = size;
        image.Stretch = Stretch.Uniform;
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        Apply(image);
        image.Loaded -= OnLoaded;
        image.Loaded += OnLoaded;
        image.DpiChanged -= OnDpiChanged;
        image.DpiChanged += OnDpiChanged;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((Image)sender);

    private static void OnDpiChanged(object sender, DpiChangedEventArgs e) => Apply((Image)sender);

    private static void Apply(Image image)
    {
        var pixels = GetSize(image) * VisualTreeHelper.GetDpi(image).DpiScaleX;
        var frames = s_frames.Value;
        // Smallest frame that is at least as large as needed (exact match when the size is one of the ico sizes).
        image.Source = frames.FirstOrDefault(f => f.PixelWidth >= Math.Round(pixels)) ?? frames[^1];
    }

    private static IReadOnlyList<BitmapFrame> LoadFrames()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/helm.ico"))!.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(f => f.PixelWidth).ToList();
    }
}
