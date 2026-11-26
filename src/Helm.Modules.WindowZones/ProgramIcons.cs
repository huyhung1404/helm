using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Avalonia.Media;

namespace Helm.Modules.WindowZones;

/// <summary>A program's icon from its .exe (Windows' shell icon), as an Avalonia picture for the page and the tab strip; cached per .exe.</summary>
internal static class ProgramIcons
{
    private static readonly ConcurrentDictionary<string, IImage?> s_icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when Windows has none (the views then show a generic glyph).</summary>
    public static IImage? For(string processPath) => s_icons.GetOrAdd(processPath, Load);

    private static IImage? Load(string path)
    {
        if (Helm.Core.Desktop.ShellIcons.ForFile(path) is not BitmapSource icon) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(icon));
        using var png = new MemoryStream();
        encoder.Save(png);
        png.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(png);
    }
}
