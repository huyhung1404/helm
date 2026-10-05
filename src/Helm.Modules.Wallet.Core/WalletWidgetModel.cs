using System.Globalization;

namespace Helm.Modules.Wallet;

/// <summary>The stretch of days the widget's tabs pick.</summary>
public enum WalletWidgetPeriod
{
    Today,

    /// <summary>Since Monday.</summary>
    Week,

    /// <summary>Since the 1st of the month.</summary>
    Month,
    All,
}

/// <summary>What the widget's ring shows.</summary>
public enum WalletWidgetChart
{
    /// <summary>The period's spending split by category.</summary>
    Categories,

    /// <summary>The balance against the period's spending.</summary>
    BalanceAndSpent,
}

/// <summary>One slice of the widget's ring (and its legend row).</summary>
/// <param name="Color">A <see cref="WalletPalette"/> slot, or <see cref="WalletWidgetModel.OtherColor"/> / <see cref="WalletWidgetModel.BalanceColor"/>.</param>
public sealed record WalletWidgetSlice(string Name, decimal Amount, double Share, int Color)
{
    public string ShareText => Share > 0 && Share < 0.005 ? "<1%" : $"{Math.Round(Share * 100):0}%";
}

/// <summary>
/// What the Android home-screen widget shows: the balance in the middle of a ring, the chosen period's spending (by
/// category, or against the balance) and income, and how many transactions wait for a category. Platform-free, so it
/// is tested here; the Android side only draws it.
/// </summary>
/// <param name="Balance">
/// The money there is now: everything received minus everything spent. Not a bank's balance: cash taken out stays the
/// user's money, and spending from the bank is read as a transaction and subtracted anyway.
/// </param>
public sealed record WalletWidgetModel(
    WalletWidgetPeriod Period,
    decimal Balance,
    decimal Spent,
    decimal Earned,
    IReadOnlyList<WalletWidgetSlice> Slices,
    int ToCategorize)
{
    /// <summary>Slices of the categories ring: the four largest, the rest folded into one "Other".</summary>
    public const int MaxSlices = 5;

    public const int OtherColor = -1;
    public const int BalanceColor = -2;

    /// <summary>The colour of the spending slice of the balance ring (orange, apart from the balance's teal).</summary>
    public const int SpentColor = 2;

    public static IReadOnlyList<string> ChartNames { get; } = ["Spending by category", "Balance and spending"];

    /// <summary>The tab's full name ("Today", "Week", ...).</summary>
    public static string TabName(WalletWidgetPeriod period) => period switch
    {
        WalletWidgetPeriod.Today => "Today",
        WalletWidgetPeriod.Week => "Week",
        WalletWidgetPeriod.Month => "Month",
        _ => "All",
    };

    /// <summary>The tab's name where four tabs share a narrow column ("D", "W", "M", "All").</summary>
    public static string ShortTabName(WalletWidgetPeriod period) => period switch
    {
        WalletWidgetPeriod.Today => "D",
        WalletWidgetPeriod.Week => "W",
        WalletWidgetPeriod.Month => "M",
        _ => "All",
    };

    /// <summary>The period after this one, for the small widget's single button.</summary>
    public static WalletWidgetPeriod Next(WalletWidgetPeriod period) => (WalletWidgetPeriod)(((int)period + 1) % 4);

    /// <summary>"Today", "This week", "This month", "All time".</summary>
    public string PeriodName => Period switch
    {
        WalletWidgetPeriod.Today => "Today",
        WalletWidgetPeriod.Week => "This week",
        WalletWidgetPeriod.Month => "This month",
        _ => "All time",
    };

    /// <summary>"today", "this week", "this month", "in total": after "Spent".</summary>
    public string PeriodWords => Period switch
    {
        WalletWidgetPeriod.Today => "today",
        WalletWidgetPeriod.Week => "this week",
        WalletWidgetPeriod.Month => "this month",
        _ => "in total",
    };

    /// <summary>The words above the amount in the ring: always the balance (the widget's main figure).</summary>
    public const string CenterLabel = "Balance";

    /// <summary>"−1.7M · +500K" under the balance on the small widget; empty when there is nothing to say.</summary>
    public string CenterDetail
    {
        get
        {
            var parts = new List<string>(2);
            if (Spent > 0) parts.Add("−" + Bare(Spent));
            if (Earned > 0) parts.Add("+" + Bare(Earned));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The count on the notification-style dot; null when nothing waits (the dot is hidden).</summary>
    public string? BadgeText => ToCategorize switch
    {
        <= 0 => null,
        > 99 => "99+",
        _ => ToCategorize.ToString(CultureInfo.CurrentCulture),
    };

    public bool IsEmpty => Slices.Count == 0;

    public string EmptyText => $"Nothing spent {PeriodWords}";

    /// <summary>The first and last local day of a period (the first is null for All).</summary>
    public static (DateOnly? From, DateOnly To) Range(WalletWidgetPeriod period, DateOnly today) => period switch
    {
        WalletWidgetPeriod.Today => (today, today),
        WalletWidgetPeriod.Week => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),
        WalletWidgetPeriod.Month => (new DateOnly(today.Year, today.Month, 1), today),
        _ => (null, today),
    };

    /// <summary>A slice's colour as ARGB on a light (false) or dark (true) surface.</summary>
    public static uint ColorOf(int color, bool dark) => color switch
    {
        OtherColor => WalletPalette.Other(dark),
        BalanceColor => dark ? 0xFF2DD4BF : 0xFF047857,
        _ => WalletPalette.Color(color, dark),
    };

    public static WalletWidgetModel Build(WalletStore store, WalletWidgetPeriod period, WalletWidgetChart chart, DateTimeOffset now, TimeZoneInfo zone)
    {
        var transactions = store.Transactions();
        var categories = store.Categories(includeHidden: true);
        var (from, to) = Range(period, WalletFormat.LocalDay(now, zone));
        var summary = WalletStats.Period(transactions, categories, from, to, zone);
        var balance = Net(transactions, categories, to, zone);
        var toCategorize = transactions.Count(t => !t.Value.IsCategorized);

        IReadOnlyList<WalletWidgetSlice> slices;
        if (chart == WalletWidgetChart.BalanceAndSpent)
        {
            var model = new WalletWidgetModel(period, balance, summary.Spent, summary.Income, [], toCategorize);
            var held = Math.Max(balance, 0);
            var total = held + summary.Spent;
            slices = total <= 0 ? [] : new[]
                {
                    new WalletWidgetSlice($"Spent {model.PeriodWords}", summary.Spent, (double)(summary.Spent / total), SpentColor),
                    new WalletWidgetSlice("Balance", held, (double)(held / total), BalanceColor),
                }
                .Where(s => s.Amount > 0)
                .ToList();
            return model with { Slices = slices };
        }

        var byId = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var rows = summary.Spending;
        var shown = rows.Count <= MaxSlices ? rows : rows.Take(MaxSlices - 1).ToList();
        var list = shown
            .Select(r => new WalletWidgetSlice(r.Name, r.Amount, r.Share,
                r.CategoryId is { } id && byId.TryGetValue(id, out var info) ? info.Color : 0))
            .ToList();
        if (shown.Count < rows.Count)
        {
            var rest = rows.Skip(shown.Count).ToList();
            var amount = rest.Sum(r => r.Amount);
            list.Add(new WalletWidgetSlice($"Other ({rest.Count})", amount, (double)(amount / summary.Spent), OtherColor));
        }
        return new WalletWidgetModel(period, balance, summary.Spent, summary.Income, list, toCategorize);
    }

    /// <summary>All money in minus all money out until <paramref name="to"/> (transfers between own accounts are neither).</summary>
    private static decimal Net(IReadOnlyList<Helm.Core.Sync.SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories,
        DateOnly to, TimeZoneInfo zone)
    {
        var all = WalletStats.Period(transactions, categories, null, to, zone);
        return all.Income - all.Spent;
    }

    /// <summary>The balance in full ("12,370,000 ₫"), with a minus sign when more went out than came in.</summary>
    public string BalanceText => (Balance < 0 ? "−" : "") + WalletFormat.Money(Balance);

    /// <summary>The balance in short ("12.4M ₫").</summary>
    public string BalanceShortText => (Balance < 0 ? "−" : "") + WalletFormat.Short(Balance);

    /// <summary>A short amount without the currency sign ("1.7M", "500K").</summary>
    private static string Bare(decimal amount) => WalletFormat.Short(amount).Replace(" " + WalletFormat.Currency, "", StringComparison.Ordinal);
}
