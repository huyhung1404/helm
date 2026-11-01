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
    private static readonly int[] Milestones = [7, 14, 21, 30, 50, 66, 90, 100, 150, 200, 250, 300, 365];

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

    /// <summary>The day being marked in the pop-up; null when it is not open.</summary>
    [ObservableProperty] private HabitMark? _mark;

    /// <summary>Missed days to explain (or a slip owned up to); the pop-up shows it over everything else until a reason is saved.</summary>
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
    [ObservableProperty] private string _reportMissedTitle = "Missed days";
    [ObservableProperty] private string _reportNoMissed = "";

    // Settings page
    [ObservableProperty] private string _newHabitTitle = "";

    /// <summary>The new habit is one to quit rather than to build.</summary>
    [ObservableProperty] private bool _newHabitIsQuit;
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

    /// <summary>
    /// The pop-up over the page is open. It shows one thing at a time: a reason to write (<see cref="ShowLapse"/>) before
    /// a day being marked (<see cref="ShowMark"/>) before a celebration (<see cref="ShowCelebration"/>).
    /// </summary>
    public bool HasPopup => PendingLapse is not null || Mark is not null || Celebration is not null;

    public bool ShowLapse => PendingLapse is not null;
    public bool ShowMark => PendingLapse is null && Mark is not null;
    public bool ShowCelebration => PendingLapse is null && Mark is null && Celebration is not null;

    /// <summary>A tap outside, Back or Escape closes the pop-up, except while skipped days wait for their reason.</summary>
    public bool CanDismissPopup => PendingLapse is null or { IsSlip: true };
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

    // ---- Marking a day -------------------------------------------------------------------------------------------

    /// <summary>
    /// A card was tapped: opens the pop-up for today (a habit to quit: for yesterday), where the day is marked with how
    /// much of it was done, or unmarked.
    /// </summary>
    [RelayCommand]
    private void TapCard(HabitCard? card)
    {
        if (card is null || HasPopup || _store.Get(card.Id) is not { } habit) return;
        var day = HabitDays.ActionDay(habit, _store.Today);
        if (day < habit.StartDay)
        {
            Message = $"“{habit.Title}” started today. Come back tomorrow to mark today as clean.";
            return;
        }
        OpenMark(card.Id, habit, day);
    }

    /// <summary>A day of a grid was tapped: opens the pop-up for that day. Days still to come do nothing.</summary>
    [RelayCommand]
    private void TapDay(HabitDayTap? tap)
    {
        if (tap is null || HasPopup || _store.Get(tap.HabitId) is not { } habit) return;
        if (tap.Day > HabitDays.ActionDay(habit, _store.Today)) return;
        if (tap.Day < habit.StartDay)
        {
            Message = $"“{habit.Title}” started on {Date(habit.StartDay)}; the days before it do not count.";
            return;
        }
        OpenMark(tap.HabitId, habit, tap.Day);
    }

    private void OpenMark(string habitId, Habit habit, DateOnly day)
    {
        var quit = habit.Kind == HabitKind.Quit;
        var done = _store.PercentOn(habitId, day);
        var canSlip = quit && done is null && day == HabitDays.ActionDay(habit, _store.Today)
            && !_store.Lapses(habitId).Any(l => l.From <= day && l.To >= day);
        var question = quit
            ? $"Did you stay away from it all of {DayName(day, inSentence: true)}?"
            : day == _store.Today ? "How much of it did you do today?" : "How much of it did you do that day?";
        var dayText = day == _store.Today ? "Today" : day == _store.Today.AddDays(-1) ? $"Yesterday, {Date(day)}" : WeekdayDate(day);
        Message = null;
        Celebration = null;
        Mark = new HabitMark(habitId, habit.Title, day, dayText, question, quit, done is not null, canSlip) { Percent = done ?? 100 };
    }

    /// <summary>One of the quick shares ("25", "50", "75", "100") under the slider.</summary>
    [RelayCommand]
    private void SetMarkPercent(string? value)
    {
        if (Mark is { } mark && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)) mark.Percent = Math.Clamp(p, HabitLimits.MinPercent, 100);
    }

    /// <summary>Saves the day with its share; a new done action day (today, or yesterday for a habit to quit) is celebrated.</summary>
    [RelayCommand]
    private void SaveMark()
    {
        if (Mark is not { } mark || _store.Get(mark.HabitId) is not { } habit) return;
        try
        {
            var result = _store.Check(mark.HabitId, mark.Day, mark.Value);
            Mark = null;
            if (result == HabitCheckResult.NotTracked)
                Message = $"{Date(mark.Day)} does not count for “{habit.Title}” any more.";
            else if (result == HabitCheckResult.Checked && mark.Day == HabitDays.ActionDay(habit, _store.Today))
                Celebrate(mark.HabitId);
            else if (result is HabitCheckResult.Checked or HabitCheckResult.Updated)
                Message = mark.IsQuit ? $"{DayName(mark.Day)} is marked clean." : $"{DayName(mark.Day)} is marked done: {mark.PercentText}.";
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark a day of a habit");
            Message = $"Could not save it: {ex.Message}";
        }
    }

    /// <summary>The day was not done (or not clean) after all.</summary>
    [RelayCommand]
    private void UnmarkDay()
    {
        if (Mark is not { } mark) return;
        try
        {
            _store.Uncheck(mark.HabitId, mark.Day);
            Mark = null;
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not unmark a day of a habit");
            Message = $"Could not save it: {ex.Message}";
        }
    }

    /// <summary>From the pop-up of a habit to quit: yesterday was not clean, so the reason is asked for.</summary>
    [RelayCommand]
    private void SlipFromMark()
    {
        if (Mark is not { CanSlip: true } mark) return;
        Mark = null;
        StartSlip(mark.HabitId);
    }

    /// <summary>A tap outside the pop-up, Back or Escape: closes what it shows, unless a reason must be written first.</summary>
    [RelayCommand]
    private void DismissPopup()
    {
        if (PendingLapse is { IsSlip: true }) CancelSlip();
        else if (PendingLapse is not null) return;
        else if (Mark is not null) Mark = null;
        else Celebration = null;
    }

    partial void OnMarkChanged(HabitMark? value) => NotifyPopup();

    private void NotifyPopup()
    {
        OnPropertyChanged(nameof(HasPopup));
        OnPropertyChanged(nameof(ShowLapse));
        OnPropertyChanged(nameof(ShowMark));
        OnPropertyChanged(nameof(ShowCelebration));
        OnPropertyChanged(nameof(CanDismissPopup));
    }

    private void Celebrate(string habitId)
    {
        if (_store.Get(habitId) is not { } habit) return;
        var done = _store.AllDoneDays();
        var today = _store.Today;
        var days = done.TryGetValue(habitId, out var d) ? d : new HashSet<DateOnly>();
        var summary = HabitStats.Compute(habit, days, today, percents: _store.Percents(habitId));
        var streak = summary.CurrentStreak;
        var milestone = Milestones.Contains(streak);
        var lapses = _store.Lapses();
        var allDone = _store.Habits().All(h =>
            HabitLapses.IsAnswered(h.Value, done.TryGetValue(h.Id, out var x) ? x : new HashSet<DateOnly>(), lapses.Where(l => l.HabitId == h.Id), today));
        var quit = habit.Kind == HabitKind.Quit;
        var message = quit
            ? streak switch
            {
                1 => "Day one clean. Every run starts with a single day.",
                _ when milestone => $"{streak} clean days in a row: you are breaking it!",
                _ => $"{streak} clean days in a row. Keep going!",
            }
            : streak switch
            {
                1 => "Day one. Every chain starts with a single link.",
                _ when milestone => $"{streak} days in a row: this is how habits are made!",
                _ => $"{streak} days in a row. Keep the chain going!",
            };
        if (allDone && _store.Habits().Count > 1) message += " And that is everything for today.";
        var stats = new List<string>();
        if (!quit && _store.PercentOn(habitId, today) is { } share and < 100) stats.Add($"Today: {share}%. Part of it still counts: showing up is what builds a habit");
        if (summary.RateYear is { } rate) stats.Add($"This year: {Percent(rate)} of days{(quit ? " clean" : "")}");
        if (summary.LongestStreak > 1) stats.Add($"Longest streak: {Plural(summary.LongestStreak, quit ? "clean day" : "day")}");
        var big = milestone || (allDone && _store.Habits().Count > 1);
        Celebration = new HabitCelebration(quit ? $"“{habit.Title}”: a clean day!" : $"“{habit.Title}” done!", message, stats, big);
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
        NotifyPopup();
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
            // A slip being written about stays until it is saved or taken back, unless the day was answered on another device.
            if (PendingLapse is { IsSlip: true } slip && _store.Get(slip.Gap.HabitId) is not null
                && !(done.TryGetValue(slip.Gap.HabitId, out var slipDays) && slipDays.Contains(slip.Gap.From))
                && !lapses.Any(l => l.HabitId == slip.Gap.HabitId && l.From <= slip.Gap.From && l.To >= slip.Gap.From)) return;
            foreach (var h in _store.Habits())
            {
                var days = done.TryGetValue(h.Id, out var d) ? d : new HashSet<DateOnly>();
                var gaps = HabitLapses.Pending(h.Id, h.Value, days, lapses.Where(l => l.HabitId == h.Id), today);
                if (gaps.Count == 0) continue;
                var gap = gaps[0];
                if (PendingLapse?.Gap != gap) LapseReason = "";
                var title = h.Value.Title;
                var dates = $"{Date(gap.From)} – {Date(gap.To)}";
                PendingLapse = h.Value.Kind == HabitKind.Quit
                    ? new HabitPendingLapse(gap, title, $"“{title}”: {gap.Days} days not marked", dates,
                        "Did you slip on those days? Be honest: write what led to it, so you (or an AI agent) can spot the triggers later and plan around them.",
                        "e.g. Alone at home late at night after a stressful week", "I stayed clean, I just forgot to mark it")
                    : new HabitPendingLapse(gap, title, $"You skipped “{title}” for {gap.Days} days", dates,
                        "What got in the way? Be honest: the reason stays with the habit, so you (or an AI agent) can spot the pattern later and plan around it.",
                        "e.g. Worked late two nights and was too tired", "I did it, I just forgot to tick it");
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
        if (PendingLapse is null)
        {
            Message = pending.IsSlip
                ? "Thanks for being honest. One slip does not undo the clean days before it: today starts a new run."
                : "Thanks for being honest. Today is a new day: pick the chain back up.";
        }
    }

    /// <summary>
    /// A habit to quit slipped yesterday: asks what led to it. A slip needs its reason (that is what shows the triggers
    /// later); until it is saved it can be taken back with <see cref="CancelSlipCommand"/>.
    /// </summary>
    [RelayCommand]
    private void Slip(HabitCard? card)
    {
        if (card is not null && !HasPopup) StartSlip(card.Id);
    }

    private void StartSlip(string habitId)
    {
        if (_store.Get(habitId) is not { Kind: HabitKind.Quit } habit) return;
        var day = HabitDays.ActionDay(habit, _store.Today);
        if (day < habit.StartDay || _store.IsDone(habitId, day)) return;
        LapseReason = "";
        Celebration = null;
        Message = null;
        PendingLapse = new HabitPendingLapse(new HabitGap(habitId, day, day), habit.Title, $"A slip on “{habit.Title}”", $"Yesterday, {Date(day)}",
            "It happens. What led to it? Write it down while you remember: the reasons show the triggers to plan around. The clean run starts again today.",
            "e.g. Could not sleep and scrolled my phone in bed", "", IsSlip: true);
    }

    [RelayCommand]
    private void CancelSlip()
    {
        if (PendingLapse is not { IsSlip: true }) return;
        PendingLapse = null;
        LapseReason = "";
        CheckLapses();
    }

    /// <summary>
    /// The habit was done after all on those days (just not ticked): ticks them all in full. The button in the pop-up
    /// says so plainly, so there is no second question (a dialog would open under the pop-up).
    /// </summary>
    [RelayCommand]
    private void MarkLapseDone()
    {
        if (PendingLapse is not { IsSlip: false } pending) return;
        for (var day = pending.Gap.From; day <= pending.Gap.To; day = day.AddDays(1)) _store.Check(pending.Gap.HabitId, day);
        LapseReason = "";
        CheckLapses();
        Refresh();
    }

    partial void OnPendingLapseChanged(HabitPendingLapse? value)
    {
        OnPropertyChanged(nameof(HasPendingLapse));
        NotifyPopup();
    }

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
            var percents = _store.Percents(choice.Id);
            var years = Enumerable.Range(habit.StartDay.Year, Math.Max(1, today.Year - habit.StartDay.Year + 1)).Reverse().ToList();
            SyncCollection(ReportYears, years);
            if (!years.Contains(ReportYear)) ReportYear = years[0];
            var year = ReportYear;
            var quit = habit.Kind == HabitKind.Quit;
            var unit = quit ? "clean day" : "day";
            var lapses = _store.Lapses(choice.Id);

            ReportGrid = HabitYearGrid.Build(choice.Id, habit, done, today, year, lapses, percents);
            var summary = HabitStats.Compute(habit, done, today, lapses, percents);
            var (doneYear, trackedYear, scoreYear) = HabitStats.Count(habit, done, today, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31), percents);
            ReportStreak = Plural(summary.CurrentStreak, unit);
            ReportLongest = Plural(HabitStats.LongestStreak(done, year), unit);
            ReportRate30 = summary.Rate30 is { } r30 ? Percent(r30) : "–";
            ReportRateYear = trackedYear == 0 ? "–" : Percent(scoreYear / trackedYear);
            ReportDone = $"{doneYear} of {Plural(trackedYear, "day")}{(quit ? " clean" : "")}";
            ReportStarted = quit ? $"Quitting since {Date(habit.StartDay)}" : $"Started on {Date(habit.StartDay)}";
            ReportMissedTitle = quit ? "Slips and days not marked" : "Missed days";
            ReportNoMissed = quit ? "No slips that year. Impressive!" : "No missed days that year. Impressive!";

            ReportMonths.Clear();
            foreach (var m in HabitStats.Months(habit, done, today, year, percents))
            {
                var name = new DateTime(year, m.Month, 1).ToString("MMM", CultureInfo.InvariantCulture);
                ReportMonths.Add(new HabitMonthBar(name, m.Rate ?? 0, m.Rate is { } rate ? Percent(rate) : "", m.Tracked > 0));
            }

            // Runs Habits asked about (or that have a reason), newest first; single missed days only as a count.
            ReportLapses.Clear();
            var singles = 0;
            foreach (var (gap, reason) in HabitLapses.Missed(choice.Id, habit, done, lapses, today, year).Reverse())
            {
                if (reason is null && gap.Days < HabitLapses.MinDays)
                {
                    singles++;
                    continue;
                }
                var dates = gap.Days == 1 ? Date(gap.From) : $"{Date(gap.From)} – {Date(gap.To)} ({gap.Days} days)";
                ReportLapses.Add(new HabitLapseRow(dates, reason?.Reason ?? (quit ? "Not marked, no reason written" : "No reason written"), reason is not null));
            }
            if (singles > 0)
                ReportLapses.Add(new HabitLapseRow(singles == 1 ? "1 single day" : $"{singles} single days",
                    quit
                        ? $"Days not marked one at a time. Habits asks about them only after {HabitLapses.MinDays} days in a row."
                        : $"Days missed one at a time. Habits asks why only after {HabitLapses.MinDays} days in a row.", false));
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
        if (_store.Add(NewHabitTitle, NewHabitIsQuit ? HabitKind.Quit : HabitKind.Build) is null)
        {
            Message = "Give the habit a name.";
            return;
        }
        var title = HabitLimits.Clip(NewHabitTitle, HabitLimits.Title);
        Message = NewHabitIsQuit
            ? $"“{title}” added. Today is its first day: come back tomorrow to mark it clean."
            : $"“{title}” added. It starts today.";
        NewHabitTitle = "";
        NewHabitIsQuit = false;
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
            var allPercents = _store.AllPercents();
            var lapses = _store.Lapses();
            var todo = new List<HabitCard>();
            var finished = new List<HabitCard>();
            var rows = new List<HabitRow>();
            var choices = new List<HabitChoice>();
            for (var i = 0; i < habits.Count; i++)
            {
                var (id, habit) = (habits[i].Id, habits[i].Value);
                var days = done.TryGetValue(id, out var d) ? d : new HashSet<DateOnly>();
                var percents = allPercents.GetValueOrDefault(id);
                var habitLapses = lapses.Where(l => l.HabitId == id).ToList();
                var summary = HabitStats.Compute(habit, days, today, habitLapses, percents);
                var answered = HabitLapses.IsAnswered(habit, days, habitLapses, today);
                var quit = habit.Kind == HabitKind.Quit;

                if (!_cards.TryGetValue(id, out var card)) _cards[id] = card = new HabitCard(id);
                card.Title = habit.Title;
                card.IsQuit = quit;
                card.IsDone = summary.DoneToday;
                card.CanSlip = quit && !answered;
                var share = summary.DoneToday ? (int)Math.Round(HabitStats.Share(percents, today) * 100) : 100;
                card.Status = quit ? QuitStatus(habit, summary, answered, today) : share < 100 ? $"Today: {share}% done" : "";
                card.Grid = HabitYearGrid.Build(id, habit, days, today, today.Year, habitLapses, percents);
                var color = HabitColors.ForRate(summary.Rate30);
                card.Color = HabitColors.Hex(color);
                card.Tint = HabitColors.Hex(HabitColors.WithAlpha(color, 0x1C));
                card.Summary = (quit
                        ? $"{habit.Title} (to quit): {(summary.DoneToday ? "yesterday marked clean" : answered ? "nothing to mark yet" : "yesterday not marked yet")}, {Plural(summary.CurrentStreak, "clean day")} in a row"
                        : $"{habit.Title}: {(summary.DoneToday ? "done today" : "not done yet today")}, {Plural(summary.CurrentStreak, "day")} in a row")
                    + (summary.Rate30 is { } rate ? $", {Percent(rate)} in the last {HabitStats.RecentDays} days" : "");
                (answered ? finished : todo).Add(card);

                if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new HabitRow(id, RenameHabit);
                row.Load(habit.Title);
                row.Detail = quit
                    ? $"To quit · since {Date(habit.StartDay)} · {Plural(days.Count, "clean day")}"
                    : $"Since {Date(habit.StartDay)} · {Plural(days.Count, "day")} done";
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

    /// <summary>"Today", "Yesterday" or the date; in lower case with <paramref name="inSentence"/>.</summary>
    private string DayName(DateOnly day, bool inSentence = false) =>
        day == Today ? (inSentence ? "today" : "Today") : day == Today.AddDays(-1) ? (inSentence ? "yesterday" : "Yesterday") : Date(day);

    private string WeekdayDate(DateOnly day) => day.ToString(day.Year == Today.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The line under a habit to quit: its clean run, and what to do next.</summary>
    private static string QuitStatus(Habit habit, HabitSummary summary, bool answered, DateOnly today)
    {
        if (HabitDays.ActionDay(habit, today) < habit.StartDay) return "Today is day one: mark it clean tomorrow";
        if (answered && !summary.DoneToday) return "Slipped yesterday: a new run starts today";
        var run = summary.CurrentStreak == 0 ? "No clean days in a row yet" : $"{Plural(summary.CurrentStreak, "clean day")} in a row";
        return answered ? run : $"{run} · was yesterday clean?";
    }

    private string Date(DateOnly day) => day.ToString(day.Year == Today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);

    internal static string Percent(double rate) => (rate * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
}
