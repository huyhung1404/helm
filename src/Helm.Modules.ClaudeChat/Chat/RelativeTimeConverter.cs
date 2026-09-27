using System.Globalization;
using System.Windows.Data;

namespace Helm.Modules.ClaudeChat.Chat;

/// <summary>"just now", "5m ago", "3h ago", "yesterday", "4d ago", then the date.</summary>
public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime time ? Format(time, DateTime.Now, culture) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static string Format(DateTime time, DateTime now, CultureInfo culture)
    {
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        if (time.Date == now.Date) return $"{(int)age.TotalHours}h ago";
        if (time.Date == now.Date.AddDays(-1)) return "yesterday";
        if (age < TimeSpan.FromDays(7)) return $"{(int)Math.Ceiling(age.TotalDays)}d ago";
        return time.ToString("d", culture);
    }
}
