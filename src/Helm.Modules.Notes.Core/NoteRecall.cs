using Helm.Core.Sync;
using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// Finding a note by what it is about. With the sentence model (<see cref="NoteMeaning"/>), notes rank by how close they
/// are in meaning, helped by the words they share with the search and by how recently they were edited, so "lỗi server
/// hôm trước" finds a note about Nginx answering 502 that has none of those words. Without it, by words only: the agent
/// sends several alternatives the note may use (synonyms, Vietnamese and English, without accents), and notes rank by
/// how many of them they hold, then by how well and how recently.
/// </summary>
public static class NoteRecall
{
    /// <summary>A note edited this long ago counts half as recent as one edited now.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    /// <summary>
    /// How close in meaning a note must be to be found without any of the words (the model gives 0–0.3 to unrelated
    /// notes and 0.35–0.7 to notes about the same thing).
    /// </summary>
    public const double MinMeaning = 0.4;

    /// <summary>The closest note is found from this much, with the notes about as close (<see cref="BestBand"/>).</summary>
    public const double MinBestMeaning = 0.3;

    public const double BestBand = 0.05;

    /// <summary>How much the words count next to the meaning (which goes from 0 to 1).</summary>
    private const double WordWeight = 0.6;

    private const double RecencyWeight = 0.1;

    /// <summary>The notes holding at least one alternative, the best first.</summary>
    public static IEnumerable<SyncedItem<NoteItem>> Rank(IEnumerable<SyncedItem<NoteItem>> notes, IReadOnlyList<string> alternatives, DateTimeOffset now)
    {
        var terms = Alternatives(alternatives);
        return notes
            .Select(n =>
            {
                var scores = terms.Select(t => TextSearch.Score(t, n.Value.DisplayTitle, n.Value.Body)).Where(s => s > 0).ToList();
                return (Note: n, Matches: scores.Count, Score: scores.Sum() + 0.5 * Recency(n.Value, now));
            })
            .Where(x => x.Matches > 0)
            .OrderByDescending(x => x.Matches).ThenByDescending(x => x.Score)
            .Select(x => x.Note);
    }

    /// <summary>
    /// The notes close in meaning to the search (<paramref name="meaning"/>: each note's similarity, from
    /// <see cref="NoteMeaning.SimilaritiesAsync"/>) or holding its words (all of <paramref name="query"/>, or any of the
    /// <paramref name="alternatives"/>), the best first: meaning, then words, then how recent.
    /// </summary>
    public static IEnumerable<SyncedItem<NoteItem>> Rank(IEnumerable<SyncedItem<NoteItem>> notes, IReadOnlyList<string> query,
        IReadOnlyList<string> alternatives, IReadOnlyDictionary<string, double> meaning, DateTimeOffset now)
    {
        var terms = Alternatives(alternatives);
        var list = notes.ToList();
        var byMeaning = FoundByMeaning(list.Select(n => n.Id), meaning);
        return list
            .Select(n =>
            {
                var words = query.Count > 0 ? TextSearch.Score(query, n.Value.DisplayTitle, n.Value.Body) : 0;
                if (terms.Count > 0)
                {
                    var scores = terms.Select(t => TextSearch.Score(t, n.Value.DisplayTitle, n.Value.Body)).Where(s => s > 0).ToList();
                    words += (double)scores.Count / terms.Count + 0.1 * scores.Sum();
                }
                var close = meaning.GetValueOrDefault(n.Id);
                return (Note: n, Found: words > 0 || byMeaning(n.Id), Score: close + WordWeight * words + RecencyWeight * Recency(n.Value, now));
            })
            .Where(x => x.Found)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Note);
    }

    /// <summary>
    /// Which of the notes <paramref name="ids"/> are close enough in meaning to be found without the words: from
    /// <see cref="MinMeaning"/>, and the closest ones from <see cref="MinBestMeaning"/> (a search padded with words like
    /// "ghi chú về… hôm trước" is less close to anything, the right note included).
    /// </summary>
    public static Func<string, bool> FoundByMeaning(IEnumerable<string> ids, IReadOnlyDictionary<string, double> meaning)
    {
        var best = ids.Select(id => meaning.GetValueOrDefault(id)).DefaultIfEmpty(0).Max();
        return id => meaning.GetValueOrDefault(id) is var close && (close >= MinMeaning || (close >= MinBestMeaning && close >= best - BestBand));
    }

    private static List<IReadOnlyList<string>> Alternatives(IReadOnlyList<string> alternatives) =>
        alternatives.Select(TextSearch.Terms).Where(t => t.Count > 0).DistinctBy(t => string.Join(' ', t)).ToList();

    private static double Recency(NoteItem note, DateTimeOffset now) => Math.Pow(0.5, Math.Max(0, (now - note.EditedAt).TotalDays) / HalfLife.TotalDays);
}
