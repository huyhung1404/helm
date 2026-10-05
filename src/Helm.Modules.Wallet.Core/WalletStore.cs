using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>Sizes the store keeps to.</summary>
public static class WalletLimits
{
    public const int Description = 300;
    public const int Note = 1000;
    public const int CategoryName = 60;
    public const int Original = 2000;
    public const decimal MaxAmount = 1_000_000_000_000m;

    public static string Clip(string? text, int max)
    {
        var s = (text ?? "").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }
}

/// <summary>What happened to a transaction read from a notification.</summary>
public enum CaptureKind
{
    Added,

    /// <summary>Already saved (the same transaction from the app and the SMS, or a notification shown again).</summary>
    Duplicate,
}

public sealed record CaptureOutcome(CaptureKind Kind, string Id, WalletTransaction Transaction);

/// <summary>
/// Money that moved in an account without a notification: the balance the bank gave does not follow from the one
/// before it. Techcombank, for one, says nothing about a payment made in its own app.
/// </summary>
/// <param name="Amount">Signed: negative went out, positive came in.</param>
/// <param name="After">When the bank gave the earlier balance.</param>
/// <param name="Before">When the bank gave the balance that showed the gap.</param>
public sealed record BalanceGap(string Bank, string Account, decimal Amount, DateTimeOffset After, DateTimeOffset Before);

/// <summary>
/// Transactions, categories and the budget on top of Helm Sync. Every write is local and immediate (the sync engine
/// uploads it in the background). Callable from any thread (the phone's notification listener writes here while the
/// pages read); <see cref="Changed"/> may be raised on a background thread after a sync.
/// </summary>
public sealed class WalletStore
{
    public const string TransactionsCollection = "wallet.transactions";
    public const string CategoriesCollection = "wallet.categories";
    public const string BudgetCollection = "wallet.budget";
    public const string BudgetId = "monthly";

    /// <summary>How far apart the same transaction can be read twice and still be recognised as one.</summary>
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromDays(3);

    private readonly ISyncedCollection<WalletTransaction> _transactions;
    private readonly ISyncedCollection<WalletCategory> _categories;
    private readonly ISyncedCollection<WalletBudget> _budget;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public WalletStore(ISyncedCollection<WalletTransaction> transactions, ISyncedCollection<WalletCategory> categories, ISyncedCollection<WalletBudget> budget, TimeProvider? time = null)
    {
        _transactions = transactions;
        _categories = categories;
        _budget = budget;
        _time = time ?? TimeProvider.System;
        _transactions.Changed += OnChanged;
        _categories.Changed += OnChanged;
        _budget.Changed += OnChanged;
    }

    /// <summary>Raised after any local write or synced change (on the writing thread or a thread-pool thread).</summary>
    public event EventHandler? Changed;

    public DateTimeOffset Now => _time.GetUtcNow();

