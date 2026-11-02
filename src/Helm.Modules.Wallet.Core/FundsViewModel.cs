using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Sync;
using Helm.Shell.Services;

namespace Helm.Modules.Wallet;

/// <summary>
/// A fund on the Wallet page, drawn as a jar whose water is what is left: its status in words, icon and colour (never
/// colour alone). Kept across refreshes, so the water drops smoothly after a new transaction.
/// </summary>
public sealed partial class FundRow : ObservableObject
{
    private readonly Action<FundRow> _open;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _kindText = "";
    [ObservableProperty] private string _kindShort = "";
    [ObservableProperty] private int _color;
    [ObservableProperty] private FundStatus _status;
    [ObservableProperty] private string _statusText = "";

    /// <summary>"182,000 ₫" left, or how far over.</summary>
    [ObservableProperty] private string _bigText = "";
    [ObservableProperty] private string _bigShort = "";

    /// <summary>"left" or "over".</summary>
    [ObservableProperty] private string _bigLabel = "";

    /// <summary>What is left as a share of what the fund held, 0–1: the water in the jar.</summary>
    [ObservableProperty] private double _leftShare;

    /// <summary>"20%" left, or "Over".</summary>
    [ObservableProperty] private string _percentText = "";

    /// <summary>Spent as a share of what the fund held, 0–100 (the meter in its pop-up).</summary>
    [ObservableProperty] private double _spentPercent;
    [ObservableProperty] private string _ofText = "";
    [ObservableProperty] private string _ofShort = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string _spentText = "";
    [ObservableProperty] private string _perDayText = "";
    [ObservableProperty] private string _carryText = "";
    [ObservableProperty] private string _categoriesText = "";

    internal FundRow(string id, Action<FundRow> open)
    {
        Id = id;
        _open = open;
    }

    public string Id { get; }

    internal FundState State { get; private set; } = null!;

    /// <summary>The fund's categories, as chips in its pop-up.</summary>
    public ObservableCollection<CategoryChoice> Categories { get; } = [];

    public bool IsOver => Status == FundStatus.Over;

    public bool IsUsedUp => Status == FundStatus.UsedUp;

    public bool IsLow => Status == FundStatus.Low;

    public bool IsOnTrack => Status == FundStatus.OnTrack;

    /// <summary>Water to draw (there is some left).</summary>
    public bool HasWater => LeftShare > 0;

    partial void OnStatusChanged(FundStatus value)
    {
        OnPropertyChanged(nameof(IsOver));
        OnPropertyChanged(nameof(IsUsedUp));
        OnPropertyChanged(nameof(IsLow));
        OnPropertyChanged(nameof(IsOnTrack));
    }

    partial void OnLeftShareChanged(double value) => OnPropertyChanged(nameof(HasWater));

    [RelayCommand]
    private void Open() => _open(this);

    internal void Update(FundState state, IReadOnlyList<CategoryInfo> categories, DateOnly month, DateOnly today)
    {
        State = state;
        var f = state.Fund;
        Name = f.Name;
        KindText = f.EveryMonth ? "Every month" : WalletFormat.Month(month) + " only";
        KindShort = f.EveryMonth ? "Monthly" : month.ToString("MMM", CultureInfo.CurrentCulture) + " only";
        Color = f.Color;
        Status = state.Status;
        var over = state.Left < 0;
        BigText = WalletFormat.Money(Math.Abs(state.Left));
        BigShort = WalletFormat.Short(Math.Abs(state.Left));
        BigLabel = over ? "over" : "left";
        LeftShare = state.LeftShare;
        PercentText = over ? "Over" : $"{Math.Round(state.LeftShare * 100):0}%";
        SpentPercent = Math.Clamp(state.Share * 100, 0, 100);
        OfText = $"{WalletFormat.Money(state.Spent)} of {WalletFormat.Money(state.Budget)}";
        OfShort = $"{WalletFormat.Short(state.Spent)} of {WalletFormat.Short(state.Budget)}";
        AmountText = WalletFormat.Money(state.Budget);
        SpentText = WalletFormat.Money(state.Spent);
        StatusText = state.Status switch
        {
            FundStatus.Over => $"Over by {WalletFormat.Money(-state.Left)}",
            FundStatus.UsedUp => $"Used up · {Math.Round(state.Share * 100):0}% spent",
            FundStatus.Low when state.RunsOutOn is { } day => $"Running low · may run out ~{day.ToString("d MMM", CultureInfo.CurrentCulture)}",
            FundStatus.Low => $"Running low · {Math.Round(state.Share * 100):0}% spent",
            _ => state.Spent == 0 ? "Not touched yet" : "On track",
        };
        var daysLeft = month.Year == today.Year && month.Month == today.Month ? DateTime.DaysInMonth(month.Year, month.Month) - today.Day + 1 : 0;
        var perDay = state.Left > 0 && daysLeft > 0 ? state.Left / daysLeft : 0;
        perDay = perDay >= 1000 ? decimal.Floor(perDay / 1000) * 1000 : decimal.Floor(perDay);
        PerDayText = perDay > 0
            ? $"You can spend about {WalletFormat.Money(perDay)} a day here for the {WalletFormat.Count(daysLeft, "day")} left."
            : over ? "Anything more spent here takes it further over." : daysLeft > 0 ? "Nothing left here this month." : "";
        CarryText = !f.EveryMonth ? "Ends with the month"
            : f.CarryOver ? state.Carried == 0 ? "Leftover carries over" : $"{WalletFormat.Signed(state.Carried)} carried in"
            : "Starts fresh each month";
        var chips = f.Categories.Select(id => categories.FirstOrDefault(c => c.Id == id)).OfType<CategoryInfo>()
            .Select(c => new CategoryChoice(c.Id, c.Name, c.Icon, c.Color)).ToList();
        if (!Categories.SequenceEqual(chips))
        {
            Categories.Clear();
            foreach (var c in chips) Categories.Add(c);
        }
        CategoriesText = chips.Count == 0 ? "No categories yet" : string.Join(", ", chips.Select(c => c.Name));
    }
}

