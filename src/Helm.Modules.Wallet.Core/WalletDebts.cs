using System.Text.Json.Serialization;
using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>Which way a debt entry moves the balance.</summary>
public enum DebtDirection
{
    /// <summary>The person owes the user.</summary>
    TheyOweMe,

    /// <summary>The user owes the person.</summary>
    IOwe,
}

/// <summary>
/// What the user adds to a person's debts. A repayment is stored as the direction that pulls the balance toward 0 (plus
/// <see cref="WalletDebt.IsRepayment"/>), so every entry adds up the same way.
/// </summary>
public enum DebtEntryKind
{
    OwesMe,
    IOwe,
    Repayment,
}

/// <summary>
/// One entry of the debt book (synced record in <c>wallet.debts</c>): who, how much and which way. Entries are never
/// edited, only added (owes me, I owe, repayment), so the book keeps every change of a balance. A person is settled when
/// their balance reaches 0: their open entries then get the same <see cref="SettledAt"/>.
/// </summary>
public sealed record WalletDebt
{
    public string Person { get; init; } = "";

    /// <summary>Always positive, in đồng; <see cref="Direction"/> says which way.</summary>
    public decimal Amount { get; init; }

    public DebtDirection Direction { get; init; }

    /// <summary>This entry is a repayment (its <see cref="Direction"/> pulls the balance toward 0).</summary>
    public bool IsRepayment { get; init; }

    /// <summary>What the money was for (may be empty).</summary>
    public string Note { get; init; } = "";

    /// <summary>The day it should be paid back, when no time was given (the end of that day, local).</summary>
    public DateOnly? DueDate { get; init; }

    /// <summary>When it should be paid back; null for a debt due on a day (<see cref="DueDate"/>) or not at all.</summary>
    public DateTimeOffset? DueAt { get; init; }

    /// <summary>When the money moved: the transaction's time for one made from a transaction, else when it was added.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the person's balance reached 0 with this entry open; null while it counts.</summary>
    public DateTimeOffset? SettledAt { get; init; }

    /// <summary>The Wallet transaction the money moved in (lent by bank transfer, paid back in cash…); null when typed in.</summary>
    public string? TransactionId { get; init; }

    [JsonIgnore]
    public bool IsSettled => SettledAt is not null;

    /// <summary>+Amount when the person owes the user, −Amount when the user owes them.</summary>
    [JsonIgnore]
    public decimal SignedAmount => Direction == DebtDirection.TheyOweMe ? Amount : -Amount;

    [JsonIgnore]
    public DebtEntryKind Kind => IsRepayment ? DebtEntryKind.Repayment : Direction == DebtDirection.TheyOweMe ? DebtEntryKind.OwesMe : DebtEntryKind.IOwe;

    /// <summary>When it is due: its time, or the end of its due day (local) when only a day was set.</summary>
    public DateTimeOffset? DueMoment(TimeZoneInfo zone) => DebtDue.Moment(DueAt, DueDate, zone);
}

/// <summary>Due dates with an optional time (DueAt) or only a day (DueDate).</summary>
public static class DebtDue
{
    public static DateTimeOffset? Moment(DateTimeOffset? dueAt, DateOnly? dueDate, TimeZoneInfo zone)
    {
        if (dueAt is { } at) return at;
        if (dueDate is not { } day) return null;
        var local = day.ToDateTime(new TimeOnly(23, 59));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The local day of a due time.</summary>
    public static DateOnly? Day(DateTimeOffset? dueAt, TimeZoneInfo zone) =>
        dueAt is { } at ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime) : null;

    /// <summary>A date picked in the UI plus an optional time of day (local); null when no date.</summary>
    public static DateTimeOffset? FromLocal(DateTime? date, TimeSpan? time, TimeZoneInfo zone)
    {
        if (date is not { } d) return null;
        var local = d.Date + (time ?? new TimeSpan(23, 59, 0));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}

/// <summary>One entry of a person in the debt book, with the balance right after it.</summary>
public sealed record DebtLedgerEntry(string Id, WalletDebt Debt, decimal BalanceAfter)
{
    public DebtEntryKind Kind => Debt.Kind;
}

/// <summary>
/// A person in the debt book: every entry of theirs since they were last settled, and what it adds up to. A balance
/// of 0 means settled.
/// </summary>
public sealed record DebtPerson(string Key, string Name, decimal Balance, IReadOnlyList<DebtLedgerEntry> Entries, DateTimeOffset? SettledAt)
{
    public bool IsSettled => Balance == 0;

    /// <summary>When the person's open debt is due (the latest due time among the entries).</summary>
    public DateTimeOffset? Due(TimeZoneInfo zone) => Entries.Select(e => e.Debt.DueMoment(zone)).Where(d => d is not null).Max();

