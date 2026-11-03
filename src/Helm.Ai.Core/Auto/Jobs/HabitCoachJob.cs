using System.Text.Json;
using System.Text.Json.Nodes;

namespace Helm.Ai.Auto.Jobs;

/// <summary>
/// P5: coaching from the reasons the user gave for the days he skipped a habit. Every Sunday for the habits that slip
/// most, and on the evening a habit is skipped a second day in a row, for that one.
/// </summary>
public sealed class HabitCoachJob : IAutoJob
{
    private const int MostHabits = 3;
    private const int MostMisses = 8;

    public string Feature => AiFeatureIds.HabitCoach;

    public IReadOnlyList<AutoSlot> Slots { get; } = [new("week", AutoEvery.Week, DayOfWeek.Sunday), new("misses", AutoEvery.Day)];

    public async Task<AutoBrief?> RunAsync(AutoJobContext context, CancellationToken ct)
    {
        var habits = AutoJson.Items(await context.ReadAsync("habits_list", null, ct), "habits");
        IReadOnlyList<JsonElement> chosen;
        if (context.Slot == "week")
        {
            chosen = [.. habits.Where(h => AutoJson.Number(h, "rateLast30Days") < 1)
                .OrderBy(h => AutoJson.Number(h, "rateLast30Days")).Take(MostHabits)];
        }
        else
        {
            // Sunday's coaching covers it.
            if (context.Today.DayOfWeek == DayOfWeek.Sunday) return null;
            chosen = [.. habits.Where(h => AutoJson.Int(h, "skippedDaysInARow") == 2).Take(MostHabits)];
        }
        if (chosen.Count == 0) return null;

        var reports = new JsonArray();
        foreach (var habit in chosen)
        {
            if (AutoJson.Text(habit, "id") is not { } id) continue;
            var report = await context.ReadAsync("habit_report", new { habit = id }, ct);
            var misses = AutoJson.Items(report, "missed").TakeLast(MostMisses).ToList();
            reports.Add(new JsonObject
            {
                ["title"] = AutoJson.Text(habit, "title"),
                ["kind"] = AutoJson.Text(habit, "kind"),
                ["rateLast30Days"] = AutoJson.Number(habit, "rateLast30Days"),
                ["skippedDaysInARow"] = AutoJson.Int(habit, "skippedDaysInARow"),
                ["missed"] = JsonNode.Parse(AutoJson.Pick(misses, "from", "to", "days", "reason")),
            });
        }
        var text = await context.AskAsync($"""
            Coach Anh on these habits (kind "quit": a habit he is giving up, and a missed day is a slip). For each, read the
            reasons he gave for the missed days, name what gets in the way in a few words, and give one or two small, concrete
            things to try this week. At most 8 short lines; kind, never preachy.
            {reports.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}
            """, ct).ConfigureAwait(false);
        var title = context.Slot == "week" ? "Huấn luyện thói quen tuần này" : $"Thói quen: {AutoJson.Text(chosen[0], "title")} lỡ 2 ngày";
        return new AutoBrief(title, text);
    }
}
