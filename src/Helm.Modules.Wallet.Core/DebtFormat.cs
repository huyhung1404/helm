using System.Globalization;

namespace Helm.Modules.Wallet;

/// <summary>Text the debt book shows (the same on both apps).</summary>
public static class DebtFormat
{
    /// <summary>"+1,500,000 ₫" (owed to the user), "−200,000 ₫" (the user owes), "0 ₫".</summary>
    public static string Balance(decimal balance) => WalletFormat.Signed(balance);

    public static string Kind(DebtEntryKind kind) => kind switch
    {
        DebtEntryKind.OwesMe => "Owes me",
        DebtEntryKind.IOwe => "I owe",
        DebtEntryKind.Repayment => "Repayment",
        _ => kind.ToString(),
    };

    /// <summary>The kinds in <see cref="DebtEntryKind"/> order, for the pickers.</summary>
    public static IReadOnlyList<string> KindNames { get; } = Enum.GetValues<DebtEntryKind>().Select(Kind).ToList();

    /// <summary>A transaction's money as a debt entry: "Lent to Nam", "Borrowed from Nam", "Nam paid back", "Paid Nam back".</summary>
    public static string ForTransaction(WalletDebt debt) => debt.Kind switch
    {
        DebtEntryKind.OwesMe => $"Lent to {debt.Person}",
        DebtEntryKind.IOwe => $"Borrowed from {debt.Person}",
        _ when debt.Direction == DebtDirection.IOwe => $"{debt.Person} paid back",
        _ => $"Paid {debt.Person} back",
    };

    /// <summary>Local date and time in the current culture's short pattern.</summary>
    public static string When(DateTimeOffset at, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("g", CultureInfo.CurrentCulture);

    /// <summary>"just now", "5 min ago", "14:30" (today), "Yesterday", "28 Sep" (this year) or "28 Sep 2025".</summary>
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
    /// "Due today 14:30", "Due tomorrow", "Overdue by 2 days", "Due 30/09 09:00": the time is shown when one was set.
    /// </summary>
    public static string Due(DateTimeOffset due, bool hasTime, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var local = TimeZoneInfo.ConvertTime(due, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var days = date.DayNumber - today.DayNumber;
        var time = hasTime ? " " + local.ToString("t", CultureInfo.CurrentCulture) : "";
        if (hasTime && due < now && days == 0) return "Overdue since" + time;
        return days switch
        {
            0 => "Due today" + time,
            1 => "Due tomorrow" + time,
            -1 => "Overdue by 1 day",
            < -1 => $"Overdue by {-days} days",
            _ => $"Due {date.ToString("d", CultureInfo.CurrentCulture)}{time}",
        };
    }
}
