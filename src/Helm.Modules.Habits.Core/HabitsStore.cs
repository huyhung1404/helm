using Helm.Core.Sync;

namespace Helm.Modules.Habits;

/// <summary>What <see cref="HabitsStore.Check"/> did.</summary>
public enum HabitCheckResult
{
    Checked,
    AlreadyDone,

    /// <summary>The day is before the habit started or in the future: it is not tracked.</summary>
    NotTracked,
    NotFound,
}

/// <summary>
/// Habits, the days they were done and the reasons for the days they were not, on top of Helm Sync. Every write is
/// local and immediate (the sync engine uploads it in the background). Callable from any thread; <see cref="Changed"/>
/// may be raised on a background thread after a sync. "Today" is the local day in the store's time zone.
/// </summary>
public sealed class HabitsStore
{
    public const string HabitsCollection = "habits.habits";
    public const string ChecksCollection = "habits.checks";
    public const string LapsesCollection = "habits.lapses";

    private readonly ISyncedCollection<Habit> _habits;
    private readonly ISyncedCollection<HabitCheck> _checks;
    private readonly ISyncedCollection<HabitLapse> _lapses;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public HabitsStore(ISyncedCollection<Habit> habits, ISyncedCollection<HabitCheck> checks, ISyncedCollection<HabitLapse> lapses,
        TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _habits = habits;
        _checks = checks;
        _lapses = lapses;
        _time = time ?? TimeProvider.System;
        Zone = zone ?? TimeZoneInfo.Local;
        _habits.Changed += OnChanged;
        _checks.Changed += OnChanged;
        _lapses.Changed += OnChanged;
    }

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    public TimeZoneInfo Zone { get; }

    public DateOnly Today => HabitDays.Day(Now, Zone);

    public static string CheckId(string habitId, DateOnly day) => $"{habitId}@{day:yyyy-MM-dd}";

    private void OnChanged(object? sender, SyncedChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    // ---- Reading -------------------------------------------------------------------------------------------------

    /// <summary>Habits in list order.</summary>
    public IReadOnlyList<SyncedItem<Habit>> Habits() =>
        _habits.All().OrderBy(h => h.Value.Order).ThenBy(h => h.Value.CreatedAt).ThenBy(h => h.Id, StringComparer.Ordinal).ToList();

    public Habit? Get(string id) => _habits.Get(id);

    /// <summary>Finds a habit by id or title (case does not matter); null when none or several match.</summary>
    public SyncedItem<Habit>? Find(string idOrTitle)
    {
        var all = Habits();
        if (all.FirstOrDefault(h => h.Id == idOrTitle) is { } byId) return byId;
        var matches = all.Where(h => string.Equals(h.Value.Title.Trim(), idOrTitle.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The days one habit was done.</summary>
    public IReadOnlySet<DateOnly> DoneDays(string habitId) =>
        _checks.All().Where(c => c.Value.HabitId == habitId).Select(c => c.Value.Day).ToHashSet();

    /// <summary>The days each habit was done, read in one pass.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<DateOnly>> AllDoneDays()
    {
        var map = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);
        foreach (var c in _checks.All())
        {
            if (!map.TryGetValue(c.Value.HabitId, out var days)) map[c.Value.HabitId] = days = [];
            days.Add(c.Value.Day);
        }
        return map.ToDictionary(p => p.Key, p => (IReadOnlySet<DateOnly>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>The reasons written for missed days (one habit, or all), oldest first.</summary>
    public IReadOnlyList<HabitLapse> Lapses(string? habitId = null) =>
        _lapses.All().Select(l => l.Value).Where(l => habitId is null || l.HabitId == habitId).OrderBy(l => l.From).ThenBy(l => l.WrittenAt).ToList();

    // ---- Habits --------------------------------------------------------------------------------------------------

    /// <summary>Creates a habit that starts today; returns its id, or null for an empty title or too many habits.</summary>
    public string? Add(string title)
    {
        var clipped = HabitLimits.Clip(title, HabitLimits.Title);
        if (clipped.Length == 0) return null;
        lock (_gate)
        {
            var all = _habits.All();
            if (all.Count >= HabitLimits.Habits) return null;
            return _habits.Add(new Habit
            {
                Title = clipped,
                CreatedAt = Now,
                StartDay = Today,
                Order = all.Count == 0 ? 0 : all.Max(h => h.Value.Order) + 1,
            });
        }
    }

    public bool Rename(string id, string title)
    {
        var clipped = HabitLimits.Clip(title, HabitLimits.Title);
        if (clipped.Length == 0) return false;
        lock (_gate)
        {
            if (_habits.Get(id) is not { } habit) return false;
            if (habit.Title == clipped) return true;
            _habits.Upsert(id, habit with { Title = clipped });
            return true;
        }
    }

    /// <summary>Moves a habit one place up (-1) or down (+1) in the list; false at either end.</summary>
    public bool Move(string id, int direction)
    {
        lock (_gate)
        {
            var all = Habits();
            var index = all.ToList().FindIndex(h => h.Id == id);
            var other = index + Math.Sign(direction);
            if (index < 0 || direction == 0 || other < 0 || other >= all.Count) return false;
            // Number the list again so equal orders (from an old sync) never stick.
            var ids = all.Select(h => h.Id).ToList();
            (ids[index], ids[other]) = (ids[other], ids[index]);
            for (var i = 0; i < ids.Count; i++)
            {
                var habit = all.First(h => h.Id == ids[i]).Value;
                if (habit.Order != i) _habits.Upsert(ids[i], habit with { Order = i });
            }
            return true;
        }
    }

    /// <summary>Deletes a habit with its done days and reasons, on every device.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!_habits.Delete(id)) return false;
            foreach (var c in _checks.All().Where(c => c.Value.HabitId == id)) _checks.Delete(c.Id);
            foreach (var l in _lapses.All().Where(l => l.Value.HabitId == id)) _lapses.Delete(l.Id);
            return true;
        }
    }

    // ---- Days ----------------------------------------------------------------------------------------------------

    /// <summary>Marks a day done. Days before the habit started and days after today are not tracked.</summary>
    public HabitCheckResult Check(string habitId, DateOnly day)
    {
        lock (_gate)
        {
            if (_habits.Get(habitId) is not { } habit) return HabitCheckResult.NotFound;
            if (day < habit.StartDay || day > Today) return HabitCheckResult.NotTracked;
            var id = CheckId(habitId, day);
            if (_checks.Get(id) is not null) return HabitCheckResult.AlreadyDone;
            _checks.Upsert(id, new HabitCheck { HabitId = habitId, Day = day, CheckedAt = Now });
            return HabitCheckResult.Checked;
        }
    }

    /// <summary>Marks a day not done again; false when it was not done.</summary>
    public bool Uncheck(string habitId, DateOnly day)
    {
        lock (_gate) return _checks.Delete(CheckId(habitId, day));
    }

    public bool IsDone(string habitId, DateOnly day) => _checks.Get(CheckId(habitId, day)) is not null;

    /// <summary>Saves why the habit was skipped from <paramref name="from"/> to <paramref name="to"/>; false for an empty reason.</summary>
    public bool WriteReason(string habitId, DateOnly from, DateOnly to, string reason)
    {
        var clipped = HabitLimits.Clip(reason, HabitLimits.Reason);
        if (clipped.Length == 0 || to < from) return false;
        lock (_gate)
        {
            if (_habits.Get(habitId) is null) return false;
            _lapses.Upsert(CheckId(habitId, from), new HabitLapse { HabitId = habitId, From = from, To = to, Reason = clipped, WrittenAt = Now });
            return true;
        }
    }
}
