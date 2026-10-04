using System.Text.Json.Serialization;

namespace Helm.Modules.Wallet;

/// <summary>Where a transaction came from.</summary>
public enum TransactionSource
{
    /// <summary>Read from a bank's notification (or SMS) on the phone.</summary>
    Notification,

    /// <summary>Typed in by hand (cash, or a bank Helm does not listen to).</summary>
    Manual,
}

/// <summary>What a category counts as in the statistics.</summary>
public enum CategoryKind
{
    Expense,
    Income,

    /// <summary>Money moved between the user's own accounts: neither spending nor income.</summary>
    Transfer,
}

/// <summary>
/// One movement of money (synced record in <c>wallet.transactions</c>). One record per transaction, so categorizing on
/// the phone and editing another on the PC never collide.
/// </summary>
public sealed record WalletTransaction
{
    /// <summary>Signed, in đồng: negative is money out, positive is money in.</summary>
    public decimal Amount { get; init; }

    /// <summary>"Techcombank", "ACB"; empty for cash and other manual entries.</summary>
    public string Bank { get; init; } = "";

    /// <summary>The account as the bank shows it (often masked, e.g. "1903xxxx0123").</summary>
    public string Account { get; init; } = "";

    /// <summary>The balance after the transaction, when the bank said it.</summary>
    public decimal? Balance { get; init; }

    /// <summary>The bank's description ("NGUYEN VAN A chuyen tien") or what the user typed.</summary>
    public string Description { get; init; } = "";

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>The category id; null while the transaction waits to be categorized.</summary>
    public string? CategoryId { get; init; }

    /// <summary>True when Helm picked the category itself from earlier ones (the pages say so, so it can be fixed).</summary>
    public bool AutoCategorized { get; init; }

    public string Note { get; init; } = "";

    public TransactionSource Source { get; init; }

    /// <summary>The notification text it was read from, to check what the bank really said.</summary>
    public string Original { get; init; } = "";

    /// <summary>What the same transaction read twice (the app's notification and the SMS) has in common.</summary>
    public string Fingerprint { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; }

    [JsonIgnore]
    public bool IsIncome => Amount > 0;

    [JsonIgnore]
    public bool IsCategorized => CategoryId is not null;
}

/// <summary>
/// A category the user added, or a change to a built-in one (synced record in <c>wallet.categories</c>, under the
/// built-in category's own id). Built-in categories exist without a record; see <see cref="WalletCategories"/>.
/// </summary>
public sealed record WalletCategory
{
    public string Name { get; init; } = "";

    public CategoryKind Kind { get; init; }

    /// <summary>Position in the pickers (ascending).</summary>
    public double Order { get; init; }

    /// <summary>Left out of the pickers; transactions already in it keep it.</summary>
    public bool Hidden { get; init; }

    /// <summary>A Fluent icon name from <see cref="WalletIcons.Choices"/>; empty keeps a built-in category's own.</summary>
    public string Icon { get; init; } = "";

    /// <summary>A colour slot of <see cref="WalletPalette"/> (1–8); 0 keeps a built-in category's own.</summary>
    public int Color { get; init; }
}

/// <summary>Spending limits shared by every device (synced record <see cref="WalletStore.BudgetId"/> in <c>wallet.budget</c>).</summary>
public sealed record WalletBudget
{
    /// <summary>What the user means to spend in a month at most; 0 for no budget.</summary>
    public decimal Monthly { get; init; }
}

/// <summary>A category as the pages see it: a built-in one with the user's changes, or one the user added.</summary>
/// <param name="Color">The colour slot of <see cref="WalletPalette"/> (1–8; 0 is the neutral grey).</param>
public sealed record CategoryInfo(string Id, string Name, CategoryKind Kind, double Order, bool IsBuiltIn, bool Hidden,
    string Icon = WalletIcons.Fallback, int Color = 0)
{
    /// <summary>Whether a transaction in that direction can go in this category.</summary>
    public bool Fits(decimal amount) => Kind switch
    {
        CategoryKind.Expense => amount < 0,
        CategoryKind.Income => amount > 0,
        _ => true,
    };
}

/// <summary>A notification Helm could not read, kept on this device so the user can check it or report it.</summary>
public sealed record UnreadNotification
{
    public DateTimeOffset At { get; init; }

    public string Bank { get; init; } = "";

    public string Title { get; init; } = "";

