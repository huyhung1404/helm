using Helm.Core.Today;

namespace Helm.Modules.Habits;

/// <summary>
/// Habits' part of Home's "today": the habits to build, ticked right on Home. Nothing when each of them is done. A
/// habit to quit is answered for yesterday, so it stays in Habits.
/// </summary>
public sealed class HabitsTodaySource(HabitsStore store) : ITodaySource
{
    public string ModuleId => HabitsIds.ModuleId;

    public event EventHandler? Changed
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }

    public TodayList? Build()
    {
        var today = store.Today;
        var habits = store.Habits().Where(h => h.Value.Kind == HabitKind.Build && h.Value.StartDay <= today).ToList();
        if (habits.Count == 0) return null;

        var allDone = store.AllDoneDays();
        var entries = new List<TodayEntry>();
        foreach (var habit in habits)
        {
            var done = allDone.TryGetValue(habit.Id, out var days) ? days : new HashSet<DateOnly>();
            var streak = HabitStats.CurrentStreak(done, today);
            var isDone = done.Contains(today);
            entries.Add(new TodayEntry(habit.Id, habit.Value.Title, streak > 0 ? $"{streak}-day streak" : null, CanTick: true, IsDone: isDone));
        }
        var doneCount = entries.Count(e => e.IsDone);
        if (doneCount == entries.Count) return null;
        // Not done first, so the list shrinks from the top as they are ticked.
        return new TodayList(ModuleId, $"{doneCount} of {entries.Count} done", [.. entries.OrderBy(e => e.IsDone)], "Open", 50);
    }

    public void Tick(string entryId, bool done)
    {
        if (done) store.Check(entryId, store.Today);
        else store.Uncheck(entryId, store.Today);
    }
}
