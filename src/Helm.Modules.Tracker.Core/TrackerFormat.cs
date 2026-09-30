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

    /// <summary>"+1,500,000 ₫" (owed to the user), "−200,000 ₫" (the user owes), "0 ₫".</summary>
    public static string Balance(decimal balance, string currency) =>
        (balance > 0 ? "+" : balance < 0 ? "−" : "") + Money(Math.Abs(balance), currency);

    /// <summary>"14:30" (culture short time) or "" for none.</summary>
    public static string Time(TimeSpan? time) =>
        time is { } t ? DateTime.Today.Add(t).ToString("t", CultureInfo.CurrentCulture) : "";

    /// <summary>A time of day as typed: "14:30", "1430", "9", "9h", "9h30", "2:05 PM". Empty text is no time (true, null).</summary>
    public static bool TryParseTime(string? text, out TimeSpan? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var s = text.Trim().ToLowerInvariant().Replace('h', ':').Replace('.', ':');
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed)
            || DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out parsed))
        {
            time = parsed.TimeOfDay;
            return true;
        }
        var digits = new string(s.Where(char.IsDigit).ToArray());
        if (digits.Length is < 1 or > 4 || digits.Length != s.Trim(':').Length) return false;
        var hours = int.Parse(digits.Length <= 2 ? digits : digits[..^2], CultureInfo.InvariantCulture);
        var minutes = digits.Length <= 2 ? 0 : int.Parse(digits[^2..], CultureInfo.InvariantCulture);
        if (hours > 23 || minutes > 59) return false;
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    public static string DebtKind(DebtEntryKind kind) => kind switch
    {
        DebtEntryKind.OwesMe => "Owes me",
        DebtEntryKind.IOwe => "I owe",
        DebtEntryKind.Repayment => "Repayment",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Thousands separators while an amount is typed ("1500000" becomes "1,500,000" or "1.500.000" by culture), keeping
    /// the caret after the same digit. Text with anything else in it (decimals, "150k", "2tr") is left as typed.
    /// </summary>
    public static (string Text, int Caret) GroupDigits(string text, int caret, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (text.Length == 0 || !text.All(c => char.IsDigit(c) || c is '.' or ',' or ' ' or '\u00a0' or '\u202f')) return (text, caret);
        var raw = new string(text.Where(char.IsDigit).ToArray());
        if (raw.Length == 0) return ("", 0);
        var digits = raw.TrimStart('0');
        if (digits.Length == 0) digits = "0";
        // Leading zeros are dropped; they came before the caret.
        var digitsBefore = Math.Max(0, text.Take(Math.Clamp(caret, 0, text.Length)).Count(char.IsDigit) - (raw.Length - digits.Length));
        var grouped = decimal.Parse(digits, CultureInfo.InvariantCulture).ToString("#,0", culture);
        var newCaret = 0;
        for (var seen = 0; newCaret < grouped.Length && seen < digitsBefore; newCaret++)
            if (char.IsDigit(grouped[newCaret])) seen++;
        return (grouped, newCaret);
    }

    public static string Direction(DebtDirection direction) => direction switch
    {
        DebtDirection.TheyOweMe => "Owes me",
        DebtDirection.IOwe => "I owe",
        _ => direction.ToString(),
    };

    /// <summary>Local date and time in the current culture's short pattern.</summary>
    public static string When(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture);

    /// <summary>
    /// "just now", "5 min ago", "14:30" (today), "Yesterday", "28 Sep" (this year) or "28 Sep 2025": the same ladder
    /// as the Notes list, so both tools speak about time the same way.
    /// </summary>
    public static string WhenRelative(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var ago = now - at;
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes} min ago";
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        if (local.Date == today) return local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "Yesterday";
        return local.Year == today.Year
            ? local.ToString("d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// "Due today 14:30", "Due tomorrow", "Overdue by 2 days", "Due 30/09 09:00": the time is shown when one was set
    /// (<see cref="TrackerItem.DueAt"/>).
    /// </summary>
    public static string Due(TrackerItem item, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (item.DueAt is not { } at) return item.DueDate is { } day ? Due(day, today) : "";
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        var date = DateOnly.FromDateTime(local.DateTime);
        var days = date.DayNumber - today.DayNumber;
        if (at < now && days == 0) return $"Overdue since {time}";
        return days switch
        {
            0 => $"Due today {time}",
            1 => $"Due tomorrow {time}",
            -1 => "Overdue by 1 day",
            < -1 => $"Overdue by {-days} days",
            _ => $"Due {date.ToString("d", CultureInfo.CurrentCulture)} {time}",
        };
    }

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
