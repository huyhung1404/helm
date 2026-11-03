using Helm.Core.Sync;
using Helm.Core.Text;

namespace Helm.Modules.Wallet;

/// <summary>
/// A bill that comes every month (electricity, water, internet, rent): synced record in <c>wallet.bills</c>. Paying
/// it saves a Wallet transaction and marks that month paid; the Tracker's daily reminder lists the months coming due.
/// </summary>
public sealed record WalletBill
{
    public string Name { get; init; } = "";

    /// <summary>The same every month, in đồng; null when it changes from month to month (electricity, water).</summary>
    public decimal? Amount { get; init; }

    /// <summary>The day of the month it is due (1-31; a shorter month's last day stands for 29-31).</summary>
    public int DueDay { get; init; }

    /// <summary>The spending category of its transactions; null: the one its name had before.</summary>
    public string? CategoryId { get; init; }

    /// <summary>The first month it counts for (its first day): a bill added after its day this month starts next month.</summary>
    public DateOnly StartMonth { get; init; }

    /// <summary>The months paid (their first days), the latest few.</summary>
    public IReadOnlyList<DateOnly> PaidMonths { get; init; } = [];

    /// <summary>What it came to last time, and when it was paid.</summary>
    public decimal? LastAmount { get; init; }
    public DateTimeOffset? LastPaidAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The day it is due in <paramref name="month"/>.</summary>
    public DateOnly DueIn(DateOnly month) => new(month.Year, month.Month, Math.Min(Math.Clamp(DueDay, 1, 31), DateTime.DaysInMonth(month.Year, month.Month)));

    /// <summary>The first month not paid yet.</summary>
    public DateOnly NextMonth()
    {
        var month = StartMonth;
        while (PaidMonths.Contains(month)) month = month.AddMonths(1);
        return month;
    }

    /// <summary>When the next unpaid month is due.</summary>
    public DateOnly NextDue() => DueIn(NextMonth());

    /// <summary>What a reminder or a list says it costs: the fixed amount, else about what it was last time.</summary>
    public decimal? Expected => Amount ?? LastAmount;
}

/// <summary>The month a payment was saved for, and what it changed (to take it back).</summary>
public sealed record BillPayment(string TransactionId, DateOnly Month, decimal Amount, decimal? PreviousLastAmount, DateTimeOffset? PreviousLastPaidAt);

/// <summary>The user's recurring bills on top of Helm Sync. Callable from any thread.</summary>
public sealed class BillBook
{
    public const string Collection = "wallet.bills";

    /// <summary>Paid months kept per bill.</summary>
    public const int KeepMonths = 24;

    private readonly ISyncedCollection<WalletBill> _bills;
    private readonly WalletStore _wallet;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public BillBook(ISyncedCollection<WalletBill> bills, WalletStore wallet, TimeProvider? time = null)
    {
        _bills = bills;
        _wallet = wallet;
        _time = time ?? TimeProvider.System;
    }

    public DateTimeOffset Now => _time.GetUtcNow();

    public WalletBill? Get(string id) => _bills.Get(id);

