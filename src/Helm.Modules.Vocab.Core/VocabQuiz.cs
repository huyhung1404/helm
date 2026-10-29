using Helm.Core.Sync;

namespace Helm.Modules.Vocab;

/// <summary>How well the user knows a word, from their quiz answers.</summary>
/// <param name="Level">0 (new, or forgotten last time) up to <see cref="VocabQuiz.TopLevel"/>: each "remembered" moves it up one,
/// a "forgot" drops it back to 0.</param>
public sealed record VocabWordStats(int Level, int Remembered, int Forgot, DateTimeOffset? LastAnswer)
{
    public static VocabWordStats New { get; } = new(0, 0, 0, null);

    public bool IsNew => Remembered + Forgot == 0;
}

/// <summary>
/// Picks the word to show next: at random, but the less the user knows a word the more often it comes up, and a word
/// answered a moment ago waits a little. Only analyzed words take part (a pending one has no meaning to check yet).
/// </summary>
public sealed class VocabQuiz(VocabStore store, Random? random = null)
{
    public const int TopLevel = 5;

    /// <summary>A word answered within this time comes up far less often.</summary>
    public static readonly TimeSpan Rest = TimeSpan.FromMinutes(10);

    private readonly Random _random = random ?? Random.Shared;

    /// <summary>Relative chance by level: a forgotten or new word is 8 times as likely as one known well.</summary>
    internal static double Weight(VocabWordStats stats) => stats.Level switch
    {
        0 => stats.IsNew ? 6 : 8,
        1 => 5,
        2 => 3,
        3 => 2,
        4 => 1,
        _ => 0.5,
    };

    public IReadOnlyDictionary<string, VocabWordStats> Stats()
    {
        var stats = new Dictionary<string, VocabWordStats>(StringComparer.Ordinal);
        foreach (var r in store.Reviews())
        {
            var s = stats.GetValueOrDefault(r.WordId) ?? VocabWordStats.New;
            stats[r.WordId] = r.Remembered
                ? s with { Level = Math.Min(TopLevel, s.Level + 1), Remembered = s.Remembered + 1, LastAnswer = r.At }
                : s with { Level = 0, Forgot = s.Forgot + 1, LastAnswer = r.At };
        }
        return stats;
    }

    public VocabWordStats StatsOf(string wordId) => Stats().GetValueOrDefault(wordId) ?? VocabWordStats.New;

    /// <summary>The next word, never <paramref name="except"/> unless it is the only one; null when no word is analyzed yet.</summary>
    public SyncedItem<VocabWord>? Next(string? except = null)
    {
        var words = store.Words().Where(w => w.Value.IsAnalyzed).ToList();
        if (words.Count == 0) return null;
        if (words.Count > 1) words.RemoveAll(w => w.Id == except);
        var stats = Stats();
        var now = store.Now;
        var weights = words.Select(w =>
        {
            var s = stats.GetValueOrDefault(w.Id) ?? VocabWordStats.New;
            var weight = Weight(s);
            return s.LastAnswer is { } at && now - at < Rest ? weight * 0.1 : weight;
        }).ToList();
        var roll = _random.NextDouble() * weights.Sum();
        for (var i = 0; i < words.Count; i++)
        {
            roll -= weights[i];
            if (roll < 0) return words[i];
        }
        return words[^1];
    }
}
