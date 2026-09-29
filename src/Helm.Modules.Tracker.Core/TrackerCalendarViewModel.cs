using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Tracker;

/// <summary>One entry on a calendar day: tapping it shows the day (a task can be ticked there, a person opened).</summary>
public sealed partial class CalendarEntryViewModel(TrackerViewModel owner, CalendarEntry entry) : ObservableObject
{
    public CalendarEntry Entry { get; } = entry;

    public string Title => Entry.Title;

    public string Line => Entry.TimeText + Entry.Title;

    public string Detail => Entry.Detail;

    public bool IsDone => Entry.IsDone;

    public bool IsOverdue => Entry.IsOverdue;

    public bool IsDebt => Entry.IsDebt;

    public bool IsProjected => Entry.IsProjected;

    /// <summary>A task from the day list opens in its list; a person opens in the debt book.</summary>
    [RelayCommand]
    private void Open() => owner.OpenCalendarEntry(Entry);
}

/// <summary>A day cell of the month or week grid.</summary>
public sealed partial class CalendarDayViewModel(TrackerViewModel owner, DateOnly date, bool inRange, bool isToday) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public DateOnly Date { get; } = date;

    public string DayNumber => Date.Day.ToString(CultureInfo.CurrentCulture);

    /// <summary>"Mon 29" in the week view (the month view has the weekday names above the grid).</summary>
    public string WeekLabel => Date.ToString("ddd d", CultureInfo.CurrentCulture);

    /// <summary>False for the days of the neighbouring months that fill the month grid.</summary>
    public bool IsInRange { get; } = inRange;

    public bool IsToday { get; } = isToday;

    public ObservableCollection<CalendarEntryViewModel> Entries { get; } = [];

    /// <summary>"+2 more" when the cell cannot show every entry.</summary>
    [ObservableProperty] private string _moreText = "";

    public bool HasMore => MoreText.Length > 0;

    partial void OnMoreTextChanged(string value) => OnPropertyChanged(nameof(HasMore));

    public string AutomationName => Date.ToString("D", CultureInfo.CurrentCulture) + (Entries.Count + (HasMore ? 1 : 0) > 0 ? $", {Entries.Count} or more items" : "");

    [RelayCommand]
    private void Select() => owner.SelectCalendarDay(Date);
}

/// <summary>
/// The Tracker's calendar (on both apps): a month or a week of due tasks and debts from every workspace, or from the
/// selected one. Tapping a day lists it below the grid, where a task can be ticked or added with that day as its due date.
/// </summary>
public sealed partial class TrackerViewModel
{
    private const int MonthCellEntries = 3;
    private const int WeekCellEntries = 12;
    private readonly Dictionary<string, TrackerItemViewModel> _calendarRows = new(StringComparer.Ordinal);
    private DateOnly _calendarAnchor;

    /// <summary>The page shows the calendar instead of the lists.</summary>
    [ObservableProperty] private bool _isCalendar;

    /// <summary>Week view (7 tall days) instead of the month grid.</summary>
    [ObservableProperty] private bool _isWeekView;

    /// <summary>Every workspace (default) or only the selected one.</summary>
    [ObservableProperty] private bool _calendarAllWorkspaces = true;

