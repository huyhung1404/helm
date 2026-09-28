using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
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
    private readonly ILogger<TrackerViewModel> _logger;
    private readonly Dictionary<string, TrackerItemViewModel> _rows = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private bool _loading;

    // Workspace
    [ObservableProperty] private WorkspaceOption? _selectedWorkspace;
    [ObservableProperty] private string _workspaceName = "";
    [ObservableProperty] private string _workspaceCurrency = "";
    [ObservableProperty] private string _newWorkspaceName = "";
    [ObservableProperty] private int _newWorkspaceKindIndex;

    // Add form
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newTitle = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newPerson = "";
    [ObservableProperty] private string _newAmount = "";
    [ObservableProperty] private int _newPriorityIndex = (int)TrackerPriority.Normal;
    [ObservableProperty] private int _newDirectionIndex;
    [ObservableProperty] private DateTime? _newDueDate;

    // Lists, totals, report
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private string _openSummary = "";
    [ObservableProperty] private string _owedToMeText = "";
    [ObservableProperty] private string _iOweText = "";
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private int _reportRangeIndex;
    [ObservableProperty] private bool _reportAllWorkspaces;
    [ObservableProperty] private string _reportCompleted = "0";
    [ObservableProperty] private string _reportCreated = "0";
    [ObservableProperty] private string _reportAverageLead = "—";
    [ObservableProperty] private string _reportMedianLead = "—";
    [ObservableProperty] private string _reportAverageWork = "—";
    [ObservableProperty] private string _reportOnTime = "—";
    [ObservableProperty] private string _reportRangeText = "";

    /// <summary>A problem with the last action (shown under the add form); null when all is well.</summary>
    [ObservableProperty] private string? _message;

    public TrackerViewModel(
        TrackerStore store,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        ILogger<TrackerViewModel> logger)
    {
        _store = store;
        _settings = settings.Get<TrackerSettings>(TrackerIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _logger = logger;

        _loading = true;
        var s = _settings.Current;
        ShowCompleted = s.ShowCompleted;
        ReportRangeIndex = (int)s.ReportRange;
        ReportAllWorkspaces = s.ReportAllWorkspaces;
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        Refresh();
    }

    public ObservableCollection<WorkspaceOption> Workspaces { get; } = [];
    public ObservableCollection<TrackerItemViewModel> OpenItems { get; } = [];
    public ObservableCollection<TrackerItemViewModel> CompletedItems { get; } = [];
    public ObservableCollection<ReportBar> ReportDays { get; } = [];
    public ObservableCollection<ReportCount> ReportPriorities { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];

    public IReadOnlyList<string> PriorityNames { get; } = Enum.GetValues<TrackerPriority>().Select(TrackerFormat.Priority).ToList();
    public IReadOnlyList<string> DirectionNames { get; } = Enum.GetValues<DebtDirection>().Select(TrackerFormat.Direction).ToList();
    public IReadOnlyList<string> KindNames { get; } = Enum.GetValues<WorkspaceKind>().Select(TrackerFormat.Kind).ToList();
    public IReadOnlyList<string> ReportRangeNames { get; } = ["Last 7 days", "Last 30 days", "Last 90 days", "All time"];

    public bool HasWorkspaces => Workspaces.Count > 0;
    public bool HasNoWorkspaces => Workspaces.Count == 0;
    public bool IsDebtWorkspace => SelectedWorkspace?.Kind == WorkspaceKind.Debts;
    public bool IsTaskWorkspace => SelectedWorkspace is { Kind: WorkspaceKind.Tasks };
    public bool HasOpen => OpenItems.Count > 0;
    public bool HasNoOpen => SelectedWorkspace is not null && OpenItems.Count == 0;
    public bool HasCompleted => CompletedItems.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public bool HasReportData => ReportDays.Any(d => d.Count > 0);

    /// <summary>"Owes me" / "Done" wording for the current workspace.</summary>
    public string CompletedHeader => IsDebtWorkspace ? "Settled" : "Completed";
    public string OpenHeader => IsDebtWorkspace ? "Outstanding" : "To do";
    public string AddHeader => IsDebtWorkspace ? "Add a debt" : "Add a task";
    public string TitlePlaceholder => IsDebtWorkspace ? "What it was for (optional)" : "What needs doing?";
    public string EmptyOpenText => IsDebtWorkspace ? "Nothing outstanding. Everyone is square." : "Nothing to do. Add a task above.";
    public string EmptyCompletedText => IsDebtWorkspace
        ? "Nothing settled yet. Tick a debt when it is paid back and it moves here, with the date."
        : "Nothing finished yet. Tick an item and it moves here, with its times recorded.";

    /// <summary>Priorities and working time mean nothing for a debt book; its report shows only counts and times.</summary>
    public bool ShowTaskReport => ReportAllWorkspaces || !IsDebtWorkspace;
    public string ReportCompletedLabel => ShowTaskReport ? "Completed" : "Settled";
    public string ReportLeadLabel => ShowTaskReport ? "Average time from added to done" : "Average time until paid back";
    public string ReportMedianLabel => ShowTaskReport ? "Median time from added to done" : "Median time until paid back";

    // ---- Workspaces ----------------------------------------------------------------------------------------------

    [RelayCommand]
    private void CreateWorkspace()
    {
        var name = NewWorkspaceName.Trim();
        var kind = (WorkspaceKind)Math.Clamp(NewWorkspaceKindIndex, 0, KindNames.Count - 1);
        if (name.Length == 0) name = kind == WorkspaceKind.Debts ? "Debts" : "To-do";
        CreateAndSelect(name, kind);
        NewWorkspaceName = "";
    }

    [RelayCommand]
    private void CreateTasksWorkspace() => CreateAndSelect("To-do", WorkspaceKind.Tasks);

    [RelayCommand]
    private void CreateDebtWorkspace() => CreateAndSelect("Debts", WorkspaceKind.Debts);

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
        OnWorkspaceKindChanged();
    }

    partial void OnWorkspaceNameChanged(string value)
    {
        if (_loading || SelectedWorkspace is not { } ws || string.IsNullOrWhiteSpace(value)) return;
        Try(() => _store.UpdateWorkspace(ws.Id, w => w with { Name = value }));
    }

    partial void OnWorkspaceCurrencyChanged(string value)
    {
        if (_loading || SelectedWorkspace is not { } ws) return;
        Try(() => _store.UpdateWorkspace(ws.Id, w => w with { Currency = value }));
    }

    // ---- Items ---------------------------------------------------------------------------------------------------

    private bool CanAdd() => SelectedWorkspace is not null
        && (NewTitle.Trim().Length > 0 || (IsDebtWorkspace && NewPerson.Trim().Length > 0));

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (SelectedWorkspace is not { } ws) return;
        decimal amount = 0;
        if (ws.Kind == WorkspaceKind.Debts && !string.IsNullOrWhiteSpace(NewAmount) && !TrackerFormat.TryParseAmount(NewAmount, out amount))
        {
            Message = $"“{NewAmount}” is not an amount. Try 150000, 150,000 or 150k.";
            return;
        }
        var draft = new TrackerItemDraft(
            NewTitle,
            (TrackerPriority)Math.Clamp(NewPriorityIndex, 0, PriorityNames.Count - 1),
            NewDueDate is { } due ? DateOnly.FromDateTime(due) : null,
            Person: NewPerson,
            Amount: amount,
            Direction: (DebtDirection)Math.Clamp(NewDirectionIndex, 0, DirectionNames.Count - 1));
        if (Try(() => _store.AddItem(ws.Id, draft)))
        {
            NewTitle = "";
            NewPerson = "";
            NewAmount = "";
            NewDueDate = null;
            NewPriorityIndex = (int)TrackerPriority.Normal;
        }
    }

    internal void ToggleComplete(TrackerItemViewModel row) =>
        Try(() => { if (row.IsCompleted) _store.Reopen(row.Id); else _store.Complete(row.Id); });

    internal void Start(TrackerItemViewModel row) => Try(() => _store.Start(row.Id));

    internal void Move(TrackerItemViewModel row, int delta) => Try(() => _store.Move(row.Id, delta));

    internal async Task DeleteAsync(TrackerItemViewModel row)
    {
        var ok = await _dialogs.ConfirmAsync(
            row.IsDebt ? "Delete this debt?" : "Delete this task?",
            $"“{row.Title}” will be removed from all your devices. Its history stays in the reports.",
            "Delete").ConfigureAwait(true);
        if (ok) Try(() => _store.DeleteItem(row.Id));
    }

    internal bool SaveEdit(TrackerItemViewModel row)
    {
        decimal amount = row.Item.Amount;
        if (row.IsDebt && !TrackerFormat.TryParseAmount(row.EditAmount, out amount))
        {
            if (!string.IsNullOrWhiteSpace(row.EditAmount))
            {
                Message = $"“{row.EditAmount}” is not an amount. Try 150000, 150,000 or 150k.";
                return false;
            }
            amount = 0;
        }
        if (!row.IsDebt && row.EditTitle.Trim().Length == 0)
        {
            Message = "A task needs a title.";
            return false;
        }
        return Try(() => _store.UpdateItem(row.Id, item => item with
        {
            Title = row.EditTitle,
            Notes = row.EditNotes,
            Priority = (TrackerPriority)Math.Clamp(row.EditPriorityIndex, 0, PriorityNames.Count - 1),
            DueDate = row.EditDueDate is { } due ? DateOnly.FromDateTime(due) : null,
            Person = row.EditPerson,
            Amount = amount,
            Direction = (DebtDirection)Math.Clamp(row.EditDirectionIndex, 0, DirectionNames.Count - 1),
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

    partial void OnReportAllWorkspacesChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(s => s.ReportAllWorkspaces = value);
        OnReportScopeChanged();
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
            RefreshWorkspaces();
            RefreshItems();
            RefreshReport();
            RefreshHistory();
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
        OnWorkspaceKindChanged();
    }

    private void LoadWorkspaceFields()
    {
        _loading = true;
        var ws = SelectedWorkspace is { } s ? _store.GetWorkspace(s.Id) : null;
        WorkspaceName = ws?.Name ?? "";
        WorkspaceCurrency = ws?.Currency ?? "";
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
            for (var i = 0; i < openRows.Count; i++)
            {
                openRows[i].CanMoveUp = i > 0 && open[i - 1].Value.Priority == open[i].Value.Priority;
                openRows[i].CanMoveDown = i < open.Count - 1 && open[i + 1].Value.Priority == open[i].Value.Priority;
            }
            Reconcile(OpenItems, openRows);
            Reconcile(CompletedItems, done.Select(i => Row(i.Id, i.Value, ws, today)).ToList());

            var started = open.Count(i => i.Value.StartedExplicitly);
            OpenSummary = ws.Kind == WorkspaceKind.Debts
                ? $"{open.Count} outstanding"
                : started > 0 ? $"{open.Count} open · {started} in progress" : $"{open.Count} open";

            if (ws.Kind == WorkspaceKind.Debts)
            {
                var totals = DebtTotals.From(items.Select(i => i.Value));
                OwedToMeText = TrackerFormat.Money(totals.OwedToMe, ws.Currency);
                IOweText = TrackerFormat.Money(totals.IOwe, ws.Currency);
                NetText = (totals.Net < 0 ? "−" : totals.Net > 0 ? "+" : "") + TrackerFormat.Money(Math.Abs(totals.Net), ws.Currency);
            }
        }
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(HasNoOpen));
        OnPropertyChanged(nameof(HasCompleted));
    }

    private TrackerItemViewModel Row(string id, TrackerItem item, TrackerWorkspace ws, DateOnly today)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            row.Update(item, ws, today);
            return row;
        }
        row = new TrackerItemViewModel(this, id, item, ws, today);
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
        OnPropertyChanged(nameof(HasReportData));
    }

    private void RefreshHistory()
    {
        var names = WorkspaceMap();
        var rows = _store.History()
            .AsEnumerable()
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
        var debt = e.WorkspaceKind == WorkspaceKind.Debts;
        var what = debt
            ? string.Join(" — ", new[] { e.Person, e.Title }.Where(s => s.Length > 0))
            : e.Title;
        return e.Kind switch
        {
            TrackerEventKind.Created => $"Added “{what}”",
            TrackerEventKind.Started => $"Started “{what}”",
            TrackerEventKind.Completed when debt => $"Settled “{what}”",
            TrackerEventKind.Completed => $"Completed “{what}” in {TrackerFormat.Duration((e.CompletedAt ?? e.At) - e.CreatedAt)}",
            TrackerEventKind.Reopened => $"Reopened “{what}”",
            TrackerEventKind.Deleted => $"Deleted “{what}”",
            _ => what,
        };
    }

    private void OnWorkspaceKindChanged()
    {
        OnPropertyChanged(nameof(IsDebtWorkspace));
        OnPropertyChanged(nameof(IsTaskWorkspace));
        OnPropertyChanged(nameof(CompletedHeader));
        OnPropertyChanged(nameof(OpenHeader));
        OnPropertyChanged(nameof(AddHeader));
        OnPropertyChanged(nameof(TitlePlaceholder));
        OnPropertyChanged(nameof(EmptyOpenText));
        OnPropertyChanged(nameof(EmptyCompletedText));
        OnReportScopeChanged();
        AddCommand.NotifyCanExecuteChanged();
    }

    private void OnReportScopeChanged()
    {
        OnPropertyChanged(nameof(ShowTaskReport));
        OnPropertyChanged(nameof(ReportCompletedLabel));
        OnPropertyChanged(nameof(ReportLeadLabel));
        OnPropertyChanged(nameof(ReportMedianLabel));
    }

    private void CreateAndSelect(string name, WorkspaceKind kind)
    {
        string? id = null;
        if (!Try(() => id = _store.AddWorkspace(name, kind, kind == WorkspaceKind.Debts ? DefaultCurrency() : ""))) return;
        Refresh();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == id) ?? SelectedWorkspace;
    }

    /// <summary>
    /// The currency of the region set in the OS ("₫" in Vietnam, even with an English display language), or nothing
    /// if unknown.
    /// </summary>
    private static string DefaultCurrency()
    {
        try { return RegionInfo.CurrentRegion.CurrencySymbol; }
        catch (ArgumentException) { return ""; }
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
