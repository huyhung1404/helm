namespace Helm.Ai.Auto.Jobs;

/// <summary>P3, every morning: today's tasks (overdue first), the habits still to do, and yesterday's spending.</summary>
public sealed class MorningBriefJob : IAutoJob
{
    public string Feature => AiFeatureIds.MorningBrief;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("day", AutoEvery.Day)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var today = context.Today;
        var tasks = AutoJson.Items(await context.ReadAsync("tracker_tasks", new { due_before = AutoText.Iso(today.AddDays(1)), limit = 30 }, ct));
        var habits = DayData.HabitsToDo(await context.ReadAsync("habits_list", null, ct));
        var yesterday = AutoText.Iso(today.AddDays(-1));
        var spent = await context.ReadAsync("wallet_summary", new { from = yesterday, to = yesterday }, ct);
        if (tasks.Count == 0 && habits.Count == 0 && AutoJson.Number(spent, "spent") == 0) return null;

        var text = await context.AskAsync($"""
            Morning brief for {AutoText.Iso(today)} ({today.DayOfWeek}). At most 6 short lines: what is due today (overdue first,
            the urgent ones by name), the habits still to do today, and what Anh spent yesterday (the total and the largest
            categories). Leave out a part that has no data.
            Tasks due today or overdue: {AutoJson.Pick(tasks, "title", "due", "priority", "list")}
            Habits not done yet today: {AutoJson.Pick(habits, "title", "currentStreak")}
            Yesterday's spending: {DayData.Spending(spent)}
            """, ct).ConfigureAwait(false);
        return new AutoBrief($"Bản tin sáng · {AutoText.Day(today)}", text);
    }
}