    public string Text { get; init; } = "";
}

/// <summary>The built-in categories: fixed ids, so every device has the same ones without creating anything.</summary>
public static class WalletCategories
{
    public const string Food = "food";
    public const string Groceries = "groceries";
    public const string Transport = "transport";
    public const string Shopping = "shopping";
    public const string Bills = "bills";
    public const string Housing = "housing";
    public const string Health = "health";
    public const string Entertainment = "entertainment";
    public const string Education = "education";
    public const string Family = "family";
    public const string Travel = "travel";
    public const string OtherExpense = "other";
    public const string Salary = "salary";
    public const string Bonus = "bonus";
    public const string OtherIncome = "other-income";
    public const string Transfer = "transfer";

    public static IReadOnlyList<CategoryInfo> BuiltIn { get; } =
    [
        new(Food, "Food & drinks", CategoryKind.Expense, 0, true, false, "Food", 2),
        new(Groceries, "Groceries", CategoryKind.Expense, 1, true, false, "Cart", 3),
        new(Transport, "Transport", CategoryKind.Expense, 2, true, false, "VehicleCar", 1),
        new(Shopping, "Shopping", CategoryKind.Expense, 3, true, false, "ShoppingBag", 5),
        new(Bills, "Bills & utilities", CategoryKind.Expense, 4, true, false, "Receipt", 4),
        new(Housing, "Housing", CategoryKind.Expense, 5, true, false, "Home", 7),
        new(Health, "Health", CategoryKind.Expense, 6, true, false, "HeartPulse", 8),
        new(Entertainment, "Entertainment", CategoryKind.Expense, 7, true, false, "MoviesAndTv", 6),
        new(Education, "Education", CategoryKind.Expense, 8, true, false, "HatGraduation", 1),
        new(Family, "Family & gifts", CategoryKind.Expense, 9, true, false, "Gift", 5),
        new(Travel, "Travel", CategoryKind.Expense, 10, true, false, "Airplane", 3),
        new(OtherExpense, "Other spending", CategoryKind.Expense, 11, true, false, "MoreHorizontal", 0),
        new(Salary, "Salary", CategoryKind.Income, 20, true, false, "Money", 6),
        new(Bonus, "Bonus", CategoryKind.Income, 21, true, false, "Trophy", 4),
        new(OtherIncome, "Other income", CategoryKind.Income, 22, true, false, "MoneyHand", 3),
        new(Transfer, "Between my accounts", CategoryKind.Transfer, 30, true, false, "ArrowSwap", 0),
    ];

    public static bool IsBuiltIn(string id) => BuiltIn.Any(c => c.Id == id);
}

/// <summary>
/// The icons a category can have: Fluent System Icons names that exist on both platforms (WPF-UI's
/// <c>SymbolRegular.&lt;Name&gt;24</c>, below U+FFFF so WPF-UI draws them, and Avalonia's <c>Symbol.&lt;Name&gt;</c>;
/// a test checks the Windows side).
/// </summary>
public static class WalletIcons
{
    /// <summary>A category without an icon of its own (or one this version does not know).</summary>
    public const string Fallback = "Tag";

    /// <summary>A transaction still to categorize.</summary>
    public const string Uncategorized = "QuestionCircle";

    public static IReadOnlyList<string> Choices { get; } =
    [
        "Food", "FoodPizza", "DrinkCoffee", "Cart", "ShoppingBag", "Receipt", "Home", "Building", "Lightbulb", "Drop",
        "Phone", "Laptop", "VehicleCar", "VehicleBus", "VehicleBicycle", "GasPump", "Airplane", "Beach", "HeartPulse",
        "Pill", "Stethoscope", "Dumbbell", "MoviesAndTv", "Games", "MusicNote2", "HatGraduation", "Book", "Gift", "Heart",
        "PersonHeart", "People", "AnimalDog", "Cut", "Sparkle", "Wrench", "Briefcase", "Money", "MoneyHand", "Trophy",
        "Savings", "Payment", "BuildingBank", "ArrowSwap", "MoreHorizontal", "Tag",
    ];

    /// <summary>The icon to show: a known one, else <see cref="Fallback"/>.</summary>
    public static string Normalize(string? icon) =>
        icon is { Length: > 0 } && (Choices.Contains(icon, StringComparer.Ordinal) || icon == Uncategorized) ? icon : Fallback;
}
