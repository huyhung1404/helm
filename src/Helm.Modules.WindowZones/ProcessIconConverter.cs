using System.Globalization;
using System.Windows.Data;
using Helm.Core.Desktop;

namespace Helm.Modules.WindowZones;

/// <summary>A program's icon from its .exe path (the WPF page's window rows; the view model only knows the path).</summary>
public sealed class ProcessIconConverter : IValueConverter
{
    public static ProcessIconConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string { Length: > 0 } path ? ShellIcons.ForFile(path) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
