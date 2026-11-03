using Helm.Core.Sync;
using Helm.Core.Text;

namespace Helm.Modules.Notes;

/// <summary>
/// Finding a note by what it is about without an embeddings service: the agent sends several alternatives the note
/// may use (synonyms, Vietnamese and English, without accents), and notes rank by how many of them they hold, then
/// by how well and how recently.
/// </summary>
public static class NoteRecall
{
    /// <summary>A note edited this long ago counts half as recent as one edited now.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    /// <summary>The notes holding at least one alternative, the best first.</summary>
    public static IEnumerable<SyncedItem<NoteItem>> Rank(IEnumerable<SyncedItem<NoteItem>> notes, IReadOnlyList<string> alternatives, DateTimeOffset now)
    {
        var terms = alternatives.Select(TextSearch.Terms).Where(t => t.Count > 0).DistinctBy(t => string.Join(' ', t)).ToList();
        return notes
            .Select(n =>
            {
                var scores = terms.Select(t => TextSearch.Score(t, n.Value.DisplayTitle, n.Value.Body)).Where(s => s > 0).ToList();
                var age = Math.Max(0, (now - n.Value.EditedAt).TotalDays);
                return (Note: n, Matches: scores.Count, Score: scores.Sum() + 0.5 * Math.Pow(0.5, age / HalfLife.TotalDays));
            })
            .Where(x => x.Matches > 0)
            .OrderByDescending(x => x.Matches).ThenByDescending(x => x.Score)
            .Select(x => x.Note);
    }
}