    /// <summary>True when the due time has a time of day (not only a day).</summary>
    public bool HasDueTime => Entries.Any(e => e.Debt.DueAt is not null);
}

/// <summary>Groups debt entries by person (the same name, whatever its case and spacing).</summary>
public static class DebtLedger
{
    /// <summary>The name with its spacing tidied up.</summary>
    public static string Clean(string person) => string.Join(' ', person.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>What makes two names the same person (also the id a note's link to the person uses).</summary>
    public static string Key(string person) => Clean(person).ToLowerInvariant();

    /// <summary>People with open entries, the ones owing (or owed) the most first. A balance that is already 0 is settled.</summary>
    public static IReadOnlyList<DebtPerson> Open(IEnumerable<SyncedItem<WalletDebt>> debts) =>
        debts.Where(d => !d.Value.IsSettled)
            .GroupBy(d => Key(d.Value.Person))
            .Select(g => Build(g.Key, g, null))
            .OrderByDescending(p => Math.Abs(p.Balance)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Each time a person was settled (their entries settled together), most recent first.</summary>
    public static IReadOnlyList<DebtPerson> Settled(IEnumerable<SyncedItem<WalletDebt>> debts) =>
        debts.Where(d => d.Value.SettledAt is not null)
            .GroupBy(d => (Key: Key(d.Value.Person), At: d.Value.SettledAt!.Value))
            .Select(g => Build(g.Key.Key, g, g.Key.At))
            .OrderByDescending(p => p.SettledAt)
            .ToList();

    private static DebtPerson Build(string key, IEnumerable<SyncedItem<WalletDebt>> entries, DateTimeOffset? settledAt)
    {
        var ordered = entries.OrderBy(e => e.Value.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var running = 0m;
        var rows = new List<DebtLedgerEntry>();
        foreach (var (id, debt, _) in ordered)
        {
            running += debt.SignedAmount;
            rows.Add(new DebtLedgerEntry(id, debt, running));
        }
        var name = ordered.Select(e => e.Value.Person).FirstOrDefault(n => n.Length > 0) ?? "";
        return new DebtPerson(key, name.Length > 0 ? Clean(name) : "(no name)", settledAt is null ? running : 0, rows, settledAt);
    }
}

/// <summary>
/// The debt book on top of Helm Sync: who owes the user and whom the user owes. An entry can come from a Wallet
/// transaction (money lent by transfer, a repayment that came in); that transaction then goes in the built-in
/// <see cref="WalletCategories.Debts"/> category, which the statistics leave out (it is neither spending nor income).
/// Callable from any thread; <see cref="Changed"/> may be raised on a background thread after a sync.
/// </summary>
public sealed class DebtBook
{
    public const string Collection = "wallet.debts";

    private readonly ISyncedCollection<WalletDebt> _debts;
    private readonly WalletStore _wallet;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public DebtBook(ISyncedCollection<WalletDebt> debts, WalletStore wallet, TimeProvider? time = null)
    {
        _debts = debts;
        _wallet = wallet;
        _time = time ?? TimeProvider.System;
        _debts.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    public WalletDebt? Get(string id) => _debts.Get(id);

    /// <summary>Every entry, settled or not, unordered.</summary>
    public IReadOnlyList<SyncedItem<WalletDebt>> All() => _debts.All();

    /// <summary>People with a balance (or back to 0 but not settled yet), the largest first.</summary>
    public IReadOnlyList<DebtPerson> Open() => DebtLedger.Open(_debts.All());

    /// <summary>Each settlement, most recent first.</summary>
    public IReadOnlyList<DebtPerson> Settled() => DebtLedger.Settled(_debts.All());

    /// <summary>Everyone in the book once: open balances first, then a settled person with their latest settlement.</summary>
    public IReadOnlyList<DebtPerson> People()
    {
        var all = _debts.All();
        var open = DebtLedger.Open(all);
        var known = open.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        return open.Concat(DebtLedger.Settled(all).Where(p => known.Add(p.Key))).ToList();
    }

    /// <summary>Names already in the book, one per person, for the person boxes.</summary>
    public IReadOnlyList<string> Names() =>
        _debts.All().Select(d => DebtLedger.Clean(d.Value.Person)).Where(n => n.Length > 0)
            .DistinctBy(DebtLedger.Key).Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>What a person owes the user (+) or the user owes them (-), over their open entries.</summary>
    public decimal Balance(string person) => OpenOf(person).Sum(d => d.Value.SignedAmount);

    /// <summary>The entry made from a transaction; null when the transaction is not a debt.</summary>
    public SyncedItem<WalletDebt>? ForTransaction(string transactionId) =>
        _debts.All().FirstOrDefault(d => d.Value.TransactionId == transactionId);

    private List<SyncedItem<WalletDebt>> OpenOf(string person)
    {
        var key = DebtLedger.Key(person);
        return _debts.All().Where(d => !d.Value.IsSettled && DebtLedger.Key(d.Value.Person) == key).ToList();
    }

    /// <summary>
    /// Adds money to a person: owes me (+), I owe (-) or a repayment (toward 0). The same person (whatever the case and
    /// spacing of the name) shares one balance; when it reaches 0 every open entry of that person is settled together.
    /// With <paramref name="transactionId"/> the entry is that transaction's money: its time is the transaction's, and
    /// the transaction goes in <see cref="WalletCategories.Debts"/> (one entry per transaction).
    /// </summary>
    /// <returns>The new entry's id.</returns>
    public string Add(string person, decimal amount, DebtEntryKind kind, string note = "", DateTimeOffset? dueAt = null, string? transactionId = null)
    {
        lock (_gate)
        {
            person = DebtLedger.Clean(person);
            if (person.Length == 0) throw new ArgumentException("Who is it with? A debt needs a person.", nameof(person));
            amount = Math.Abs(decimal.Round(amount, 2));
            if (amount == 0) throw new ArgumentException("Enter an amount.", nameof(amount));
            if (amount > WalletLimits.MaxAmount) throw new ArgumentException("That amount is too large.", nameof(amount));
            WalletTransaction? transaction = null;
            if (transactionId is not null)
            {
                transaction = _wallet.Get(transactionId) ?? throw new InvalidOperationException("That transaction no longer exists.");
                if (ForTransaction(transactionId) is { } linked)
                    throw new InvalidOperationException($"This transaction is already in the debt book ({linked.Value.Person}).");
            }

            var open = OpenOf(person);
            var balance = open.Sum(d => d.Value.SignedAmount);
            // The name as it was first written, so one person does not show up twice.
            if (open.Count > 0) person = open.OrderBy(d => d.Value.CreatedAt).First().Value.Person;
            var direction = kind switch
            {
                DebtEntryKind.Repayment when balance == 0 => throw new InvalidOperationException($"{person} has nothing to repay: the balance is 0."),
                DebtEntryKind.Repayment => balance > 0 ? DebtDirection.IOwe : DebtDirection.TheyOweMe,
                DebtEntryKind.IOwe => DebtDirection.IOwe,
                _ => DebtDirection.TheyOweMe,
            };
            var due = dueAt ?? open.Select(d => d.Value.DueAt).Where(d => d is not null).Max();
            var debt = new WalletDebt
            {
                Person = person,
                Amount = amount,
                Direction = direction,
                IsRepayment = kind == DebtEntryKind.Repayment,
                Note = WalletLimits.Clip(note, WalletLimits.Note),
                DueAt = due,
                DueDate = due is { } at ? DebtDue.Day(at, TimeZoneInfo.Local) : open.Select(d => d.Value.DueDate).Where(d => d is not null).Max(),
                CreatedAt = transaction?.OccurredAt ?? Now,
                TransactionId = transactionId,
            };
            var id = _debts.Add(debt);
            if (transactionId is not null) _wallet.SetCategory(transactionId, WalletCategories.Debts);

            if (balance + debt.SignedAmount == 0)
            {
                var now = Now;
                foreach (var (entryId, entry, _) in OpenOf(person)) _debts.Upsert(entryId, entry with { SettledAt = now });
            }
            return id;
        }
    }

    /// <summary>A transaction's money as a debt entry of <paramref name="person"/> (see <see cref="Add"/>).</summary>
    public string AddFromTransaction(string transactionId, string person, DebtEntryKind kind, string note = "")
    {
        var transaction = _wallet.Get(transactionId) ?? throw new InvalidOperationException("That transaction no longer exists.");
        return Add(person, Math.Abs(transaction.Amount), kind, note, transactionId: transactionId);
    }

    /// <summary>Sets (or clears) when a person's open debts are due: every open entry of that person gets the new time.</summary>
    public int SetDue(string person, DateTimeOffset? dueAt)
    {
        lock (_gate)
        {
            var open = OpenOf(person);
            var day = DebtDue.Day(dueAt, TimeZoneInfo.Local);
            foreach (var (id, entry, _) in open)
            {
                var updated = entry with { DueAt = dueAt, DueDate = day };
                if (updated != entry) _debts.Upsert(id, updated);
            }
            return open.Count;
        }
    }

    /// <summary>
    /// Removes an entry. A transaction it was made from goes back to the ones to categorize (it no longer is a debt).
    /// </summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_debts.Get(id) is not { } debt) return false;
            if (!_debts.Delete(id)) return false;
            if (debt.TransactionId is { } tx && _wallet.Get(tx) is { CategoryId: WalletCategories.Debts })
                _wallet.SetCategory(tx, null);
            return true;
        }
    }

    /// <summary>
    /// Writes entries that come from elsewhere (the Tracker's old debt book) under their own ids, so doing it again,
    /// or on another device, writes the same records. An entry that is already there is left as it is, except that it
    /// is settled when the incoming one is.
    /// </summary>
    /// <returns>How many entries were written.</returns>
    public int Import(IEnumerable<(string Id, WalletDebt Debt)> entries)
    {
        var written = 0;
        lock (_gate)
        {
            foreach (var (id, debt) in entries)
            {
                if (_debts.Get(id) is { } existing)
                {
                    if (existing.SettledAt is not null || debt.SettledAt is null) continue;
                    _debts.Upsert(id, existing with { SettledAt = debt.SettledAt });
                }
                else
                {
                    _debts.Upsert(id, debt with { Person = DebtLedger.Clean(debt.Person), Amount = Math.Abs(debt.Amount) });
                }
                written++;
            }
        }
        return written;
    }
}
