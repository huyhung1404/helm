using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Helm.Modules.Tracker;

/// <summary>Tracker's app icon (Assets/tracker.png, 256 px with transparency), used wherever the module appears.</summary>
public static class TrackerLogo
{
    /// <summary>Frozen: shared by the navigation, Home and the pages, which may live on different windows.</summary>
    public static ImageSource Image { get; } = Load();

    private static ImageSource Load()
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri("pack://application:,,,/Helm.Modules.Tracker;component/Assets/tracker.png", UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
