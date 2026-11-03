namespace Helm.Ai.Auto.Jobs;

/// <summary>P4, every evening: the tasks done today, those slipping to tomorrow, the habits left, one suggestion for tomorrow.</summary>
public sealed class EveningReviewJob : IAutoJob
{
    public string Feature => AiFeatureIds.EveningReview;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("day", AutoEvery.Day)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var today = context.Today;
        var done = AutoJson.Items(await context.ReadAsync("tracker_tasks", new { status = "done", limit = 200 }, ct))
            .Where(t => AutoText.LocalDay(AutoJson.Text(t, "done")) == today).ToList();
        var slipping = AutoJson.Items(await context.ReadAsync("tracker_tasks", new { due_before = AutoText.Iso(today.AddDays(1)), limit = 30 }, ct));
        var tomorrow = AutoJson.Items(await context.ReadAsync("tracker_tasks", new { due_before = AutoText.Iso(today.AddDays(2)), limit = 40 }, ct))
            .Where(t => slipping.All(s => AutoJson.Text(s, "id") != AutoJson.Text(t, "id"))).ToList();
        var habits = DayData.HabitsToDo(await context.ReadAsync("habits_list", null, ct));
        if (done.Count == 0 && slipping.Count == 0 && tomorrow.Count == 0 && habits.Count == 0) return null;

        var text = await context.AskAsync($"""
            Evening review for {AutoText.Iso(today)}. At most 6 short lines: what Anh finished today (a word of praise, briefly),
            what is still open and slips to tomorrow, the habits left today, and then ONE concrete suggestion for tomorrow
            (what to start with, from the data). Leave out a part that has no data.
            Done today: {AutoJson.Pick(done, "title", "list")}
            Due today or overdue, still open: {AutoJson.Pick(slipping, "title", "due", "priority")}
            Due tomorrow: {AutoJson.Pick(tomorrow, "title", "due", "priority")}
            Habits not done yet today: {AutoJson.Pick(habits, "title", "currentStreak")}
            """, ct).ConfigureAwait(false);
        return new AutoBrief($"Tổng kết tối · {AutoText.Day(today)}", text);
    }
}
