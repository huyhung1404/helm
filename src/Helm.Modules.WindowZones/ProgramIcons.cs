using Avalonia.Media;

namespace Helm.Modules.WindowZones;

/// <summary>A program's icon from its .exe (Windows' shell icon), as an Avalonia picture for the page and the tab strip; cached per .exe (by ShellIcons).</summary>
internal static class ProgramIcons
{
    /// <summary>Null when Windows has none (the views then show a generic glyph).</summary>
    public static IImage? For(string processPath) => Helm.Core.Desktop.ShellIcons.ForFile(processPath);
}
