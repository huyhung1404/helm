using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Habits;

/// <summary>
/// The Habits pages, shared by the Windows and Android apps: the Habits tab (habits to do today, then the ones done),
/// the Report tab, the celebration, the reason asked for skipped days, and the settings both apps have. All members are
/// used on the UI thread; store changes are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class HabitsViewModel : ObservableObject
{
    private static readonly int[] Milestones = [7, 14, 21, 30, 50, 66, 100, 150, 200, 250, 300, 365];

    private readonly HabitsStore _store;
    private readonly ISettingsStore<HabitsSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly HabitReminderService _reminders;
    private readonly ILogger<HabitsViewModel> _logger;
    private readonly Dictionary<string, HabitCard> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HabitRow> _rows = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private bool _loading;
    private bool _refreshingReport;

    /// <summary>A problem or a notice about the last action; null when there is nothing to say.</summary>
    [ObservableProperty] private string? _message;

    [ObservableProperty] private bool _isReportTab;

    [ObservableProperty] private HabitCelebration? _celebration;

    /// <summary>Missed days to explain; the page shows it over everything else until a reason is saved.</summary>
    [ObservableProperty] private HabitPendingLapse? _pendingLapse;

    [ObservableProperty] private string _lapseReason = "";

    // Report
    [ObservableProperty] private HabitChoice? _reportHabit;
    [ObservableProperty] private int _reportYear;
    [ObservableProperty] private HabitYearGrid? _reportGrid;
    [ObservableProperty] private string _reportStreak = "";
    [ObservableProperty] private string _reportLongest = "";
    [ObservableProperty] private string _reportRate30 = "";
    [ObservableProperty] private string _reportRateYear = "";
    [ObservableProperty] private string _reportDone = "";
    [ObservableProperty] private string _reportStarted = "";

    // Settings page
    [ObservableProperty] private string _newHabitTitle = "";
    [ObservableProperty] private bool _playCelebrations;
    [ObservableProperty] private bool _remindersEnabled;
    [ObservableProperty] private int _reminderHourIndex;

    public HabitsViewModel(HabitsStore store, ISettingsStoreFactory settings, IUiDispatcher ui, IDialogService dialogs, HabitReminderService reminders,
        ILogger<HabitsViewModel> logger)
    {
        _store = store;
        _settings = settings.Get<HabitsSettings>(HabitsIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _reminders = reminders;
        _logger = logger;

        _loading = true;
        var s = _settings.Current;
        PlayCelebrations = s.PlayCelebrations;
        RemindersEnabled = s.RemindersEnabled;
        ReminderHourIndex = Math.Clamp(s.ReminderHour, 0, 23);
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        Refresh();
    }

    /// <summary>Raised when a habit is done here for today (true for a big one: a milestone or the last habit of the day).</summary>
    public event EventHandler<bool>? Celebrated;

    /// <summary>Habits not done yet today, in list order.</summary>
    public ObservableCollection<HabitCard> ToDo { get; } = [];

    /// <summary>Habits done today, below the others.</summary>
    public ObservableCollection<HabitCard> DoneToday { get; } = [];

    public ObservableCollection<HabitChoice> ReportHabits { get; } = [];

    /// <summary>The years with days of the chosen habit, newest first.</summary>
    public ObservableCollection<int> ReportYears { get; } = [];

    public ObservableCollection<HabitMonthBar> ReportMonths { get; } = [];

    /// <summary>The runs of missed days in the chosen year, newest first.</summary>
    public ObservableCollection<HabitLapseRow> ReportLapses { get; } = [];

    /// <summary>The habits on the settings page.</summary>
    public ObservableCollection<HabitRow> ManageHabits { get; } = [];

    /// <summary>"00:00" … "23:00" in the current culture's short time format; the index is the hour.</summary>
    public IReadOnlyList<string> ReminderHourNames { get; } =
        Enumerable.Range(0, 24).Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString("t", CultureInfo.CurrentCulture)).ToList();

    public bool IsHabitsTab => !IsReportTab;
    public bool HasHabits => ManageHabits.Count > 0;
    public bool HasNoHabits => ManageHabits.Count == 0;
    public bool HasToDo => ToDo.Count > 0;
    public bool HasDoneToday => DoneToday.Count > 0;

    /// <summary>Every habit is done for today.</summary>
    public bool AllDone => ToDo.Count == 0 && DoneToday.Count > 0;

    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasCelebration => Celebration is not null;
    public bool IsBigCelebration => Celebration?.Big == true;

    /// <summary>Confetti with the celebration card (the card itself always shows).</summary>
    public bool PlayConfetti => Celebration is not null && PlayCelebrations;

    public bool HasPendingLapse => PendingLapse is not null;
    public bool CanSaveReason => LapseReason.Trim().Length > 0;
    public bool HasReport => ReportHabit is not null && ReportGrid is not null;
    public bool HasReportLapses => ReportLapses.Count > 0;
    public bool CanAddHabit => NewHabitTitle.Trim().Length > 0;

    // ---- Tabs ----------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowHabits() => IsReportTab = false;

    [RelayCommand]
    private void ShowReport() => IsReportTab = true;

    partial void OnIsReportTabChanged(bool value) => OnPropertyChanged(nameof(IsHabitsTab));

    [RelayCommand]
    private void DismissMessage() => Message = null;

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    // ---- Ticking -------------------------------------------------------------------------------------------------

    /// <summary>A card was tapped: done today (after asking), or, on a card already done, not done after all.</summary>
    [RelayCommand]
    private async Task TapCard(HabitCard? card)
    {
        if (card is null || HasPendingLapse) return;
        try
        {
            var today = _store.Today;
            if (card.IsDone)
            {
                if (!await _dialogs.ConfirmAsync("Not done today?", $"Mark “{card.Title}” as not done today?", "Not done")) return;
                _store.Uncheck(card.Id, today);
                Message = null;
            }
            else
            {
                if (!await _dialogs.ConfirmAsync("Done today?", $"Are you sure you did “{card.Title}” today?", "Yes, I did it")) return;
                if (_store.Check(card.Id, today) == HabitCheckResult.Checked) Celebrate(card.Id);
            }
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tick a habit");
            Message = $"Could not save it: {ex.Message}";
        }
    }

    /// <summary>A day of a grid was tapped: tick or untick it (after asking). Days that do not count do nothing.</summary>
    [RelayCommand]
    private async Task TapDay(HabitDayTap? tap)
    {
        if (tap is null || HasPendingLapse || _store.Get(tap.HabitId) is not { } habit) return;
        try
        {
            var today = _store.Today;
            if (tap.Day > today) return;
            if (tap.Day < habit.StartDay)
            {
                Message = $"“{habit.Title}” started on {Date(habit.StartDay)}; the days before it do not count.";
                return;
            }
            var day = tap.Day == today ? "today" : Date(tap.Day);
            if (_store.IsDone(tap.HabitId, tap.Day))
            {
                if (!await _dialogs.ConfirmAsync("Not done?", $"Mark “{habit.Title}” as not done on {day}?", "Not done")) return;
                _store.Uncheck(tap.HabitId, tap.Day);
                Message = null;
            }
            else
            {
                if (!await _dialogs.ConfirmAsync("Done?", $"Are you sure you did “{habit.Title}” on {day}?", "Yes, I did it")) return;
                if (_store.Check(tap.HabitId, tap.Day) == HabitCheckResult.Checked)
                {
                    if (tap.Day == today) Celebrate(tap.HabitId);
                    else Message = $"{Date(tap.Day)} is marked done.";
                }
            }
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not change a day of a habit");
            Message = $"Could not save it: {ex.Message}";
        }
    }

    private void Celebrate(string habitId)
    {
        if (_store.Get(habitId) is not { } habit) return;
        var done = _store.AllDoneDays();
        var today = _store.Today;
        var days = done.TryGetValue(habitId, out var d) ? d : new HashSet<DateOnly>();
        var summary = HabitStats.Compute(habit, days, today);
        var streak = summary.CurrentStreak;
        var milestone = Milestones.Contains(streak);
        var allDone = _store.Habits().All(h => done.TryGetValue(h.Id, out var x) && x.Contains(today));
        var message = streak switch
        {
            1 => "Day one. Every chain starts with a single link.",
            _ when milestone => $"{streak} days in a row: this is how habits are made!",
            _ => $"{streak} days in a row. Keep the chain going!",
        };
        if (allDone && _store.Habits().Count > 1) message += " And that is everything for today.";
        var stats = new List<string>();
        if (summary.RateYear is { } rate) stats.Add($"This year: {Percent(rate)} of days");
        if (summary.LongestStreak > 1) stats.Add($"Longest streak: {Plural(summary.LongestStreak, "day")}");
        var big = milestone || (allDone && _store.Habits().Count > 1);
        Celebration = new HabitCelebration($"“{habit.Title}” done!", message, stats, big);
        Message = null;
        Celebrated?.Invoke(this, big);
    }

    [RelayCommand]
    private void DismissCelebration() => Celebration = null;

    partial void OnCelebrationChanged(HabitCelebration? value)
    {
        OnPropertyChanged(nameof(HasCelebration));
        OnPropertyChanged(nameof(IsBigCelebration));
        OnPropertyChanged(nameof(PlayConfetti));
    }

    // ---- Reasons for missed days ---------------------------------------------------------------------------------

    /// <summary>
    /// Looks for skipped days that still need a reason and asks for the first of them. Called when the page is shown
    /// (coming back to the habits), and again after each reason.
    /// </summary>
    public void CheckLapses()
    {
        try
        {
            var today = _store.Today;
            var done = _store.AllDoneDays();
            var lapses = _store.Lapses();
            foreach (var h in _store.Habits())
            {
                var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
                var gaps = HabitLapses.Pending(h.Id, h.Value, days, lapses.Where(l => l.HabitId == h.Id), today);
                if (gaps.Count == 0) continue;
                var gap = gaps[0];
                if (PendingLapse?.Gap != gap) LapseReason = "";
                PendingLapse = new HabitPendingLapse(gap, h.Value.Title, $"You skipped “{h.Value.Title}” for {gap.Days} days",
                    $"{Date(gap.From)} – {Date(gap.To)}");
                return;
            }
            PendingLapse = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look for skipped days");
        }
    }

    [RelayCommand]
    private void SaveReason()
    {
        if (PendingLapse is not { } pending || !CanSaveReason) return;
        if (!_store.WriteReason(pending.Gap.HabitId, pending.Gap.From, pending.Gap.To, LapseReason))
        {
            Message = "Could not save the reason. Write a few words and try again.";
            return;
        }
        LapseReason = "";
        CheckLapses();
        if (PendingLapse is null) Message = "Thanks for being honest. Today is a new day: pick the chain back up.";
    }

    /// <summary>The habit was done after all on those days (just not ticked): ticks them all, after asking.</summary>
    [RelayCommand]
    private async Task MarkLapseDone()
    {
        if (PendingLapse is not { } pending) return;
        if (!await _dialogs.ConfirmAsync("Done on those days?", $"Mark “{pending.HabitTitle}” as done on every day from {pending.Dates}?", "Yes, I did it"))
            return;
        for (var day = pending.Gap.From; day <= pending.Gap.To; day = day.AddDays(1)) _store.Check(pending.Gap.HabitId, day);
        LapseReason = "";
        CheckLapses();
        Refresh();
    }

    partial void OnPendingLapseChanged(HabitPendingLapse? value) => OnPropertyChanged(nameof(HasPendingLapse));

    partial void OnLapseReasonChanged(string value) => OnPropertyChanged(nameof(CanSaveReason));

    // ---- Report --------------------------------------------------------------------------------------------------

    partial void OnReportHabitChanged(HabitChoice? value)
    {
        if (!_refreshingReport) RefreshReport();
        OnPropertyChanged(nameof(HasReport));
    }

    partial void OnReportYearChanged(int value)
    {
        if (!_refreshingReport) RefreshReport();
    }

    partial void OnReportGridChanged(HabitYearGrid? value) => OnPropertyChanged(nameof(HasReport));

    private void RefreshReport()
    {
        _refreshingReport = true;
        try
        {
            if (ReportHabit is not { } choice || _store.Get(choice.Id) is not { } habit)
            {
                ReportYears.Clear();
                ReportMonths.Clear();
                ReportLapses.Clear();
                ReportGrid = null;
                OnPropertyChanged(nameof(HasReportLapses));
                return;
            }
            var today = _store.Today;
            var done = _store.DoneDays(choice.Id);
            var years = Enumerable.Range(habit.StartDay.Year, Math.Max(1, today.Year - habit.StartDay.Year + 1)).Reverse().ToList();
            SyncCollection(ReportYears, years);
            if (!years.Contains(ReportYear)) ReportYear = years[0];
            var year = ReportYear;

            ReportGrid = HabitYearGrid.Build(choice.Id, habit, done, today, year);
            var summary = HabitStats.Compute(habit, done, today);
            var (doneYear, trackedYear) = HabitStats.Count(habit, done, today, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));
            ReportStreak = Plural(summary.CurrentStreak, "day");
            ReportLongest = Plural(HabitStats.LongestStreak(done, year), "day");
            ReportRate30 = summary.Rate30 is { } r30 ? Percent(r30) : "–";
            ReportRateYear = trackedYear == 0 ? "–" : Percent((double)doneYear / trackedYear);
            ReportDone = $"{doneYear} of {Plural(trackedYear, "day")}";
            ReportStarted = $"Started on {Date(habit.StartDay)}";

            ReportMonths.Clear();
            foreach (var m in HabitStats.Months(habit, done, today, year))
            {
                var name = new DateTime(year, m.Month, 1).ToString("MMM", CultureInfo.InvariantCulture);
                ReportMonths.Add(new HabitMonthBar(name, m.Rate ?? 0, m.Rate is { } rate ? Percent(rate) : "", m.Tracked > 0));
            }

            // Runs Habits asked about (or that have a reason), newest first; single missed days only as a count.
            ReportLapses.Clear();
            var singles = 0;
            foreach (var (gap, reason) in HabitLapses.Missed(choice.Id, habit, done, _store.Lapses(choice.Id), today, year).Reverse())
            {
                if (reason is null && gap.Days < HabitLapses.MinDays)
                {
                    singles++;
                    continue;
                }
                var dates = gap.Days == 1 ? Date(gap.From) : $"{Date(gap.From)} – {Date(gap.To)} ({gap.Days} days)";
                ReportLapses.Add(new HabitLapseRow(dates, reason?.Reason ?? "No reason written", reason is not null));
            }
            if (singles > 0)
                ReportLapses.Add(new HabitLapseRow(singles == 1 ? "1 single day" : $"{singles} single days",
                    $"Days missed one at a time. Habits asks why only after {HabitLapses.MinDays} days in a row.", false));
            OnPropertyChanged(nameof(HasReportLapses));
        }
        finally
        {
            _refreshingReport = false;
        }
    }

    // ---- Settings page -------------------------------------------------------------------------------------------

    partial void OnNewHabitTitleChanged(string value) => OnPropertyChanged(nameof(CanAddHabit));

    [RelayCommand]
    private void AddHabit()
    {
        if (!CanAddHabit) return;
        if (_store.Habits().Count >= HabitLimits.Habits)
        {
            Message = $"You can track up to {HabitLimits.Habits} habits. Delete one you no longer need first.";
            return;
        }
        if (_store.Add(NewHabitTitle) is null)
        {
            Message = "Give the habit a name.";
            return;
        }
        Message = $"“{HabitLimits.Clip(NewHabitTitle, HabitLimits.Title)}” added. It starts today.";
        NewHabitTitle = "";
        Refresh();
    }

    private void RenameHabit(HabitRow row, string title)
    {
        if (_store.Rename(row.Id, title)) return;
        // Empty: put the stored title back.
        if (_store.Get(row.Id) is { } habit) _ui.Post(() => row.Load(habit.Title));
    }

    [RelayCommand]
    private void MoveHabitUp(HabitRow? row)
    {
        if (row is not null && _store.Move(row.Id, -1)) Refresh();
    }

    [RelayCommand]
    private void MoveHabitDown(HabitRow? row)
    {
        if (row is not null && _store.Move(row.Id, 1)) Refresh();
    }

    [RelayCommand]
    private async Task DeleteHabit(HabitRow? row)
    {
        if (row is null || _store.Get(row.Id) is not { } habit) return;
        var days = _store.DoneDays(row.Id).Count;
        if (!await _dialogs.ConfirmAsync($"Delete “{habit.Title}”?",
                $"Its {Plural(days, "done day")} and the reasons for skipped days are deleted on all your devices. This cannot be undone.", "Delete"))
            return;
        _store.Delete(row.Id);
        Message = $"“{habit.Title}” deleted.";
        Refresh();
    }

    partial void OnPlayCelebrationsChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayConfetti));
        if (!_loading) _settings.Update(s => s.PlayCelebrations = value);
    }

    partial void OnRemindersEnabledChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.RemindersEnabled = value);
    }

    partial void OnReminderHourIndexChanged(int value)
    {
        if (!_loading && value >= 0) _settings.Update(s => s.ReminderHour = Math.Clamp(value, 0, 23));
    }

    /// <summary>Shows the reminder right away (to try the notification), whatever the hour.</summary>
    [RelayCommand]
    private void RemindNow()
    {
        try
        {
            Message = _reminders.RemindNow() ? null : $"No habit has been skipped for {HabitLapses.MinDays} days or more, so there is nothing to remind you about.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Habits reminder failed");
            Message = $"Could not show the reminder: {ex.Message}";
        }
    }

    // ---- Refresh -------------------------------------------------------------------------------------------------

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    /// <summary>Reads the store again: the cards, the settings list and the report (also when the day changes).</summary>
    public void Refresh()
    {
        try
        {
            var today = _store.Today;
            var habits = _store.Habits();
            var done = _store.AllDoneDays();
            var todo = new List<HabitCard>();
            var finished = new List<HabitCard>();
            var rows = new List<HabitRow>();
            var choices = new List<HabitChoice>();
            for (var i = 0; i < habits.Count; i++)
            {
                var (id, habit) = (habits[i].Id, habits[i].Value);
                var days = done.TryGetValue(id, out var d) ? d : new HashSet<DateOnly>();
                var summary = HabitStats.Compute(habit, days, today);

                if (!_cards.TryGetValue(id, out var card)) _cards[id] = card = new HabitCard(id);
                card.Title = habit.Title;
                card.IsDone = summary.DoneToday;
                card.Grid = HabitYearGrid.Build(id, habit, days, today, today.Year);
                var color = HabitColors.ForRate(summary.Rate30);
                card.Color = HabitColors.Hex(color);
                card.Tint = HabitColors.Hex(HabitColors.WithAlpha(color, 0x1C));
                card.Summary = $"{habit.Title}: {(summary.DoneToday ? "done today" : "not done yet today")}, {Plural(summary.CurrentStreak, "day")} in a row"
                    + (summary.Rate30 is { } rate ? $", {Percent(rate)} in the last {HabitStats.RecentDays} days" : "");
                (summary.DoneToday ? finished : todo).Add(card);

                if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new HabitRow(id, RenameHabit);
                row.Load(habit.Title);
                row.Detail = $"Since {Date(habit.StartDay)} · {Plural(days.Count, "day")} done";
                row.CanMoveUp = i > 0;
                row.CanMoveDown = i < habits.Count - 1;
                rows.Add(row);

                choices.Add(ReportHabits.FirstOrDefault(c => c.Id == id && c.Title == habit.Title) ?? new HabitChoice(id, habit.Title));
            }
            foreach (var gone in _cards.Keys.Except(habits.Select(h => h.Id)).ToList())
            {
                _cards.Remove(gone);
                _rows.Remove(gone);
            }
            SyncCollection(ToDo, todo);
            SyncCollection(DoneToday, finished);
            SyncCollection(ManageHabits, rows);

            _refreshingReport = true;
            var selected = ReportHabit?.Id;
            SyncCollection(ReportHabits, choices);
            ReportHabit = choices.FirstOrDefault(c => c.Id == selected) ?? choices.FirstOrDefault();
            _refreshingReport = false;
            RefreshReport();

            OnPropertyChanged(nameof(HasHabits));
            OnPropertyChanged(nameof(HasNoHabits));
            OnPropertyChanged(nameof(HasToDo));
            OnPropertyChanged(nameof(HasDoneToday));
            OnPropertyChanged(nameof(AllDone));
            // A reason written on another device, or the days ticked after all: the question may be answered already.
            if (PendingLapse is not null) CheckLapses();
        }
        catch (Exception ex)
        {
            // A refresh comes from a sync on a timer; it must never take the app down.
            _logger.LogError(ex, "Refreshing Habits failed");
        }
        finally
        {
            _refreshingReport = false;
        }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>Makes <paramref name="target"/> hold <paramref name="items"/> in order, touching only what changed.</summary>
    private static void SyncCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], items[i])) continue;
            var existing = target.IndexOf(items[i]);
            if (existing > i) target.Move(existing, i);
            else target.Insert(i, items[i]);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    private DateOnly Today => _store.Today;

    private string Date(DateOnly day) => day.ToString(day.Year == Today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);

    internal static string Percent(double rate) => (rate * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
