using Helm.Core.Sync;

namespace Helm.Modules.Wallet;

/// <summary>A category the history points to for a transaction, and how sure it is (0–1).</summary>
/// <param name="Matches">Earlier transactions in that category that look like this one.</param>
public sealed record CategoryGuess(string CategoryId, double Confidence, int Matches)
{
    /// <summary>Sure enough to file the transaction without asking (or asking AI).</summary>
    public bool IsSure => Confidence >= WalletClassifier.SureAt && Matches >= 2;
}

/// <summary>
/// Files a transaction from the user's own history, without AI: earlier transactions of the same direction whose
/// description has the same words (the same shop, the same person), or most of them, vote for their category; one
/// in the same amount band (a fixed price, the rent) counts a little more. Sure only when at least two agree and
/// almost nothing points elsewhere; anything less is a guess that AI or the user settles.
/// </summary>
public static class WalletClassifier
{
    /// <summary>Two earlier transactions with the same words, both in one category, reach it; one alone does not.</summary>
    public const double SureAt = 0.75;

    /// <summary>The vote of one earlier transaction with exactly the same words.</summary>
    private const double Same = 1.0;

    /// <summary>Most of the words alike (Jaccard 0.6 and up: "GRAB FOOD" and "GRAB FOOD HCM").</summary>
    private const double Alike = 0.6;

    /// <summary>Some words alike (Jaccard 0.34 and up).</summary>
    private const double Related = 0.25;

    /// <summary>No words to compare (a bare transfer): the same account and the very same amount.</summary>
    private const double SameAmount = 0.4;

    /// <summary>How much the evidence must add up to before agreement alone makes a guess sure.</summary>
    private const double Evidence = 0.5;

    public static CategoryGuess? Guess(WalletTransaction transaction, IEnumerable<SyncedItem<WalletTransaction>> history,
        Func<string, CategoryInfo?> category, string? exceptId = null)
    {
        if (transaction.Amount == 0) return null;
        var words = WalletText.Words(transaction.Description);
        var votes = new Dictionary<string, (double Score, int Count)>(StringComparer.Ordinal);
        var total = 0.0;
        foreach (var item in history)
        {
            var past = item.Value;
            if (item.Id == exceptId || ReferenceEquals(past, transaction) || past.CategoryId is not { } id
                || Math.Sign(past.Amount) != Math.Sign(transaction.Amount)) continue;
            var vote = Vote(transaction, words, past);
            if (vote <= 0) continue;
            total += vote;
            var (score, count) = votes.GetValueOrDefault(id);
            votes[id] = (score + vote, count + 1);
        }
        // The best category the transaction can still go in (not hidden, the right direction).
        foreach (var (id, (score, count)) in votes.OrderByDescending(v => v.Value.Score).ThenByDescending(v => v.Value.Count))
        {
            if (category(id) is not { Hidden: false } info || !info.Fits(transaction.Amount)) continue;
            var agreement = score / total;
            var weight = score / (score + Evidence);
            return new CategoryGuess(id, Math.Round(agreement * weight, 3), count);
        }
        return null;
    }

    private static double Vote(WalletTransaction transaction, IReadOnlySet<string> words, WalletTransaction past)
    {
        var pastWords = WalletText.Words(past.Description);
        double vote;
        if (words.Count == 0 || pastWords.Count == 0)
        {
            // Nothing to read in the description: only the very same amount from the same account says anything.
            vote = words.Count == 0 && pastWords.Count == 0 && past.Amount == transaction.Amount && past.Bank == transaction.Bank
                && past.Account == transaction.Account ? SameAmount : 0;
            return vote;
        }
        if (words.SetEquals(pastWords)) vote = Same;
        else
        {
            var similarity = WalletText.Similarity(words, pastWords);
            vote = similarity >= 0.6 ? Alike : similarity >= 0.34 ? Related : 0;
        }
        // The same amount band (within half as much again) is the same kind of purchase more often than not.
        if (vote > 0 && InBand(transaction.Amount, past.Amount)) vote *= 1.2;
        return vote;
    }

    private static bool InBand(decimal a, decimal b)
    {
        var (small, large) = Math.Abs(a) < Math.Abs(b) ? (Math.Abs(a), Math.Abs(b)) : (Math.Abs(b), Math.Abs(a));
        return small > 0 && large <= small * 1.5m;
    }
}
