using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>Money put towards a goal (positive) or taken back out of it (negative).</summary>
public sealed record GoalEntry
{
    public DateTimeOffset At { get; init; }

    public decimal Amount { get; init; }
}

public enum GoalState
{
    /// <summary>Still saving: what is set aside is held off the balance.</summary>
    Saving,

    /// <summary>Bought: the purchase is one spending transaction, the money set aside is let go.</summary>
    Bought,

    /// <summary>Given up: what was set aside went back to the balance.</summary>
    GaveUp,
}

/// <summary>
/// A goal (synced record in <c>wallet.goals</c>): something the user saves up for, its price and the money set aside for
/// it. While saving, that money is off the balance but is not spending: it is in no spending figure and no fund, and it
/// all comes back if the user gives up. The photo is a small JPEG in the blob store (its key lives only in this record).
/// </summary>
public sealed record WalletGoal
{
    public string Name { get; init; } = "";

    /// <summary>What it costs, in đồng.</summary>
    public decimal Price { get; init; }

    /// <summary>When the user wants it by; null when there is no date.</summary>
    public DateOnly? Due { get; init; }

    public BlobRef? Photo { get; init; }

    /// <summary>Every amount set aside or taken out, oldest first.</summary>
    public IReadOnlyList<GoalEntry> Entries { get; init; } = [];

    public GoalState State { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it was bought or given up.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>What it really cost (bought).</summary>
    public decimal? Paid { get; init; }

    /// <summary>The spending transaction of the purchase (bought).</summary>
    public string? TransactionId { get; init; }

    /// <summary>A colour slot of <see cref="WalletPalette"/> (1–8), for a goal without a photo.</summary>
    public int Color { get; init; }

    /// <summary>Position on the page (ascending).</summary>
    public double Order { get; init; }

    public decimal Saved => Entries.Sum(e => e.Amount);

    /// <summary>What is still missing (0 once there is enough).</summary>
    public decimal Left => Math.Max(0, Price - Saved);

    /// <summary>Saved as a share of the price, 0–1: how far the photo is lit.</summary>
    public double Share => Price <= 0 ? 0 : (double)Math.Clamp(Saved / Price, 0, 1);

    public bool IsReady => Price > 0 && Saved >= Price;

    public bool IsSaving => State == GoalState.Saving;

    /// <summary>The money it holds off the balance: what is set aside, while saving.</summary>
    public decimal Held => IsSaving ? Saved : 0;
}

/// <summary>
/// The goals on top of Helm Sync, and the balance they hold money off. Callable from any thread; <see cref="Changed"/>
/// may be raised on a background thread after a sync.
/// </summary>
public sealed class GoalBook
{
    public const string Collection = "wallet.goals";

    public const int NameLength = 40;

    /// <summary>The note on a purchase's transaction, so the Wallet shows where it came from.</summary>
    public const string PurchaseNote = "Bought with a savings goal";

    private readonly ISyncedCollection<WalletGoal> _goals;
    private readonly WalletStore _wallet;
    private readonly BlobStore? _blobs;
    private readonly object _gate = new();

