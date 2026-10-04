using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Wallet;

/// <summary>
/// The Wallet pages, shared by the Windows and Android apps: the month and its numbers, the transactions waiting for a
/// category, the spending by category, the month's transactions, adding one by hand, and the settings both apps have.
/// All members are used on the UI thread; store changes are posted through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class WalletViewModel : ObservableObject
{
    /// <summary>Transactions to categorize listed at once (the rest wait their turn).</summary>
    public const int ToCategorizeShown = 20;

    private const int SuggestionCount = 4;

    private readonly WalletStore _store;
    private readonly WalletCapture _capture;
    private readonly ISettingsStore<WalletSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IWalletPlatform? _platform;
    private readonly ILogger<WalletViewModel> _logger;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, TransactionRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CategoryEditRow> _categoryRows = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private bool _loading;
    private DateOnly _month;
    private IReadOnlyList<string> _similarIds = [];
    private string? _similarCategory;

    [ObservableProperty] private string _monthTitle = "";
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private bool _isCurrentMonth;

    /// <summary>A problem or a notice about the last action; null when there is nothing to say.</summary>
    [ObservableProperty] private string? _message;

    // The month in numbers
    [ObservableProperty] private string _spentText = "";
    [ObservableProperty] private string _incomeText = "";
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private bool _isNetNegative;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string _compareText = "";
    [ObservableProperty] private string _spentTodayText = "";

    // The budget
    [ObservableProperty] private bool _hasBudget;
    [ObservableProperty] private double _budgetPercent;
    [ObservableProperty] private bool _isOverBudget;
    [ObservableProperty] private string _budgetText = "";
    [ObservableProperty] private string _budgetDetail = "";

    // To categorize
    [ObservableProperty] private string _toCategorizeTitle = "";
    [ObservableProperty] private string _moreToCategorizeText = "";
    [ObservableProperty] private string _similarText = "";

    // The month's transactions
    [ObservableProperty] private int _filterIndex;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _emptyText = "";

    // Adding a transaction by hand
    [ObservableProperty] private bool _isAddOpen;
    [ObservableProperty] private string _newAmount = "";
    [ObservableProperty] private bool _newIsIncome;
    [ObservableProperty] private string _newDescription = "";
    [ObservableProperty] private DateTime? _newDate;
    [ObservableProperty] private CategoryChoice? _newCategory;

    /// <summary>The time of day (Android's time picker); <see cref="NewTimeText"/> is the same as typed text (PC).</summary>
    [ObservableProperty] private TimeSpan? _newTime;
    [ObservableProperty] private string _newTimeText = "";

    /// <summary>A category to make for this transaction instead of picking one.</summary>
    [ObservableProperty] private string _addCategoryName = "";

    // Settings
    [ObservableProperty] private string _monthlyBudgetText = "";
    [ObservableProperty] private bool _listenEnabled;
    [ObservableProperty] private bool _techcombankEnabled;
    [ObservableProperty] private bool _acbEnabled;
    [ObservableProperty] private bool _askForCategory;
    [ObservableProperty] private bool _autoCategorize;
    [ObservableProperty] private bool _hasNotificationAccess;
    [ObservableProperty] private string _newCategoryName = "";
    [ObservableProperty] private int _newCategoryKindIndex;
    [ObservableProperty] private string _testText = "";
    [ObservableProperty] private int _testBankIndex;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testOk;

    public WalletViewModel(
        WalletStore store,
        WalletCapture capture,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        IServiceProvider services,
        ILogger<WalletViewModel> logger)
        // The phone's notification access and widget; none on Windows.
        : this(store, capture, settings, ui, dialogs, clipboard, services.GetService(typeof(IWalletPlatform)) as IWalletPlatform, logger, TimeZoneInfo.Local)
    {
    }

    internal WalletViewModel(
        WalletStore store,
        WalletCapture capture,
        ISettingsStoreFactory settings,
        IUiDispatcher ui,
        IDialogService dialogs,
        IClipboardService clipboard,
        IWalletPlatform? platform,
        ILogger<WalletViewModel> logger,
        TimeZoneInfo zone)
    {
        _store = store;
        _capture = capture;
        _settings = settings.Get<WalletSettings>(WalletIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        _platform = platform;
        _logger = logger;
        _zone = zone;
        _month = WalletFormat.MonthOf(store.Now, zone);

        LoadSettings();
        _store.Changed += (_, _) => ScheduleRefresh();
        // Unread notifications arrive from the listener; toggles may change on the other page.
        _settings.Changed += (_, _) => _ui.Post(() =>
        {
            LoadSettings();
            RefreshUnread();
        });
        Refresh();
    }

    public static IReadOnlyList<string> FilterNames { get; } = ["All", "Spending", "Money in"];

    public static IReadOnlyList<string> KindNames { get; } = ["Spending", "Money in", "Between my accounts"];

    public static IReadOnlyList<string> TestBankNames { get; } = BankSources.All.Select(b => b.Name).ToList();

    public static string KindName(CategoryKind kind) => kind switch
    {
        CategoryKind.Expense => "Spending",
        CategoryKind.Income => "Money in",
        _ => "Between my accounts",
    };

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    /// <summary>The month's spending by category: the donut and its legend (largest first, at most six plus Other).</summary>
    [ObservableProperty] private DonutModel _spendingDonut = DonutModel.Empty;

    /// <summary>The month's money in, spent and net as bars on one scale.</summary>
    [ObservableProperty] private IReadOnlyList<CashFlowRow> _cashFlow = [];

    /// <summary>What was spent each day of the month, with the budget per day.</summary>
    [ObservableProperty] private BarChartModel _dailyChart = BarChartModel.Empty;

    /// <summary>The last six months' spending, the month shown in the accent; a click opens a month.</summary>
    [ObservableProperty] private BarChartModel _monthsChart = BarChartModel.Empty;

    /// <summary>How far into the month today is (0–100), the budget meter's "on track" mark; -1 outside the current month.</summary>
    [ObservableProperty] private double _budgetPacePercent = -1;

    /// <summary>The money in by category, largest first.</summary>
    public ObservableCollection<CategorySpendRow> Earning { get; } = [];

    public ObservableCollection<TransactionRow> ToCategorize { get; } = [];

    public ObservableCollection<DayGroup> Days { get; } = [];

    /// <summary>Categories for a transaction typed by hand (in its direction).</summary>
    public ObservableCollection<CategoryChoice> NewCategories { get; } = [];

    public ObservableCollection<CategoryEditRow> CategoryRows { get; } = [];

    public ObservableCollection<UnreadRow> Unread { get; } = [];

    public bool HasSpending => !SpendingDonut.IsEmpty;

    public bool HasDailyChart => !DailyChart.IsEmpty;

    public bool HasMonthsChart => !MonthsChart.IsEmpty;

    partial void OnSpendingDonutChanged(DonutModel value) => OnPropertyChanged(nameof(HasSpending));

    partial void OnDailyChartChanged(BarChartModel value) => OnPropertyChanged(nameof(HasDailyChart));

    partial void OnMonthsChartChanged(BarChartModel value) => OnPropertyChanged(nameof(HasMonthsChart));

    public bool HasEarning => Earning.Count > 0;

    public bool HasToCategorize => ToCategorize.Count > 0;

    public bool HasMoreToCategorize => MoreToCategorizeText.Length > 0;

    public bool HasSimilar => SimilarText.Length > 0;

    public bool HasDays => Days.Count > 0;

    public bool HasUnread => Unread.Count > 0;

    public bool HasSpentToday => SpentTodayText.Length > 0;

    public bool HasCompare => CompareText.Length > 0;

    public bool HasTestResult => TestResult.Length > 0;

    /// <summary>Whether this device reads bank notifications (the phone); the PC only shows what the phone saved.</summary>
    public bool CanListen => _platform is not null;

    public bool CanPinWidget => _platform?.CanPinWidget == true;

    /// <summary>Notification access is missing while listening is on: the page asks for it first.</summary>
    public bool NeedsAccess => CanListen && ListenEnabled && !HasNotificationAccess;

    public string AccessText => HasNotificationAccess
        ? "Helm can read notifications. New transactions from your banks are saved by themselves."
        : "Helm cannot read notifications yet. Turn Helm on in Notification access.";

    partial void OnSimilarTextChanged(string value) => OnPropertyChanged(nameof(HasSimilar));

    partial void OnMoreToCategorizeTextChanged(string value) => OnPropertyChanged(nameof(HasMoreToCategorize));

    partial void OnSpentTodayTextChanged(string value) => OnPropertyChanged(nameof(HasSpentToday));

    partial void OnCompareTextChanged(string value) => OnPropertyChanged(nameof(HasCompare));

    partial void OnTestResultChanged(string value) => OnPropertyChanged(nameof(HasTestResult));

    // ---- The month -----------------------------------------------------------------------------------------------

    [RelayCommand]
    private void PreviousMonth()
    {
        _month = _month.AddMonths(-1);
        Refresh();
    }

    [RelayCommand]
    private void NextMonth()
    {
        if (!CanGoNext) return;
        _month = _month.AddMonths(1);
        Refresh();
    }

    [RelayCommand]
    private void ThisMonth()
    {
        _month = WalletFormat.MonthOf(_store.Now, _zone);
        Refresh();
    }

    partial void OnFilterIndexChanged(int value) => RefreshDays(Categories());

    partial void OnSearchTextChanged(string value) => RefreshDays(Categories());

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- Categorizing --------------------------------------------------------------------------------------------

    internal void Categorize(TransactionRow row, string? categoryId)
    {
        try
        {
            if (!_store.SetCategory(row.Id, categoryId)) return;
            row.IsExpanded = false;
            // "The same for similar ones": other transactions to categorize with a description like this one.
            var similar = categoryId is null ? [] : _store.SimilarUncategorized(row.Id);
            _similarIds = similar;
            _similarCategory = categoryId;
            SimilarText = similar.Count == 0 || _store.Category(categoryId!) is not { } c
                ? ""
                : $"Put {WalletFormat.Count(similar.Count, "similar transaction")} in {c.Name} too?";
            Refresh();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Report(ex, "Could not change the category.");
        }
    }

    [RelayCommand]
    private void ApplySimilar()
    {
        if (_similarCategory is not { } category) return;
        foreach (var id in _similarIds) _store.SetCategory(id, category);
        DismissSimilar();
        Refresh();
    }

    [RelayCommand]
    private void DismissSimilar()
    {
        _similarIds = [];
        _similarCategory = null;
        SimilarText = "";
    }

    internal bool SaveEdit(TransactionRow row)
    {
        if (!WalletText.TryParseAmount(row.EditAmount, out var amount))
        {
            Message = "Enter the amount, e.g. 50000, 50k or 1.5tr.";
            return false;
        }
        if (!WalletText.TryParseTime(row.EditTimeText, out var typed))
        {
            Message = "Enter the time as 14:30.";
            return false;
        }
        var date = row.EditDate?.Date ?? LocalDate(row.Transaction.OccurredAt);
        // No time given: keep the one it had.
        var at = At(date, typed ?? row.EditTime, LocalTime(row.Transaction.OccurredAt));
        try
        {
            _store.Update(row.Id, row.EditIsIncome ? amount : -amount, row.EditDescription, at, row.EditNote);
            Message = null;
            Refresh();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Report(ex, "Could not save the transaction.");
            return false;
        }
    }

    internal async Task DeleteAsync(TransactionRow row)
    {
        var what = $"{WalletFormat.Signed(row.Transaction.Amount)} · {row.Title}";
        if (!await _dialogs.ConfirmAsync("Delete this transaction?", $"{what}\nIt is removed from all your devices.", "Delete")) return;
        try
        {
            _store.Delete(row.Id);
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not delete the transaction.");
        }
    }

    internal void CopyText(string text)
    {
        if (text.Length == 0) return;
        _clipboard.SetText(text);
        Message = "Copied.";
    }

    internal DateTime LocalDate(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone).Date;

    internal TimeSpan LocalTime(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone).TimeOfDay;

    private DateTimeOffset ToInstant(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        try
        {
            return new DateTimeOffset(unspecified, _zone.GetUtcOffset(unspecified));
        }
        catch (ArgumentException)
        {
            return _store.Now;
        }
    }

    // ---- Adding by hand ------------------------------------------------------------------------------------------

    public bool CanAdd => WalletText.TryParseAmount(NewAmount, out _);

    partial void OnNewAmountChanged(string value) => OnPropertyChanged(nameof(CanAdd));

    partial void OnNewIsIncomeChanged(bool value) => RefreshNewCategories(Categories());

    [RelayCommand]
    private void OpenAdd()
    {
        NewAmount = "";
        NewDescription = "";
        NewIsIncome = false;
        NewDate = LocalDate(_store.Now);
        var now = TimeZoneInfo.ConvertTime(_store.Now, _zone).TimeOfDay;
        NewTime = new TimeSpan(now.Hours, now.Minutes, 0);
        NewCategory = null;
        AddCategoryName = "";
        RefreshNewCategories(Categories());
        IsAddOpen = true;
    }

    private bool _syncingTime;

    partial void OnNewTimeChanged(TimeSpan? value)
    {
        if (_syncingTime) return;
        _syncingTime = true;
        NewTimeText = value is { } t ? WalletText.FormatTime(t) : "";
        _syncingTime = false;
    }

    partial void OnNewTimeTextChanged(string value)
    {
        if (_syncingTime || !WalletText.TryParseTime(value, out var time)) return;
        _syncingTime = true;
        NewTime = time;
        _syncingTime = false;
    }

    /// <summary>A day and a time of day (the zone's) as an instant; no time keeps the time it already had, or now.</summary>
    internal DateTimeOffset At(DateTime day, TimeSpan? time, TimeSpan fallback) => ToInstant(day.Date + (time ?? fallback));

    [RelayCommand]
    private void CancelAdd() => IsAddOpen = false;

    [RelayCommand]
    private void Add()
    {
        if (!WalletText.TryParseAmount(NewAmount, out var amount))
        {
            Message = "Enter the amount, e.g. 50000, 50k or 1.5tr.";
            return;
        }
        if (!WalletText.TryParseTime(NewTimeText, out var typed))
        {
            Message = "Enter the time as 14:30 (or leave it empty for now).";
            return;
        }
        var now = TimeZoneInfo.ConvertTime(_store.Now, _zone);
        var day = NewDate?.Date ?? now.Date;
        var at = At(day, typed ?? NewTime, now.TimeOfDay);
        try
        {
            var signed = NewIsIncome ? amount : -amount;
            string? category;
            if (AddCategoryName.Trim().Length > 0)
            {
                // A new category for it, of its direction.
                category = _store.AddCategory(AddCategoryName, NewIsIncome ? CategoryKind.Income : CategoryKind.Expense);
                AddCategoryName = "";
            }
            else
            {
                category = NewCategory?.Id ?? _store.SureCategory(NewDescription, signed);
            }
            _store.AddManual(signed, NewDescription, at, category);
            IsAddOpen = false;
            Message = null;
            // Show the month it went in.
            _month = new DateOnly(day.Year, day.Month, 1);
            Refresh();
        }
        catch (ArgumentException ex)
        {
            // A new category whose name is taken, or no amount: say so as it is.
            Message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not save the transaction.");
        }
    }

    private void RefreshNewCategories(IReadOnlyList<CategoryInfo> categories)
    {
        var selected = NewCategory?.Id;
        var sign = NewIsIncome ? 1m : -1m;
        NewCategories.Clear();
        foreach (var c in categories.Where(c => c.Fits(sign) && !c.Hidden)) NewCategories.Add(new CategoryChoice(c.Id, c.Name, c.Icon, c.Color));
        NewCategory = NewCategories.FirstOrDefault(c => c.Id == selected);
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

    private IReadOnlyList<CategoryInfo> Categories() => _store.Categories(includeHidden: true);

    /// <summary>Reads everything again (the pages call it when shown: the phone may have saved transactions meanwhile).</summary>
    public void Refresh()
    {
        try
        {
            var categories = Categories();
            var now = _store.Now;
            var current = WalletFormat.MonthOf(now, _zone);
            if (_month > current) _month = current;
            MonthTitle = WalletFormat.Month(_month);
            IsCurrentMonth = _month == current;
            CanGoNext = _month < current;

            var transactions = _store.Transactions();
            var summary = WalletStats.Month(transactions, categories, _month, now, _zone);
            SpentText = WalletFormat.Money(summary.Spent);
            IncomeText = WalletFormat.Money(summary.Income);
            NetText = WalletFormat.Signed(summary.Net);
            IsNetNegative = summary.Net < 0;
            CountText = WalletFormat.Count(summary.Count, "transaction");
            SpentTodayText = IsCurrentMonth && summary.SpentToday > 0 ? $"Today {WalletFormat.Money(summary.SpentToday)}" : "";
            CompareText = Compare(summary);
            RefreshBudget(summary, now);
            CashFlow = WalletCharts.CashFlow(summary);
            SpendingDonut = WalletCharts.Spending(summary, categories);
            DailyChart = WalletCharts.Daily(transactions, categories, _month, now, _zone, _store.MonthlyBudget);
            MonthsChart = WalletCharts.Months(transactions, categories, _month, now, _zone, 6, OpenMonth);
            Fill(Earning, summary.Earning.Select(s => Row(s, categories)).ToList());
            OnPropertyChanged(nameof(HasEarning));

            RefreshToCategorize(transactions, categories);
            RefreshDays(categories, transactions);
            RefreshNewCategories(categories);
            RefreshCategoryRows(categories);
            RefreshBudgetText();
            RefreshUnread();
            RefreshAccess();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh Wallet");
            Message = "Could not read your transactions. Try again in a moment.";
        }
    }

    private static CategorySpendRow Row(CategorySpend s, IReadOnlyList<CategoryInfo> categories)
    {
        var c = s.CategoryId is { } id ? categories.FirstOrDefault(x => x.Id == id) : null;
        return new CategorySpendRow(
            s.Name,
            c?.Icon ?? WalletIcons.Uncategorized,
            c?.Color ?? 0,
            WalletFormat.Money(s.Amount),
            Math.Round(s.Share * 100, 1),
            s.Share > 0 && s.Share < 0.005 ? "<1%" : $"{Math.Round(s.Share * 100):0}%",
            WalletFormat.Count(s.Count, "transaction"));
    }

    /// <summary>A month picked on the six-month chart.</summary>
    private void OpenMonth(DateOnly month)
    {
        _month = month;
        Refresh();
    }

    private string Compare(MonthSummary summary)
    {
        var previous = summary.Month.AddMonths(-1).ToString("MMMM", CultureInfo.CurrentCulture);
        if (IsCurrentMonth)
        {
            if (summary.PreviousSpentSameDay <= 0) return "";
            var diff = summary.Spent - summary.PreviousSpentSameDay;
            var percent = Math.Abs(Math.Round(diff * 100 / summary.PreviousSpentSameDay));
            return diff switch
            {
                0 => $"The same as {previous} by this day",
                < 0 => $"{percent}% less than {previous} by this day",
                _ => $"{percent}% more than {previous} by this day",
            };
        }
        return summary.PreviousSpent > 0 ? $"{previous}: {WalletFormat.Money(summary.PreviousSpent)}" : "";
    }

    private void RefreshBudget(MonthSummary summary, DateTimeOffset now)
    {
        var budget = WalletStats.Budget(_store.MonthlyBudget, summary, now, _zone);
        HasBudget = budget is not null;
        if (budget is null)
        {
            BudgetPercent = 0;
            BudgetPacePercent = -1;
            IsOverBudget = false;
            BudgetText = "";
            BudgetDetail = "";
            return;
        }
        BudgetPercent = budget.Percent;
        var today = WalletFormat.LocalDay(now, _zone);
        BudgetPacePercent = IsCurrentMonth ? Math.Round(today.Day * 100.0 / DateTime.DaysInMonth(today.Year, today.Month), 1) : -1;
        IsOverBudget = budget.IsOver;
        BudgetText = $"{WalletFormat.Money(budget.Spent)} of {WalletFormat.Money(budget.Budget)}";
        BudgetDetail = budget.IsOver
            ? $"Over budget by {WalletFormat.Money(-budget.Left)}"
            : budget.DaysLeft > 0 && IsCurrentMonth
                ? $"{WalletFormat.Money(budget.Left)} left · {WalletFormat.Money(budget.PerDay)} a day for {WalletFormat.Count(budget.DaysLeft, "day")}"
                : $"{WalletFormat.Money(budget.Left)} left";
    }

    private void RefreshToCategorize(IReadOnlyList<Helm.Core.Sync.SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories)
    {
        var waiting = transactions.Where(t => !t.Value.IsCategorized).ToList();
        var rows = new List<TransactionRow>();
        foreach (var t in waiting.Take(ToCategorizeShown))
        {
            var row = RowFor(t.Id, t.Value, categories);
            row.SetSuggestions(_store.Suggest(t.Value, SuggestionCount));
            rows.Add(row);
        }
        Fill(ToCategorize, rows);
        ToCategorizeTitle = waiting.Count == 0 ? "" : $"{waiting.Count} to categorize";
        MoreToCategorizeText = waiting.Count > ToCategorizeShown ? $"{waiting.Count - ToCategorizeShown} more after these" : "";
        OnPropertyChanged(nameof(HasToCategorize));
        if (_similarIds.Count > 0 && !_similarIds.Any(id => waiting.Any(w => w.Id == id))) DismissSimilar();
    }

    private void RefreshDays(IReadOnlyList<CategoryInfo> categories, IReadOnlyList<Helm.Core.Sync.SyncedItem<WalletTransaction>>? transactions = null)
    {
        transactions ??= _store.Transactions();
        var search = WalletText.Fold(SearchText.Trim());
        var inMonth = transactions.Where(t =>
        {
            var day = WalletFormat.LocalDay(t.Value.OccurredAt, _zone);
            if (day.Year != _month.Year || day.Month != _month.Month) return false;
            if (FilterIndex == 1 && t.Value.Amount > 0 || FilterIndex == 2 && t.Value.Amount < 0) return false;
            if (search.Length == 0) return true;
            var category = t.Value.CategoryId is { } c ? categories.FirstOrDefault(x => x.Id == c)?.Name ?? "" : "";
            return WalletText.Fold(t.Value.Description + " " + t.Value.Note + " " + t.Value.Bank + " " + category).Contains(search, StringComparison.Ordinal);
        }).ToList();

        var today = WalletFormat.LocalDay(_store.Now, _zone);
        var groups = inMonth
            .GroupBy(t => WalletFormat.LocalDay(t.Value.OccurredAt, _zone))
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var spent = -g.Where(t => t.Value.Amount < 0).Sum(t => t.Value.Amount);
                var rows = g.Select(t => RowFor(t.Id, t.Value, categories)).ToList();
                return new DayGroup(WalletFormat.Day(g.Key, today), spent > 0 ? $"−{WalletFormat.Money(spent)}" : "", rows);
            })
            .ToList();
        Days.Clear();
        foreach (var g in groups) Days.Add(g);
        EmptyText = search.Length > 0 || FilterIndex != 0
            ? "Nothing matches."
            : CanListen ? "No transactions this month yet. They appear here as your bank's notifications come in." : "No transactions this month yet.";
        OnPropertyChanged(nameof(HasDays));
        // Rows no longer shown are let go.
        var shown = new HashSet<string>(groups.SelectMany(g => g.Rows).Concat(ToCategorize).Select(r => r.Id), StringComparer.Ordinal);
        foreach (var id in _rows.Keys.Where(id => !shown.Contains(id)).ToList()) _rows.Remove(id);
    }

    private TransactionRow RowFor(string id, WalletTransaction t, IReadOnlyList<CategoryInfo> categories)
    {
        if (!_rows.TryGetValue(id, out var row)) _rows[id] = row = new TransactionRow(this, id);
        if (row.IsEditing) return row;
        var category = t.CategoryId is { } c ? categories.FirstOrDefault(x => x.Id == c) : null;
        var title = t.Description.Length > 0 ? t.Description : t.Bank.Length > 0 ? t.Bank : "Cash";
        var parts = new List<string> { WalletFormat.Time(t.OccurredAt, _zone) };
        if (t.Bank.Length > 0) parts.Add(t.Bank);
        else if (t.Source == TransactionSource.Manual) parts.Add("Added by hand");
        var choices = categories.Where(x => x.Fits(t.Amount) && (!x.Hidden || x.Id == t.CategoryId)).ToList();
        row.Update(t, title, string.Join(" · ", parts), category, choices);
        return row;
    }

    private static void Fill<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private void Report(Exception ex, string message)
    {
        _logger.LogWarning(ex, "Wallet: {Message}", message);
        Message = message;
    }

    // ---- Settings ------------------------------------------------------------------------------------------------

    private void LoadSettings()
    {
        _loading = true;
        var s = _settings.Current;
        ListenEnabled = s.ListenEnabled;
        TechcombankEnabled = s.IsBankEnabled(BankSources.Techcombank.Id);
        AcbEnabled = s.IsBankEnabled(BankSources.Acb.Id);
        AskForCategory = s.AskForCategory;
        AutoCategorize = s.AutoCategorize;
        _loading = false;
        RefreshBudgetText();
    }

    /// <summary>The budget box, when the budget changed elsewhere (typed text that means the same is left alone).</summary>
    private void RefreshBudgetText()
    {
        var budget = _store.MonthlyBudget;
        var shown = WalletText.TryParseAmount(MonthlyBudgetText, out var typed) ? typed : 0;
        if (shown == budget) return;
        _loading = true;
        MonthlyBudgetText = budget > 0 ? budget.ToString("#,0", CultureInfo.CurrentCulture) : "";
        _loading = false;
    }

    partial void OnListenEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsAccess));
        if (!_loading) _settings.Update(s => s.ListenEnabled = value);
    }

    partial void OnTechcombankEnabledChanged(bool value) => SetBank(BankSources.Techcombank.Id, value);

    partial void OnAcbEnabledChanged(bool value) => SetBank(BankSources.Acb.Id, value);

    private void SetBank(string id, bool enabled)
    {
        if (_loading) return;
        _settings.Update(s =>
        {
            s.DisabledBanks.RemoveAll(b => b == id);
            if (!enabled) s.DisabledBanks.Add(id);
        });
    }

    partial void OnAskForCategoryChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.AskForCategory = value);
    }

    partial void OnAutoCategorizeChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.AutoCategorize = value);
    }

    partial void OnMonthlyBudgetTextChanged(string value)
    {
        if (_loading) return;
        try
        {
            if (string.IsNullOrWhiteSpace(value))
                _store.SetMonthlyBudget(0);
            else if (WalletText.TryParseAmount(value, out var amount))
                _store.SetMonthlyBudget(amount);
            else
            {
                Message = "Enter the budget as an amount, e.g. 10000000 or 10tr.";
                return;
            }
            Message = null;
            LoadSettings();
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not save the budget.");
        }
    }

    partial void OnHasNotificationAccessChanged(bool value)
    {
        OnPropertyChanged(nameof(NeedsAccess));
        OnPropertyChanged(nameof(AccessText));
    }

    /// <summary>Reads notification access again (the user comes back from Android's settings).</summary>
    public void RefreshAccess()
    {
        try
        {
            HasNotificationAccess = _platform?.HasNotificationAccess == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the notification access");
        }
    }

    [RelayCommand]
    private void OpenNotificationAccess() => _platform?.OpenNotificationAccess();

    [RelayCommand]
    private void OpenAppDetails() => _platform?.OpenAppDetails();

    [RelayCommand]
    private void PinWidget() => _platform?.PinWidget();

    // Categories

    private void RefreshCategoryRows(IReadOnlyList<CategoryInfo> categories)
    {
        var rows = new List<CategoryEditRow>();
        foreach (var c in categories)
        {
            if (!_categoryRows.TryGetValue(c.Id, out var row)) _categoryRows[c.Id] = row = new CategoryEditRow(this, c);
            else row.Reset(c);
            rows.Add(row);
        }
        foreach (var id in _categoryRows.Keys.Where(id => categories.All(c => c.Id != id)).ToList()) _categoryRows.Remove(id);
        Fill(CategoryRows, rows);
    }

    internal void RenameCategory(CategoryEditRow row, string name)
    {
        if (WalletLimits.Clip(name, WalletLimits.CategoryName).Length == 0) return;
        try
        {
            _store.RenameCategory(row.Id, name);
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not rename the category.");
        }
    }

    internal void SetCategoryLook(string id, string icon, int color)
    {
        try
        {
            _store.SetCategoryLook(id, icon, color);
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not change the category.");
        }
    }

    /// <summary>"New category" in a transaction's row: made for its direction, and the transaction goes in it.</summary>
    internal bool CreateCategoryFor(TransactionRow row, string name)
    {
        var kind = row.Transaction.Amount > 0 ? CategoryKind.Income : CategoryKind.Expense;
        try
        {
            var id = _store.AddCategory(name, kind);
            Message = $"Category “{WalletLimits.Clip(name, WalletLimits.CategoryName)}” added. Change its icon and colour in Wallet's settings.";
            Categorize(row, id);
            return true;
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not add the category.");
            return false;
        }
    }

    internal void SetCategoryShown(string id, bool shown)
    {
        try
        {
            _store.SetCategoryHidden(id, !shown);
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not change the category.");
        }
    }

    internal async Task DeleteCategoryAsync(CategoryEditRow row)
    {
        if (row.IsBuiltIn) return;
        if (!await _dialogs.ConfirmAsync($"Delete “{row.Name}”?", "Its transactions go back to the ones to categorize, on all your devices.", "Delete")) return;
        try
        {
            _store.DeleteCategory(row.Id);
            Refresh();
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not delete the category.");
        }
    }

    [RelayCommand]
    private void AddCategory()
    {
        var kind = NewCategoryKindIndex switch { 1 => CategoryKind.Income, 2 => CategoryKind.Transfer, _ => CategoryKind.Expense };
        try
        {
            var id = _store.AddCategory(NewCategoryName, kind);
            NewCategoryName = "";
            Message = null;
            Refresh();
            // Straight to its icon and colour.
            if (_categoryRows.TryGetValue(id, out var row)) row.IsEditingLook = true;
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (InvalidOperationException ex)
        {
            Report(ex, "Could not add the category.");
        }
    }

    // Trying a notification, and the ones Helm could not read

    partial void OnTestTextChanged(string value) => RunTest();

    partial void OnTestBankIndexChanged(int value) => RunTest();

    private void RunTest()
    {
        var bank = BankSources.All[Math.Clamp(TestBankIndex, 0, BankSources.All.Count - 1)];
        var (ok, text) = WalletCapture.Try(bank, TestText, _store.Now, _zone);
        TestOk = ok;
        TestResult = text;
    }

    private void RefreshUnread()
    {
        var today = WalletFormat.LocalDay(_store.Now, _zone);
        var rows = _settings.Current.Unread
            .Select(u => new UnreadRow(u, $"{WalletFormat.Day(WalletFormat.LocalDay(u.At, _zone), today)} {WalletFormat.Time(u.At, _zone)}", Copy))
            .ToList();
        Unread.Clear();
        foreach (var r in rows) Unread.Add(r);
        OnPropertyChanged(nameof(HasUnread));
    }

    private void Copy(UnreadNotification n) => CopyText(n.Title.Length > 0 ? n.Title + "\n" + n.Text : n.Text);

    [RelayCommand]
    private void ClearUnread() => _capture.ClearUnread();

    // Export

    /// <summary>Every transaction as CSV (the page saves or shares it).</summary>
    public string Csv() => WalletCsv.Build(_store, _zone);

    public void ReportExport(string path) => Message = $"Saved to {path}.";

    public void ReportExportFailure(Exception ex) => Report(ex, "Could not export: " + ex.Message);
}
