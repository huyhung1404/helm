using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Links;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Modules.Wallet;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Tracker;

/// <summary>One bar of the "completed per day" chart.</summary>
public sealed record ReportBar(string Label, string ToolTip, int Count, double BarHeight);

/// <summary>A labelled number in the report (e.g. "Urgent", 3).</summary>
public sealed record ReportCount(string Label, int Count);

/// <summary>One line of the history list.</summary>
public sealed record HistoryRow(string When, string Text, string Workspace, TrackerEventKind Kind);

/// <summary>
/// The Tracker page, shared by the Windows and Android apps: workspace picker and management, the add form, open and
/// completed lists, the report and the history. All members are used on the UI thread; store changes (local or
/// synced) are coalesced into one refresh through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class TrackerViewModel : ObservableObject
{
    public const double MaxBarHeight = 64;
    private const int HistoryLimit = 100;
    private const int CompletedLimit = 50;

    private readonly TrackerStore _store;
    private readonly ISettingsStore<TrackerSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly TrackerReminderService _reminders;
    private readonly ILogger<TrackerViewModel> _logger;
    private readonly LinkHub? _links;
    private readonly DebtBook? _debts;
    private int _linksQueued;
    private readonly Dictionary<string, TrackerItemViewModel> _rows = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private bool _loading;

    // Workspace
    [ObservableProperty] private WorkspaceOption? _selectedWorkspace;
    [ObservableProperty] private string _workspaceName = "";
    [ObservableProperty] private string _newWorkspaceName = "";

    // Add form
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newTitle = "";
    [ObservableProperty] private int _newPriorityIndex = (int)TrackerPriority.Normal;
    [ObservableProperty] private int _newRepeatIndex;
    [ObservableProperty] private string _newRepeatDays = "7";
    [ObservableProperty] private DateTime? _newDueDate;
    [ObservableProperty] private string _newDueTimeText = "";
    [ObservableProperty] private TimeSpan? _newDueTime;
    private bool _syncingTime;

    // Lists, totals, report
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private string _openSummary = "";
    [ObservableProperty] private int _reportRangeIndex;
    [ObservableProperty] private bool _reportAllWorkspaces;
    [ObservableProperty] private string _reportCompleted = "0";
    [ObservableProperty] private string _reportCreated = "0";
    [ObservableProperty] private string _reportAverageLead = "—";
    [ObservableProperty] private string _reportMedianLead = "—";
    [ObservableProperty] private string _reportAverageWork = "—";
    [ObservableProperty] private string _reportOnTime = "—";
    [ObservableProperty] private string _reportRangeText = "";

    // Android widget look (device-local)
    [ObservableProperty] private int _widgetBackgroundIndex;
    [ObservableProperty] private int _widgetOpacity = 100;
    [ObservableProperty] private int _widgetTextIndex;

    // Reminders
    [ObservableProperty] private bool _remindersEnabled;
    [ObservableProperty] private int _reminderHourIndex;
    [ObservableProperty] private int _remindDaysIndex;

    /// <summary>A problem with the last action (shown under the add form); null when all is well.</summary>
    [ObservableProperty] private string? _message;

    public TrackerViewModel(
        TrackerStore store,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        TrackerReminderService reminders,
        ILogger<TrackerViewModel> logger,
        LinkHub? links = null,
        DebtBook? debts = null)
    {
        _store = store;
        _links = links;
        _debts = debts;
        _settings = settings.Get<TrackerSettings>(TrackerIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _reminders = reminders;
        _logger = logger;

        _loading = true;
        var s = _settings.Current;
        ShowCompleted = s.ShowCompleted;
        ReportRangeIndex = (int)s.ReportRange;
        ReportAllWorkspaces = s.ReportAllWorkspaces;
        RemindersEnabled = s.RemindersEnabled;
        ReminderHourIndex = Math.Clamp(s.ReminderHour, 0, 23);
        RemindDaysIndex = Math.Clamp(s.RemindDaysBefore, 0, RemindDaysNames.Count - 1);
        WidgetBackgroundIndex = (int)s.WidgetBackground;
        WidgetOpacity = Math.Clamp(s.WidgetOpacity, 0, 100);
        WidgetTextIndex = (int)s.WidgetText;
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        // The calendar shows when debts are due.
        if (_debts is not null) _debts.Changed += (_, _) => ScheduleRefresh();
        if (_links is not null) _links.Changed += (_, _) => ScheduleLinksRefresh();
        Refresh();
    }

    // ---- Links to notes ------------------------------------------------------------------------------------------

    /// <summary>True when notes can be linked (the Notes tool is there).</summary>
    public bool CanLink => _links?.Provider(LinkKinds.Note) is not null;

    /// <summary>The notes of a task (not of a subtask: those belong to their task).</summary>
    internal LinksViewModel? LinksFor(TrackerItemViewModel row) =>
        !CanLink || row.IsSubtask ? null
            : new LinksViewModel(_links!, new LinkRef(LinkKinds.Task, TaskLinkProvider.LinkId(row.Id, row.Item)), [LinkKinds.Note], () => row.Item.Title);

    /// <summary>Shows a task, e.g. from a note linked to it: its list, with the row open.</summary>
    public void ShowItem(string workspaceId, string itemId)
    {
        ShowWorkspace(workspaceId);
        if (_rows.TryGetValue(itemId, out var row)) row.IsExpanded = true;
    }

    /// <summary>Shows a person of Wallet's debt book (a debt due on the calendar).</summary>
    private void ShowPerson(string key) => _links?.Provider(LinkKinds.Person)?.Open(key);

    private void ScheduleLinksRefresh()
    {
        if (Interlocked.Exchange(ref _linksQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _linksQueued, 0);
            foreach (var row in _rows.Values) row.RefreshLinks();
        });
    }

    public ObservableCollection<WorkspaceOption> Workspaces { get; } = [];
    public ObservableCollection<TrackerItemViewModel> OpenItems { get; } = [];
    public ObservableCollection<TrackerItemViewModel> CompletedItems { get; } = [];

    public ObservableCollection<ReportBar> ReportDays { get; } = [];
    public ObservableCollection<ReportCount> ReportPriorities { get; } = [];

    /// <summary>Repeating tasks and how many of their days were done in the report's range.</summary>
    public ObservableCollection<ReportCount> ReportRepeats { get; } = [];

    public bool HasReportRepeats => ReportRepeats.Count > 0;
    public ObservableCollection<HistoryRow> History { get; } = [];

    public IReadOnlyList<string> PriorityNames { get; } = Enum.GetValues<TrackerPriority>().Select(TrackerFormat.Priority).ToList();

    /// <summary>Index 0: once, 1: every day for ever, 2: every day for a number of days.</summary>
    public IReadOnlyList<string> RepeatNames { get; } = ["Does not repeat", "Every day", "Every day, for a number of days"];

    public bool NewRepeatsForDays => NewRepeatIndex == 2;

    partial void OnNewRepeatIndexChanged(int value) => OnPropertyChanged(nameof(NewRepeatsForDays));

    /// <summary>Repeat choice as typed: (repeat?, last day or null for ever); false when the number of days is not a number.</summary>
    private bool TryRepeat(int index, string days, DateOnly firstDay, out bool repeat, out DateOnly? until)
    {
        repeat = index > 0;
        until = null;
        if (index != 2) return true;
        if (!int.TryParse(days.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n) || n < 1 || n > 3650)
        {
            Message = $"\u201c{days}\u201d is not a number of days. Try 7 or 30.";
            return false;
        }
        until = firstDay.AddDays(n - 1);
        return true;
    }
    public IReadOnlyList<string> ReportRangeNames { get; } = ["Last 7 days", "Last 30 days", "Last 90 days", "All time"];
    public IReadOnlyList<string> WidgetBackgroundNames { get; } = WidgetLook.BackgroundNames;
    public IReadOnlyList<string> WidgetTextNames { get; } = WidgetLook.TextNames;

    /// <summary>Raised after the widget's look changed, so the platform redraws its widgets.</summary>
    public event EventHandler? WidgetStyleChanged;

    partial void OnWidgetBackgroundIndexChanged(int oldValue, int newValue)
    {
        if (_loading || newValue < 0) return;
        var background = (WidgetBackground)Math.Clamp(newValue, 0, WidgetBackgroundNames.Count - 1);
        var opacity = WidgetLook.OpacityAfter((WidgetBackground)Math.Max(oldValue, 0), background, WidgetOpacity);
        _settings.Update(s =>
        {
            s.WidgetBackground = background;
            s.WidgetOpacity = opacity;
        });
        if (opacity != WidgetOpacity)
        {
            _loading = true;
            WidgetOpacity = opacity;
            _loading = false;
        }
        WidgetStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnWidgetOpacityChanged(int value)
    {
        if (_loading) return;
        _settings.Update(s => s.WidgetOpacity = Math.Clamp(value, 0, 100));
        WidgetStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnWidgetTextIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        _settings.Update(s => s.WidgetText = (WidgetText)Math.Clamp(value, 0, WidgetTextNames.Count - 1));
        WidgetStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"00:00" … "23:00" in the current culture's short time format; the index is the hour.</summary>
    public IReadOnlyList<string> ReminderHourNames { get; } =
        Enumerable.Range(0, 24).Select(h => new DateTime(2000, 1, 1, h, 0, 0).ToString("t", CultureInfo.CurrentCulture)).ToList();

    /// <summary>Index = days before the due date.</summary>
    public IReadOnlyList<string> RemindDaysNames { get; } = ["On the due date", "1 day before", "2 days before", "3 days before"];

    public bool HasWorkspaces => Workspaces.Count > 0;
    public bool HasNoWorkspaces => Workspaces.Count == 0;

    /// <summary>The lists, the report and the history (not while the calendar is shown).</summary>
    public bool ShowListsView => HasWorkspaces && !IsCalendar;

    public bool ShowCalendarView => HasWorkspaces && IsCalendar;

    public bool IsTaskWorkspace => SelectedWorkspace is { Kind: WorkspaceKind.Tasks };
    public bool HasOpen => OpenItems.Count > 0;
    public bool HasNoOpen => SelectedWorkspace is not null && !HasOpen;
    public bool HasCompleted => CompletedItems.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasReportData => ReportDays.Any(d => d.Count > 0);


    // ---- Workspaces ----------------------------------------------------------------------------------------------

    [RelayCommand]
    private void CreateWorkspace()
    {
        var name = NewWorkspaceName.Trim();
        CreateAndSelect(name.Length == 0 ? "To-do" : name);
        NewWorkspaceName = "";
    }

    /// <summary>Shows a workspace, e.g. the one of an item picked in the command palette.</summary>
    public void ShowWorkspace(string workspaceId)
    {
        if (Workspaces.FirstOrDefault(w => w.Id == workspaceId) is { } ws && !ReferenceEquals(ws, SelectedWorkspace)) SelectedWorkspace = ws;
    }

    [RelayCommand]
    private void CreateTasksWorkspace() => CreateAndSelect("To-do");

    [RelayCommand]
    private async Task DeleteWorkspaceAsync()
    {
        if (SelectedWorkspace is not { } ws) return;
        var count = _store.Items(ws.Id).Count;
        var ok = await _dialogs.ConfirmAsync(
            $"Delete “{ws.Name}”?",
            count == 0
                ? "The workspace will be removed from all your devices. Its history stays in the reports."
                : $"The workspace and its {count} item{(count == 1 ? "" : "s")} will be removed from all your devices. Their history stays in the reports.",
            "Delete").ConfigureAwait(true);
        if (!ok) return;
        Try(() => _store.DeleteWorkspace(ws.Id));
    }

    partial void OnSelectedWorkspaceChanged(WorkspaceOption? value)
    {
        if (_loading) return;
        _settings.Update(s => s.SelectedWorkspaceId = value?.Id);
        LoadWorkspaceFields();
        RefreshItems();
        RefreshReport();
        OnPropertyChanged(nameof(IsTaskWorkspace));
        AddCommand.NotifyCanExecuteChanged();
    }

    partial void OnWorkspaceNameChanged(string value)
    {
        if (_loading || SelectedWorkspace is not { } ws || string.IsNullOrWhiteSpace(value)) return;
        Try(() => _store.UpdateWorkspace(ws.Id, w => w with { Name = value }));
    }

    // ---- Items ---------------------------------------------------------------------------------------------------

    private bool CanAdd() => SelectedWorkspace is not null && NewTitle.Trim().Length > 0;

    partial void OnNewDueTimeTextChanged(string value)
    {
        if (_syncingTime || !TrackerFormat.TryParseTime(value, out var time)) return;
        _syncingTime = true;
        NewDueTime = time;
        _syncingTime = false;
    }

    partial void OnNewDueTimeChanged(TimeSpan? value)
    {
        if (_syncingTime) return;
        _syncingTime = true;
        NewDueTimeText = TrackerFormat.Time(value);
        _syncingTime = false;
    }

    /// <summary>The due time typed in the add form, or null (a date without a time is due at the end of that day).</summary>
    private bool TryNewDue(out DateTimeOffset? due)
    {
        due = null;
        if (!TrackerFormat.TryParseTime(NewDueTimeText, out var time))
        {
            Message = $"\u201c{NewDueTimeText}\u201d is not a time. Try 14:30 or 9h.";
            return false;
        }
        due = TrackerDue.FromLocal(NewDueDate, time ?? NewDueTime, TimeZoneInfo.Local);
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (SelectedWorkspace is not { } ws) return;
        if (!TryNewDue(out var dueAt)) return;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_store.Now, TimeZoneInfo.Local).DateTime);
        if (!TryRepeat(NewRepeatIndex, NewRepeatDays, today, out var repeat, out var until)) return;
        var draft = new TrackerItemDraft(
            NewTitle,
            (TrackerPriority)Math.Clamp(NewPriorityIndex, 0, PriorityNames.Count - 1),
            NewDueDate is { } day ? DateOnly.FromDateTime(day) : null,
            DueAt: NewDueTime is null && string.IsNullOrWhiteSpace(NewDueTimeText) ? null : dueAt,
            RepeatDaily: repeat,
            RepeatUntil: until);
        if (!Try(() => _store.AddItem(ws.Id, draft))) return;
        NewTitle = "";
        NewDueDate = null;
        NewDueTimeText = "";
        NewRepeatIndex = 0;
        NewPriorityIndex = (int)TrackerPriority.Normal;
    }

    internal void ToggleComplete(TrackerItemViewModel row) =>
        Try(() => { if (row.IsCompleted) _store.Reopen(row.Id); else _store.Complete(row.Id); });

    internal void Start(TrackerItemViewModel row) => Try(() => _store.Start(row.Id));

    internal bool AddSubtask(TrackerItemViewModel parent, string title)
    {
        if (SelectedWorkspace is not { } ws) return false;
        return Try(() => _store.AddItem(ws.Id, new TrackerItemDraft(title, parent.Item.Priority, ParentId: parent.Id)));
    }

    internal void Move(TrackerItemViewModel row, int delta) => Try(() => _store.Move(row.Id, delta));

    internal async Task DeleteAsync(TrackerItemViewModel row)
    {
        var ok = await _dialogs.ConfirmAsync(
            "Delete this task?",
            row.Item.RepeatDaily
                ? $"\u201c{row.Title}\u201d repeats every day. Deleting it removes today's and stops the repeating on all your devices; the days already done stay in the reports."
                : $"\u201c{row.Title}\u201d will be removed from all your devices. Its history stays in the reports.",
            "Delete").ConfigureAwait(true);
        if (ok) Try(() => _store.DeleteItem(row.Id));
    }

    internal bool SaveEdit(TrackerItemViewModel row)
    {
        if (row.EditTitle.Trim().Length == 0)
        {
            Message = "A task needs a title.";
            return false;
        }
        var repeat = row.Item.RepeatDaily;
        var until = row.Item.RepeatUntil;
        if (row.IsRepeating && !TryRepeat(row.EditRepeatIndex, row.EditRepeatDays, row.Item.OccurrenceDate ?? DateOnly.FromDateTime(DateTime.Today), out repeat, out until))
            return false;
        if (row.IsRepeating && !repeat && row.Item.SeriesId is { } series) Try(() => _store.StopRepeating(series));
        return Try(() => _store.UpdateItem(row.Id, item => item with
        {
            Title = row.EditTitle,
            Notes = row.EditNotes,
            Priority = (TrackerPriority)Math.Clamp(row.EditPriorityIndex, 0, PriorityNames.Count - 1),
            DueDate = row.EditDueDate is { } due ? DateOnly.FromDateTime(due) : null,
            DueAt = row.EditDueDate is not null && row.EditDueTime is not null ? TrackerDue.FromLocal(row.EditDueDate, row.EditDueTime, TimeZoneInfo.Local) : null,
            RepeatDaily = repeat,
            RepeatUntil = until,
        }));
    }

    partial void OnShowCompletedChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(s => s.ShowCompleted = value);
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- Report and export ---------------------------------------------------------------------------------------

    partial void OnReportRangeIndexChanged(int value)
    {
        if (_loading) return;
        _settings.Update(s => s.ReportRange = (ReportRange)Math.Clamp(value, 0, ReportRangeNames.Count - 1));
        RefreshReport();
    }

    partial void OnRemindersEnabledChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.RemindersEnabled = value);
    }

    partial void OnReminderHourIndexChanged(int value)
    {
        if (!_loading && value >= 0) _settings.Update(s => s.ReminderHour = Math.Clamp(value, 0, 23));
    }

    partial void OnRemindDaysIndexChanged(int value)
    {
        if (!_loading && value >= 0) _settings.Update(s => s.RemindDaysBefore = Math.Clamp(value, 0, RemindDaysNames.Count - 1));
    }

    /// <summary>Shows today's reminder right away (to try the notification), whatever the hour.</summary>
    [RelayCommand]
    private void RemindNow()
    {
        try
        {
            Message = _reminders.RemindNow() ? null : "Nothing is due soon or overdue, so there is nothing to remind you about.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tracker reminder failed");
            Message = $"Could not show the reminder: {ex.Message}";
        }
    }

    partial void OnReportAllWorkspacesChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(s => s.ReportAllWorkspaces = value);
        RefreshReport();
    }

    /// <summary>Every item of every workspace as CSV (see <see cref="TrackerCsv"/>).</summary>
    public string ItemsCsv() => TrackerCsv.Items(_store.AllItems(), WorkspaceMap());

    /// <summary>The whole history as CSV, one row per event.</summary>
    public string HistoryCsv() => TrackerCsv.History(_store.History(), WorkspaceMap());

    [RelayCommand]
    private void CopyItemsCsv()
    {
        _clipboard.SetText(ItemsCsv());
        Message = "All items copied as CSV. Paste them into a spreadsheet.";
    }

    [RelayCommand]
    private void CopyHistoryCsv()
    {
        _clipboard.SetText(HistoryCsv());
        Message = "The history was copied as CSV. Paste it into a spreadsheet.";
    }

    /// <summary>For pages that write the file themselves (e.g. a save dialog on Windows).</summary>
    public void ReportExport(string what, string path) => Message = $"{what} saved to {path}.";

    public void ReportExportFailure(Exception ex)
    {
        _logger.LogWarning(ex, "Tracker export failed");
        Message = $"Could not save the file: {ex.Message}";
    }

    // ---- Refresh -------------------------------------------------------------------------------------------------

    /// <summary>Coalesces bursts of store changes (a sync can touch hundreds of records) into one UI refresh.</summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        try
        {
            // A new day: today's repeating tasks appear (a no-op when they are already there).
            _store.EnsureRepeats();
            RefreshWorkspaces();
            RefreshItems();
            RefreshReport();
            RefreshHistory();
            RefreshCalendar();
        }
        catch (Exception ex)
        {
            // Store reads hit SQLite; a failure must never take the app down.
            _logger.LogError(ex, "Tracker refresh failed");
            Message = $"Could not load the tracker: {ex.Message}";
        }
    }

    private void RefreshWorkspaces()
    {
        var workspaces = _store.Workspaces();
        var byId = Workspaces.ToDictionary(w => w.Id, StringComparer.Ordinal);
        var wanted = new List<WorkspaceOption>();
        foreach (var (id, ws, _) in workspaces)
        {
            if (byId.TryGetValue(id, out var option))
            {
                option.Name = ws.Name;
                option.Kind = ws.Kind;
            }
            else
            {
                option = new WorkspaceOption(id, ws.Name, ws.Kind);
            }
            wanted.Add(option);
        }

        _loading = true;
        try
        {
            var selectedId = SelectedWorkspace?.Id ?? _settings.Current.SelectedWorkspaceId;
            Reconcile(Workspaces, wanted);
            var selected = wanted.FirstOrDefault(w => w.Id == selectedId) ?? wanted.FirstOrDefault();
            if (!ReferenceEquals(selected, SelectedWorkspace)) SelectedWorkspace = selected;
            if (selected?.Id != _settings.Current.SelectedWorkspaceId) _settings.Update(s => s.SelectedWorkspaceId = selected?.Id);
        }
        finally
        {
            _loading = false;
        }
        LoadWorkspaceFields();
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(HasNoWorkspaces));
        OnPropertyChanged(nameof(ShowListsView));
        OnPropertyChanged(nameof(ShowCalendarView));
        OnPropertyChanged(nameof(IsTaskWorkspace));
        AddCommand.NotifyCanExecuteChanged();
    }

    private void LoadWorkspaceFields()
    {
        _loading = true;
        var ws = SelectedWorkspace is { } s ? _store.GetWorkspace(s.Id) : null;
        WorkspaceName = ws?.Name ?? "";
        _loading = false;
    }

    private void RefreshItems()
    {
        var ws = SelectedWorkspace is { } s ? _store.GetWorkspace(s.Id) : null;
        if (ws is null || SelectedWorkspace is null)
        {
            _rows.Clear();
            Reconcile(OpenItems, []);
            Reconcile(CompletedItems, []);
        }
        else
        {
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_store.Now, TimeZoneInfo.Local).DateTime);
            var items = _store.Items(SelectedWorkspace.Id);
            var open = TrackerOrdering.Open(items);
            var done = TrackerOrdering.Completed(items).Take(CompletedLimit).ToList();

            var live = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
            foreach (var stale in _rows.Keys.Where(k => !live.Contains(k)).ToList()) _rows.Remove(stale);

            var openRows = open.Select(i => Row(i.Id, i.Value, ws, today)).ToList();
            foreach (var row in openRows)
                row.SetSubtasks(_store.Subtasks(row.Id).Select(sub => Row(sub.Id, sub.Value, ws, today)).ToList());
            for (var i = 0; i < openRows.Count; i++)
            {
                openRows[i].CanMoveUp = i > 0 && open[i - 1].Value.Priority == open[i].Value.Priority;
                openRows[i].CanMoveDown = i < open.Count - 1 && open[i + 1].Value.Priority == open[i].Value.Priority;
            }
            Reconcile(OpenItems, openRows);
            var doneRows = done.Select(i => Row(i.Id, i.Value, ws, today)).ToList();
            foreach (var row in doneRows)
                row.SetSubtasks(_store.Subtasks(row.Id).Select(sub => Row(sub.Id, sub.Value, ws, today)).ToList());
            Reconcile(CompletedItems, doneRows);

            var started = open.Count(i => i.Value.StartedExplicitly);
            OpenSummary = started > 0 ? $"{open.Count} open · {started} in progress" : $"{open.Count} open";

        }
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(HasNoOpen));
        OnPropertyChanged(nameof(HasCompleted));
    }

    /// <summary>Re-reads every row's secondary line so "just now" becomes "1 min ago" without a reload.</summary>
    public void RefreshDetails()
    {
        foreach (var row in OpenItems) row.NotifyDetails();
        foreach (var row in CompletedItems) row.NotifyDetails();
    }

    private TrackerItemViewModel Row(string id, TrackerItem item, TrackerWorkspace ws, DateOnly today)
    {
        var completions = item.SeriesId is { } series ? _store.SeriesCompletions(series) : 0;
        if (_rows.TryGetValue(id, out var row))
        {
            var changed = row.Completions != completions;
            row.Completions = completions;
            row.Update(item, ws, today);
            if (changed) row.NotifyDetails();
            return row;
        }
        row = new TrackerItemViewModel(this, id, item, ws, today) { Completions = completions };
        _rows[id] = row;
        return row;
    }

    private void RefreshReport()
    {
        var range = (ReportRange)Math.Clamp(ReportRangeIndex, 0, ReportRangeNames.Count - 1);
        var workspaceId = ReportAllWorkspaces ? null : SelectedWorkspace?.Id;
        var report = TrackerReport.Build(_store.History(), range, _store.Now, TimeZoneInfo.Local, workspaceId);

        ReportCompleted = report.CompletedCount.ToString(CultureInfo.CurrentCulture);
        ReportCreated = report.CreatedCount.ToString(CultureInfo.CurrentCulture);
        ReportAverageLead = TrackerFormat.Duration(report.AverageLeadTime);
        ReportMedianLead = TrackerFormat.Duration(report.MedianLeadTime);
        ReportAverageWork = TrackerFormat.Duration(report.AverageWorkTime);
        ReportOnTime = report.OnTimeRate is { } rate ? rate.ToString("P0", CultureInfo.CurrentCulture) : "—";
        ReportRangeText = report.From is { } from
            ? $"{from.ToString("d", CultureInfo.CurrentCulture)} – {report.To.ToString("d", CultureInfo.CurrentCulture)}"
            : $"Until {report.To.ToString("d", CultureInfo.CurrentCulture)}";

        var max = Math.Max(1, report.Days.Count == 0 ? 1 : report.Days.Max(d => d.Count));
        var labelEvery = report.Days.Count <= 7 ? 1 : report.Days.Count <= 31 ? 7 : 14;
        var bars = report.Days.Select((d, i) => new ReportBar(
            Label: (report.Days.Count - 1 - i) % labelEvery == 0
                ? report.Days.Count <= 7 ? d.Date.ToString("ddd", CultureInfo.CurrentCulture) : d.Date.ToString("d MMM", CultureInfo.CurrentCulture)
                : "",
            ToolTip: $"{d.Date.ToString("D", CultureInfo.CurrentCulture)}: {d.Count} completed",
            Count: d.Count,
            BarHeight: d.Count == 0 ? 2 : Math.Max(4, MaxBarHeight * d.Count / max))).ToList();
        Replace(ReportDays, bars);
        Replace(ReportPriorities, Enum.GetValues<TrackerPriority>().OrderByDescending(p => p)
            .Select(p => new ReportCount(TrackerFormat.Priority(p), report.ByPriority.GetValueOrDefault(p))));
        Replace(ReportRepeats, report.Repeats.Select(r => new ReportCount(r.Title, r.Count)));
        OnPropertyChanged(nameof(HasReportRepeats));
        OnPropertyChanged(nameof(HasReportData));
    }

    private void RefreshHistory()
    {
        var names = WorkspaceMap();
        var rows = _store.History()
            .Where(e => e.WorkspaceKind == WorkspaceKind.Tasks)
            .Reverse()
            .Take(HistoryLimit)
            .Select(e => new HistoryRow(
                TrackerFormat.When(e.At),
                Describe(e),
                names.GetValueOrDefault(e.WorkspaceId)?.Name ?? "(deleted workspace)",
                e.Kind));
        Replace(History, rows);
        OnPropertyChanged(nameof(HasHistory));
    }

    private static string Describe(TrackerEvent e)
    {
        var what = e.Title;
        return e.Kind switch
        {
            TrackerEventKind.Created => $"Added “{what}”",
            TrackerEventKind.Started => $"Started “{what}”",
            TrackerEventKind.Completed => $"Completed “{what}” in {TrackerFormat.Duration((e.CompletedAt ?? e.At) - e.CreatedAt)}",
            TrackerEventKind.Reopened => $"Reopened “{what}”",
            TrackerEventKind.Deleted => $"Deleted “{what}”",
            _ => what,
        };
    }

    private void CreateAndSelect(string name)
    {
        string? id = null;
        if (!Try(() => id = _store.AddWorkspace(name))) return;
        Refresh();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == id) ?? SelectedWorkspace;
    }

    private Dictionary<string, TrackerWorkspace> WorkspaceMap() =>
        _store.Workspaces().ToDictionary(w => w.Id, w => w.Value, StringComparer.Ordinal);

    /// <summary>Runs a store action; problems become <see cref="Message"/> instead of exceptions.</summary>
    private bool Try(Action action)
    {
        try
        {
            action();
            Message = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Message = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tracker action failed");
            Message = $"That did not work: {ex.Message}";
            return false;
        }
    }

    /// <summary>Makes <paramref name="target"/> equal to <paramref name="wanted"/> with minimal moves, keeping instances.</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keep.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], wanted[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], wanted[i])) { existing = j; break; }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, wanted[i]);
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
