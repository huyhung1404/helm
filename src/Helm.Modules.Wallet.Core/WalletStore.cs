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
                ? b with { Name = changed.Name.Length > 0 ? changed.Name : b.Name, Hidden = changed.Hidden, Order = changed.Order }
                : b);
            stored.Remove(b.Id);
        }
        list.AddRange(stored.Select(s => new CategoryInfo(s.Key, s.Value.Name, s.Value.Kind, s.Value.Order, false, s.Value.Hidden)));
        return list.Where(c => includeHidden || !c.Hidden).OrderBy(c => c.Kind).ThenBy(c => c.Order).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public CategoryInfo? Category(string id) => Categories(includeHidden: true).FirstOrDefault(c => c.Id == id);

    /// <summary>Adds a category of the user's; returns its id.</summary>
    public string AddCategory(string name, CategoryKind kind)
    {
        var clipped = WalletLimits.Clip(name, WalletLimits.CategoryName);
        if (clipped.Length == 0) throw new ArgumentException("A category needs a name.", nameof(name));
        lock (_gate)
        {
            var all = Categories(includeHidden: true);
            if (all.Any(c => string.Equals(c.Name, clipped, StringComparison.CurrentCultureIgnoreCase) && c.Kind == kind))
                throw new ArgumentException($"There is already a category called “{clipped}”.", nameof(name));
            var order = all.Where(c => c.Kind == kind).Select(c => c.Order).DefaultIfEmpty(0).Max() + 1;
            return _categories.Add(new WalletCategory { Name = clipped, Kind = kind, Order = order });
        }
    }

    public bool RenameCategory(string id, string name)
    {
        var clipped = WalletLimits.Clip(name, WalletLimits.CategoryName);
        if (clipped.Length == 0) return false;
        lock (_gate)
        {
            if (Category(id) is not { } c) return false;
            _categories.Upsert(id, new WalletCategory { Name = clipped, Kind = c.Kind, Order = c.Order, Hidden = c.Hidden });
            return true;
        }
    }

    /// <summary>Hides a category from the pickers (or shows it again); its transactions keep it.</summary>
    public bool SetCategoryHidden(string id, bool hidden)
    {
        lock (_gate)
        {
            if (Category(id) is not { } c) return false;
            _categories.Upsert(id, new WalletCategory { Name = c.Name, Kind = c.Kind, Order = c.Order, Hidden = hidden });
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