    public GoalBook(ISyncedCollection<WalletGoal> goals, WalletStore wallet, BlobStore? blobs = null)
    {
        _goals = goals;
        _wallet = wallet;
        _blobs = blobs;
        _goals.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public WalletStore Wallet => _wallet;

    /// <summary>Whether goals can have photos here (the blob store is there; tests have none).</summary>
    public bool HasPhotos => _blobs is not null;

    /// <summary>The blobs a goal's record names: it syncs after them.</summary>
    public static IEnumerable<string> PhotosOf(WalletGoal goal) => goal.Photo is { } photo ? [photo.Id] : [];

    public WalletGoal? Get(string id) => _goals.Get(id);

    /// <summary>The goals being saved for (page order), then the bought and given-up ones (latest first).</summary>
    public IReadOnlyList<SyncedItem<WalletGoal>> All()
    {
        var all = _goals.All();
        return all.Where(g => g.Value.IsSaving).OrderBy(g => g.Value.Order).ThenBy(g => g.Value.CreatedAt)
            .Concat(all.Where(g => !g.Value.IsSaving).OrderByDescending(g => g.Value.EndedAt))
            .ToList();
    }

    public IReadOnlyList<SyncedItem<WalletGoal>> Saving() => All().Where(g => g.Value.IsSaving).ToList();

    /// <summary>A goal by its id, or by its name (folded, a single match); null when none or several match.</summary>
    public SyncedItem<WalletGoal>? Find(string idOrName)
    {
        var all = All();
        if (all.FirstOrDefault(g => g.Id == idOrName) is { } byId) return byId;
        var wanted = WalletText.Fold(idOrName.Trim());
        var named = all.Where(g => WalletText.Fold(g.Value.Name) == wanted).ToList();
        if (named.Count == 0) named = all.Where(g => WalletText.Fold(g.Value.Name).Contains(wanted, StringComparison.Ordinal)).ToList();
        var saving = named.Where(g => g.Value.IsSaving).ToList();
        return saving.Count == 1 ? saving[0] : named.Count == 1 ? named[0] : null;
    }

    /// <summary>All the money set aside in the goals being saved for.</summary>
    public decimal SetAside() => _goals.All().Sum(g => g.Value.Held);

    /// <summary>
    /// The money there is to spend: everything received minus everything spent until today (the widget's balance), minus
    /// what is set aside.
    /// </summary>
    public decimal Available(TimeZoneInfo zone) => WalletWidgetModel.Net(_wallet, WalletFormat.LocalDay(_wallet.Now, zone), zone) - SetAside();

    // ---- Changes -------------------------------------------------------------------------------------------------

    /// <summary>Adds a goal; returns its id. <paramref name="start"/> is money already saved for it (set aside now).</summary>
    /// <exception cref="ArgumentException">No name or price.</exception>
    /// <exception cref="InvalidOperationException">More to start with than is available.</exception>
    public string Add(string name, decimal price, DateOnly? due, decimal start, TimeZoneInfo zone)
    {
        var goal = Check(new WalletGoal { Name = name, Price = price, Due = due, CreatedAt = _wallet.Now });
        start = Math.Min(decimal.Round(Math.Max(0, start), 0), goal.Price);
        lock (_gate)
        {
            if (start > 0) Afford(start, zone);
            var all = _goals.All();
            goal = goal with
            {
                Order = all.Select(g => g.Value.Order).DefaultIfEmpty(0).Max() + 1,
                Color = WalletPalette.NextSlot(all.Where(g => g.Value.IsSaving).Select(g => g.Value.Color)),
                Entries = start > 0 ? [new GoalEntry { At = _wallet.Now, Amount = start }] : [],
            };
            return _goals.Add(goal);
        }
    }

    /// <summary>Changes what the goal form edits; the money set aside stays.</summary>
    public bool Update(string id, string name, decimal price, DateOnly? due)
    {
        lock (_gate)
        {
            if (_goals.Get(id) is not { } old) return false;
            _goals.Upsert(id, Check(old with { Name = name, Price = price, Due = due }));
            return true;
        }
    }

    /// <summary>
    /// Sets money aside: never more than is still missing. Returns the entry written (its amount may be less than
    /// asked), so it can be taken back.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not saving any more, already enough, or more than is available.</exception>
    public GoalEntry Put(string id, decimal amount, TimeZoneInfo zone)
    {
        if (amount <= 0) throw new InvalidOperationException("Enter the amount to set aside.");
        lock (_gate)
        {
            var goal = Saving(id);
            if (goal.IsReady) throw new InvalidOperationException($"{goal.Name} already has all it needs.");
            var entry = new GoalEntry { At = _wallet.Now, Amount = Math.Min(decimal.Round(amount, 0), goal.Left) };
            Afford(entry.Amount, zone);
            _goals.Upsert(id, goal with { Entries = [.. goal.Entries, entry] });
            return entry;
        }
    }

    /// <summary>Takes money back out: it is in the balance again. Returns the entry written.</summary>
    /// <exception cref="InvalidOperationException">Not saving any more, or more than is set aside.</exception>
    public GoalEntry TakeOut(string id, decimal amount)
    {
        if (amount <= 0) throw new InvalidOperationException("Enter the amount to take out.");
        lock (_gate)
        {
            var goal = Saving(id);
            amount = decimal.Round(amount, 0);
            if (amount > goal.Saved) throw new InvalidOperationException($"Only {WalletFormat.Money(goal.Saved)} is set aside for {goal.Name}.");
            var entry = new GoalEntry { At = _wallet.Now, Amount = -amount };
            _goals.Upsert(id, goal with { Entries = [.. goal.Entries, entry] });
            return entry;
        }
    }

    /// <summary>Takes back one entry (Undo of a set-aside or take-out).</summary>
    public bool Remove(string id, GoalEntry entry)
    {
        lock (_gate)
        {
            if (_goals.Get(id) is not { IsSaving: true } goal || !goal.Entries.Contains(entry)) return false;
            var list = goal.Entries.ToList();
            list.RemoveAt(list.LastIndexOf(entry));
            _goals.Upsert(id, goal with { Entries = list });
            return true;
        }
    }

    /// <summary>Gives the goal up: everything set aside goes back to the balance. Returns that amount.</summary>
    public decimal GiveUp(string id)
    {
        lock (_gate)
        {
            var goal = Saving(id);
            _goals.Upsert(id, goal with { State = GoalState.GaveUp, EndedAt = _wallet.Now });
            return goal.Saved;
        }
    }

    /// <summary>
    /// The user bought it: one spending transaction of what it really cost, and the money set aside is let go (a cheaper
    /// price leaves the rest in the balance; a dearer one takes the extra from it). Returns the transaction's id.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not saving any more, or the extra is more than is available.</exception>
    public string Buy(string id, decimal paid, string? categoryId, TimeZoneInfo zone)
    {
        if (paid <= 0) throw new InvalidOperationException("Enter what it cost.");
        paid = Math.Min(decimal.Round(paid, 0), WalletLimits.MaxAmount);
        lock (_gate)
        {
            var goal = Saving(id);
            if (paid > goal.Saved) Afford(paid - goal.Saved, zone);
            var transaction = _wallet.AddManual(-paid, goal.Name, _wallet.Now, categoryId, PurchaseNote);
            _goals.Upsert(id, goal with { State = GoalState.Bought, EndedAt = _wallet.Now, Paid = paid, TransactionId = transaction });
            return transaction;
        }
    }

    /// <summary>Back to saving (Undo of Give up or Buy): a purchase's transaction is deleted.</summary>
    public bool Reopen(string id)
    {
        lock (_gate)
        {
            if (_goals.Get(id) is not { IsSaving: false } goal) return false;
            if (goal.TransactionId is { } transaction) _wallet.Delete(transaction);
            _goals.Upsert(id, goal with { State = GoalState.Saving, EndedAt = null, Paid = null, TransactionId = null });
            return true;
        }
    }

    /// <summary>Deletes a goal that is bought or given up (a purchase's transaction stays: it is real spending).</summary>
    /// <exception cref="InvalidOperationException">Still saving: give it up first, so its money comes back.</exception>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_goals.Get(id) is not { } goal) return false;
            if (goal.IsSaving) throw new InvalidOperationException($"{goal.Name} still holds money: give it up first.");
            if (!_goals.Delete(id)) return false;
            if (goal.Photo is { } photo) _blobs?.Discard(photo.Id);
            return true;
        }
    }

