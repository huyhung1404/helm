using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Helm.Modules.Vault.Items;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vault.Views;

/// <summary>An item's own picture (bytes from the sealed item) as a small frozen bitmap; null when absent or unreadable.</summary>
public sealed class BytesToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            // A damaged picture shows the letter avatar instead.
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True for a non-empty string (InfoBar.IsOpen from an error message).</summary>
public sealed class NotEmptyToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string { Length: > 0 };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n != 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>The icon of a kind of item (the same symbols as the Android app).</summary>
public sealed class KindIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        VaultItemKind.Login => SymbolRegular.Key24,
        VaultItemKind.Note => SymbolRegular.Note24,
        VaultItemKind.Card => SymbolRegular.Payment24,
        VaultItemKind.Identity => SymbolRegular.ContactCard24,
        VaultItemKind.Document => SymbolRegular.Document24,
        VaultItemKind.Token => SymbolRegular.Code24,
        _ => SymbolRegular.LockClosed24,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>The editor's height limit: the room above the list minus its margins, so Save stays on screen.</summary>
public sealed class EditorHeightConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double height && height > 120 ? height - 48 : double.PositiveInfinity;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => DependencyProperty.UnsetValue;
}
