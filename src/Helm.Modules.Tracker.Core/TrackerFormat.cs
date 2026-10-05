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
