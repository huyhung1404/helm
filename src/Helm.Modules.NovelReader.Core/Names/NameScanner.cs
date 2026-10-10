using Helm.Modules.NovelReader.Text;

namespace Helm.Modules.NovelReader.Names;

/// <summary>How sure the scan is that a word is a name.</summary>
public enum NameConfidence
{
    /// <summary>Added to the novel's names by itself.</summary>
    Sure,
    /// <summary>Shown in Suggestions for the reader to check.</summary>
    Maybe,
}

/// <summary>A word the scan takes for a name: how often it appears, how it reads, and examples of it in the novel.</summary>
public sealed record NameFinding(string Chinese, int Count, string Vietnamese, NameConfidence Confidence, IReadOnlyList<string> Examples)
{
    /// <summary>What kind of name it is, when an AI said so ("person", "place", "group", "title").</summary>
    public string? Kind { get; init; }
}

/// <summary>The names found in a novel, sure ones first.</summary>
public sealed record NameScanResult(IReadOnlyList<NameFinding> Sure, IReadOnlyList<NameFinding> Maybe)
{
    public static NameScanResult Empty { get; } = new([], []);
}

/// <summary>
/// Finds character names in a novel without any service, from how the words are used. The candidates are
/// <see cref="NameSuggester"/>'s (a common surname and one or two more characters, repeated); then:
/// <list type="bullet">
/// <item>a candidate with a grammar word in it is not a name ("白的", "向那", "时不");</item>
/// <item>a preposition stuck to a name is not one ("向林宛" when "林宛" is a name);</item>
/// <item>a name stands next to punctuation, or after words like 对/向/和 and before words like 道/说/笑 — the share of
/// such places decides: most of them, sure; some, maybe; few, no;</item>
/// <item>a word that nearly always goes on with the same character is part of something longer ("景德皇" of "景德皇帝");</item>
/// <item>houses and families ("林府", "洛家") are only suggested;</item>
/// <item>the given name of a sure full name counts on its own ("景桓" when "洛景桓" is sure).</item>
/// </list>
/// Words people also say on their own, with the scan wider (<see cref="Candidates"/>), go to an AI to decide.
/// </summary>
public static class NameScanner
{
    /// <summary>Most places a sure name stands are name-like.</summary>
    public const double SureShare = 0.6;

    /// <summary>Some places are: worth a look.</summary>
    public const double MaybeShare = 0.35;

    /// <summary>A name is added by itself only when the novel uses it at least this often; rarer ones are suggested.</summary>
    public const int SureCount = 5;

    /// <summary>Grammar words: never inside a name after its surname.</summary>
    private const string GrammarChars = "的了不是在就也都而及与或这那他她它们我你您咱个一已未要想会能可着过把被让给向对从到时曾何么吗呢吧啊些所其此每各很太更最又还再才只并没别";

    /// <summary>A name does not end with these: they start the next word ("洛小" of "洛小姐").</summary>
    private const string BadEndings = "小大老声";

    /// <summary>Words before a name: "对林宛说", "向萧珩".</summary>
    private const string Before = "对向和与跟见让叫被给是将把问找替帮同像连随望扶抱拉朝";

    /// <summary>Words after a name: "林宛道", "萧珩笑".</summary>
    private const string After = "道说笑看点皱冷轻低抬摇沉心眼脸声手身怔愣微淡忙便却也已正又还早听想望走站坐转回";

    private const string Punctuation = "，。！？：；、“”‘’「」『』（）()《》 　…—.,!?:;\"'";

    /// <summary>Endings of houses and families: suggested, never added by themselves.</summary>
    private const string Households = "府家氏宅门";

