using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>One column of a column chart.</summary>
/// <param name="Label">Under the column ("1", "Oct"); empty for none (labels are sparing).</param>
/// <param name="Caption">The readout when the column is under the pointer or tapped ("Sat 3 Oct · 1,250,000 ₫").</param>
/// <param name="IsEmphasis">Drawn in the accent; the others are context, in grey.</param>
/// <param name="IsEmpty">A slot with no column (a day still to come).</param>
/// <param name="Command">Runs when the column is clicked or tapped (e.g. open that month).</param>
/// <param name="Value2">The second series' column beside it, in a two-series chart; null in a one-series chart.</param>
public sealed record ChartBar(string Label, double Value, string Caption, bool IsEmphasis = true, bool IsEmpty = false, IRelayCommand? Command = null, double? Value2 = null);

/// <summary>
/// A column chart of one series (or two side by side): the columns, one reference line, and the column whose readout
/// shows first. The series' colours are slots of <see cref="WalletPalette"/>; a two-series chart names them in a key.
/// </summary>
/// <param name="Reference">A level drawn across the plot (the budget per day); null for none.</param>
public sealed record BarChartModel(IReadOnlyList<ChartBar> Bars, double? Reference, string ReferenceLabel, int DefaultIndex)
{
    public static BarChartModel Empty { get; } = new([], null, "", -1);

    /// <summary>The top of the scale: a round number at or above every column and the reference.</summary>
    public double Max => WalletCharts.NiceMax(Math.Max(Bars.Count == 0 ? 0 : Bars.Max(b => Math.Max(b.Value, b.Value2 ?? 0)), Reference ?? 0));

    /// <summary>The first series' colour slot.</summary>
    public int Color { get; init; } = 1;

    /// <summary>The second series' colour slot.</summary>
    public int Color2 { get; init; }

    public string Name { get; init; } = "";

    public string Name2 { get; init; } = "";

    /// <summary>Two columns per slot.</summary>
    public bool IsGrouped => Bars.Any(b => b.Value2 is not null);

    /// <summary>The scale's top as a short amount ("1.5M ₫"), written on its gridline.</summary>
    public string MaxLabel => WalletFormat.Short((decimal)Max);

    public string HalfLabel => WalletFormat.Short((decimal)(Max / 2));

    public bool IsEmpty => Bars.Count == 0 || Bars.All(b => b.Value <= 0 && (b.Value2 ?? 0) <= 0);

    /// <summary>Whether there is a reference line (its key goes under the chart).</summary>
    public bool HasReference => Reference is > 0;
}

/// <summary>A category's slice of the donut.</summary>
/// <param name="Color">The category's colour slot (<see cref="WalletPalette"/>); -1 for "Other".</param>
public sealed record DonutSegment(string Name, string Icon, int Color, decimal Amount, double Share, string AmountText, string ShareText, int Count);

/// <summary>The month's spending by category, as a donut: at most six categories, the rest folded into "Other".</summary>
public sealed record DonutModel(IReadOnlyList<DonutSegment> Segments, string CenterValue, string CenterLabel)
{
    public const int MaxSegments = 6;

    public static DonutModel Empty { get; } = new([], "", "");

    public bool IsEmpty => Segments.Count == 0;
}

/// <summary>One bar of the cash-flow chart.</summary>
/// <param name="Fraction">The bar's length as a share of the longest (0–1).</param>
public sealed record CashFlowRow(string Name, string Icon, int Color, string AmountText, double Fraction);

/// <summary>
/// The cash-flow colours (slots of <see cref="WalletPalette"/>): aqua, orange and blue, a trio checked to stay apart for
/// every kind of colour blindness when all three are on screen (green and orange are not).
/// </summary>
public static class CashFlowColors
{
    public const int MoneyIn = 3;
    public const int Spent = 2;
    public const int Net = 1;
}

/// <summary>A column's place in the plot, in the control's own coordinates.</summary>
/// <param name="Series">0 for the first series, 1 for the second.</param>
public readonly record struct BarRect(int Index, double X, double Y, double Width, double Height, int Series = 0);

/// <summary>Where everything of a column chart goes, for a given size: the same on both platforms.</summary>
public sealed record ChartGeometry(
    IReadOnlyList<BarRect> Bars,
    double PlotLeft,
    double PlotRight,
    double PlotTop,
    double BaselineY,
    double HalfY,
    double? ReferenceY,
    double SlotWidth,
    IReadOnlyList<(double X, string Text)> Labels)
{
    /// <summary>The column under a point (its whole slot counts, wider than the column), or -1.</summary>
    public int IndexAt(double x)
    {
        if (SlotWidth <= 0 || x < PlotLeft || x >= PlotRight) return -1;
        return (int)((x - PlotLeft) / SlotWidth);
    }
}

