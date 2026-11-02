namespace Helm.Modules.Wallet;

/// <summary>A jar in the Funds widget: the fund, the water left (0–1), and what is left or over in short.</summary>
public sealed record FundsWidgetJar(string Name, int Color, FundStatus Status, double Level, string AmountText, bool IsOver);

/// <summary>
/// What the Android Funds widget shows: the month's funds, the ones that need a look first (over, used up, low, then
/// the least left), what is left of them all, and a line about the rest. Pure; the widget draws it.
/// </summary>
public sealed record FundsWidgetModel(string Title, string LeftText, IReadOnlyList<FundsWidgetJar> Jars, int Hidden, string OutsideText)
{
    public bool IsEmpty => Jars.Count == 0;

    /// <summary>"+2 more on track · Outside funds 1.73M ₫", or what to do when there are no funds.</summary>
    public string Footer
    {
        get
        {
            if (IsEmpty) return "No funds yet. Open Wallet to add one.";
            var parts = new List<string>();
            if (Hidden > 0) parts.Add($"+{Hidden} more");
            if (OutsideText.Length > 0) parts.Add("Outside funds " + OutsideText);
            return string.Join(" · ", parts);
        }
    }

    public static FundsWidgetModel Build(FundBook funds, WalletStore store, int shown, DateTimeOffset now, TimeZoneInfo zone)
    {
        var month = WalletFormat.MonthOf(now, zone);
        var transactions = store.Transactions();
        var states = funds.States(transactions, month, now, zone);
        var jars = states
            .OrderByDescending(s => s.Status)
            .ThenBy(s => s.LeftShare)
            .Take(Math.Max(0, shown))
            .Select(s => new FundsWidgetJar(s.Fund.Name, s.Fund.Color, s.Status, s.LeftShare,
                s.Left < 0 ? WalletFormat.Short(-s.Left) + " over" : WalletFormat.Short(s.Left) + " left", s.Left < 0))
            .ToList();
        var outside = funds.Outside(transactions, month, zone);
        return new FundsWidgetModel(
            "Funds · " + month.ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture),
            states.Count == 0 ? "" : WalletFormat.Short(states.Sum(s => s.Left)) + " left",
            jars,
            states.Count - jars.Count,
            outside.Spent > 0 ? WalletFormat.Short(outside.Spent) : "");
    }
}