    /// <param name="paragraphs">The whole novel, paragraph by paragraph.</param>
    /// <param name="isKnown">Whether the dictionaries or the reader already have the word.</param>
    /// <param name="hanViet">The Hán Việt reading of a word.</param>
    public static NameScanResult Scan(IReadOnlyList<string> paragraphs, Func<string, bool> isKnown, Func<string, string> hanViet)
    {
        var candidates = Candidates(paragraphs, isKnown, hanViet, 300);
        var sure = new List<NameFinding>();
        var maybe = new List<NameFinding>();
        foreach (var (finding, share, endsLonger, hasSurname) in candidates)
        {
            var word = finding.Chinese;
            if (word.Skip(1).Any(c => GrammarChars.Contains(c)) || BadEndings.Contains(word[^1])) continue;
            // Without a surname ("阿七", "小桃") only the reader (or an AI) can tell: suggested at most.
            if (Households.Contains(word[^1]) || endsLonger || !hasSurname)
            {
                if (share >= MaybeShare) maybe.Add(finding with { Confidence = NameConfidence.Maybe });
                continue;
            }
            if (share >= SureShare && finding.Count >= SureCount) sure.Add(finding with { Confidence = NameConfidence.Sure });
            else if (share >= MaybeShare) maybe.Add(finding with { Confidence = NameConfidence.Maybe });
        }

        // "向林宛" is "向" + a name and "谢珩低" a name + "低": not names.
        var sureWords = new HashSet<string>(sure.Select(f => f.Chinese), StringComparer.Ordinal);
        bool StuckToName(string word) => word.Length >= 3
            && ((Before.Contains(word[0]) && sureWords.Contains(word[1..])) || sureWords.Contains(word[..^1]));
        sure.RemoveAll(f => StuckToName(f.Chinese));
        maybe.RemoveAll(f => StuckToName(f.Chinese));

        // The given name of a sure full name, where it stands alone ("景桓" when "洛景桓" is sure).
        foreach (var full in sure.ToList())
        {
            if (full.Chinese.Length != 3) continue;
            var given = full.Chinese[1..];
            if (sureWords.Contains(given) || isKnown(given) || given.Any(c => GrammarChars.Contains(c))) continue;
            var (count, share, examples) = Places(paragraphs, given, isKnown, skipAfter: full.Chinese[0]);
            if (count < NameSuggester.MinCount || share < MaybeShare) continue;
            maybe.RemoveAll(f => f.Chinese == given);
            sure.Add(new NameFinding(given, count, ChineseText.TitleCase(hanViet(given)), NameConfidence.Sure, examples));
            sureWords.Add(given);
        }

        return new NameScanResult(
            sure.OrderByDescending(f => f.Count).ToList(),
            maybe.Where(f => !sureWords.Contains(f.Chinese)).OrderByDescending(f => f.Count).ToList());
    }

