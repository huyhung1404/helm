using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits for AI agents over MCP: read the habits with their streaks and rates, read one habit's year (the days done,
/// every run of missed days with the reason the user wrote, month by month), and tick a day. The reasons are there so an
/// agent can look for what gets in the way. Nothing is ever deleted or unticked through here. Days are the user's local
/// ones, written like 2026-10-01.
/// </summary>
public sealed class HabitsMcpTools(HabitsStore store) : IMcpToolProvider
{
    public string? ModuleId => HabitsIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("habits_list",
            "The user's daily habits: whether each is done today, the current and longest streak, the share of days done in the last 30 days " +
            "and this year, and how many days in a row it has been skipped.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List())) { ReadOnly = true },
        new("habit_report",
            "One habit's year: the days done, each month's share of days done, and every run of missed days with the reason the user gave for it. " +
            "Use it to find what gets in the way of a habit and suggest changes.",
            McpArgs.Schema(
                ("habit", McpArgs.Text("The habit's id or title."), true),
                ("year", McpArgs.Number("The year, e.g. 2026. Default: this year."), false)),
            (args, _) => Task.FromResult<object?>(Report(args))) { ReadOnly = true },
        new("habit_check",
            "Mark a habit as done for a day (today by default). Only days from the habit's start to today count.",
            McpArgs.Schema(
                ("habit", McpArgs.Text("The habit's id or title."), true),
                ("day", McpArgs.Text("The day, e.g. 2026-10-01. Default: today."), false)),
            (args, _) => Task.FromResult<object?>(Check(args))),
    ];

    private object List()
    {
        var today = store.Today;
        var done = store.AllDoneDays();
        return new JsonObject
        {
            ["today"] = Day(today),
            ["habits"] = new JsonArray(store.Habits().Select(h =>
            {
                var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
                var s = HabitStats.Compute(h.Value, days, today);
                return (JsonNode)new JsonObject
                {
                    ["id"] = h.Id,
                    ["title"] = h.Value.Title,
                    ["started"] = Day(h.Value.StartDay),
                    ["doneToday"] = s.DoneToday,
                    ["currentStreak"] = s.CurrentStreak,
                    ["longestStreak"] = s.LongestStreak,
                    ["rateLast30Days"] = Rate(s.Rate30),
                    ["rateThisYear"] = Rate(s.RateYear),
                    ["skippedDaysInARow"] = s.MissedRun,
                };
            }).ToArray()),
        };
    }

    private object Report(JsonElement args)
    {
        var habit = Find(McpArgs.RequiredString(args, "habit"));
        var today = store.Today;
        var year = McpArgs.Int(args, "year") ?? today.Year;
        if (year < habit.Value.StartDay.Year || year > today.Year)
            throw new McpToolException($"“{habit.Value.Title}” has days from {habit.Value.StartDay.Year} to {today.Year}; pick a year in that range.");
        var done = store.DoneDays(habit.Id);
        var (doneYear, trackedYear) = HabitStats.Count(habit.Value, done, today, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
        return new JsonObject
        {
            ["id"] = habit.Id,
            ["title"] = habit.Value.Title,
            ["started"] = Day(habit.Value.StartDay),
            ["year"] = year,
            ["today"] = Day(today),
            ["daysDone"] = doneYear,
            ["daysCounted"] = trackedYear,
            ["rate"] = Rate(trackedYear == 0 ? null : (double)doneYear / trackedYear),
            ["currentStreak"] = HabitStats.CurrentStreak(done, today),
            ["longestStreakThisYear"] = HabitStats.LongestStreak(done, year),
            ["months"] = new JsonArray(HabitStats.Months(habit.Value, done, today, year).Where(m => m.Tracked > 0).Select(m => (JsonNode)new JsonObject
            {
                ["month"] = m.Month,
                ["done"] = m.Done,
                ["counted"] = m.Tracked,
                ["rate"] = Rate(m.Rate),
            }).ToArray()),
            ["missed"] = new JsonArray(HabitLapses.Missed(habit.Id, habit.Value, done, store.Lapses(habit.Id), today, year).Select(x => (JsonNode)Without(new JsonObject
            {
                ["from"] = Day(x.Gap.From),
                ["to"] = Day(x.Gap.To),
                ["days"] = x.Gap.Days,
                ["reason"] = x.Reason?.Reason,
            })).ToArray()),
            ["daysDoneList"] = new JsonArray(done.Where(d => d.Year == year).OrderBy(d => d).Select(d => (JsonNode)Day(d)!).ToArray()),
        };
    }

    private object Check(JsonElement args)
    {
        var habit = Find(McpArgs.RequiredString(args, "habit"));
        var day = McpArgs.String(args, "day") is { } text ? ParseDay(text) : store.Today;
        var result = store.Check(habit.Id, day);
        if (result == HabitCheckResult.NotTracked)
            throw new McpToolException($"{Day(day)} does not count for “{habit.Value.Title}”: only days from {Day(habit.Value.StartDay)} to today do.");
        return new JsonObject
        {
            ["id"] = habit.Id,
            ["title"] = habit.Value.Title,
            ["day"] = Day(day),
            ["result"] = result == HabitCheckResult.AlreadyDone ? "already done" : "done",
            ["currentStreak"] = HabitStats.CurrentStreak(store.DoneDays(habit.Id), store.Today),
        };
    }

    private Helm.Core.Sync.SyncedItem<Habit> Find(string idOrTitle) =>
        store.Find(idOrTitle) ?? throw new McpToolException($"No single habit matches “{idOrTitle}”. Call habits_list for the ids.");

    private static DateOnly ParseDay(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day : throw new McpToolException($"{text} is not a day like 2026-10-01.");

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static double? Rate(double? rate) => rate is { } r ? Math.Round(r, 3) : null;

    private static JsonObject Without(JsonObject o)
    {
        foreach (var key in o.Where(p => p.Value is null).Select(p => p.Key).ToList()) o.Remove(key);
        return o;
    }
}
