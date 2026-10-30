using Helm.Core.Sync;

namespace Helm.Modules.Vocab;

/// <summary>How well the user knows a word, from their quiz answers.</summary>
/// <param name="Level">0 (new, or forgotten last time) up to <see cref="VocabQuiz.TopLevel"/>: each "remembered" on or after the
/// due time moves it up one, a "forgot" drops it back to 0.</param>
/// <param name="Due">When to see it again (null for a new word: due now).</param>
/// <param name="ForgotAt">When each "forgot" was, oldest first.</param>
public sealed record VocabWordStats(int Level, int Remembered, int Forgot, DateTimeOffset? LastAnswer, DateTimeOffset? Due, IReadOnlyList<DateTimeOffset> ForgotAt)
{
    public static VocabWordStats New { get; } = new(0, 0, 0, null, null, []);

    /// <summary>"Forgot" answers after a moment (an agent's help): the ones that count towards a word being hard.</summary>
    public int ForgotSince(DateTimeOffset? since) => since is { } at ? ForgotAt.Count(f => f > at) : Forgot;

    public bool IsNew => Remembered + Forgot == 0;

    public bool IsDue(DateTimeOffset now) => !IsNew && Due is { } due && due <= now;

    /// <summary>Known well enough to wait a week or more.</summary>
    public bool IsLearned => Level >= VocabQuiz.LearnedLevel;
}

/// <summary>What the Learn tab has for today.</summary>
public sealed record VocabDay(int Due, int New, int Learned, int Total);

/// <summary>
/// Spaced repetition: each word comes back when it is about to be forgotten. Remembering a word that is due doubles its
/// gap (1, 2, 4 … 128 days); forgetting it brings it back in 10 minutes and starts over. The next word is the most
/// overdue one, else a new one (English or Chinese at random, then the most overdue or oldest of that language), else
/// (all done for now) extra practice, picked at random with the weaker words more often. Only analyzed words take part (a pending one has no meaning to check yet), and none the user hid.
/// </summary>
public sealed class VocabQuiz(VocabStore store, Random? random = null)
{
    public const int TopLevel = 8;

    public const int LearnedLevel = 4;

    /// <summary>"I know this" puts a word here: back in a month.</summary>
    public const int KnownLevel = 6;

    /// <summary>A word forgotten this many times (since an agent last helped with it) is often forgotten.</summary>
    public const int HardForgets = 4;

    /// <summary>A forgotten word comes back this soon.</summary>
    public static readonly TimeSpan Relearn = TimeSpan.FromMinutes(10);

    /// <summary>A word answered within this time waits during extra practice.</summary>
    public static readonly TimeSpan Rest = TimeSpan.FromMinutes(10);

    private readonly Random _random = random ?? Random.Shared;

    /// <summary>The gap after reaching a level: 1 day at level 1, doubling up to 128 days.</summary>
    public static TimeSpan Interval(int level) => level <= 0 ? Relearn : TimeSpan.FromDays(Math.Pow(2, Math.Min(level, TopLevel) - 1));

    /// <summary>Relative chance during extra practice: a forgotten or new word is 8 times as likely as one known well.</summary>
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
            if (!r.Remembered)
            {
                stats[r.WordId] = s with { Level = 0, Forgot = s.Forgot + 1, LastAnswer = r.At, Due = r.At + Relearn, ForgotAt = [.. s.ForgotAt, r.At] };
                continue;
            }
            if (r.Known)
            {
                var known = Math.Max(KnownLevel, s.Level);
                stats[r.WordId] = s with { Level = known, Remembered = s.Remembered + 1, LastAnswer = r.At, Due = r.At + Interval(known) };
                continue;
            }
            // Remembered before it was due (extra practice): it counts, but the gap does not grow.
            var onTime = s.Due is not { } due || r.At >= due;
            var level = onTime ? Math.Min(TopLevel, s.Level + 1) : Math.Max(1, s.Level);
            stats[r.WordId] = s with { Level = level, Remembered = s.Remembered + 1, LastAnswer = r.At, Due = r.At + Interval(level) };
        }
        return stats;
    }

    public VocabWordStats StatsOf(string wordId) => Stats().GetValueOrDefault(wordId) ?? VocabWordStats.New;

    /// <summary>Forgotten <see cref="HardForgets"/> times or more since an agent last helped with it.</summary>
    public static bool IsHard(VocabWord word, VocabWordStats stats) => word.IsAnalyzed && stats.ForgotSince(word.HelpedAt) >= HardForgets;

    /// <summary>The words the user keeps forgetting (hidden ones aside), the most forgotten first.</summary>
    public IReadOnlyList<SyncedItem<VocabWord>> HardWords()
    {
        var stats = Stats();
        return store.Words()
            .Where(w => !w.Value.Hidden && stats.GetValueOrDefault(w.Id) is { } s && IsHard(w.Value, s))
            .OrderByDescending(w => stats[w.Id].ForgotSince(w.Value.HelpedAt)).ToList();
    }

    /// <summary>The words on the cards: analyzed and not hidden.</summary>
    private List<SyncedItem<VocabWord>> Learnable() => store.Words().Where(w => w.Value.IsAnalyzed && !w.Value.Hidden).ToList();

    /// <summary>How many words are due, new and learned now (hidden ones aside).</summary>
    public VocabDay Today()
    {
        var words = Learnable();
        var stats = Stats();
        var now = store.Now;
        var all = words.Select(w => stats.GetValueOrDefault(w.Id) ?? VocabWordStats.New).ToList();
        return new VocabDay(all.Count(s => s.IsDue(now)), all.Count(s => s.IsNew), all.Count(s => s.IsLearned), all.Count);
    }

    /// <summary>The words of one language, picked at random among the languages these words have.</summary>
    private List<SyncedItem<VocabWord>> OneLanguage(List<SyncedItem<VocabWord>> words)
    {
        var languages = words.Select(w => w.Value.Language).Distinct().ToList();
        if (languages.Count < 2) return words;
        var language = languages[_random.Next(languages.Count)];
        return words.Where(w => w.Value.Language == language).ToList();
    }

    /// <summary>The next word, never <paramref name="except"/> unless it is the only one; null when no word is analyzed yet.</summary>
    public SyncedItem<VocabWord>? Next(string? except = null)
    {
        var words = Learnable();
        if (words.Count == 0) return null;
        if (words.Count > 1) words.RemoveAll(w => w.Id == except);
        var stats = Stats();
        var now = store.Now;
        VocabWordStats Of(SyncedItem<VocabWord> w) => stats.GetValueOrDefault(w.Id) ?? VocabWordStats.New;

        // 1. The most overdue word, 2. else a word never answered, the oldest first (the order they were added). In both,
        // English and Chinese are picked at random first, so a run of words added in one language does not hide the other.
        if (OneLanguage(words.Where(w => Of(w).IsDue(now)).ToList()).MinBy(w => Of(w).Due) is { } due) return due;
        if (OneLanguage(words.Where(w => Of(w).IsNew).ToList())
                .OrderBy(w => w.Value.CreatedAt).ThenBy(w => w.Id, StringComparer.Ordinal).FirstOrDefault() is { } fresh) return fresh;
        // 3. All done for now: extra practice, weaker words more often.
        var weights = words.Select(w =>
        {
            var s = Of(w);
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
