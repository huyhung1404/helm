using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>Spending of one category in a month.</summary>
/// <param name="CategoryId">Null for the transactions still to categorize.</param>
public sealed record CategorySpend(string? CategoryId, string Name, decimal Amount, int Count, double Share);

/// <summary>A month in numbers. Transfers between the user's own accounts count neither as spending nor as income.</summary>
public sealed record MonthSummary(
    DateOnly Month,
    decimal Spent,
    decimal Income,
    int Count,
    int Uncategorized,
    decimal SpentToday,
    IReadOnlyList<CategorySpend> Spending,
    IReadOnlyList<CategorySpend> Earning,
    decimal PreviousSpent,
    decimal PreviousSpentSameDay)
{
    public decimal Net => Income - Spent;
}

/// <summary>Where the month stands against the monthly budget.</summary>
/// <param name="Percent">Spent as a share of the budget, 0–100 (more is clamped; see <see cref="IsOver"/>).</param>
/// <param name="PerDay">What can still be spent each day until the month ends; 0 when nothing is left.</param>
public sealed record BudgetStatus(decimal Budget, decimal Spent, decimal Left, int Percent, bool IsOver, int DaysLeft, decimal PerDay);

/// <summary>The statistics of the Wallet pages and widget. Pure functions over the transactions, tested in the core.</summary>
public static class WalletStats
{
    public const string UncategorizedName = "To categorize";

    public static MonthSummary Month(IEnumerable<SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories,
        DateOnly month, DateTimeOffset now, TimeZoneInfo zone)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        var byId = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var previous = month.AddMonths(-1);
        var today = WalletFormat.LocalDay(now, zone);
        // "At this point last month": the same day of the month (or the month's last day), for the current month.
        var sameDay = Math.Min(today.Day, DateTime.DaysInMonth(previous.Year, previous.Month));
        var all = transactions.Select(t => t.Value).ToList();

        decimal spent = 0, income = 0, spentToday = 0, previousSpent = 0, previousSameDay = 0;
        int count = 0, uncategorized = 0;
        var spending = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.Ordinal);
        var earning = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.Ordinal);
        foreach (var t in all)
        {
            var day = WalletFormat.LocalDay(t.OccurredAt, zone);
            var kind = t.CategoryId is { } c && byId.TryGetValue(c, out var info) ? info.Kind : (CategoryKind?)null;
            if (kind == CategoryKind.Transfer) continue;
            if (day.Year == previous.Year && day.Month == previous.Month && t.Amount < 0)
            {
                previousSpent -= t.Amount;
                if (day.Day <= sameDay) previousSameDay -= t.Amount;
            }
            if (day.Year != month.Year || day.Month != month.Month) continue;
            count++;
            if (!t.IsCategorized) uncategorized++;
            var key = t.CategoryId is { } id && byId.ContainsKey(id) ? id : "";
            if (t.Amount < 0)
            {
                spent -= t.Amount;
                if (day == today) spentToday -= t.Amount;
                var s = spending.GetValueOrDefault(key);
                spending[key] = (s.Amount - t.Amount, s.Count + 1);
            }
            else
            {
                income += t.Amount;
                var e = earning.GetValueOrDefault(key);
                earning[key] = (e.Amount + t.Amount, e.Count + 1);
            }
        }

        List<CategorySpend> Rows(Dictionary<string, (decimal Amount, int Count)> sums, decimal total) => sums
            .Select(kv => new CategorySpend(kv.Key.Length == 0 ? null : kv.Key, kv.Key.Length == 0 ? UncategorizedName : byId[kv.Key].Name,
                kv.Value.Amount, kv.Value.Count, total == 0 ? 0 : (double)(kv.Value.Amount / total)))
            .OrderByDescending(r => r.Amount)
            .ToList();

        var isCurrent = month.Year == today.Year && month.Month == today.Month;
        return new MonthSummary(month, spent, income, count, uncategorized, isCurrent ? spentToday : 0, Rows(spending, spent), Rows(earning, income),
            previousSpent, isCurrent ? previousSameDay : previousSpent);
    }

    /// <summary>The month against the budget; null without a budget.</summary>
    public static BudgetStatus? Budget(decimal budget, MonthSummary month, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (budget <= 0) return null;
        var today = WalletFormat.LocalDay(now, zone);
        var days = DateTime.DaysInMonth(month.Month.Year, month.Month.Month);
        var isCurrent = month.Month.Year == today.Year && month.Month.Month == today.Month;
        var isPast = month.Month < new DateOnly(today.Year, today.Month, 1);
        var daysLeft = isCurrent ? days - today.Day + 1 : isPast ? 0 : days;
        var left = budget - month.Spent;
        var percent = (int)Math.Clamp(Math.Round(month.Spent * 100 / budget), 0, 100);
        var raw = left > 0 && daysLeft > 0 ? left / daysLeft : 0;
        // Round down to thousands: "120,000 ₫ a day" reads better than "121,483 ₫".
        var perDay = raw >= 1000 ? decimal.Floor(raw / 1000) * 1000 : decimal.Floor(raw);
        return new BudgetStatus(budget, month.Spent, left, percent, left < 0, daysLeft, perDay);
    }
}
