using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>Money moved into (positive) or out of (negative) a fund for one month only ("Cover it…").</summary>
public sealed record FundMove
{
    /// <summary>The first day of the month the move counts in.</summary>
    public DateOnly Month { get; init; }

    public decimal Amount { get; init; }
}

/// <summary>
/// A fund (synced record in <c>wallet.funds</c>): an amount for a month that the spending in its categories comes off.
/// It comes back every month (<see cref="EveryMonth"/>) from <see cref="Month"/> on, or counts in <see cref="Month"/>
/// only. A category belongs to at most one fund in a month, so no spending is counted twice.
/// </summary>
public sealed record WalletFund
{
    public string Name { get; init; } = "";

    /// <summary>What the fund holds each month, in đồng.</summary>
    public decimal Amount { get; init; }

    /// <summary>Comes back every month with the same amount; false: only for <see cref="Month"/>.</summary>
    public bool EveryMonth { get; init; } = true;

    /// <summary>The first day of the month the fund starts (every month) or counts in (one month).</summary>
    public DateOnly Month { get; init; }

    /// <summary>The ids of the categories whose spending comes off the fund.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>An every-month fund's leftover (or overspend) moves into the next month's.</summary>
    public bool CarryOver { get; init; }

    /// <summary>How much may be spent (percent) before the fund warns; 100 warns only when it goes over.</summary>
    public int AlertPercent { get; init; } = 80;

    /// <summary>A colour slot of <see cref="WalletPalette"/> (1–8).</summary>
    public int Color { get; init; }

    /// <summary>Position on the pages (ascending).</summary>
    public double Order { get; init; }

    /// <summary>Money moved in or out for single months.</summary>
    public IReadOnlyList<FundMove> Moves { get; init; } = [];

    /// <summary>Whether the fund counts in <paramref name="month"/> (its first day).</summary>
    public bool CountsIn(DateOnly month) => EveryMonth ? Month <= month : Month == month;
}

/// <summary>Where a fund stands in a month, worst last.</summary>
public enum FundStatus
{
    OnTrack,

    /// <summary>Past its warning share, or spending at this pace would empty it before the month ends.</summary>
    Low,

    /// <summary>Nothing (or almost nothing) left.</summary>
    UsedUp,

    /// <summary>More spent than it held.</summary>
    Over,
}

/// <summary>A fund in a month: what it held (amount, moves and what carried over), what was spent from it and what is left.</summary>
/// <param name="RunsOutOn">The day the money would run out at this month's pace (the current month only); null when it lasts.</param>
public sealed record FundState(string Id, WalletFund Fund, decimal Budget, decimal Spent, decimal Left, double Share, DateOnly? RunsOutOn,
    FundStatus Status, decimal Carried)
{
    /// <summary>What is left as a share of what it held, 0–1 (0 when over).</summary>
    public double LeftShare => Budget <= 0 || Left <= 0 ? 0 : (double)Math.Min(1, Left / Budget);
}

/// <summary>The spending of a month that no fund holds: uncategorized, or in categories outside every fund.</summary>
public sealed record OutsideFunds(decimal Spent, IReadOnlyList<string?> CategoryIds);

/// <summary>
/// The funds on top of Helm Sync, and where each stands in a month. Callable from any thread; <see cref="Changed"/> may
/// be raised on a background thread after a sync.
/// </summary>
public sealed class FundBook
{
    public const string Collection = "wallet.funds";

    /// <summary>Months an every-month fund looks back to carry its leftover over (older months are let go).</summary>
    public const int CarryMonths = 24;

    public const int NameLength = 40;

    private readonly ISyncedCollection<WalletFund> _funds;
    private readonly WalletStore _wallet;
    private readonly object _gate = new();

