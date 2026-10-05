using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Links;
using Helm.Core.Services;
using Helm.Shell.Services;
using Microsoft.Extensions.Logging;

namespace Helm.Modules.Wallet;

/// <summary>
/// The Wallet's debt book, shared by the Windows and Android pages: the totals, adding an entry, everyone with a
/// balance and everyone settled. All members are used on the UI thread; store changes are posted through
/// <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class DebtsViewModel : ObservableObject
{
    /// <summary>Settlements listed at most.</summary>
    public const int SettledLimit = 50;

    private readonly DebtBook _book;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly ILogger<DebtsViewModel> _logger;
    private readonly LinkHub? _links;
    private readonly Dictionary<string, DebtPersonViewModel> _people = new(StringComparer.Ordinal);
    private int _refreshQueued;
    private string? _openKey;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newPerson = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newAmount = "";
    [ObservableProperty] private int _newKindIndex;
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private DateTime? _newDueDate;
    [ObservableProperty] private TimeSpan? _newDueTime;
    [ObservableProperty] private bool _showSettled;

    [ObservableProperty] private string _owedToMeText = "";
    [ObservableProperty] private string _iOweText = "";
    [ObservableProperty] private string _netText = "";
    [ObservableProperty] private string _openSummary = "";

    /// <summary>A problem or a notice about the last action; null when there is nothing to say.</summary>
    [ObservableProperty] private string? _message;

    public DebtsViewModel(DebtBook book, IUiDispatcher ui, IDialogService dialogs, ILogger<DebtsViewModel> logger, LinkHub? links = null)
    {
        _book = book;
        _ui = ui;
        _dialogs = dialogs;
        _logger = logger;
        _links = links;
        _book.Changed += (_, _) => ScheduleRefresh();
        if (_links is not null) _links.Changed += (_, _) => _ui.Post(() =>
        {
            foreach (var person in _people.Values) person.RefreshLinks();
        });
        Refresh();
    }

    internal DebtBook Book => _book;

    internal DateTimeOffset Now => _book.Now;

    public static IReadOnlyList<string> KindNames => DebtFormat.KindNames;

    /// <summary>People with a balance, the largest first.</summary>
    public ObservableCollection<DebtPersonViewModel> People { get; } = [];

    /// <summary>People whose balance reached 0, most recently settled first.</summary>
    public ObservableCollection<DebtPersonViewModel> SettledPeople { get; } = [];

    /// <summary>Names already in the book, for the person boxes (the same name adds to that person).</summary>
    public ObservableCollection<string> PersonNames { get; } = [];

    /// <summary>Up to 6 names that contain what is typed in the person box (not shown once it is an exact name).</summary>
    public ObservableCollection<string> PersonSuggestions { get; } = [];

    public bool HasPersonSuggestions => PersonSuggestions.Count > 0;

    public bool HasPeople => People.Count > 0;

    public bool HasNoPeople => People.Count == 0;

    public bool HasSettled => SettledPeople.Count > 0;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnNewPersonChanged(string value) => UpdatePersonSuggestions();

    [RelayCommand]
    private void DismissMessage() => Message = null;

    [RelayCommand]
    private void PickPerson(string? name)
    {
        if (name is { Length: > 0 }) NewPerson = name;
    }

    [RelayCommand]
    private void ToggleSettled() => ShowSettled = !ShowSettled;

    /// <summary>Shows a person with their details open (from a linked note, the calendar or a transaction).</summary>
    public void ShowPerson(string key)
    {
        _openKey = key;
        Refresh();
        if (People.Concat(SettledPeople).FirstOrDefault(p => p.Key == key) is { } person) person.Open();
    }

    // ---- Adding --------------------------------------------------------------------------------------------------

    private bool CanAdd() => NewPerson.Trim().Length > 0 && NewAmount.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (!WalletText.TryParseAmount(NewAmount, out var amount))
        {
            Message = $"“{NewAmount}” is not an amount. Try 150000, 150.000, 150k or 1.5tr.";
            return;
        }
        var kind = (DebtEntryKind)Math.Clamp(NewKindIndex, 0, KindNames.Count - 1);
        var due = DebtDue.FromLocal(NewDueDate, NewDueTime, TimeZoneInfo.Local);
        if (!Try(() => _book.Add(NewPerson, amount, kind, NewNote, due))) return;
        NewPerson = "";
        NewAmount = "";
        NewNote = "";
        NewDueDate = null;
        NewDueTime = null;
        NewKindIndex = 0;
        Message = null;
        Refresh();
    }

    /// <summary>The money of a transaction as a debt entry; the transaction then counts neither as spending nor income.</summary>
    internal bool AddFromTransaction(string transactionId, string person, DebtEntryKind kind, string note)
    {
        if (person.Trim().Length == 0)
        {
            Message = "Who is it with? Type a name.";
            return false;
        }
        return Try(() => _book.AddFromTransaction(transactionId, person, kind, note));
    }

    internal void SetDue(DebtPersonViewModel person, DateTimeOffset? due) => Try(() => _book.SetDue(person.Name, due));

    internal async Task DeleteEntryAsync(DebtPersonViewModel person, DebtEntryRow entry)
    {
        var detail = $"{entry.KindText} {entry.AmountText} for {person.Name} will be removed from all your devices and the balance changes by that much.";
        if (entry.FromTransaction) detail += " Its transaction goes back to the ones to categorize.";
        if (!await _dialogs.ConfirmAsync("Delete this entry?", detail, "Delete")) return;
        Try(() => _book.Delete(entry.Id));
    }

    internal LinksViewModel? LinksFor(DebtPersonViewModel person) =>
        _links?.Provider(LinkKinds.Note) is null ? null : new LinksViewModel(_links, new LinkRef(LinkKinds.Person, person.Key), [LinkKinds.Note], () => person.Name);

    private bool Try(Action action)
    {
        try
        {
            action();
            Refresh();
            return true;
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message.Split(" (Parameter", StringSplitOptions.None)[0];
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
        }
        return false;
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

    public void Refresh()
    {
        try
        {
            var now = _book.Now;
            var open = _book.Open();
            var settled = _book.Settled().Take(SettledLimit);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            DebtPersonViewModel Person(DebtPerson p, string key)
            {
                seen.Add(key);
                if (_people.TryGetValue(key, out var vm)) vm.Update(p, now);
                else _people[key] = vm = new DebtPersonViewModel(this, p, now);
                return vm;
            }
            var owing = open.Where(p => !p.IsSettled).Select(p => Person(p, "open|" + p.Key)).ToList();
            var square = open.Where(p => p.IsSettled).Select(p => Person(p, "zero|" + p.Key))
                .Concat(settled.Select(p => Person(p, $"settled|{p.Key}|{p.SettledAt:O}"))).ToList();
            foreach (var stale in _people.Keys.Where(k => !seen.Contains(k)).ToList()) _people.Remove(stale);
            Reconcile(People, owing);
            Reconcile(SettledPeople, square);
            Reconcile(PersonNames, _book.Names());
            UpdatePersonSuggestions();

            var owedToMe = owing.Where(p => p.OwedToMe).Sum(p => p.Balance);
            var iOwe = -owing.Where(p => p.IOwe).Sum(p => p.Balance);
            OwedToMeText = WalletFormat.Money(owedToMe);
            IOweText = WalletFormat.Money(iOwe);
            NetText = DebtFormat.Balance(owedToMe - iOwe);
            OpenSummary = owing.Count == 1 ? "1 person" : $"{owing.Count} people";
            OnPropertyChanged(nameof(HasPeople));
            OnPropertyChanged(nameof(HasNoPeople));
            OnPropertyChanged(nameof(HasSettled));

            if (_openKey is { } key && People.Concat(SettledPeople).FirstOrDefault(p => p.Key == key) is { } shown)
            {
                _openKey = null;
                shown.Open();
                if (shown.IsSettled) ShowSettled = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not refresh the debt book");
            Message = "Could not read your debts. Try again in a moment.";
        }
    }

    private void UpdatePersonSuggestions()
    {
        var typed = DebtLedger.Clean(NewPerson);
        var matches = typed.Length == 0 || PersonNames.Any(n => DebtLedger.Key(n) == DebtLedger.Key(typed))
            ? []
            : PersonNames.Where(n => n.Contains(typed, StringComparison.CurrentCultureIgnoreCase)).Take(6).ToList();
        if (PersonSuggestions.SequenceEqual(matches)) return;
        PersonSuggestions.Clear();
        foreach (var name in matches) PersonSuggestions.Add(name);
        OnPropertyChanged(nameof(HasPersonSuggestions));
    }

    /// <summary>Makes the list hold exactly these items in this order, touching only what changed (open rows stay open).</summary>
    private static void Reconcile<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
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
}

/// <summary>One entry in a person's history: when, what, how much, and the balance right after it.</summary>
public sealed partial class DebtEntryRow(DebtsViewModel owner, DebtPersonViewModel person, DebtLedgerEntry entry) : ObservableObject
{
    public string Id { get; } = entry.Id;

    public string When { get; } = DebtFormat.When(entry.Debt.CreatedAt);

    public string KindText { get; } = DebtFormat.Kind(entry.Kind);

    /// <summary>"+500,000 ₫" (the balance went up: they owe more) or "−200,000 ₫".</summary>
    public string AmountText { get; } = (entry.Debt.SignedAmount >= 0 ? "+" : "−") + WalletFormat.Money(entry.Debt.Amount);

    public string BalanceText { get; } = "= " + DebtFormat.Balance(entry.BalanceAfter);

    public string Note { get; } = entry.Debt.Note;

    public bool HasNote => Note.Length > 0;

    public bool IsIncrease => entry.Debt.SignedAmount > 0;

    /// <summary>The money moved in a Wallet transaction (shown with a bank icon).</summary>
    public bool FromTransaction { get; } = entry.Debt.TransactionId is not null;

    [RelayCommand]
    private Task DeleteAsync() => owner.DeleteEntryAsync(person, this);
}

/// <summary>
/// A person in the debt book: their balance (every entry with the same name adds up), their history, and when it is
/// due. There is no tick box: a person is settled when the balance reaches 0. Amounts are never edited, only added
/// (owes me, I owe, repayment), so the history shows every change. A tap on the row opens the details (history and
/// due time); another tap closes them. Instances are kept across refreshes so an open row stays open.
/// </summary>
public sealed partial class DebtPersonViewModel : ObservableObject
{
    private readonly DebtsViewModel _owner;
    private DebtPerson _person;
    private DateTimeOffset _now;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private DateTime? _editDueDate;
    [ObservableProperty] private TimeSpan? _editDueTime;

    internal DebtPersonViewModel(DebtsViewModel owner, DebtPerson person, DateTimeOffset now)
    {
        _owner = owner;
        _person = person;
        _now = now;
        Rebuild();
    }

    public string Key => _person.Key;

    private LinksViewModel? _links;
    private bool _linksMade;

    /// <summary>The notes linked to this person; null without the Notes tool.</summary>
    public LinksViewModel? Links
    {
        get
        {
            if (!_linksMade)
            {
                _linksMade = true;
                _links = _owner.LinksFor(this);
            }
            return _links;
        }
    }

    internal void RefreshLinks() => _links?.Refresh();

    /// <summary>Opens the details; a no-op when they are open.</summary>
    internal void Open()
    {
        if (!IsExpanded) ToggleExpanded();
    }

    public string Name => _person.Name;

    internal decimal Balance => _person.Balance;

    public bool IsSettled => _person.SettledAt is not null || _person.IsSettled;

    /// <summary>"+1,500,000 ₫" when they owe the user, "−200,000 ₫" when the user owes them, "Settled" at 0.</summary>
    public string BalanceText => IsSettled ? "Settled" : DebtFormat.Balance(_person.Balance);

    public bool OwedToMe => !IsSettled && _person.Balance > 0;

    public bool IOwe => !IsSettled && _person.Balance < 0;

    public DateTimeOffset? Due => IsSettled ? null : _person.Due(TimeZoneInfo.Local);

    public bool IsOverdue => Due is { } due && due < _now;

    /// <summary>"Owes me · 3 entries · Due tomorrow 18:00" (open) or "Settled Yesterday · 2 entries".</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (_person.SettledAt is { } at) parts.Add("Settled " + DebtFormat.WhenRelative(at, _now));
            else if (OwedToMe) parts.Add("Owes me");
            else if (IOwe) parts.Add("I owe");
            parts.Add(_person.Entries.Count == 1 ? "1 entry" : $"{_person.Entries.Count} entries");
            if (Due is { } due) parts.Add(DebtFormat.Due(due, _person.HasDueTime, _now));
            return string.Join(" · ", parts);
        }
    }

    public ObservableCollection<DebtEntryRow> Entries { get; } = [];

    internal void Update(DebtPerson person, DateTimeOffset now)
    {
        if (person == _person && now == _now) return;
        var dueBefore = Due;
        _person = person;
        _now = now;
        Rebuild();
        OnPropertyChanged(string.Empty);
        // The date and time boxes show the new due time after +1 day, Set or a sync.
        if (IsExpanded && Due != dueBefore) LoadEditDue();
    }

    private void Rebuild()
    {
        Entries.Clear();
        // Newest first, like a bank statement.
        foreach (var entry in _person.Entries.Reverse()) Entries.Add(new DebtEntryRow(_owner, this, entry));
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded) LoadEditDue();
    }

    private void LoadEditDue()
    {
        var due = Due is { } d ? TimeZoneInfo.ConvertTime(d, TimeZoneInfo.Local) : (DateTimeOffset?)null;
        EditDueDate = due?.Date;
        EditDueTime = due is { } t && _person.HasDueTime ? t.TimeOfDay : null;
    }

    /// <summary>Moves the due time later by a number of days ("1", "7", "30"); from now when there was none.</summary>
    [RelayCommand]
    private void Extend(string? days)
    {
        if (!int.TryParse(days, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return;
        var from = Due is { } due && due > _now ? due : _now;
        _owner.SetDue(this, from.AddDays(n));
    }

    [RelayCommand]
    private void SetDue() => _owner.SetDue(this, DebtDue.FromLocal(EditDueDate, EditDueTime, TimeZoneInfo.Local));

    [RelayCommand]
    private void ClearDue() => _owner.SetDue(this, null);

    public override string ToString() => Name;
}
