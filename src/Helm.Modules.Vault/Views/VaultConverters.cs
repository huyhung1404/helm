using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Helm.Modules.Vault.Items;
using Wpf.Ui.Controls;

namespace Helm.Modules.Vault.Views;

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
        VaultItemKind.Identity => SymbolRegular.PersonKey24,
        VaultItemKind.Document => SymbolRegular.Document24,
        _ => SymbolRegular.LockClosed24,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
