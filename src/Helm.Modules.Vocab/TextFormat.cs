using System.Globalization;
using System.Windows.Data;

namespace Helm.Modules.Vocab;

/// <summary>
/// A run's text from an optional value: <c>ConverterParameter="{}: {0}"</c> formats it when there is one, and the part
/// after a <c>|</c> stands in when there is none (nothing by default). Spaces between the pieces of a line that share
/// one baseline ("&#x2002;" before the Hán Việt) and "–" for a missing Similar both come from it.
/// </summary>
public sealed class TextFormat : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string ?? value?.ToString() ?? "";
        var format = parameter as string ?? "{0}";
        var bar = format.IndexOf('|', StringComparison.Ordinal);
        var empty = bar < 0 ? "" : format[(bar + 1)..];
        if (bar >= 0) format = format[..bar];
        return text.Length == 0 ? empty : format.Contains("{0}", StringComparison.Ordinal) ? string.Format(culture, format, text) : format;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>The text in capitals (the canvas's <c>text-transform: uppercase</c>: a part's role, "MEANING").</summary>
public sealed class UpperCase : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string ?? "").ToUpper(culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
