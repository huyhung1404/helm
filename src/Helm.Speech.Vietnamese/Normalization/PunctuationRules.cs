// Port of sea-g2p src/punc.rs.
using System.Text;
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Trailing punctuation normalization ("punc_norm"), a pure string operation that gives the voice a predictable sentence
/// ending: a very short fragment (one or two words) ending in "." mid-string gets a "," instead, so a list marker such as
/// "3." does not stop the voice dead; a short final sentence (under five words) ends in exactly one "."; a longer one
/// gains a "." only when it does not already end in , . ! ?. Also the rule that two marks separated only by spaces
/// collapse to the strongest.
/// </summary>
internal static partial class PunctuationRules
{
    [GeneratedRegex(@"[.,!?;:](?:[ \t]*[.,!?;:])+", Rx.Options)]
    private static partial Regex RePunctRun();

    [GeneratedRegex(@"\.(\s+|\z)", Rx.Options)]
    private static partial Regex ReSentenceDot();

    private static int Rank(char c) => c switch
    {
        '?' => 3,
        '!' => 2,
        '.' => 1,
        _ => 0,
    };

    /// <summary>Collapse every space-separated run of marks to its strongest one (the first on a tie): "domine? ," is "domine?".</summary>
    public static string CollapsePunctRuns(string text) => RePunctRun().Replace(text, m =>
    {
        var best = '\0';
        foreach (var c in m.Value)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (best == '\0' || Rank(c) > Rank(best)) best = c;
        }
        return best.ToString();
    });

    private const int ShortSentenceMaxWords = 4;
    private const int SuperShortMaxWords = 2;

    private static bool IsTrailingPunct(int c) =>
        c is ',' or '.' or '!' or '?' or ';' or ':' or '…' or '‥' or '․';

    private static bool IsSentenceEnd(int c) => c is ',' or '.' or '!' or '?';

    /// <summary>Words containing at least one alphanumeric character; free-standing punctuation does not count.</summary>
    private static int WordCount(string text) =>
        RustText.SplitWhitespace(text).Count(w => RustText.Any(w, RustText.IsAlphanumeric));

    private static string SoftenShortSegments(string text)
    {
        var dots = ReSentenceDot().Matches(text);
        if (dots.Count == 0) return text;
        var result = new StringBuilder(text.Length);
        var last = 0;
        foreach (Match m in dots)
        {
            var dotPos = m.Index;
            var segment = text[last..dotPos];
            var ws = text[(dotPos + 1)..(m.Index + m.Length)];
            var hasMore = RustText.Any(text[(m.Index + m.Length)..], RustText.IsAlphanumeric);
            result.Append(segment);
            var wc = WordCount(segment);
            result.Append(hasMore && wc >= 1 && wc <= SuperShortMaxWords ? ',' : '.');
            result.Append(ws);
            last = m.Index + m.Length;
        }
        result.Append(text[last..]);
        return result.ToString();
    }

    /// <summary><c>sea_g2p.punc_norm(text)</c>.</summary>
    public static string ApplyPuncNorm(string text)
    {
        var trimmed = text.TrimEnd();
        if (trimmed.Length == 0) return trimmed;
        var softened = SoftenShortSegments(trimmed);
        trimmed = softened.TrimEnd();
        if (WordCount(trimmed) <= ShortSentenceMaxWords)
        {
            var stripped = RustText.TrimEndMatches(trimmed, c => IsTrailingPunct(c) || RustText.IsWhitespace(c));
            return stripped.Length == 0 ? "." : stripped + ".";
        }
        return IsSentenceEnd(RustText.LastChar(trimmed)) ? trimmed : trimmed + ".";
    }
}