    private void OnChanged(object? sender, SyncedChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    // ---- Transactions --------------------------------------------------------------------------------------------

    /// <summary>All transactions, newest first.</summary>
    public IReadOnlyList<SyncedItem<WalletTransaction>> Transactions() =>
        _transactions.All().OrderByDescending(t => t.Value.OccurredAt).ThenByDescending(t => t.Id, StringComparer.Ordinal).ToList();

    public WalletTransaction? Get(string id) => _transactions.Get(id);

    /// <summary>Transactions waiting for a category, newest first.</summary>
    public IReadOnlyList<SyncedItem<WalletTransaction>> Uncategorized() => Transactions().Where(t => !t.Value.IsCategorized).ToList();

    /// <summary>
    /// Saves a transaction read from a notification, unless it is already saved. With <paramref name="autoCategorize"/>,
    /// a transaction whose description is the same as earlier ones that all went in one category goes there too.
    /// </summary>
    public CaptureOutcome AddCaptured(ParsedTransaction parsed, bool autoCategorize)
    {
        lock (_gate)
        {
            var fingerprint = parsed.Fingerprint;
            foreach (var t in _transactions.All())
            {
                if (t.Value.Fingerprint == fingerprint && (t.Value.OccurredAt - parsed.OccurredAt).Duration() <= DuplicateWindow)
                    return new CaptureOutcome(CaptureKind.Duplicate, t.Id, t.Value);
            }
            var description = WalletLimits.Clip(parsed.Description, WalletLimits.Description);
            var category = autoCategorize ? SureCategory(description, parsed.Amount) : null;
            var transaction = new WalletTransaction
            {
                Amount = ClampAmount(parsed.Amount),
                Bank = parsed.Bank,
                Account = WalletLimits.Clip(parsed.Account, 40),
                Balance = parsed.Balance,
                Description = description,
                OccurredAt = parsed.OccurredAt,
                CategoryId = category,
                AutoCategorized = category is not null,
                Source = TransactionSource.Notification,
                Original = WalletLimits.Clip(parsed.Original, WalletLimits.Original),
                Fingerprint = fingerprint,
                CreatedAt = Now,
            };
            var id = _transactions.Add(transaction);
            return new CaptureOutcome(CaptureKind.Added, id, transaction);
        }
    }

    /// <summary>Saves a transaction typed by hand; returns its id.</summary>
    /// <param name="amount">Signed: negative is money out.</param>
    public string AddManual(decimal amount, string description, DateTimeOffset occurredAt, string? categoryId, string note = "")
    {
        if (amount == 0) throw new ArgumentException("Enter an amount.", nameof(amount));
        lock (_gate)
        {
            return _transactions.Add(new WalletTransaction
            {
                Amount = ClampAmount(amount),
                Description = WalletLimits.Clip(description, WalletLimits.Description),
                OccurredAt = occurredAt,
                CategoryId = categoryId,
                Note = WalletLimits.Clip(note, WalletLimits.Note),
                Source = TransactionSource.Manual,
                CreatedAt = Now,
            });
        }
    }

    /// <summary>A balance older than this says nothing useful about what is missing (Helm may not have been listening).</summary>
    public static readonly TimeSpan GapWindow = TimeSpan.FromDays(31);

    /// <summary>What a gap is saved as, so the pages say where it came from.</summary>
    public const string GapDescription = "Not in a bank notification (from the balance)";

    /// <summary>
    /// The money that moved without a notification before transaction <paramref name="id"/>: its balance minus the
    /// account's previous balance and its own amount. Null when nothing is missing, when the account has no earlier
    /// balance within <see cref="GapWindow"/>, or when an entry typed by hand between the two already has that amount
    /// (the user wrote it down, or answered an earlier reminder).
    /// </summary>
    public BalanceGap? FindGap(string id)
    {
        lock (_gate)
        {
            if (_transactions.Get(id) is not { Balance: { } balance, Bank.Length: > 0 } t) return null;
            WalletTransaction? previous = null;
            foreach (var other in _transactions.All())
            {
                var o = other.Value;
                if (other.Id == id || o.Balance is null || o.Bank != t.Bank || o.Account != t.Account) continue;
                if (o.OccurredAt > t.OccurredAt || o.OccurredAt == t.OccurredAt && o.CreatedAt >= t.CreatedAt) continue;
                if (previous is null || o.OccurredAt > previous.OccurredAt || o.OccurredAt == previous.OccurredAt && o.CreatedAt > previous.CreatedAt)
                    previous = o;
            }
            if (previous is null || t.OccurredAt - previous.OccurredAt > GapWindow) return null;
            var gap = balance - (previous.Balance!.Value + t.Amount);
            if (Math.Abs(gap) < 1) return null;
            var after = previous.OccurredAt;
            var noted = _transactions.All().Any(o => o.Value.Source == TransactionSource.Manual && o.Value.Amount == gap
                && o.Value.OccurredAt >= after && o.Value.OccurredAt <= t.OccurredAt);
            return noted ? null : new BalanceGap(t.Bank, t.Account, gap, after, t.OccurredAt);
        }
    }

    /// <summary>Saves a gap the user confirmed, just before the notification that showed it (the real time is unknown).</summary>
    public string AddGap(BalanceGap gap, string? categoryId)
    {
        if (gap.Amount == 0) throw new ArgumentException("Nothing is missing.", nameof(gap));
        lock (_gate)
        {
            return _transactions.Add(new WalletTransaction
            {
                Amount = ClampAmount(gap.Amount),
                Bank = gap.Bank,
                Account = gap.Account,
                Description = GapDescription,
                OccurredAt = gap.Before.AddSeconds(-1),
                CategoryId = categoryId,
                Source = TransactionSource.Manual,
                CreatedAt = Now,
            });
        }
    }

    /// <summary>Puts a transaction in a category (null: back to "to categorize").</summary>
    public bool SetCategory(string id, string? categoryId)
    {
        lock (_gate)
        {
            if (_transactions.Get(id) is not { } t) return false;
            _transactions.Upsert(id, t with { CategoryId = categoryId, AutoCategorized = false });
            return true;
        }
    }

    /// <summary>Changes what the user can edit: amount, description, date, note.</summary>
    public bool Update(string id, decimal amount, string description, DateTimeOffset occurredAt, string note)
    {
        if (amount == 0) return false;
        lock (_gate)
        {
            if (_transactions.Get(id) is not { } t) return false;
            var updated = t with
            {
                Amount = ClampAmount(amount),
                Description = WalletLimits.Clip(description, WalletLimits.Description),
                OccurredAt = occurredAt,
                Note = WalletLimits.Clip(note, WalletLimits.Note),
            };
            // A category for the other direction no longer fits (e.g. Food after turning the amount into income).
            if (updated.CategoryId is { } c && Category(c) is { } info && !info.Fits(updated.Amount)) updated = updated with { CategoryId = null, AutoCategorized = false };
            _transactions.Upsert(id, updated);
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate) return _transactions.Delete(id);
    }

    private static decimal ClampAmount(decimal amount) => Math.Clamp(decimal.Round(amount, 2), -WalletLimits.MaxAmount, WalletLimits.MaxAmount);

    // ---- Suggestions ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Categories for a transaction, best first: those of earlier transactions with a similar description, then the
    /// ones used most in that direction lately, then the rest in picker order.
    /// </summary>
    public IReadOnlyList<CategoryInfo> Suggest(WalletTransaction transaction, int count)
    {
        var categories = Categories().Where(c => c.Fits(transaction.Amount)).ToList();
        if (categories.Count == 0 || count <= 0) return [];
        var words = WalletText.Words(transaction.Description);
        var since = transaction.OccurredAt.AddDays(-120);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var t in _transactions.All())
        {
            if (t.Value.CategoryId is not { } c || ReferenceEquals(t.Value, transaction) || Math.Sign(t.Value.Amount) != Math.Sign(transaction.Amount)) continue;
            var similarity = WalletText.Similarity(words, WalletText.Words(t.Value.Description));
            var score = similarity >= 0.34 ? 10 + similarity * 10 : 0;
            // Lately used categories come next.
            if (t.Value.OccurredAt >= since) score += 0.1;
            scores[c] = scores.GetValueOrDefault(c) + score;
        }
        return categories
            .OrderByDescending(c => scores.GetValueOrDefault(c.Id))
            .ThenBy(c => c.Order)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// The category of earlier transactions with the same description (the same words), when there were at least two
    /// and all of them went in one category; null otherwise.
    /// </summary>
    internal string? SureCategory(string description, decimal amount)
    {
        var words = WalletText.Words(description);
        if (words.Count == 0) return null;
        string? found = null;
        var count = 0;
        foreach (var t in _transactions.All())
        {
            if (t.Value.CategoryId is not { } c || Math.Sign(t.Value.Amount) != Math.Sign(amount) || !words.SetEquals(WalletText.Words(t.Value.Description))) continue;
            if (found is not null && found != c) return null;
            found = c;
            count++;
        }
        return count >= 2 && found is not null && Category(found) is { Hidden: false } info && info.Fits(amount) ? found : null;
    }

    /// <summary>Transactions still to categorize whose description is like this one's (for "the same for similar ones").</summary>
    public IReadOnlyList<string> SimilarUncategorized(string id)
    {
        if (_transactions.Get(id) is not { } source) return [];
        var words = WalletText.Words(source.Description);
        if (words.Count == 0) return [];
        return _transactions.All()
            .Where(t => t.Id != id && !t.Value.IsCategorized && Math.Sign(t.Value.Amount) == Math.Sign(source.Amount)
                && WalletText.Similarity(words, WalletText.Words(t.Value.Description)) >= 0.6)
            .Select(t => t.Id)
            .ToList();
    }

    // ---- Categories ----------------------------------------------------------------------------------------------

    /// <summary>The built-in categories with the user's changes, then the user's own, in picker order.</summary>
    public IReadOnlyList<CategoryInfo> Categories(bool includeHidden = false)
    {
        var stored = _categories.All().ToDictionary(c => c.Id, c => c.Value, StringComparer.Ordinal);
        var list = new List<CategoryInfo>();
        foreach (var b in WalletCategories.BuiltIn)
        {
            list.Add(stored.TryGetValue(b.Id, out var changed)
                ? b with
                {
                    Name = changed.Name.Length > 0 ? changed.Name : b.Name,
                    Hidden = changed.Hidden,
                    Order = changed.Order,
                    Icon = changed.Icon.Length > 0 ? WalletIcons.Normalize(changed.Icon) : b.Icon,
                    Color = changed.Color is >= 1 and <= WalletPalette.Slots ? changed.Color : b.Color,
                }
                : b);
            stored.Remove(b.Id);
        }
        list.AddRange(stored.Select(s => new CategoryInfo(s.Key, s.Value.Name, s.Value.Kind, s.Value.Order, false, s.Value.Hidden,
            WalletIcons.Normalize(s.Value.Icon), s.Value.Color is >= 0 and <= WalletPalette.Slots ? s.Value.Color : 0)));
        return list.Where(c => includeHidden || !c.Hidden).OrderBy(c => c.Kind).ThenBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public CategoryInfo? Category(string id) => Categories(includeHidden: true).FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Adds a category of the user's; returns its id. Without a colour it gets the one its kind uses least, so new
    /// categories do not all look alike.
    /// </summary>
    public string AddCategory(string name, CategoryKind kind, string? icon = null, int? color = null)
    {
        var clipped = WalletLimits.Clip(name, WalletLimits.CategoryName);
        if (clipped.Length == 0) throw new ArgumentException("A category needs a name.", nameof(name));
        lock (_gate)
        {
            var all = Categories(includeHidden: true);
            if (all.Any(c => string.Equals(c.Name, clipped, StringComparison.CurrentCultureIgnoreCase) && c.Kind == kind))
                throw new ArgumentException($"There is already a category called “{clipped}”.", nameof(name));
            var order = all.Where(c => c.Kind == kind).Select(c => c.Order).DefaultIfEmpty(0).Max() + 1;
            var slot = color is >= 1 and <= WalletPalette.Slots ? color.Value : WalletPalette.NextSlot(all.Where(c => c.Kind == kind && !c.Hidden).Select(c => c.Color));
            return _categories.Add(new WalletCategory { Name = clipped, Kind = kind, Order = order, Icon = WalletIcons.Normalize(icon), Color = slot });
        }
    }

    public bool RenameCategory(string id, string name)
    {
        var clipped = WalletLimits.Clip(name, WalletLimits.CategoryName);
        if (clipped.Length == 0) return false;
        return Change(id, c => c with { Name = clipped });
    }

    /// <summary>Hides a category from the pickers (or shows it again); its transactions keep it.</summary>
    public bool SetCategoryHidden(string id, bool hidden) => Change(id, c => c with { Hidden = hidden });

    /// <summary>Changes a category's icon (one of <see cref="WalletIcons.Choices"/>) and colour slot (1–8).</summary>
    public bool SetCategoryLook(string id, string icon, int color) =>
        Change(id, c => c with { Icon = WalletIcons.Normalize(icon), Color = color is >= 0 and <= WalletPalette.Slots ? color : c.Color });

    /// <summary>Writes a category's whole record (a built-in one gets a record under its own id).</summary>
    private bool Change(string id, Func<CategoryInfo, CategoryInfo> change)
    {
        lock (_gate)
        {
            if (Category(id) is not { } c) return false;
            var n = change(c);
            _categories.Upsert(id, new WalletCategory { Name = n.Name, Kind = n.Kind, Order = n.Order, Hidden = n.Hidden, Icon = n.Icon, Color = n.Color });
            return true;
        }
    }

    /// <summary>Deletes a category of the user's; its transactions go back to "to categorize". Built-in ones can only be hidden.</summary>
    public bool DeleteCategory(string id)
    {
        if (WalletCategories.IsBuiltIn(id)) return false;
        lock (_gate)
        {
            if (!_categories.Delete(id)) return false;
            foreach (var t in _transactions.All().Where(t => t.Value.CategoryId == id))
                _transactions.Upsert(t.Id, t.Value with { CategoryId = null, AutoCategorized = false });
            return true;
        }
    }

    // ---- Budget --------------------------------------------------------------------------------------------------

    /// <summary>The monthly budget; 0 for none.</summary>
    public decimal MonthlyBudget => _budget.Get(BudgetId)?.Monthly ?? 0;

    public void SetMonthlyBudget(decimal amount)
    {
        lock (_gate)
        {
            var value = Math.Clamp(decimal.Round(amount, 0), 0, WalletLimits.MaxAmount);
            if (value == MonthlyBudget) return;
            _budget.Upsert(BudgetId, new WalletBudget { Monthly = value });
        }
    }
}
