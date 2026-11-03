using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Helm.Modules.Vocab;

/// <summary>
/// Vocabulary's own colours (the "Vocab…" brushes the views take as DynamicResource): VocabPaletteDark.xaml or
/// VocabPaletteLight.xaml in the application's resources, the one of the theme in use, swapped when it changes.
/// </summary>
public static class VocabPalette
{
    private static readonly Uri Dark = new("pack://application:,,,/Helm.Modules.Vocab;component/VocabPaletteDark.xaml");
    private static readonly Uri Light = new("pack://application:,,,/Helm.Modules.Vocab;component/VocabPaletteLight.xaml");
    private static ResourceDictionary? s_current;

    /// <summary>Puts the palette in place (once; every Vocabulary window calls it before it builds its content).</summary>
    public static void Ensure()
    {
        if (Application.Current is null) return;
        if (s_current is null) ApplicationThemeManager.Changed += (_, _) => Apply();
        Apply();
    }

    private static void Apply()
    {
        var resources = Application.Current.Resources;
        var source = IsDark(resources) ? Dark : Light;
        if (s_current?.Source == source && resources.MergedDictionaries.Contains(s_current)) return;
        if (s_current is not null) resources.MergedDictionaries.Remove(s_current);
        s_current = new ResourceDictionary { Source = source };
        resources.MergedDictionaries.Add(s_current);
    }

    // The theme's own text colour tells it apart, whatever started it (the app's setting, the system, a test).
    private static bool IsDark(ResourceDictionary resources) =>
        resources["TextFillColorPrimaryBrush"] is SolidColorBrush { Color: var c } ? 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 128
            : ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
}
