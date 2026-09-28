using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Tracker;

/// <summary>One entry in a person's history: when, what, how much, and the balance right after it.</summary>
public sealed partial class DebtEntryRow(TrackerViewModel owner, DebtPersonViewModel person, DebtLedgerEntry entry, string currency) : ObservableObject
{
    public string Id { get; } = entry.Id;

    public string When { get; } = TrackerFormat.When(entry.Item.CreatedAt);

    public string KindText { get; } = TrackerFormat.DebtKind(entry.Kind);

    /// <summary>"+500,000 ₫" (the balance went up: they owe more) or "−200,000 ₫".</summary>
    public string AmountText { get; } = (entry.Item.SignedAmount >= 0 ? "+" : "−") + TrackerFormat.Money(entry.Item.Amount, currency);

    public string BalanceText { get; } = "= " + TrackerFormat.Balance(entry.BalanceAfter, currency);

    public string Note { get; } = entry.Item.Title;

    public bool HasNote => Note.Length > 0;

    public bool IsIncrease => entry.Item.SignedAmount > 0;

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteDebtEntryAsync(person, this);
}

/// <summary>
/// A person in the debt book: their balance (every entry with the same name adds up), their history, and when it is
/// due. There is no tick box: a person is settled when the balance reaches 0. Amounts are never edited, only added
/// (owes me, I owe, repayment), so the history shows every change. A click on the row opens the details (history and
/// due time); another click closes them. Instances are kept across refreshes (matched by name) so an open row stays open.
/// </summary>
public sealed partial class DebtPersonViewModel : ObservableObject
{
    private readonly TrackerViewModel _owner;
    private DebtPerson _person;
    private string _currency;
    private DateTimeOffset _now;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private string _editDueTimeText = "";
    [ObservableProperty] private TimeSpan? _editDueTime;
    private bool _syncingTime;

    internal DebtPersonViewModel(TrackerViewModel owner, DebtPerson person, string currency, DateTimeOffset now)
    {
        _owner = owner;
        _person = person;
        _currency = currency;
        _now = now;
        Rebuild();
    }

    public string Key => _person.Key;

    public string Name => _person.Name;

    public bool IsSettled => _person.SettledAt is not null || _person.IsSettled;

    /// <summary>"+1,500,000 ₫" when they owe the user, "−200,000 ₫" when the user owes them, "Settled" at 0.</summary>
    public string BalanceText => IsSettled ? "Settled" : TrackerFormat.Balance(_person.Balance, _currency);

    public bool OwedToMe => !IsSettled && _person.Balance > 0;

    public bool IOwe => !IsSettled && _person.Balance < 0;

    public DateTimeOffset? Due => IsSettled ? null : _person.Due(TimeZoneInfo.Local);

    public bool IsOverdue => Due is { } due && due < _now;

    /// <summary>"Owes me · 3 entries · Due tomorrow 18:00" (open) or "Settled 28/09/2026 14:00 · 2 entries".</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (_person.SettledAt is { } at) parts.Add("Settled " + TrackerFormat.When(at));
            else if (OwedToMe) parts.Add("Owes me");
            else if (IOwe) parts.Add("I owe");
            parts.Add(_person.Entries.Count == 1 ? "1 entry" : $"{_person.Entries.Count} entries");
            if (!IsSettled && _person.Entries.LastOrDefault(e => e.Item.DueAt is not null || e.Item.DueDate is not null) is { } dueEntry)
                parts.Add(TrackerFormat.Due(dueEntry.Item with { DueAt = Due }, _now));
            return string.Join(" · ", parts);
        }
    }

    public ObservableCollection<DebtEntryRow> Entries { get; } = [];

    public string Currency => _currency;

    partial void OnEditDueTimeTextChanged(string value)
    {
        if (_syncingTime) return;
        if (TrackerFormat.TryParseTime(value, out var time)) SetTime(time);
    }

    partial void OnEditDueTimeChanged(TimeSpan? value)
    {
        if (_syncingTime) return;
        _syncingTime = true;
        EditDueTimeText = TrackerFormat.Time(value);
        _syncingTime = false;
    }

    private void SetTime(TimeSpan? time)
    {
        _syncingTime = true;
        EditDueTime = time;
        _syncingTime = false;
    }

    internal void Update(DebtPerson person, string currency, DateTimeOffset now)
    {
        if (person == _person && currency == _currency && now == _now) return;
        _person = person;
        _currency = currency;
        _now = now;
        Rebuild();
        OnPropertyChanged(string.Empty);
    }

    private void Rebuild()
    {
        Entries.Clear();
        // Newest first, like a bank statement.
        foreach (var entry in _person.Entries.Reverse()) Entries.Add(new DebtEntryRow(_owner, this, entry, _currency));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded) return;
        var due = Due is { } d ? TimeZoneInfo.ConvertTime(d, TimeZoneInfo.Local) : (DateTimeOffset?)null;
        EditDueDate = due?.Date;
        var hasTime = _person.Entries.Any(e => e.Item.DueAt is not null);
        _syncingTime = true;
        EditDueTime = due is { } t && hasTime ? t.TimeOfDay : null;
        EditDueTimeText = TrackerFormat.Time(EditDueTime);
        _syncingTime = false;
    }

    /// <summary>Moves the due time later by a number of days ("1", "7", "30"); from now when there was none.</summary>
    [RelayCommand]
    private void Extend(string? days)
    {
        if (!int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return;
        var from = Due is { } due && due > _now ? due : _now;
        _owner.SetDebtDue(this, from.AddDays(n));
    }

    [RelayCommand]
    private void SetDue()
    {
        if (EditDueDate is null)
        {
            _owner.SetDebtDue(this, null);
            return;
        }
        _owner.SetDebtDue(this, TrackerDue.FromLocal(EditDueDate, EditDueTime, TimeZoneInfo.Local));
    }

    [RelayCommand]
    private void ClearDue() => _owner.SetDebtDue(this, null);

    public override string ToString() => Name;
}