/// <summary>A fund to take money from ("Cover it…"), with what it has left.</summary>
public sealed partial class FundChoice : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public FundChoice(string id, string name, Action<FundChoice> pick)
    {
        Id = id;
        Name = name;
        PickCommand = new RelayCommand(() => pick(this));
    }

    public string Id { get; }

    public string Name { get; }

    public IRelayCommand PickCommand { get; }
}

/// <summary>
/// The Wallet's funds, shared by the Windows and Android pages: the jars, their totals and warnings, the spending
/// outside every fund, the fund form, moving money between funds, and deleting one. Part of <see cref="WalletViewModel"/>
/// (it refreshes them with the month); used on the UI thread.
/// </summary>
public sealed partial class FundsViewModel : ObservableObject
{
    private readonly FundBook _book;
    private readonly WalletViewModel _owner;
    private readonly IDialogService _dialogs;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, FundRow> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<CategoryInfo> _categories = [];
    private IReadOnlyList<string?> _outsideCategories = [];
    private DateOnly _month;

    [ObservableProperty] private FundRow? _selected;

    // Totals
    [ObservableProperty] private string _leftText = "";
    [ObservableProperty] private string _totalText = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private string _usedPercentText = "";
    [ObservableProperty] private string _alertText = "";

    /// <summary>All the funds together are over (more spent than they held).</summary>
    [ObservableProperty] private bool _totalIsOver;

    /// <summary>The worst status among the funds (the summary's colour and icon).</summary>
    [ObservableProperty] private FundStatus _alertLevel;

    // Outside funds
    [ObservableProperty] private bool _hasOutside;
    [ObservableProperty] private string _outsideText = "";
    [ObservableProperty] private string _outsideShort = "";
    [ObservableProperty] private string _outsideNames = "";

    // The fund form
    [ObservableProperty] private string? _formId;
    [ObservableProperty] private string _formName = "";
    [ObservableProperty] private string _formAmount = "";

    /// <summary>0 every month, 1 this month only.</summary>
    [ObservableProperty] private int _formKindIndex;

    /// <summary>0 start fresh, 1 carry the leftover over.</summary>
    [ObservableProperty] private int _formCarryIndex;

    /// <summary>0 warn at 80%, 1 at 90%, 2 only when over.</summary>
    [ObservableProperty] private int _formAlertIndex;

    // Moving money in ("Cover it…")
    [ObservableProperty] private string _coverAmount = "";
    [ObservableProperty] private string? _coverFrom;

    internal FundsViewModel(FundBook book, WalletViewModel owner, IDialogService dialogs, TimeZoneInfo zone)
    {
        _book = book;
        _owner = owner;
        _dialogs = dialogs;
        _zone = zone;
    }

    internal FundBook Book => _book;

    public ObservableCollection<FundRow> Rows { get; } = [];

    /// <summary>The transactions the selected fund's pop-up lists (its spending this month, newest first).</summary>
    public ObservableCollection<TransactionRow> SelectedRows { get; } = [];