/// <summary>The data and the layout of the Wallet charts. Pure functions, tested in the core.</summary>
public static class WalletCharts
{
    /// <summary>Columns are at most this wide, and touching columns keep this gap.</summary>
    public const double MaxBarWidth = 24;

    public const double BarGap = 2;

    /// <summary>The smallest round number at or above <paramref name="value"/>: 1, 2, 2.5 or 5 times a power of ten.</summary>
    public static double NiceMax(double value)
    {
        if (value <= 0 || double.IsNaN(value)) return 1;
        var power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * power >= value - 1e-9) return step * power;
        return 10 * power;
    }

    /// <summary>Lays out a column chart in a box: a caption band on top, labels under the baseline.</summary>
    public static ChartGeometry Layout(BarChartModel model, double width, double height, double top, double bottom, double left = 0, double right = 0)
    {
        var plotLeft = left;
        var plotRight = Math.Max(plotLeft, width - right);
        var baseline = Math.Max(top, height - bottom);
        var plotHeight = Math.Max(0, baseline - top);
        var n = model.Bars.Count;
        var slot = n == 0 ? 0 : (plotRight - plotLeft) / n;
        var grouped = model.IsGrouped;
        // Two series: two columns side by side in the slot, with the same gap between them.
        var barWidth = grouped ? Math.Clamp((slot - BarGap * 2) / 2, 1, MaxBarWidth) : Math.Clamp(slot - BarGap, 1, MaxBarWidth);
        var max = model.Max;
        var bars = new List<BarRect>(n);
        var labels = new List<(double, string)>();

        // A tiny amount still shows as a sliver.
        BarRect Column(int index, double x, double value, int series)
        {
            var h = Math.Max(2, plotHeight * Math.Min(1, value / max));
            return new BarRect(index, x, baseline - h, barWidth, h, series);
        }

        for (var i = 0; i < n; i++)
        {
            var b = model.Bars[i];
            var center = plotLeft + slot * (i + 0.5);
            if (b.Label.Length > 0) labels.Add((center, b.Label));
            if (b.IsEmpty) continue;
            if (grouped)
            {
                if (b.Value > 0) bars.Add(Column(i, center - BarGap / 2 - barWidth, b.Value, 0));
                if (b.Value2 is > 0 and var v2) bars.Add(Column(i, center + BarGap / 2, v2, 1));
            }
            else if (b.Value > 0)
            {
                bars.Add(Column(i, center - barWidth / 2, b.Value, 0));
            }
        }
        double? reference = model.Reference is { } r && r > 0 ? baseline - plotHeight * Math.Min(1, r / max) : null;
        return new ChartGeometry(bars, plotLeft, plotRight, top, baseline, baseline - plotHeight / 2, reference, slot, labels);
    }

    /// <summary>
    /// What was spent each day of a month (transfers between the user's accounts left out). In the current month the
    /// days still to come are empty slots and today's readout shows first; with a budget, its share per day is the
    /// reference line.
    /// </summary>
    public static BarChartModel Daily(IEnumerable<SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories,
        DateOnly month, DateTimeOffset now, TimeZoneInfo zone, decimal budget)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        var days = DateTime.DaysInMonth(month.Year, month.Month);
        var transfers = categories.Where(c => c.Kind == CategoryKind.Transfer).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var spent = new decimal[days + 1];
        var counts = new int[days + 1];
        foreach (var t in transactions.Select(t => t.Value))
        {
            if (t.Amount >= 0 || t.CategoryId is { } c && transfers.Contains(c)) continue;
            var day = WalletFormat.LocalDay(t.OccurredAt, zone);
            if (day.Year != month.Year || day.Month != month.Month) continue;
            spent[day.Day] -= t.Amount;
            counts[day.Day]++;
        }
        var today = WalletFormat.LocalDay(now, zone);
        var isCurrent = today.Year == month.Year && today.Month == month.Month;
        var bars = new List<ChartBar>(days);
        for (var d = 1; d <= days; d++)
        {
            var date = new DateOnly(month.Year, month.Month, d);
            var future = isCurrent && d > today.Day;
            var label = d == 1 || d % 5 == 0 && d <= days - 2 || d == days ? d.ToString(CultureInfo.CurrentCulture) : "";
            var what = spent[d] > 0 ? $"{WalletFormat.Money(spent[d])} · {WalletFormat.Count(counts[d], "payment")}" : future ? "still to come" : "nothing spent";
            bars.Add(new ChartBar(label, (double)spent[d], $"{WalletFormat.Day(date, today)} · {what}", true, future));
        }
        var lastSpent = Array.FindLastIndex(spent, s => s > 0);
        var defaultIndex = isCurrent ? today.Day - 1 : lastSpent > 0 ? lastSpent - 1 : days - 1;
        double? reference = budget > 0 ? (double)(budget / days) : null;
        return new BarChartModel(bars, reference, budget > 0 ? $"Budget a day {WalletFormat.Short(budget / days)}" : "", defaultIndex);
    }

    /// <summary>
    /// What was spent in the months up to <paramref name="month"/> (the one shown is in the accent, the others grey);
    /// a click on a month opens it.
    /// </summary>
    public static BarChartModel Months(IReadOnlyList<SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories,
        DateOnly month, DateTimeOffset now, TimeZoneInfo zone, int count, Action<DateOnly>? open)
    {
        month = new DateOnly(month.Year, month.Month, 1);
        var bars = new List<ChartBar>(count);
        for (var i = count - 1; i >= 0; i--)
        {
            var m = month.AddMonths(-i);
            var s = WalletStats.Month(transactions, categories, m, now, zone);
            var label = m.ToString("MMM", CultureInfo.CurrentCulture);
            var command = open is null || i == 0 ? null : new RelayCommand(() => open(m));
            bars.Add(new ChartBar(label, (double)s.Income, $"{WalletFormat.Month(m)} · in {WalletFormat.Short(s.Income)}, spent {WalletFormat.Short(s.Spent)}, net {WalletFormat.Short(s.Net)}",
                i == 0, false, command, (double)s.Spent));
        }
        return new BarChartModel(bars, null, "", count - 1)
        {
            Color = CashFlowColors.MoneyIn,
            Color2 = CashFlowColors.Spent,
            Name = "Money in",
            Name2 = "Spent",
        };
    }

    /// <summary>
    /// The month's cash flow as three bars on one scale: money in, spent, and the difference (net); each with its
    /// amount at the end, so the colour is never the only cue.
    /// </summary>
    public static IReadOnlyList<CashFlowRow> CashFlow(MonthSummary summary)
    {
        var net = summary.Net;
        var max = Math.Max(Math.Max(summary.Income, summary.Spent), Math.Abs(net));
        double Fraction(decimal v) => max == 0 ? 0 : (double)(Math.Abs(v) / max);
        return
        [
            new("Money in", "MoneyHand", CashFlowColors.MoneyIn, WalletFormat.Money(summary.Income), Fraction(summary.Income)),
            new("Spent", "Payment", CashFlowColors.Spent, WalletFormat.Money(summary.Spent), Fraction(summary.Spent)),
            new(net < 0 ? "Net (spent more)" : "Net (saved)", "Savings", CashFlowColors.Net, WalletFormat.Signed(net), Fraction(net)),
        ];
    }

    /// <summary>The month's spending by category, largest first, at most six slices plus "Other".</summary>
    public static DonutModel Spending(MonthSummary summary, IReadOnlyList<CategoryInfo> categories)
    {
        if (summary.Spent <= 0 || summary.Spending.Count == 0) return DonutModel.Empty;
        var byId = categories.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var rows = summary.Spending.ToList();
        var shown = rows.Count <= DonutModel.MaxSegments ? rows : rows.Take(DonutModel.MaxSegments - 1).ToList();
        var segments = shown.Select(s =>
        {
            var c = s.CategoryId is { } id && byId.TryGetValue(id, out var info) ? info : null;
            return Segment(s.Name, c?.Icon ?? WalletIcons.Uncategorized, c?.Color ?? 0, s.Amount, s.Share, s.Count);
        }).ToList();
        if (shown.Count < rows.Count)
        {
            var rest = rows.Skip(shown.Count).ToList();
            var amount = rest.Sum(r => r.Amount);
            segments.Add(Segment($"Other ({rest.Count})", "MoreHorizontal", -1, amount, (double)(amount / summary.Spent), rest.Sum(r => r.Count)));
        }
        return new DonutModel(segments, WalletFormat.Short(summary.Spent), "spent");
    }

    private static DonutSegment Segment(string name, string icon, int color, decimal amount, double share, int count) =>
        new(name, icon, color, amount, share, WalletFormat.Money(amount),
            share > 0 && share < 0.005 ? "<1%" : $"{Math.Round(share * 100):0}%", count);
}
