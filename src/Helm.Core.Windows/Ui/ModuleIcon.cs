using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Helm.Core.Modules;
using Wpf.Ui.Controls;

namespace Helm.Core.Ui;

/// <summary>Builds the icon element for a module: its <see cref="IHelmModule.IconImage"/> when set, else its symbol.</summary>
public static class ModuleIcon
{
    /// <param name="fontSize">Symbol size; null keeps the size the host gives it (cards, navigation items).</param>
    /// <param name="imageSize">Width and height of a picture icon, which has no font size to inherit.</param>
    public static IconElement Create(IHelmModule module, double? fontSize = null, double imageSize = 20)
    {
        if (module.IconImage is { } image)
        {
            var icon = new ImageIcon { Source = image, Width = imageSize, Height = imageSize };
            // Bitmap icons are drawn much smaller than their source (256 px into 18-56 px); the default linear scaling
            // drops thin strokes. Vector icons (DrawingImage) are unaffected.
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.HighQuality);
            return icon;
        }
        var symbol = new SymbolIcon { Symbol = module.Icon };
        if (fontSize is { } size) symbol.FontSize = size;
        return symbol;
    }
}

/// <summary>
/// XAML: <c>Content="{Binding Converter={StaticResource ModuleIcon}, ConverterParameter=32}"</c> on a
/// ContentPresenter whose DataContext is an <see cref="IHelmModule"/>. The parameter is the size in DIPs.
/// </summary>
public sealed class ModuleIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IHelmModule module) return null;
        var size = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 20;
        return ModuleIcon.Create(module, size, size);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