    public bool HasSelectedRows => SelectedRows.Count > 0;

    public bool HasFunds => Rows.Count > 0;

    public bool HasNoFunds => Rows.Count == 0;

    public bool HasAlerts => AlertLevel != FundStatus.OnTrack;

    public bool AlertIsOver => AlertLevel == FundStatus.Over;

    public bool AlertIsUsedUp => AlertLevel == FundStatus.UsedUp;

    public bool AlertIsLow => AlertLevel == FundStatus.Low;

    partial void OnAlertLevelChanged(FundStatus value)
    {
        OnPropertyChanged(nameof(HasAlerts));
        OnPropertyChanged(nameof(AlertIsOver));
        OnPropertyChanged(nameof(AlertIsUsedUp));
        OnPropertyChanged(nameof(AlertIsLow));
    }

    // ---- Refresh -------------------------------------------------------------------------------------------------

    internal void Update(IReadOnlyList<SyncedItem<WalletTransaction>> transactions, IReadOnlyList<CategoryInfo> categories, DateOnly month, DateTimeOffset now)
    {
        _categories = categories;
        _month = month;
        var today = WalletFormat.LocalDay(now, _zone);
        var states = _book.States(transactions, month, now, _zone);
        var rows = new List<FundRow>();
        foreach (var s in states)
        {
            if (!_rows.TryGetValue(s.Id, out var row)) _rows[s.Id] = row = new FundRow(s.Id, _owner.OpenFund);
            row.Update(s, categories, month, today);
            rows.Add(row);
        }
        foreach (var id in _rows.Keys.Where(id => states.All(s => s.Id != id)).ToList()) _rows.Remove(id);
        if (!Rows.SequenceEqual(rows))
        {
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
        }
        OnPropertyChanged(nameof(HasFunds));
        OnPropertyChanged(nameof(HasNoFunds));
        if (Selected is { } selected && !_rows.ContainsKey(selected.Id)) Selected = null;

        var total = states.Sum(s => s.Budget);
        var spent = states.Sum(s => s.Spent);
        LeftText = WalletFormat.Money(states.Sum(s => s.Left));
        TotalIsOver = states.Sum(s => s.Left) < 0;
        TotalText = WalletFormat.Money(total);
        UsedPercent = total > 0 ? Math.Min(100, (double)(spent * 100 / total)) : 0;
        UsedPercentText = $"{Math.Round(UsedPercent):0}% used";
        var counts = new List<string>();
        void Count(FundStatus status, string word)
        {
            var n = states.Count(s => s.Status == status);
            if (n > 0) counts.Add($"{n} {word}");
        }
        Count(FundStatus.Over, "over");
        Count(FundStatus.UsedUp, "used up");
        Count(FundStatus.Low, "running low");
        AlertText = counts.Count == 0 ? "All funds on track" : string.Join(" · ", counts);
        AlertLevel = states.Count == 0 ? FundStatus.OnTrack : states.Max(s => s.Status);

        var outside = _book.Outside(transactions, month, _zone);
        _outsideCategories = outside.CategoryIds;
        HasOutside = outside.Spent > 0;
        OutsideText = WalletFormat.Money(outside.Spent);
        OutsideShort = WalletFormat.Short(outside.Spent);
        OutsideNames = string.Join(", ", outside.CategoryIds.Select(id => id is null ? WalletStats.UncategorizedName : categories.FirstOrDefault(c => c.Id == id)?.Name ?? ""));
    }

    internal void ShowRows(IReadOnlyList<TransactionRow> rows)
    {
        if (!SelectedRows.SequenceEqual(rows))
        {
            SelectedRows.Clear();
            foreach (var r in rows) SelectedRows.Add(r);
        }
        OnPropertyChanged(nameof(HasSelectedRows));
    }

    /// <summary>" · Eating: 120,000 ₫ left" or " · Eating is over by 50,000 ₫" for a category's fund; empty outside funds.</summary>
    internal string NoteFor(string categoryId, DateOnly month, IReadOnlyList<SyncedItem<WalletTransaction>> transactions, DateTimeOffset now)
    {
        if (_book.FundOf(categoryId, month) is not { } fund) return "";
        var state = _book.States(transactions, month, now, _zone).FirstOrDefault(s => s.Id == fund.Id);
        if (state is null) return "";
        return state.Left < 0 ? $" · {fund.Value.Name} is over by {WalletFormat.Money(-state.Left)}" : $" · {fund.Value.Name}: {WalletFormat.Money(state.Left)} left";
    }

