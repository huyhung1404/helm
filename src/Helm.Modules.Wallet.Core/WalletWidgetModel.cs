namespace Helm.Modules.Wallet;

/// <summary>One category on the home-screen widget.</summary>
public sealed record WalletWidgetRow(string Name, string Amount, int Percent);

/// <summary>
/// What the Android home-screen widget shows: this month's spending against the budget, today's, how many transactions
/// wait for a category, and where most of the money went. Platform-free, so it is tested here; the Android side only
/// draws it.
/// </summary>
public sealed record WalletWidgetModel(
    string Title,
    string Spent,
    string Detail,
    bool HasBudget,
    int BudgetPercent,
    bool IsOver,
    string Today,
    int ToCategorize,
    IReadOnlyList<WalletWidgetRow> Rows)
{
    public const int MaxRows = 3;

    public string ToCategorizeText => ToCategorize == 0 ? "All categorized" : $"{ToCategorize} to categorize";

    public static WalletWidgetModel Build(WalletStore store, DateTimeOffset now, TimeZoneInfo zone)
    {
        var month = WalletFormat.MonthOf(now, zone);
        var transactions = store.Transactions();
        var summary = WalletStats.Month(transactions, store.Categories(includeHidden: true), month, now, zone);
        var budget = WalletStats.Budget(store.MonthlyBudget, summary, now, zone);
        var detail = budget switch
        {
            null => $"Income {WalletFormat.Short(summary.Income)}",
            { IsOver: true } => $"Over budget by {WalletFormat.Short(-budget.Left)}",
            _ => $"of {WalletFormat.Short(budget.Budget)} · {WalletFormat.Short(budget.PerDay)} a day left",
        };
        var rows = summary.Spending.Take(MaxRows)
            .Select(s => new WalletWidgetRow(s.Name, WalletFormat.Short(s.Amount), (int)Math.Round(s.Share * 100)))
            .ToList();
        return new WalletWidgetModel(
            $"Spent in {month.ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture)}",
            WalletFormat.Money(summary.Spent),
            detail,
            budget is not null,
            budget?.Percent ?? 0,
            budget?.IsOver == true,
            $"Today {WalletFormat.Short(summary.SpentToday)}",
            transactions.Count(t => !t.Value.IsCategorized),
            rows);
    }
}
