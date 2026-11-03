using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;

namespace Helm.Modules.Wallet;

/// <summary>One recurring bill on the Wallet page: what it costs, its day, and whether this month is paid.</summary>
public sealed partial class BillRow : ObservableObject
{
    private readonly BillsViewModel _owner;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _amountText = "";
    [ObservableProperty] private string _dueText = "";

    /// <summary>"Paid", "Due in 3 days", "5 days late", "From December".</summary>
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private bool _isPaid;
    [ObservableProperty] private bool _isLate;

    /// <summary>Its amount changes every month: "Paid" first asks how much.</summary>
    [ObservableProperty] private bool _varies;

    /// <summary>"Paid" on a bill that varies: the amount box is open.</summary>
    [ObservableProperty] private bool _isAskingAmount;
    [ObservableProperty] private string _payAmount = "";

    internal BillRow(string id, BillsViewModel owner)
    {
        Id = id;
        _owner = owner;
    }

    public string Id { get; }

    /// <summary>This month is not paid yet.</summary>
    [ObservableProperty] private bool _canPay;

    internal WalletBill Bill { get; private set; } = new();

    /// <summary>It counts this month (paid, or added before its day): the summary's "2 of 4 paid".</summary>
    internal bool CountsThisMonth { get; private set; }

    internal void Show(WalletBill bill, DateOnly today)
    {
        Bill = bill;
        var month = new DateOnly(today.Year, today.Month, 1);
        Name = bill.Name;
        Varies = bill.Amount is null;
        AmountText = bill.Amount is { } fixedAmount ? WalletFormat.Money(fixedAmount)
            : bill.LastAmount is { } last ? $"varies · last {WalletFormat.Money(last)}" : "varies";
        DueText = $"Due on the {Ordinal(bill.DueDay)}";
        IsPaid = bill.PaidMonths.Contains(month);
        var due = bill.DueIn(month);
        IsLate = !IsPaid && bill.StartMonth <= month && due < today;
        // A bill added after its day still takes this month's payment (late), as Ask Helm's bill_pay does.
        CanPay = !IsPaid;
        CountsThisMonth = IsPaid || bill.StartMonth <= month;
        StateText = IsPaid ? "Paid"
            : bill.StartMonth > month ? "From " + bill.StartMonth.ToString("MMMM", CultureInfo.CurrentCulture)
            : due == today ? "Due today"
            : due > today ? (due.DayNumber - today.DayNumber == 1 ? "Due tomorrow" : $"Due in {due.DayNumber - today.DayNumber} days")
            : today.DayNumber - due.DayNumber == 1 ? "1 day late" : $"{today.DayNumber - due.DayNumber} days late";
        if (!CanPay) IsAskingAmount = false;
    }

    /// <summary>"Paid": saved at once for a fixed amount; a bill that varies asks how much first.</summary>
    [RelayCommand]
    private void Pay()
    {
        if (Varies && !IsAskingAmount)
        {
            PayAmount = Bill.LastAmount is { } last ? WalletFormat.Money(last).Replace(" " + WalletFormat.Currency, "") : "";
            IsAskingAmount = true;
            return;
        }
        if (_owner.Pay(this, Varies ? PayAmount : null)) IsAskingAmount = false;
    }

    [RelayCommand]
    private void CancelPay() => IsAskingAmount = false;

    [RelayCommand]
    private void Edit() => _owner.BeginEdit(this);

    [RelayCommand]
    private void Delete() => _owner.Delete(this);

