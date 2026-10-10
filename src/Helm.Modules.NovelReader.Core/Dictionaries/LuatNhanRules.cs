namespace Helm.Modules.NovelReader.Dictionaries;

/// <summary>
/// LuatNhan ("multiplication rules") from QuickTranslator: a pattern such as <c>在{0}身上=trên người {0}</c> where
/// <c>{0}</c> is a name or a pronoun, which reorders the words around it. Rules are indexed by the first character of
/// their literal prefix, or of their suffix when they start with <c>{0}</c>, so matching at a position only looks at
/// the few rules that can match there (QuickTranslator ran a regex per rule).
/// </summary>
public sealed class LuatNhanRules
{
    private const string Slot = "{0}";

    private readonly Dictionary<char, List<Rule>> _byPrefix = new();
    private readonly Dictionary<char, List<Rule>> _bySuffix = new();

    public int Count { get; private set; }

    /// <summary>One rule: the literal text before and after the slot, and the translation with its own slot.</summary>
    public sealed record Rule(string Prefix, string Suffix, string Template);

    /// <summary>A rule that matched: how many characters it covers and the translation with the slot filled in.</summary>
    public readonly record struct Match(int Length, string Text);

    /// <summary>A name or pronoun found at a position, which can fill a slot.</summary>
    public readonly record struct Filler(int Length, string Text);

    public static LuatNhanRules Parse(string text)
    {
        var rules = new LuatNhanRules();
        foreach (var (key, value) in PhraseDictionary.ParseLines(text)) rules.Add(key, value);
        return rules;
    }

    /// <summary>Adds a rule; patterns without exactly one slot or without literal text are ignored.</summary>
    public bool Add(string pattern, string template)
    {
        var at = pattern.IndexOf(Slot, StringComparison.Ordinal);
        if (at < 0 || pattern.IndexOf(Slot, at + Slot.Length, StringComparison.Ordinal) >= 0) return false;
        var rule = new Rule(pattern[..at], pattern[(at + Slot.Length)..], template);
        if (rule.Prefix.Length > 0) Index(_byPrefix, rule.Prefix[0], rule);
        else if (rule.Suffix.Length > 0) Index(_bySuffix, rule.Suffix[0], rule);
        else return false;
        Count++;
        return true;
    }

    /// <summary>
    /// The longest rule that matches at <paramref name="start"/>. <paramref name="fillersAt"/> gives the names and
    /// pronouns found at a position, longest first.
    /// </summary>
    public Match? MatchAt(string text, int start, Func<int, IReadOnlyList<Filler>> fillersAt)
    {
        Match? best = null;
        var span = text.AsSpan();
        if (_byPrefix.TryGetValue(text[start], out var prefixed))
        {
            foreach (var rule in prefixed)
            {
                if (!span[start..].StartsWith(rule.Prefix, StringComparison.Ordinal)) continue;
                var slot = start + rule.Prefix.Length;
                if (slot >= text.Length) continue;
                foreach (var filler in fillersAt(slot))
                {
                    var end = slot + filler.Length;
                    if (!span[end..].StartsWith(rule.Suffix, StringComparison.Ordinal)) continue;
                    var length = end + rule.Suffix.Length - start;
                    if (best is null || length > best.Value.Length) best = new Match(length, Fill(rule, filler.Text));
                    break;
                }
            }
        }
        foreach (var filler in fillersAt(start))
        {
            var end = start + filler.Length;
            if (end >= text.Length || !_bySuffix.TryGetValue(text[end], out var suffixed)) continue;
            foreach (var rule in suffixed)
            {
                if (!span[end..].StartsWith(rule.Suffix, StringComparison.Ordinal)) continue;
                var length = filler.Length + rule.Suffix.Length;
                if (best is null || length > best.Value.Length) best = new Match(length, Fill(rule, filler.Text));
            }
        }
        return best;
    }

    private static string Fill(Rule rule, string filler) => rule.Template.Replace(Slot, filler, StringComparison.Ordinal).Trim();

    private static void Index(Dictionary<char, List<Rule>> index, char key, Rule rule)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(rule);
    }
}