    /// <summary>
    /// Every candidate with how name-like the places it stands are (0 to 1), whether it nearly always goes on with the
    /// same character, and whether it starts with a surname; for the AI, which decides on its own, nothing is filtered.
    /// Two kinds: a common surname and one or two more characters (<see cref="NameSuggester"/>), and words that keep
    /// coming right before a verb of speaking ("阿七道", "小桃问"), which catches names without a surname.
    /// </summary>
    public static IReadOnlyList<(NameFinding Finding, double Share, bool EndsLonger, bool HasSurname)> Candidates(IReadOnlyList<string> paragraphs,
        Func<string, bool> isKnown, Func<string, string> hanViet, int max)
    {
        var result = new List<(NameFinding, double, bool, bool)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var suggestion in NameSuggester.Suggest(paragraphs, isKnown, hanViet, max))
        {
            var (count, share, examples, endsLonger) = PlacesAndNext(paragraphs, suggestion.Chinese, isKnown, skipAfter: null);
            result.Add((new NameFinding(suggestion.Chinese, suggestion.Count, suggestion.Suggested, NameConfidence.Maybe, examples), share, endsLonger, true));
            seen.Add(suggestion.Chinese);
        }
        foreach (var (word, count) in Speakers(paragraphs, isKnown).Where(s => !seen.Contains(s.Word)).Take(max / 3))
        {
            var (total, share, examples, endsLonger) = PlacesAndNext(paragraphs, word, isKnown, skipAfter: null);
            result.Add((new NameFinding(word, Math.Max(total, count), ChineseText.TitleCase(hanViet(word)), NameConfidence.Maybe, examples), share, endsLonger, false));
        }
        return result;
    }

    /// <summary>Verbs of speaking: whoever stands right before one is often a character.</summary>
    private const string Speaking = "道说问喊叫笑答";

    /// <summary>The minimum times a word must come before a verb of speaking to be a candidate.</summary>
    public const int MinSpeakerCount = 4;

    /// <summary>Two- or three-character words that keep coming right before a verb of speaking, after punctuation.</summary>
    private static IEnumerable<(string Word, int Count)> Speakers(IReadOnlyList<string> paragraphs, Func<string, bool> isKnown)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var paragraph in paragraphs)
        {
            for (var k = 2; k < paragraph.Length; k++)
            {
                if (!Speaking.Contains(paragraph[k])) continue;
                for (var length = 2; length <= 3 && k - length >= 0; length++)
                {
                    var start = k - length;
                    var before = start == 0 ? '。' : paragraph[start - 1];
                    if (!Punctuation.Contains(before)) continue;
                    var word = paragraph.Substring(start, length);
                    if (!word.All(ChineseText.IsHan) || word.Any(c => GrammarChars.Contains(c))) continue;
                    counts[word] = counts.GetValueOrDefault(word) + 1;
                }
            }
        }
        return counts.Where(c => c.Value >= MinSpeakerCount && !isKnown(c.Key))
            .OrderByDescending(c => c.Value).Select(c => (c.Key, c.Value));
    }

    private static (int Count, double Share, IReadOnlyList<string> Examples) Places(IReadOnlyList<string> paragraphs, string word,
        Func<string, bool> isKnown, char? skipAfter)
    {
        var (count, share, examples, _) = PlacesAndNext(paragraphs, word, isKnown, skipAfter);
        return (count, share, examples);
    }

    /// <summary>
    /// Where a word stands: how many times (not counting the times right after <paramref name="skipAfter"/>, i.e. inside
    /// a longer name), the share of name-like places, two examples, and whether one character nearly always follows it.
    /// A place where a dictionary word or idiom runs over the start or the end of the word ("太阳穴": "太阳"; "恼羞成怒":
    /// the whole idiom) is inside another word: never name-like.
    /// </summary>
    private static (int Count, double Share, IReadOnlyList<string> Examples, bool EndsLonger) PlacesAndNext(IReadOnlyList<string> paragraphs,
        string word, Func<string, bool> isKnown, char? skipAfter)
    {
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Known(string text, int from, int to)
        {
            if (from < 0 || to > text.Length) return false;
            for (var k = from; k < to; k++)
                if (!ChineseText.IsHan(text[k])) return false;
            var piece = text[from..to];
            if (!known.TryGetValue(piece, out var yes)) known[piece] = yes = isKnown(piece);
            return yes;
        }
        // Words of 2 to 4 characters that start before the word and end inside or after it, or start inside it and end after it.
        bool Inside(string text, int at)
        {
            var end = at + word.Length;
            for (var from = at - 3; from < at; from++)
                for (var to = Math.Max(from + 2, at + 1); to <= from + 4; to++)
                    if (Known(text, from, to)) return true;
            for (var from = at; from < end; from++)
                for (var to = Math.Max(from + 2, end + 1); to <= from + 4; to++)
                    if (Known(text, from, to)) return true;
            return false;
        }
        int count = 0, good = 0;
        // Places where the word stands alone come first: a given name is shown used on its own, not inside its full name.
        var examples = new List<string>(2);
        var others = new List<string>(2);
        var next = new Dictionary<char, int>();
        foreach (var paragraph in paragraphs)
        {
            for (var i = paragraph.IndexOf(word, StringComparison.Ordinal); i >= 0; i = paragraph.IndexOf(word, i + 1, StringComparison.Ordinal))
            {
                var before = i == 0 ? '。' : paragraph[i - 1];
                if (skipAfter is { } skip && before == skip) continue;
                var after = i + word.Length >= paragraph.Length ? '。' : paragraph[i + word.Length];
                count++;
                var inside = Inside(paragraph, i);
                if (!inside && (Punctuation.Contains(before) || Before.Contains(before) || Punctuation.Contains(after) || After.Contains(after))) good++;
                if (ChineseText.IsHan(after)) next[after] = next.GetValueOrDefault(after) + 1;
                var alone = !inside && (Punctuation.Contains(before) || Before.Contains(before));
                if (alone && examples.Count < 2) examples.Add(Excerpt(paragraph, i, word.Length));
                else if (!alone && others.Count < 2) others.Add(Excerpt(paragraph, i, word.Length));
            }
        }
        var top = next.Count == 0 ? 0 : next.Values.Max();
        var topChar = next.Count == 0 ? '\0' : next.First(kv => kv.Value == top).Key;
        // "景德皇" nearly always goes on as "景德皇帝": part of a longer word (a name goes on with all sorts of words).
        var endsLonger = count >= NameSuggester.MinCount && top >= count * 0.8 && !After.Contains(topChar);
        examples.AddRange(others.Take(2 - examples.Count));
        return (count, count == 0 ? 0 : (double)good / count, examples, endsLonger);
    }

    /// <summary>The word with some text around it, for the reader or the AI to see how it is used.</summary>
    private static string Excerpt(string paragraph, int start, int length)
    {
        const int Around = 18;
        var from = Math.Max(0, start - Around);
        var to = Math.Min(paragraph.Length, start + length + Around);
        return (from > 0 ? "…" : "") + paragraph[from..to] + (to < paragraph.Length ? "…" : "");
    }
}
