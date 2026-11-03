using System.Globalization;
using System.Text;
using System.Text.Json;
using Helm.Ai;
using Helm.Ai.Assistant;

namespace Helm.Modules.Wallet;

/// <summary>
/// A category for a new transaction: one of the user's (from the history, or AI's pick), or a new one AI proposes,
/// which is only a suggestion: nothing is created until the user accepts it.
/// </summary>
/// <param name="NewName">The category AI proposes to add (none of the user's fits); null for an existing one.</param>
public sealed record CategoryProposal(string? CategoryId, string? NewName, CategoryKind NewKind, bool FromAi)
{
    public bool IsNew => NewName is not null;
}

/// <summary>
/// Files a transaction the history cannot (see <see cref="WalletClassifier"/>): the history first, without AI; only
/// when it is not sure, one question to AI through the router (<see cref="AiFeatureIds.WalletCategorize"/>: a PC agent,
/// the API only if the user chose so) with the user's categories and a few transactions like it. AI answers one of
/// them, or a new name when none fits.
/// </summary>
public sealed class WalletCategoryAi(WalletStore store, AiOneShot ai, TimeZoneInfo? zone = null)
{
    /// <summary>Earlier filed transactions shown to AI: enough to see the user's habits, few enough to stay cheap.</summary>
    public const int Examples = 5;

    internal const string System = """
        You file one bank transaction of a Vietnamese user into one of their spending or income categories.
        Reply with ONLY one JSON object and nothing else:
        {"category":"<a name exactly as listed>"} when one of the user's categories fits,
        {"new_category":"<a short category name in the language of the user's category names>"} when none fits at all,
        {"category":null} when you cannot tell.
        Prefer the user's categories, and follow how they filed transactions like it.
        """;

    private readonly TimeZoneInfo _zone = zone ?? TimeZoneInfo.Local;

    /// <summary>The history's sure category, else AI's answer; null when neither can tell (or AI is out of reach).</summary>
    public async Task<CategoryProposal?> ProposeAsync(string transactionId, CancellationToken ct)
    {
        if (store.Get(transactionId) is not { } transaction || transaction.IsCategorized) return null;
        if (store.Guess(transaction, transactionId) is { IsSure: true } sure) return new CategoryProposal(sure.CategoryId, null, Kind(transaction), false);
        var answer = await ai.AskAsync(new AiOneShotRequest(AiFeatureIds.WalletCategorize, System, Prompt(transaction)), ct);
        return answer.Text is { } text ? Read(text, transaction) : null;
    }

    internal string Prompt(WalletTransaction transaction)
    {
        var categories = Fitting(transaction);
        var text = new StringBuilder()
            .Append(transaction.Amount < 0 ? "Categories for spending: " : "Categories for money in: ")
            .AppendJoin("; ", categories.Select(c => c.Name)).Append('\n');
        var alike = store.SimilarFiled(transaction, Examples);
        if (alike.Count > 0)
        {
            text.Append("How the user filed transactions like it:\n");
            foreach (var t in alike)
                text.Append("- ").Append(WalletFormat.Signed(t.Amount, CultureInfo.InvariantCulture)).Append(" “").Append(t.Description).Append("” → ")
                    .Append(store.Category(t.CategoryId!)?.Name ?? "?").Append('\n');
        }
        var at = TimeZoneInfo.ConvertTime(transaction.OccurredAt, _zone);
        text.Append("The transaction: ").Append(WalletFormat.Signed(transaction.Amount, CultureInfo.InvariantCulture))
            .Append(", ").Append(at.ToString("dddd HH:mm", CultureInfo.InvariantCulture));
        if (transaction.Bank.Length > 0) text.Append(", ").Append(transaction.Bank);
        text.Append(", “").Append(transaction.Description).Append('”');
        return text.ToString();
    }

    /// <summary>AI's answer, checked: a category of the user's that fits, or a new name that is not one of them.</summary>
    internal CategoryProposal? Read(string answer, WalletTransaction transaction)
    {
        if (AiJson.Object(answer) is not { } json) return null;
        var fitting = Fitting(transaction);
        if (json.TryGetProperty("category", out var picked) && picked.ValueKind == JsonValueKind.String)
            return Find(fitting, picked.GetString()) is { } known ? new CategoryProposal(known.Id, null, Kind(transaction), true) : null;
        if (!json.TryGetProperty("new_category", out var proposed) || proposed.ValueKind != JsonValueKind.String) return null;
        var name = proposed.GetString()!.Trim().Trim('"', '“', '”');
        // A name it says is new may be one of the user's after all.
        if (Find(fitting, name) is { } existing) return new CategoryProposal(existing.Id, null, Kind(transaction), true);
        if (name.Length is 0 or > 40 || name.Any(char.IsControl) || Find(store.Categories(includeHidden: true), name) is not null) return null;
        return new CategoryProposal(null, name, Kind(transaction), true);
    }

    private IReadOnlyList<CategoryInfo> Fitting(WalletTransaction transaction) =>
        [.. store.Categories().Where(c => c.Fits(transaction.Amount))];

    private static CategoryInfo? Find(IEnumerable<CategoryInfo> categories, string? name) =>
        name is { Length: > 0 }
            ? categories.FirstOrDefault(c => string.Equals(WalletText.Fold(c.Name.Trim()), WalletText.Fold(name.Trim()), StringComparison.Ordinal))
            : null;

    private static CategoryKind Kind(WalletTransaction transaction) => transaction.Amount < 0 ? CategoryKind.Expense : CategoryKind.Income;
}

/// <summary>The one JSON object in a model's answer (bare, in a code fence, or with words around it).</summary>
internal static class AiJson
{
    public static JsonElement? Object(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(answer[start..(end + 1)]);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
