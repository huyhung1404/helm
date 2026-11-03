using System.Globalization;

namespace Helm.Ai.Auto.Jobs;

/// <summary>
/// W3: on Sundays the week's spending against the week before, with the funds running low; on the 1st the month
/// before against the one before it.
/// </summary>
public sealed class SpendingDigestJob : IAutoJob
{
    public string Feature => AiFeatureIds.SpendingDigest;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("week", AutoEvery.Week, DayOfWeek.Sunday), new("month", AutoEvery.Month, MonthDay: 1)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var today = context.Today;
        DateOnly from, to, beforeFrom, beforeTo;
        string what, title;
        if (context.Slot == "month")
        {
            from = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
            to = from.AddMonths(1).AddDays(-1);
            beforeFrom = from.AddMonths(-1);
            beforeTo = from.AddDays(-1);
            what = $"the month {from.ToString("MM/yyyy", CultureInfo.InvariantCulture)}";
            title = $"Chi tiêu tháng {from.ToString("MM/yyyy", CultureInfo.InvariantCulture)}";
        }
        else
        {
            // Monday to this Sunday.
            from = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            to = today;
            beforeFrom = from.AddDays(-7);
            beforeTo = from.AddDays(-1);
            what = $"the week {AutoText.Iso(from)} to {AutoText.Iso(to)}";
            title = $"Chi tiêu tuần · {from.ToString("dd/MM", CultureInfo.InvariantCulture)}–{to.ToString("dd/MM", CultureInfo.InvariantCulture)}";
        }
        var now = await context.ReadAsync("wallet_summary", new { from = AutoText.Iso(from), to = AutoText.Iso(to) }, ct);
        if (now is null || AutoJson.Number(now, "spent") == 0) return null;
        var before = await context.ReadAsync("wallet_summary", new { from = AutoText.Iso(beforeFrom), to = AutoText.Iso(beforeTo) }, ct);
        // The funds are this month's: worth a word on Sundays, not for a month already over.
        var funds = context.Slot == "week"
            ? AutoJson.Items(await context.ReadAsync("wallet_funds", null, ct)).Where(f => AutoJson.Text(f, "status") != "on track").ToList()
            : [];

        var text = await context.AskAsync($"""
            Spending digest for {what}. At most 6 short lines: the total, the 3 categories Anh spent most on, how it compares
            with the period before (more or less, by how much, and which category moved most), and the funds running low or
            over, if any.
            This period: {DayData.Spending(now)}
            The period before: {DayData.Spending(before)}
            Funds not on track: {AutoJson.Pick(funds, "fund", "held", "spent", "left", "status", "runsOutOn")}
            """, ct).ConfigureAwait(false);
        return new AutoBrief(title, text);
    }
}