    private WalletGoal Saving(string id) => _goals.Get(id) switch
    {
        null => throw new InvalidOperationException("That goal no longer exists."),
        { IsSaving: false } g => throw new InvalidOperationException($"{g.Name} is {(g.State == GoalState.Bought ? "bought" : "given up")} already."),
        var g => g,
    };

    /// <summary>Money can leave the balance only while there is that much in it.</summary>
    private void Afford(decimal amount, TimeZoneInfo zone)
    {
        var available = Available(zone);
        if (amount > available)
            throw new InvalidOperationException(available > 0 ? $"Only {WalletFormat.Money(available)} is available." : "There is no money available to set aside.");
    }

    private static WalletGoal Check(WalletGoal goal)
    {
        var name = WalletLimits.Clip(goal.Name, NameLength);
        if (name.Length == 0) throw new ArgumentException("Name what you are saving for.", nameof(goal));
        if (goal.Price <= 0) throw new ArgumentException("Enter its price.", nameof(goal));
        return goal with { Name = name, Price = Math.Min(decimal.Round(goal.Price, 0), WalletLimits.MaxAmount) };
    }

    // ---- Photos --------------------------------------------------------------------------------------------------

    /// <summary>Sets a goal's photo (already made small by the platform); the old one is dropped.</summary>
    public async Task SetPhotoAsync(string id, byte[] picture, CancellationToken ct = default)
    {
        if (_blobs is null || _goals.Get(id) is null) return;
        using var content = new MemoryStream(picture);
        var blob = await _blobs.ImportAsync(content, null, ct).ConfigureAwait(false);
        lock (_gate)
        {
            // Read again: the goal may have changed (money set aside) while the picture was stored.
            if (_goals.Get(id) is not { } goal)
            {
                _blobs.Discard(blob.Id);
                return;
            }
            _goals.Upsert(id, goal with { Photo = blob });
            if (goal.Photo is { } old) _blobs.Discard(old.Id);
        }
    }

    public void RemovePhoto(string id)
    {
        lock (_gate)
        {
            if (_goals.Get(id) is not { Photo: { } photo } goal) return;
            _goals.Upsert(id, goal with { Photo = null });
            _blobs?.Discard(photo.Id);
        }
    }

    /// <summary>The photo's JPEG, or null when there is none or it is not on this device yet.</summary>
    public async Task<byte[]?> ReadPhotoAsync(BlobRef photo, CancellationToken ct = default)
    {
        if (_blobs is null) return null;
        try
        {
            return await _blobs.ReadAllAsync(photo, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BlobUnavailableException or System.Security.Cryptography.CryptographicException or IOException
            or HttpRequestException or TaskCanceledException or SyncAuthException or SyncRequestException)
        {
            // Offline or a timeout: no photo this time; it is fetched again with the next refresh.
            return null;
        }
    }
}

/// <summary>How far a goal is, in words: shared by the pages, the widget and the AI tools.</summary>
public static class GoalMath
{
    /// <summary>
    /// What to set aside each week to have it by its date (rounded up to a thousand); null without a date, once there is
    /// enough, or when the date has passed.
    /// </summary>
    public static decimal? PerWeek(WalletGoal goal, DateOnly today)
    {
        if (goal.Due is not { } due || goal.Left <= 0 || due < today) return null;
        var weeks = Math.Max(1, (int)Math.Ceiling((due.DayNumber - today.DayNumber + 1) / 7.0));
        return Math.Ceiling(goal.Left / weeks / 1000) * 1000;
    }

    /// <summary>"33%": never 100% before there is enough (99.6% saved says 99%).</summary>
    public static string Percent(WalletGoal goal) => goal.IsReady ? "100%" : $"{Math.Floor(goal.Share * 100):0}%";
}