    [ObservableProperty] private string _calendarTitle = "";
    [ObservableProperty] private DateOnly? _calendarSelectedDay;
    [ObservableProperty] private string _calendarSelectedTitle = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddOnCalendarDayCommand))] private string _calendarNewTitle = "";

    public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = [];

    /// <summary>"Mon", "Tue"… from the first day of the week of this culture.</summary>
    public IReadOnlyList<string> CalendarWeekdayNames { get; } = Enumerable.Range(0, 7)
        .Select(i => CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames[((int)FirstDayOfWeek + i) % 7]).ToList();

    /// <summary>The tasks due on the selected day (tick, edit, delete as in the lists).</summary>
    public ObservableCollection<TrackerItemViewModel> CalendarDayItems { get; } = [];

    /// <summary>The debts due and the coming days of repeating tasks on the selected day (they have no row of their own).</summary>
    public ObservableCollection<CalendarEntryViewModel> CalendarDayOthers { get; } = [];

    public bool HasCalendarDaySelection => CalendarSelectedDay is not null;

    public bool IsCalendarDayEmpty => CalendarSelectedDay is not null && CalendarDayItems.Count == 0 && CalendarDayOthers.Count == 0;

    /// <summary>Adding on a day needs a to-do list: the selected one, else the first.</summary>
    public bool CanAddOnCalendarDay => CalendarSelectedDay is not null && TasksWorkspaceForCalendar() is not null;

    private static DayOfWeek FirstDayOfWeek => CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    private DateOnly TodayLocal => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_store.Now, TimeZoneInfo.Local).DateTime);

    partial void OnIsCalendarChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowListsView));
        OnPropertyChanged(nameof(ShowCalendarView));
        if (!value) return;
        if (_calendarAnchor == default) _calendarAnchor = TodayLocal;
        RefreshCalendar();
    }

    partial void OnIsWeekViewChanged(bool value) => RefreshCalendar();

    partial void OnCalendarAllWorkspacesChanged(bool value) => RefreshCalendar();

    partial void OnCalendarSelectedDayChanged(DateOnly? value)
    {
        OnPropertyChanged(nameof(HasCalendarDaySelection));
        OnPropertyChanged(nameof(CanAddOnCalendarDay));
        AddOnCalendarDayCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ShowCalendar() => IsCalendar = true;

    [RelayCommand]
    private void ShowLists() => IsCalendar = false;

    [RelayCommand]
    private void SetCalendarMonth() => IsWeekView = false;

    [RelayCommand]
    private void SetCalendarWeek() => IsWeekView = true;

    [RelayCommand]
    private void CalendarPrevious() => MoveCalendar(-1);

    [RelayCommand]
    private void CalendarNext() => MoveCalendar(1);

    [RelayCommand]
    private void CalendarToday()
    {
        _calendarAnchor = TodayLocal;
        CalendarSelectedDay = _calendarAnchor;
        RefreshCalendar();
    }

    private void MoveCalendar(int direction)
    {
        _calendarAnchor = IsWeekView ? _calendarAnchor.AddDays(7 * direction) : _calendarAnchor.AddMonths(direction);
        RefreshCalendar();
    }

    internal void SelectCalendarDay(DateOnly day)
    {
        CalendarSelectedDay = CalendarSelectedDay == day ? null : day;
        RefreshCalendar();
    }

    internal void OpenCalendarEntry(CalendarEntry entry)
    {
        if (entry.PersonKey is { } key && entry.WorkspaceId is { } book)
        {
            IsCalendar = false;
            ShowPerson(book, key);
        }
        else if (entry.ItemId is { } id && entry.WorkspaceId is { } ws)
        {
            IsCalendar = false;
            ShowItem(ws, id);
        }
    }

    private bool CanAddOnDay() => CanAddOnCalendarDay && CalendarNewTitle.Trim().Length > 0;

    /// <summary>A task due on the selected day, in the selected to-do list (or the first one).</summary>
    [RelayCommand(CanExecute = nameof(CanAddOnDay))]
    private void AddOnCalendarDay()
    {
        if (CalendarSelectedDay is not { } day || TasksWorkspaceForCalendar() is not { } ws) return;
        if (!Try(() => _store.AddItem(ws, new TrackerItemDraft(CalendarNewTitle.Trim(), DueDate: day)))) return;
        CalendarNewTitle = "";
    }

    private string? TasksWorkspaceForCalendar()
    {
        if (SelectedWorkspace is { Kind: WorkspaceKind.Tasks } selected) return selected.Id;
        return Workspaces.FirstOrDefault(w => w.Kind == WorkspaceKind.Tasks)?.Id;
    }

    /// <summary>Rebuilds the grid and the selected day (after a move, a store change or a setting).</summary>
    private void RefreshCalendar()
    {
        if (!IsCalendar) return;
        if (_calendarAnchor == default) _calendarAnchor = TodayLocal;
        var days = IsWeekView ? TrackerCalendar.WeekDays(_calendarAnchor, FirstDayOfWeek) : TrackerCalendar.MonthDays(_calendarAnchor, FirstDayOfWeek);
        var workspace = CalendarAllWorkspaces ? null : SelectedWorkspace?.Id;
        var from = days[0];
        var to = days[^1];
        if (CalendarSelectedDay is { } picked && (picked < from || picked > to)) CalendarSelectedDay = null;
        var entries = TrackerCalendar.Entries(_store, from, to, workspace, TimeZoneInfo.Local);
        var today = TodayLocal;
        var perCell = IsWeekView ? WeekCellEntries : MonthCellEntries;

        CalendarTitle = IsWeekView
            ? $"{from.ToString("d MMM", CultureInfo.CurrentCulture)} – {to.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}"
            : _calendarAnchor.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        CalendarDays.Clear();
        foreach (var date in days)
        {
            var cell = new CalendarDayViewModel(this, date, IsWeekView || date.Month == _calendarAnchor.Month, date == today)
            {
                IsSelected = date == CalendarSelectedDay,
            };
            var list = entries.GetValueOrDefault(date) ?? [];
            foreach (var entry in list.Take(perCell)) cell.Entries.Add(new CalendarEntryViewModel(this, entry));
            if (list.Count > perCell) cell.MoreText = $"+{list.Count - perCell} more";
            CalendarDays.Add(cell);
        }
        RefreshCalendarDay(entries, today);
    }

    private void RefreshCalendarDay(IReadOnlyDictionary<DateOnly, IReadOnlyList<CalendarEntry>> entries, DateOnly today)
    {
        var rows = new List<TrackerItemViewModel>();
        CalendarDayOthers.Clear();
        if (CalendarSelectedDay is { } day)
        {
            CalendarSelectedTitle = day == today ? "Today" : day.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
            foreach (var entry in entries.GetValueOrDefault(day) ?? [])
            {
                if (entry.ItemId is { } id && _store.GetItem(id) is { } item && _store.GetWorkspace(item.WorkspaceId) is { } ws)
                {
                    if (_calendarRows.TryGetValue(id, out var row)) row.Update(item, ws, today);
                    else _calendarRows[id] = row = new TrackerItemViewModel(this, id, item, ws, today);
                    rows.Add(row);
                }
                else CalendarDayOthers.Add(new CalendarEntryViewModel(this, entry));
            }
        }
        foreach (var stale in _calendarRows.Keys.Where(k => rows.All(r => r.Id != k)).ToList()) _calendarRows.Remove(stale);
        Reconcile(CalendarDayItems, rows);
        OnPropertyChanged(nameof(IsCalendarDayEmpty));
        OnPropertyChanged(nameof(CanAddOnCalendarDay));
        AddOnCalendarDayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The open tasks and debts with a due date as an iCalendar file (Google Calendar, Outlook, phones).</summary>
    public string CalendarIcs() => TrackerIcs.Build(_store, TimeZoneInfo.Local);
}