    // ---- The fund form -------------------------------------------------------------------------------------------

    public ObservableCollection<CategoryPick> FormPicks { get; } = [];

    public string FormTitle => FormId is null ? "Add fund" : "Edit fund";

    public string FormSaveText => FormId is null ? "Add fund" : "Save";

    public bool FormEveryMonth => FormKindIndex == 0;

    public string FormAmountHint => WalletText.TryParseAmount(FormAmount, out var a)
        ? "= " + WalletFormat.Money(a) + (FormEveryMonth ? " every month" : " for " + WalletFormat.Month(_month))
        : "Type 2tr, 500k";

    public string FormKindHelp => FormEveryMonth
        ? "Comes back every month with the same amount."
        : $"Only for {WalletFormat.Month(_month)}: it ends with the month.";

    public string FormCarryHelp => FormCarryIndex == 1
        ? "What is left, or overspent, moves into next month's fund."
        : "Every month starts again at the full amount.";

    public static IReadOnlyList<string> FormKindNames { get; } = ["Every month", "This month only"];

    public static IReadOnlyList<string> FormCarryNames { get; } = ["Start fresh", "Carry leftover"];

    public static IReadOnlyList<string> FormAlertNames { get; } = ["At 80%", "At 90%", "Only when over"];

    partial void OnFormIdChanged(string? value)
    {
        OnPropertyChanged(nameof(FormTitle));
        OnPropertyChanged(nameof(FormSaveText));
    }

    partial void OnFormAmountChanged(string value)
    {
        OnPropertyChanged(nameof(FormAmountHint));
        _owner.PopupError = null;
    }

    partial void OnFormKindIndexChanged(int value)
    {
        OnPropertyChanged(nameof(FormEveryMonth));
        OnPropertyChanged(nameof(FormAmountHint));
        OnPropertyChanged(nameof(FormKindHelp));
    }

    partial void OnFormCarryIndexChanged(int value) => OnPropertyChanged(nameof(FormCarryHelp));

    [RelayCommand]
    private void OpenAdd() => OpenForm(null, "", "", true, false, 80, []);