    internal static string Ordinal(int day) => day + (day % 100 is 11 or 12 or 13 ? "th" : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
}

/// <summary>
/// The Wallet page's Bills section (both apps): the recurring bills (<see cref="BillBook"/>, synced <c>wallet.bills</c>)
/// with this month paid or not, adding, editing and deleting one, and "Paid", which saves the spending transaction and
/// marks the month paid as Ask Helm's bill_pay does. The last payment or deletion can be taken back (Undo). UI thread.
/// </summary>
public sealed partial class BillsViewModel : ObservableObject
{
    private readonly BillBook _book;
    private readonly IUiDispatcher _ui;
    private readonly TimeZoneInfo _zone;
    private Action? _undo;
    private int _refreshQueued;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FormTitle))] private bool _isEditing;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FormTitle))] private string? _editId;
    [ObservableProperty] private string _formName = "";

    /// <summary>The amount every month; empty: it varies (electricity, water).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FormAmountHint))] private string _formAmount = "";
    [ObservableProperty] private double _formDay = 1;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasFormProblem))] private string _formProblem = "";

    /// <summary>"Paid Tiền điện · 452.000 ₫" with Undo, or a problem; "" when there is nothing to say.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string _message = "";
    [ObservableProperty] private bool _canUndo;

    /// <summary>"2 of 4 paid this month".</summary>
    [ObservableProperty] private string _summaryText = "";

    public BillsViewModel(BillBook book, IUiDispatcher ui, TimeZoneInfo? zone = null)
    {
        _book = book;
        _ui = ui;
        _zone = zone ?? TimeZoneInfo.Local;
        _book.Changed += (_, _) => ScheduleRefresh();
        Refresh();
    }

    public ObservableCollection<BillRow> Rows { get; } = [];

    public bool HasBills => Rows.Count > 0;

    public bool HasNoBills => Rows.Count == 0;

    public bool HasMessage => Message.Length > 0;

    public bool HasFormProblem => FormProblem.Length > 0;

    public string FormTitle => EditId is null ? "New bill" : "Edit bill";

    public string FormAmountHint => FormAmount.Trim().Length == 0 ? "Empty: it changes every month"
        : WalletText.TryParseAmount(FormAmount, out var a) ? "= " + WalletFormat.Money(a) : "Type 250k, 1.5tr";

    /// <summary>The due day as typed on the phone (the PC's number box binds <see cref="FormDay"/>).</summary>
    public string FormDayText
    {
        get => ((int)Math.Round(double.IsNaN(FormDay) ? 0 : FormDay)).ToString(CultureInfo.InvariantCulture);
        set => FormDay = int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var day) ? day : 0;
    }

    partial void OnFormDayChanged(double value) => OnPropertyChanged(nameof(FormDayText));

    private DateOnly Today => WalletFormat.LocalDay(_book.Now, _zone);

    public void Refresh()
    {
        var today = Today;
        var all = _book.All();
        var old = Rows.ToDictionary(r => r.Id);
        var rows = all.Select(b =>
        {
            var row = old.GetValueOrDefault(b.Id) ?? new BillRow(b.Id, this);
            row.Show(b.Value, today);
            return row;
        }).ToList();
        if (!Rows.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id)))
        {
            Rows.Clear();
            foreach (var row in rows) Rows.Add(row);
        }
        var counting = rows.Where(r => r.CountsThisMonth).ToList();
        SummaryText = counting.Count == 0 ? "" : $"{counting.Count(r => r.IsPaid)} of {counting.Count} paid this month";
        OnPropertyChanged(nameof(HasBills));
        OnPropertyChanged(nameof(HasNoBills));
        if (EditId is { } id && _book.Get(id) is null) CancelEdit();
    }

    /// <summary>Several changes from sync come as one refresh.</summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        });
    }

    [RelayCommand]
    private void BeginAdd()
    {
        EditId = null;
        FormName = "";
        FormAmount = "";
        FormDay = Math.Min(Today.Day, 28);
        FormProblem = "";
        IsEditing = true;
    }

    internal void BeginEdit(BillRow row)
    {
        EditId = row.Id;
        FormName = row.Bill.Name;
        FormAmount = row.Bill.Amount is { } amount ? WalletFormat.Money(amount).Replace(" " + WalletFormat.Currency, "") : "";
        FormDay = row.Bill.DueDay;
        FormProblem = "";
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditId = null;
        FormProblem = "";
    }

    [RelayCommand]
    private void SaveEdit()
    {
        decimal? amount = null;
        if (FormAmount.Trim().Length > 0)
        {
            if (!WalletText.TryParseAmount(FormAmount, out var parsed) || parsed <= 0)
            {
                FormProblem = "That is not an amount: type 250k or 1.5tr, or leave it empty when it changes every month.";
                return;
            }
            amount = parsed;
        }
        var day = (int)Math.Round(double.IsNaN(FormDay) ? 0 : FormDay);
        try
        {
            if (EditId is { } id) _book.Update(id, FormName, amount, day);
            else _book.Add(FormName, amount, day);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            FormProblem = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
            return;
        }
        CancelEdit();
        Refresh();
    }

    /// <summary>Saves the payment (a spending transaction) and marks the month paid; false when the amount is not one.</summary>
    internal bool Pay(BillRow row, string? typed)
    {
        decimal? amount = null;
        if (typed is not null)
        {
            if (!WalletText.TryParseAmount(typed, out var parsed) || parsed <= 0)
            {
                Say("How much was it? Type 450k or 1.2tr.", null);
                return false;
            }
            amount = parsed;
        }
        BillPayment payment;
        try
        {
            var today = Today;
            payment = _book.Pay(row.Id, amount, month: new DateOnly(today.Year, today.Month, 1));
        }
        catch (InvalidOperationException ex)
        {
            Say(ex.Message, null);
            return false;
        }
        var id = row.Id;
        Say($"Paid {row.Name} · {WalletFormat.Money(payment.Amount)}", () => _book.Unpay(id, payment));
        Refresh();
        return true;
    }

    internal void Delete(BillRow row)
    {
        var id = row.Id;
        var bill = row.Bill;
        if (!_book.Delete(id)) return;
        Say($"Deleted {bill.Name}", () => _book.Restore(id, bill));
        Refresh();
    }

    [RelayCommand]
    private void Undo()
    {
        var undo = _undo;
        Say("", null);
        undo?.Invoke();
        Refresh();
    }

    [RelayCommand]
    private void DismissMessage() => Say("", null);

    private void Say(string text, Action? undo)
    {
        Message = text;
        _undo = undo;
        CanUndo = undo is not null;
    }
}
