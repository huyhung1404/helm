using System.Globalization;

namespace Helm.Modules.Tracker;

/// <summary>Text shown for durations, amounts, dates and priorities (the same on both apps).</summary>
public static class TrackerFormat
{
    /// <summary>"45m", "3h 20m", "2d 4h"; "—" for none.</summary>
    public static string Duration(TimeSpan? span)
    {
        if (span is not { } s) return "—";
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        if (s.TotalMinutes < 1) return "<1m";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes}m";
        if (s.TotalDays < 1) return s.Minutes == 0 ? $"{(int)s.TotalHours}h" : $"{(int)s.TotalHours}h {s.Minutes}m";
        return s.Hours == 0 ? $"{(int)s.TotalDays}d" : $"{(int)s.TotalDays}d {s.Hours}h";
    }

    /// <summary>"1,500,000 ₫" (no decimals unless there are any).</summary>
    public static string Money(decimal amount, string currency, CultureInfo? culture = null)
    {
        var text = amount.ToString("#,0.##", culture ?? CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(currency) ? text : $"{text} {currency.Trim()}";
    }

    /// <summary>
    /// Reads an amount as people type it: "1500000", "1,500,000", "1.500.000", "1.5k", "500k", "2m", "2tr".
    /// A single '.' or ',' followed by 1–2 digits is a decimal point; otherwise separators group thousands.
    /// </summary>
    public static bool TryParseAmount(string? text, out decimal amount)
    {
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToLowerInvariant().Replace(" ", "").Replace(" ", "");

        decimal multiplier = 1;
        if (s.EndsWith("tr", StringComparison.Ordinal)) { multiplier = 1_000_000; s = s[..^2]; }
        else if (s.EndsWith('m')) { multiplier = 1_000_000; s = s[..^1]; }
        else if (s.EndsWith('k')) { multiplier = 1_000; s = s[..^1]; }
        if (s.Length == 0) return false;

        var separators = s.Count(c => c is '.' or ',');
        var last = s.LastIndexOfAny(['.', ',']);
        string normalized;
        if (separators == 0)
            normalized = s;
        else if (separators == 1 && (s.Length - last - 1 is 1 or 2 || multiplier != 1))
            normalized = s.Remove(last, 1).Insert(last, "."); // decimal point ("1.5k", "12,50")
        else if (last >= 0 && s.Length - last - 1 is 1 or 2 && s.Count(c => c == s[last]) == 1)
            normalized = new string(s[..last].Where(char.IsDigit).ToArray()) + "." + s[(last + 1)..]; // "1,234.56"
        else
            normalized = new string(s.Where(char.IsDigit).ToArray()); // thousands groups only

        if (!normalized.All(c => char.IsDigit(c) || c == '.') || normalized.Count(c => c == '.') > 1) return false;
        if (!decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return false;
        amount = decimal.Round(value * multiplier, 2);
        return true;
    }

    public static string Priority(TrackerPriority priority) => priority switch
    {
        TrackerPriority.Urgent => "Urgent",
        TrackerPriority.High => "High",
        TrackerPriority.Normal => "Normal",
        TrackerPriority.Low => "Low",
        _ => priority.ToString(),
    };

    public static string Kind(WorkspaceKind kind) => kind switch
    {
        WorkspaceKind.Tasks => "To-do list",
        WorkspaceKind.Debts => "Debt book",
        _ => kind.ToString(),
    };

    public static string Direction(DebtDirection direction) => direction switch
    {
        DebtDirection.TheyOweMe => "Owes me",
        DebtDirection.IOwe => "I owe",
        _ => direction.ToString(),
    };

    /// <summary>Local date and time in the current culture's short pattern.</summary>
    public static string When(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture);

    public static string Due(DateOnly due, DateOnly today)
    {
        var days = due.DayNumber - today.DayNumber;
        return days switch
        {
            0 => "Due today",
            1 => "Due tomorrow",
            -1 => "Overdue by 1 day",
            < -1 => $"Overdue by {-days} days",
            _ => $"Due {due.ToString("d", CultureInfo.CurrentCulture)}",
        };
    }
}