    /// <summary>A new fund for the spending outside every fund: its categories come picked.</summary>
    [RelayCommand]
    private void OpenOutside() => OpenForm(null, "", "", true, false, 80, _outsideCategories.OfType<string>().ToList());

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected?.State.Fund is not { } f) return;
        OpenForm(Selected.Id, f.Name, f.Amount.ToString("#,0", CultureInfo.CurrentCulture), f.EveryMonth, f.CarryOver, f.AlertPercent, f.Categories);
    }

    private void OpenForm(string? id, string name, string amount, bool everyMonth, bool carry, int alert, IReadOnlyList<string> picked)
    {
        FormId = id;
        FormName = name;
        FormAmount = amount;
        FormKindIndex = everyMonth ? 0 : 1;
        FormCarryIndex = carry ? 1 : 0;
        FormAlertIndex = alert switch { 90 => 1, 100 => 2, _ => 0 };
        var others = _book.In(_month).Where(f => f.Id != id).ToList();
        FormPicks.Clear();
        foreach (var c in _categories.Where(c => c.Kind == CategoryKind.Expense && (!c.Hidden || picked.Contains(c.Id))))
        {
            var owner = others.FirstOrDefault(f => f.Value.Categories.Contains(c.Id, StringComparer.Ordinal));
            FormPicks.Add(new CategoryPick(c.Id, c.Name, c.Icon, c.Color, TogglePick)
            {
                IsSelected = picked.Contains(c.Id),
                Note = owner is null ? "" : "in " + owner.Value.Name,
            });
        }
        _owner.ShowPopup(WalletPopup.FundForm, stack: id is not null);
    }

    private static void TogglePick(CategoryPick pick) => pick.IsSelected = !pick.IsSelected;

    [RelayCommand]
    private void SaveForm()
    {
        if (FormName.Trim().Length == 0)
        {
            _owner.PopupError = "Give the fund a name.";
            return;
        }
        if (!WalletText.TryParseAmount(FormAmount, out var amount) || amount <= 0)
        {
            _owner.PopupError = "Enter the fund's amount, e.g. 2tr or 500k.";
            return;
        }
        var categories = FormPicks.Where(p => p.IsSelected).Select(p => p.Id).ToList();
        var alert = FormAlertIndex switch { 1 => 90, 2 => 100, _ => 80 };
        try
        {
            if (FormId is { } id)
            {
                _book.Update(id, FormName, amount, FormEveryMonth, categories, FormCarryIndex == 1, alert);
                _owner.DismissPopupCommand.Execute(null);
                _owner.Refresh();
                _owner.ShowToast("Fund saved");
            }
            else
            {
                var added = _book.Add(FormName, amount, FormEveryMonth, _month, categories, FormCarryIndex == 1, alert);
                _owner.ClosePopupCommand.Execute(null);
                _owner.Refresh();
                _owner.ShowToast($"Fund added: {_book.Get(added)?.Name}");
            }
        }
        catch (ArgumentException ex)
        {
            _owner.PopupError = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }

    // ---- Moving money in -----------------------------------------------------------------------------------------

    public ObservableCollection<FundChoice> CoverChoices { get; } = [];

    public bool HasNoCoverChoices => CoverChoices.Count == 0;

    public string CoverHint => WalletText.TryParseAmount(CoverAmount, out var a) ? "= " + WalletFormat.Money(a) : "Type an amount, like 342k";

    /// <summary>"Home & bills: 0 ₫ left" and "Da Lat trip: 1,658,000 ₫ left" after the move.</summary>
    public string CoverPreview
    {
        get
        {
            if (Selected is not { } to || CoverFrom is not { } fromId || !_rows.TryGetValue(fromId, out var from)) return "";
            if (!WalletText.TryParseAmount(CoverAmount, out var a) || a <= 0) return "";
            static string Left(string name, decimal left) => left >= 0 ? $"{name}: {WalletFormat.Money(left)} left" : $"{name}: over by {WalletFormat.Money(-left)}";
            return Left(to.Name, to.State.Left + a) + "\n" + Left(from.Name, from.State.Left - a);
        }
    }

    public bool HasCoverPreview => CoverPreview.Length > 0;

    partial void OnCoverAmountChanged(string value) => CoverChanged();

    partial void OnCoverFromChanged(string? value) => CoverChanged();

    private void CoverChanged()
    {
        OnPropertyChanged(nameof(CoverHint));
        OnPropertyChanged(nameof(CoverPreview));
        OnPropertyChanged(nameof(HasCoverPreview));
        _owner.PopupError = null;
    }

    [RelayCommand]
    private void OpenCover()
    {
        if (Selected is not { } to) return;
        var sources = Rows.Where(r => r.Id != to.Id && r.State.Left > 0).OrderByDescending(r => r.State.Left).ToList();
        CoverChoices.Clear();
        foreach (var r in sources) CoverChoices.Add(new FundChoice(r.Id, $"{r.Name} · {r.BigShort} left", PickCover));
        OnPropertyChanged(nameof(HasNoCoverChoices));
        CoverFrom = sources.FirstOrDefault()?.Id;
        foreach (var c in CoverChoices) c.IsSelected = c.Id == CoverFrom;
        CoverAmount = to.State.Left < 0 ? (-to.State.Left).ToString("#,0", CultureInfo.CurrentCulture) : "";
        _owner.ShowPopup(WalletPopup.Cover);
    }

    private void PickCover(FundChoice choice)
    {
        CoverFrom = choice.Id;
        foreach (var c in CoverChoices) c.IsSelected = c == choice;
    }

    [RelayCommand]
    private void SaveCover()
    {
        if (Selected is not { } to) return;
        if (CoverFrom is not { } fromId || !_rows.TryGetValue(fromId, out var from))
        {
            _owner.PopupError = "Pick the fund it comes from.";
            return;
        }
        if (!WalletText.TryParseAmount(CoverAmount, out var amount) || amount <= 0)
        {
            _owner.PopupError = "Enter the amount to move.";
            return;
        }
        try
        {
            _book.Move(fromId, to.Id, amount, _month);
            _owner.DismissPopupCommand.Execute(null);
            _owner.Refresh();
            _owner.ShowToast($"Moved {WalletFormat.Money(amount)} from {from.Name} to {to.Name}");
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }

    // ---- Deleting ------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (Selected is not { } row) return;
        if (!await _dialogs.ConfirmAsync($"Delete {row.Name}?", "Its categories move to Outside funds. No transaction is changed.", "Delete")) return;
        try
        {
            _book.Delete(row.Id);
            Selected = null;
            _owner.ClosePopupCommand.Execute(null);
            _owner.Refresh();
            _owner.ShowToast($"Deleted {row.Name}");
        }
        catch (InvalidOperationException ex)
        {
            _owner.PopupError = ex.Message;
        }
    }
}
