using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace Helm.Modules.Wallet;

/// <summary>Category icon names and colour slots to WPF symbols and brushes (in the current theme).</summary>
public static class WalletConverters
{
    /// <summary>An icon name (<see cref="WalletIcons"/>) to its 24 px Fluent symbol.</summary>
    public static IValueConverter Symbol { get; } = new Converter(v =>
        Enum.TryParse<SymbolRegular>(WalletIcons.Normalize(v as string) + "24", out var s) ? s : SymbolRegular.Tag24);

    /// <summary>A colour slot to the solid category colour (the icon, the swatch).</summary>
    public static IValueConverter Ink { get; } = new Converter(v => WalletTheme.Brush(Slot(v), 0xFF));

    /// <summary>A colour slot to the soft disc behind a category's icon.</summary>
    public static IValueConverter Fill { get; } = new Converter(v => WalletTheme.Brush(Slot(v), 0x30));

    private static int Slot(object? value) => value is int i ? i : 0;

    private sealed class Converter(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
    }
}

/// <summary>The theme the charts draw in, read from the app's text colour (light text: dark theme).</summary>
internal static class WalletTheme
{
    public static bool IsDark(FrameworkElement? element = null)
    {
        var brush = (element?.TryFindResource("TextFillColorPrimaryBrush") ?? Application.Current?.TryFindResource("TextFillColorPrimaryBrush")) as SolidColorBrush;
        if (brush is null) return false;
        var c = brush.Color;
        return 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B > 128;
    }

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static SolidColorBrush Frozen(uint argb)
    {
        var b = new SolidColorBrush(ToColor(argb));
        b.Freeze();
        return b;
    }

    /// <summary>A category colour (-1 is "Other") with an alpha.</summary>
    public static SolidColorBrush Brush(int slot, byte alpha, FrameworkElement? element = null)
    {
        var dark = IsDark(element);
        var argb = slot < 0 ? WalletPalette.Other(dark) : WalletPalette.Color(slot, dark);
        return Frozen(WalletPalette.WithAlpha(argb, alpha));
    }

    public static Brush Text(FrameworkElement element, string key, Brush fallback) => element.TryFindResource(key) as Brush ?? fallback;
}
