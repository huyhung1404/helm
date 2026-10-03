using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Helm.Modules.Missions;

/// <summary>Visible when every bound value is true.</summary>
public sealed class AllTrueToVisibility : IMultiValueConverter
{
    public static AllTrueToVisibility Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => [];
}
