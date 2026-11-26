using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Helm.Shell.Views;

/// <summary>Pictures the shell draws: Helm's icon (title bar, the phone's drawer).</summary>
internal static class ShellAssets
{
    private static readonly Lazy<Bitmap?> Icon = new(() =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Helm.Shell.UI/Assets/helm.png"));
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null; // a missing picture leaves the name alone
        }
    });

    public static Bitmap? AppIcon => Icon.Value;
}