    /// <summary>Every bill, the one due soonest first.</summary>
    public IReadOnlyList<SyncedItem<WalletBill>> All() =>
        [.. _bills.All().OrderBy(b => b.Value.NextDue()).ThenBy(b => b.Value.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The bills whose next unpaid month is due on or before <paramref name="horizon"/> (overdue ones too).</summary>
    public IReadOnlyList<SyncedItem<WalletBill>> DueBy(DateOnly horizon) => [.. All().Where(b => b.Value.NextDue() <= horizon)];

    /// <summary>The bill of that name (accents and case aside), else the only one whose name contains it.</summary>
    public SyncedItem<WalletBill>? Find(string name)
    {
        var folded = TextSearch.Fold(name.Trim());
        if (folded.Length == 0) return null;
        var all = _bills.All();
        if (all.FirstOrDefault(b => TextSearch.Fold(b.Value.Name) == folded) is { } same) return same;
        var near = all.Where(b => TextSearch.Fold(b.Value.Name).Contains(folded, StringComparison.Ordinal) || folded.Contains(TextSearch.Fold(b.Value.Name), StringComparison.Ordinal)).ToList();
        return near.Count == 1 ? near[0] : null;
    }

    /// <summary>Adds a bill. It counts from this month when its day has not passed yet, else from next month.</summary>
    public string Add(string name, decimal? amount, int dueDay, string? categoryId = null)
    {
        name = DebtLedger.Clean(name);
        if (name.Length == 0) throw new ArgumentException("Name the bill.", nameof(name));
        if (dueDay is < 1 or > 31) throw new ArgumentException("The due day is 1 to 31.", nameof(dueDay));
        if (amount is { } a && (a <= 0 || a > WalletLimits.MaxAmount)) throw new ArgumentException("That amount is not a bill's.", nameof(amount));
        lock (_gate)
        {
            if (Find(name) is { } existing && TextSearch.Fold(existing.Value.Name) == TextSearch.Fold(name))
                throw new InvalidOperationException($"There is already a bill {existing.Value.Name}.");
            var today = WalletFormat.LocalDay(Now, TimeZoneInfo.Local);
            var month = new DateOnly(today.Year, today.Month, 1);
            var bill = new WalletBill { Name = WalletLimits.Clip(name, WalletLimits.Note), Amount = amount, DueDay = dueDay, CategoryId = categoryId, CreatedAt = Now };
            return _bills.Add(bill with { StartMonth = bill.DueIn(month) >= today ? month : month.AddMonths(1) });
        }
    }

    public bool Delete(string id) => _bills.Delete(id);

    /// <summary>
    /// Saves a payment: a spending transaction, and the month it pays marked paid. That month is the unpaid one due
    /// nearest to <paramref name="when"/> (last month, this one or next), so paying a few days late or early counts
    /// for the right month.
    /// </summary>
    public BillPayment Pay(string id, decimal? amount, DateTimeOffset? when = null)
    {
        lock (_gate)
        {
            var bill = _bills.Get(id) ?? throw new InvalidOperationException("That bill no longer exists.");
            var paid = amount ?? bill.Amount ?? throw new InvalidOperationException($"{bill.Name} changes every month: how much was it?");
            var at = when ?? Now;
            var day = WalletFormat.LocalDay(at, TimeZoneInfo.Local);
            var here = new DateOnly(day.Year, day.Month, 1);
            var month = new[] { here.AddMonths(-1), here, here.AddMonths(1) }
                .Where(m => !bill.PaidMonths.Contains(m))
                .OrderBy(m => Math.Abs(bill.DueIn(m).DayNumber - day.DayNumber)).ThenBy(m => m)
                .First();
            var transaction = _wallet.AddManual(-paid, bill.Name, at, bill.CategoryId ?? _wallet.SureCategory(bill.Name, -paid));
            _bills.Upsert(id, bill with
            {
                PaidMonths = [.. bill.PaidMonths.Append(month).Order().TakeLast(KeepMonths)],
                LastAmount = paid,
                LastPaidAt = at,
            });
            return new BillPayment(transaction, month, paid, bill.LastAmount, bill.LastPaidAt);
        }
    }

    /// <summary>Takes a payment back: its transaction goes, its month is unpaid again.</summary>
    public void Unpay(string id, BillPayment payment)
    {
        lock (_gate)
        {
            _wallet.Delete(payment.TransactionId);
            if (_bills.Get(id) is not { } bill) return;
            _bills.Upsert(id, bill with
            {
                PaidMonths = [.. bill.PaidMonths.Where(m => m != payment.Month)],
                LastAmount = payment.PreviousLastAmount,
                LastPaidAt = payment.PreviousLastPaidAt,
            });
        }
    }
}