    public FundBook(ISyncedCollection<WalletFund> funds, WalletStore wallet)
    {
        _funds = funds;
        _wallet = wallet;
        _funds.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public WalletStore Wallet => _wallet;

    public WalletFund? Get(string id) => _funds.Get(id);

    /// <summary>Every fund, in page order.</summary>
    public IReadOnlyList<SyncedItem<WalletFund>> All() =>
        _funds.All().OrderBy(f => f.Value.Order).ThenBy(f => f.Value.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The funds that count in <paramref name="month"/>, in page order.</summary>
    public IReadOnlyList<SyncedItem<WalletFund>> In(DateOnly month)
    {
        month = First(month);
        return All().Where(f => f.Value.CountsIn(month)).ToList();
    }

    /// <summary>The fund a category's spending comes off in <paramref name="month"/>; null outside every fund.</summary>
    public SyncedItem<WalletFund>? FundOf(string? categoryId, DateOnly month) =>
        categoryId is null ? null : In(month).FirstOrDefault(f => f.Value.Categories.Contains(categoryId, StringComparer.Ordinal));

    // ---- Changes -------------------------------------------------------------------------------------------------

    /// <summary>Adds a fund; returns its id. The categories it takes leave the funds that had them in the same months.</summary>
    public string Add(string name, decimal amount, bool everyMonth, DateOnly month, IEnumerable<string> categories, bool carryOver = false, int alertPercent = 80)
    {
        var fund = Check(new WalletFund
        {
            Name = name,
            Amount = amount,
            EveryMonth = everyMonth,
            Month = First(month),
            Categories = categories.Distinct(StringComparer.Ordinal).ToList(),
            CarryOver = carryOver && everyMonth,
            AlertPercent = alertPercent,
        });
        lock (_gate)
        {
            var all = _funds.All();
            fund = fund with
            {
                Order = all.Select(f => f.Value.Order).DefaultIfEmpty(0).Max() + 1,
                Color = WalletPalette.NextSlot(all.Select(f => f.Value.Color)),
            };
            var id = _funds.Add(fund);
            Release(id, fund);
            return id;
        }
    }

    /// <summary>Changes what the fund form edits; the moves, colour and order stay.</summary>
    public bool Update(string id, string name, decimal amount, bool everyMonth, IEnumerable<string> categories, bool carryOver, int alertPercent)
    {
        lock (_gate)
        {
            if (_funds.Get(id) is not { } old) return false;
            var fund = Check(old with
            {
                Name = name,
                Amount = amount,
                EveryMonth = everyMonth,
                Categories = categories.Distinct(StringComparer.Ordinal).ToList(),
                CarryOver = carryOver && everyMonth,
                AlertPercent = alertPercent,
            });
            _funds.Upsert(id, fund);
            Release(id, fund);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate) return _funds.Delete(id);
    }

    /// <summary>
    /// Moves money from one fund to another for one month only: the source holds less that month, the target more.
    /// Both keep their usual amounts in other months.
    /// </summary>
    public bool Move(string fromId, string toId, decimal amount, DateOnly month)
    {
        if (amount <= 0 || fromId == toId) return false;
        month = First(month);
        amount = Math.Min(decimal.Round(amount, 0), WalletLimits.MaxAmount);
        lock (_gate)
        {
            if (_funds.Get(fromId) is not { } source || _funds.Get(toId) is not { } target) return false;
            _funds.Upsert(fromId, source with { Moves = AddMove(source.Moves, month, -amount) });
            _funds.Upsert(toId, target with { Moves = AddMove(target.Moves, month, amount) });
            return true;
        }
    }

    private static IReadOnlyList<FundMove> AddMove(IReadOnlyList<FundMove> moves, DateOnly month, decimal amount)
    {
        var total = moves.Where(m => m.Month == month).Sum(m => m.Amount) + amount;
        var list = moves.Where(m => m.Month != month).ToList();
        if (total != 0) list.Add(new FundMove { Month = month, Amount = total });
        return list;
    }

    private static WalletFund Check(WalletFund fund)
    {
        var name = WalletLimits.Clip(fund.Name, NameLength);
        if (name.Length == 0) throw new ArgumentException("Give the fund a name.", nameof(fund));
        if (fund.Amount <= 0) throw new ArgumentException("Enter the fund's amount.", nameof(fund));
        return fund with
        {
            Name = name,
            Amount = Math.Min(decimal.Round(fund.Amount, 0), WalletLimits.MaxAmount),
            AlertPercent = fund.AlertPercent is 80 or 90 or 100 ? fund.AlertPercent : 80,
        };
    }

    /// <summary>A category lives in one fund: the ones <paramref name="fund"/> took leave every fund that shares a month with it.</summary>
    private void Release(string id, WalletFund fund)
    {
        foreach (var other in _funds.All())
        {
            if (other.Id == id || !Overlap(fund, other.Value)) continue;
            var kept = other.Value.Categories.Where(c => !fund.Categories.Contains(c, StringComparer.Ordinal)).ToList();
            if (kept.Count != other.Value.Categories.Count) _funds.Upsert(other.Id, other.Value with { Categories = kept });
        }
    }

    private static bool Overlap(WalletFund a, WalletFund b) => (a.EveryMonth, b.EveryMonth) switch
    {
        (true, true) => true,
        (true, false) => b.Month >= a.Month,
        (false, true) => a.Month >= b.Month,
        _ => a.Month == b.Month,
    };

    // ---- Where the funds stand -----------------------------------------------------------------------------------

    /// <summary>Every fund that counts in <paramref name="month"/>, in page order, with what it held, spent and has left.</summary>
    public IReadOnlyList<FundState> States(IEnumerable<SyncedItem<WalletTransaction>> transactions, DateOnly month, DateTimeOffset now, TimeZoneInfo zone) =>
        FundMath.States(In(month), Spending(transactions, zone), First(month), WalletFormat.LocalDay(now, zone));

    /// <summary>The month's spending that no fund holds.</summary>
    public OutsideFunds Outside(IEnumerable<SyncedItem<WalletTransaction>> transactions, DateOnly month, TimeZoneInfo zone)
    {
        month = First(month);
        var held = In(month).SelectMany(f => f.Value.Categories).ToHashSet(StringComparer.Ordinal);
        decimal spent = 0;
        var categories = new List<string?>();
        foreach (var s in Spending(transactions, zone))
        {
            if (s.Month != month || s.CategoryId is { } c && held.Contains(c)) continue;
            spent += s.Amount;
            if (!categories.Contains(s.CategoryId)) categories.Add(s.CategoryId);
        }
        return new OutsideFunds(spent, categories);
    }

    /// <summary>Money out, by month and category; transfers between the user's own accounts and debts are not spending.</summary>
    private IReadOnlyList<FundMath.Spend> Spending(IEnumerable<SyncedItem<WalletTransaction>> transactions, TimeZoneInfo zone)
    {
        var kinds = _wallet.Categories(includeHidden: true).ToDictionary(c => c.Id, c => c.Kind, StringComparer.Ordinal);
        var list = new List<FundMath.Spend>();
        foreach (var t in transactions)
        {
            var v = t.Value;
            if (v.Amount >= 0 || v.CategoryId is { } c && kinds.GetValueOrDefault(c) == CategoryKind.Transfer) continue;
            list.Add(new FundMath.Spend(First(WalletFormat.LocalDay(v.OccurredAt, zone)), WalletFormat.LocalDay(v.OccurredAt, zone), v.CategoryId, -v.Amount));
        }
        return list;
    }

    internal static DateOnly First(DateOnly day) => new(day.Year, day.Month, 1);
}

/// <summary>Where funds stand: pure functions over the spending, tested in the core.</summary>
public static class FundMath
{
    /// <summary>Within this share of what it held, a fund counts as used up.</summary>
    public const decimal UsedUpShare = 0.02m;

    /// <summary>Money out on a day, in a category (null: still to categorize).</summary>
    public sealed record Spend(DateOnly Month, DateOnly Day, string? CategoryId, decimal Amount);

    public static IReadOnlyList<FundState> States(IEnumerable<SyncedItem<WalletFund>> funds, IReadOnlyList<Spend> spending, DateOnly month, DateOnly today)
    {
        var byMonth = spending.Where(s => s.CategoryId is not null).ToLookup(s => (s.Month, s.CategoryId!));
        return funds.Select(f => State(f.Id, f.Value, byMonth, month, today)).ToList();
    }

    private static FundState State(string id, WalletFund fund, ILookup<(DateOnly, string), Spend> spending, DateOnly month, DateOnly today)
    {
        decimal Spent(DateOnly m) => fund.Categories.Sum(c => spending[(m, c)].Sum(s => s.Amount));
        decimal Held(DateOnly m) => fund.Amount + fund.Moves.Where(x => x.Month == m).Sum(x => x.Amount);

        // What carried over from the months before (every-month funds that carry their leftover).
        decimal carried = 0;
        if (fund.EveryMonth && fund.CarryOver)
        {
            var start = fund.Month > month.AddMonths(-FundBook.CarryMonths) ? fund.Month : month.AddMonths(-FundBook.CarryMonths);
            for (var m = start; m < month; m = m.AddMonths(1)) carried += Held(m) - Spent(m);
        }
        var budget = Held(month) + carried;
        var spent = Spent(month);
        var left = budget - spent;
        var share = budget > 0 ? (double)(spent / budget) : spent > 0 ? 1 : 0;

        // At this month's pace, the day the money runs out (the current month only).
        DateOnly? runsOut = null;
        var isCurrent = month.Year == today.Year && month.Month == today.Month;
        if (isCurrent && spent > 0 && left > 0)
        {
            var perDay = spent / today.Day;
            var day = today.Day + (int)Math.Floor(left / perDay);
            if (day < DateTime.DaysInMonth(month.Year, month.Month)) runsOut = new DateOnly(month.Year, month.Month, Math.Max(day, today.Day));
        }

        var status = left < 0 ? FundStatus.Over
            : budget <= 0 || left <= budget * UsedUpShare ? FundStatus.UsedUp
            : fund.AlertPercent < 100 && share * 100 >= fund.AlertPercent || runsOut is not null ? FundStatus.Low
            : FundStatus.OnTrack;
        return new FundState(id, fund, budget, spent, left, share, runsOut, status, carried);
    }
}

/// <summary>
/// When a fund warns on the phone: once it is past its warning share, used up or over, once per level and month. The
/// pace ("may run out ~30 Oct") stays on the page: early in a month one big payment would set it off.
/// </summary>
public static class FundAlerts
{
    /// <summary>A warning to show: the fund, how bad it is, and the words.</summary>
    public sealed record Warning(string FundId, FundStatus Level, string Title, string Text);

    /// <summary>The level a fund warns at now: over, used up, past its warning share, or none (on track).</summary>
    public static FundStatus Level(FundState state) => state.Status switch
    {
        FundStatus.Over or FundStatus.UsedUp => state.Status,
        _ when state.Fund.AlertPercent < 100 && state.Share * 100 >= state.Fund.AlertPercent => FundStatus.Low,
        _ => FundStatus.OnTrack,
    };

    /// <summary>
    /// The warnings due in <paramref name="month"/>: funds worse than they were last warned about. <paramref name="told"/>
    /// is updated (a fund that got better, e.g. after money moved in, may warn again later); older months are forgotten.
    /// </summary>
    public static IReadOnlyList<Warning> Due(IReadOnlyList<FundState> states, DateOnly month, IDictionary<string, FundStatus> told)
    {
        var prefix = $"{month:yyyy-MM}|";
        foreach (var stale in told.Keys.Where(k => !k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) told.Remove(stale);
        var due = new List<Warning>();
        foreach (var s in states)
        {
            var key = prefix + s.Id;
            var level = Level(s);
            var before = told.TryGetValue(key, out var t) ? t : FundStatus.OnTrack;
            if (level > before) due.Add(Describe(s, level));
            if (level != before)
            {
                if (level == FundStatus.OnTrack) told.Remove(key);
                else told[key] = level;
            }
        }
        return due;
    }

    private static Warning Describe(FundState s, FundStatus level)
    {
        var name = s.Fund.Name;
        var of = $"{WalletFormat.Money(s.Spent)} of {WalletFormat.Money(s.Budget)}";
        return level switch
        {
            FundStatus.Over => new(s.Id, level, $"{name} is over", $"Over by {WalletFormat.Money(-s.Left)} this month ({of})."),
            FundStatus.UsedUp => new(s.Id, level, $"{name} is used up", $"{WalletFormat.Money(Math.Max(0, s.Left))} left ({of})."),
            _ => new(s.Id, level, $"{name} is running low", $"{WalletFormat.Money(s.Left)} left · {Math.Round(s.Share * 100):0}% spent ({of})."),
        };
    }
}
