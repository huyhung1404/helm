using System.Globalization;
using Avalonia.Data.Converters;

namespace Helm.Core.Ui;

/// <summary>Shows a text in capitals (the amber line above a glass page's title): {Binding X, Converter={x:Static core:UpperCase.Instance}}.</summary>
public sealed class UpperCase : IValueConverter
{
    public static UpperCase Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString()?.ToUpper(culture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
