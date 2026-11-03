using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Helm.Core.Mcp;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits for AI agents over MCP: read the habits with their streaks and rates, read one habit's year (the days done,
/// every run of missed days with the reason the user wrote, month by month), and tick a day. The reasons are there so an
/// agent can look for what gets in the way. Nothing is ever deleted or unticked through here. Days are the user's local
/// ones, written like 2026-10-01. A habit to quit is ticked the day after, for each day kept clean of it.
/// </summary>
public sealed class HabitsMcpTools(HabitsStore store) : IMcpToolProvider
{
    public string? ModuleId => HabitsIds.ModuleId;

    public IEnumerable<McpTool> Tools =>
    [
        new("habits_list",
            "The user's daily habits: whether each is done today, the current and longest streak, the share of days done in the last 30 days " +
            "and this year, and how many days in a row it has been skipped. A day can be done in part (a percent): it counts in full for the " +
            "streaks and by its share in the rates. A habit with kind \"quit\" is one the user is giving up: each day " +
            "kept clean of it is ticked the day after, so it has cleanYesterday instead of doneToday, its streaks count clean days, and its " +
            "skipped days are slips or days not marked.",
            McpTool.NoArguments(), (_, _) => Task.FromResult<object?>(List())) { ReadOnly = true },
        new("habit_report",
            "One habit's year: the days done (with the percent of the days done only in part), each month's share of days done, and every run " +
            "of missed days with the reason the user gave for it. " +
            "For a habit to quit, the days done are clean days and the reasons tell what led to a slip. " +
            "Use it to find what gets in the way of a habit (or what triggers a slip) and suggest changes.",
            McpArgs.Schema(
                ("habit", McpArgs.Text("The habit's id or title."), true),
                ("year", McpArgs.Number("The year, e.g. 2026. Default: this year."), false)),
            (args, _) => Task.FromResult<object?>(Report(args))) { ReadOnly = true },
        new("habit_check",
            "Mark a habit as done for a day (today by default), in full or in part. Only days from the habit's start to today count; a day " +
            "done already takes the new percent. For a habit to quit this marks a day clean: only days that are over count, the default is " +
            "yesterday, and there is no percent.",
            McpArgs.Schema(
                ("habit", McpArgs.Text("The habit's id or title."), true),
                ("day", McpArgs.Text("The day, e.g. 2026-10-01. Default: today (yesterday for a habit to quit)."), false),
                ("percent", McpArgs.Number($"How much of it was done, {HabitLimits.MinPercent}–100. Default: 100."), false)),
            (args, _) => Task.FromResult<object?>(Check(args)))
        {
            // Only a day this call ticked: a day that was done before stays done.
            Undo = (result, _) =>
            {
                if (result.TryGetProperty("checked_now", out var now) && now.ValueKind == JsonValueKind.True)
                    store.Uncheck(result.GetProperty("id").GetString()!, DateOnly.ParseExact(result.GetProperty("day").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            },
        },
    ];

    private object List()
    {
        var today = store.Today;
        var done = store.AllDoneDays();
        var percents = store.AllPercents();
        var lapses = store.Lapses();
        return new JsonObject
        {
            ["today"] = Day(today),
            ["habits"] = new JsonArray(store.Habits().Select(h =>
            {
                var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
                var shares = percents.GetValueOrDefault(h.Id);
                var s = HabitStats.Compute(h.Value, days, today, lapses.Where(l => l.HabitId == h.Id), shares);
                var quit = h.Value.Kind == HabitKind.Quit;
                return (JsonNode)Without(new JsonObject
                {
                    ["id"] = h.Id,
                    ["title"] = h.Value.Title,
                    ["kind"] = Kind(h.Value),
                    ["started"] = Day(h.Value.StartDay),
                    [quit ? "cleanYesterday" : "doneToday"] = s.DoneToday,
                    ["percentToday"] = !quit && s.DoneToday ? (int)Math.Round(HabitStats.Share(shares, today) * 100) : null,
                    ["currentStreak"] = s.CurrentStreak,
                    ["longestStreak"] = s.LongestStreak,
                    ["rateLast30Days"] = Rate(s.Rate30),
                    ["rateThisYear"] = Rate(s.RateYear),
                    ["skippedDaysInARow"] = s.MissedRun,
                });
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
        var percents = store.Percents(habit.Id);
        var lapses = store.Lapses(habit.Id);
        var (doneYear, trackedYear, scoreYear) = HabitStats.Count(habit.Value, done, today, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), percents);
        return new JsonObject
        {
            ["id"] = habit.Id,
            ["title"] = habit.Value.Title,
            ["kind"] = Kind(habit.Value),
            ["started"] = Day(habit.Value.StartDay),
            ["year"] = year,
            ["today"] = Day(today),
            ["daysDone"] = doneYear,
            ["daysCounted"] = trackedYear,
            ["rate"] = Rate(trackedYear == 0 ? null : scoreYear / trackedYear),
            ["currentStreak"] = HabitStats.Compute(habit.Value, done, today, lapses, percents).CurrentStreak,
            ["longestStreakThisYear"] = HabitStats.LongestStreak(done, year),
            ["months"] = new JsonArray(HabitStats.Months(habit.Value, done, today, year, percents).Where(m => m.Tracked > 0).Select(m => (JsonNode)new JsonObject
            {
                ["month"] = m.Month,
                ["done"] = m.Done,
                ["counted"] = m.Tracked,
                ["rate"] = Rate(m.Rate),
            }).ToArray()),
            ["missed"] = new JsonArray(HabitLapses.Missed(habit.Id, habit.Value, done, lapses, today, year).Select(x => (JsonNode)Without(new JsonObject
            {
                ["from"] = Day(x.Gap.From),
                ["to"] = Day(x.Gap.To),
                ["days"] = x.Gap.Days,
                ["reason"] = x.Reason?.Reason,
            })).ToArray()),
            ["daysDoneList"] = new JsonArray(done.Where(d => d.Year == year).OrderBy(d => d).Select(d => (JsonNode)Day(d)!).ToArray()),
            ["partialDays"] = new JsonArray(percents.Where(p => p.Key.Year == year && p.Value < 100).OrderBy(p => p.Key)
                .Select(p => (JsonNode)new JsonObject { ["day"] = Day(p.Key), ["percent"] = p.Value }).ToArray()),
        };
    }

    private object Check(JsonElement args)
    {
        var habit = Find(McpArgs.RequiredString(args, "habit"));
        var quit = habit.Value.Kind == HabitKind.Quit;
        var actionDay = HabitDays.ActionDay(habit.Value, store.Today);
        var day = McpArgs.String(args, "day") is { } text ? ParseDay(text) : actionDay;
        var percent = McpArgs.Int(args, "percent") ?? 100;
        if (percent is < HabitLimits.MinPercent or > 100)
            throw new McpToolException($"The percent must be from {HabitLimits.MinPercent} to 100; leave it out for a day done in full.");
        var result = store.Check(habit.Id, day, percent);
        if (result == HabitCheckResult.NotTracked)
        {
            throw new McpToolException(quit
                ? $"{Day(day)} does not count for “{habit.Value.Title}”: it is a habit to quit, so only days from {Day(habit.Value.StartDay)} to yesterday (days that are over) can be marked clean."
                : $"{Day(day)} does not count for “{habit.Value.Title}”: only days from {Day(habit.Value.StartDay)} to today do.");
        }
        var word = quit ? "clean" : "done";
        return new JsonObject
        {
            ["id"] = habit.Id,
            ["title"] = habit.Value.Title,
            ["kind"] = Kind(habit.Value),
            ["day"] = Day(day),
            ["checked_now"] = result == HabitCheckResult.Checked,
            ["result"] = result switch
            {
                HabitCheckResult.AlreadyDone => $"already {word}",
                HabitCheckResult.Updated => "percent changed",
                _ => word,
            },
            ["percent"] = store.PercentOn(habit.Id, day),
            ["currentStreak"] = HabitStats.CurrentStreak(store.DoneDays(habit.Id), actionDay),
        };
    }

    private static string Kind(Habit habit) => habit.Kind == HabitKind.Quit ? "quit" : "build";

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
