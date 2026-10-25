using System.Text;
using Helm.Modules.NovelReader.Books;
using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Scraping;

/// <summary>
/// Continuing a novel's download: which chapters of its web list it lacks (new ones, and ones that failed before), and
/// the novel with them put in. Chapters are matched by title (letters and digits only), so it works on any device from
/// the novel's own text. A new chapter goes right after the chapter before it in the list; the novel's own chapters keep
/// their order, and where each one went is told so the reading place can follow.
/// </summary>
public static class BookUpdate
{
    /// <summary>The novel's result: its chapters, the new place of each old chapter, and which chapters are new.</summary>
    public sealed record Merged(IReadOnlyList<Chapter> Chapters, IReadOnlyList<int> NewIndexOfOld, IReadOnlyList<int> Added);

    /// <summary>The chapters of <paramref name="list"/> the novel does not have.</summary>
    public static IReadOnlyList<WebLink> Missing(IReadOnlyList<Chapter> existing, IReadOnlyList<WebLink> list)
    {
        var have = Counts(Own(existing).Select(c => c.Title));
        var missing = new List<WebLink>();
        foreach (var link in list)
        {
            var key = MatchKey(link.Text);
            if (key.Length > 0 && have.TryGetValue(key, out var n) && n > 0) have[key] = n - 1;
            else missing.Add(link);
        }
        return missing;
    }

    /// <summary>Puts the chapters that came (<see cref="ScrapeOutcome.Got"/>) into the novel, in the list's order.</summary>
    public static Merged Merge(IReadOnlyList<Chapter> existing, IReadOnlyList<ScrapeItem> items)
    {
        var own = Own(existing);
        var places = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var i = 0; i < own.Count; i++)
        {
            var key = MatchKey(own[i].Title);
            if (!places.TryGetValue(key, out var queue)) places[key] = queue = new Queue<int>();
            queue.Enqueue(i);
        }

        var chapters = new List<Chapter>(own.Count + items.Count);
        var newIndexOfOld = new int[existing.Count];
        var added = new List<int>();
        var next = 0;

        void TakeOwnUpTo(int last)
        {
            for (; next <= last; next++)
            {
                newIndexOfOld[Original(next)] = chapters.Count;
                chapters.Add(own[next]);
            }
        }

        // Positions in `existing` of the chapters kept in `own` (an empty "前言" of an empty novel is dropped).
        var originals = existing.Select((c, i) => (c, i)).Where(x => !IsEmpty(x.c, existing.Count)).Select(x => x.i).ToList();
        int Original(int ownIndex) => originals[ownIndex];

        foreach (var item in items)
        {
            if (places.TryGetValue(MatchKey(item.Title), out var queue) && queue.Count > 0)
            {
                // The novel has it: its chapters up to this one go first, as they are.
                TakeOwnUpTo(queue.Dequeue());
                continue;
            }
            if (item is not { Outcome: ScrapeOutcome.Got, Chapter: { } chapter }) continue;
            added.Add(chapters.Count);
            chapters.Add(new Chapter(chapter.Title, chapter.Paragraphs));
        }
        TakeOwnUpTo(own.Count - 1);

        return new Merged(chapters, newIndexOfOld, added);
    }

    /// <summary>A title reduced to its letters and digits ("第1 章  山寺祸端" = "第1章山寺祸端").</summary>
    public static string Key(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        var sb = new StringBuilder(title.Length);
        foreach (var c in title)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// How a title is matched: as written into the text (<see cref="ScrapedBook.Heading"/>: cut, maybe in 【】), letters
    /// and digits only, in Simplified, so a list's "藍念到底是誰？" is the novel's "【蓝念到底是谁？】".
    /// </summary>
    private static string MatchKey(string title) => Key(TraditionalChinese.ToSimplified(ScrapedBook.Heading(title)));

    private static List<Chapter> Own(IReadOnlyList<Chapter> existing) => existing.Where(c => !IsEmpty(c, existing.Count)).ToList();

    /// <summary>What <see cref="ChapterSplitter"/> gives for a novel with no text: one empty "前言".</summary>
    private static bool IsEmpty(Chapter chapter, int count) => count == 1 && chapter.Paragraphs.Count == 0;

    private static Dictionary<string, int> Counts(IEnumerable<string> titles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var title in titles)
        {
            var key = MatchKey(title);
            if (key.Length > 0) counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }
}
