// Port of sea-g2p src/g2p/mod.rs.
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.G2p;

/// <summary>
/// Grapheme-to-phoneme conversion for mixed Vietnamese and English text. Each token is looked up in the dictionary; a
/// word both languages claim takes the language of its nearest unambiguous neighbour (punctuation does not count toward
/// the distance, and . ! ? ; : ( ) [ ] { } stop the search). An unknown word is split by dynamic programming over
/// dictionary pieces, and spelled letter by letter as the last resort. The caches only save work: they never change a
/// result, and they are safe to share between threads.
/// </summary>
internal sealed partial class G2pEngine(PhonemeDictionary dict)
{
    [GeneratedRegex(@"(?i)(<en>.*?</en>)|(" + Rx.W + @"+(?:['’]" + Rx.W + @"+)*)|([^" + Rx.WordClass + @"\s])", Rx.Options)]
    private static partial Regex ReToken();

    [GeneratedRegex(@"(" + Rx.W + @"+(?:['’]" + Rx.W + @"+)*)|([^" + Rx.WordClass + @"\s])", Rx.Options)]
    private static partial Regex ReTagContent();

    [GeneratedRegex(@"(?i)</?en>", Rx.Options)]
    private static partial Regex ReTagStrip();

    private const string ViAccents = "àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ";
    private const string Vowels = "aeiouyàáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵ";
    private const string Common = "common";
    private const string Punct = "punct";
    private const char Sep = '\x1F';

    private static readonly object Missing = new();
    private readonly ConcurrentDictionary<string, object> _merged = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _common = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _segmentation = new(StringComparer.Ordinal);

    private sealed class Token(string lang, string content, string? phone, bool isExplicitEn)
    {
        public string Lang = lang;
        public readonly string Content = content;
        public readonly string? Phone = phone;
        public readonly bool IsExplicitEn = isExplicitEn;
    }

    /// <summary>Does the segment contain both a vowel and a consonant? Rejects "n", "st", "e".</summary>
    private static bool HasVowelAndConsonant(string s)
    {
        var hasV = false;
        var hasC = false;
        foreach (var cp in RustText.CodePoints(s))
        {
            var lower = RustText.LowerChar(cp);
            var lc = RustText.FirstChar(lower);
            if (lc < 0x10000 && Vowels.Contains((char)lc)) hasV = true;
            else if (RustText.IsAlphabetic(lc)) hasC = true;
            if (hasV && hasC) return true;
        }
        return false;
    }

    /// <summary>The form a punctuation token keeps in the phoneme string, or null to drop it.</summary>
    private static string? MapPunct(string s) => s switch
    {
        "," => ",",
        "." => ".",
        "!" => "!",
        "?" => "?",
        ";" or ":" => ",",
        "…" or "‥" or "․" => ".",
        _ => null,
    };

    private string? CachedMerged(string word)
    {
        if (_merged.TryGetValue(word, out var hit)) return hit as string;
        var value = dict.LookupMerged(word);
        if (_merged.Count >= 50_000) _merged.Clear();
        _merged[word] = (object?)value ?? Missing;
        return value;
    }

    private (string Vi, string En)? CachedCommon(string word)
    {
        if (_common.TryGetValue(word, out var hit)) return hit as Tuple<string, string> is { } t ? (t.Item1, t.Item2) : null;
        var value = dict.LookupCommon(word);
        if (_common.Count >= 50_000) _common.Clear();
        _common[word] = value is { } v ? Tuple.Create(v.Vi, v.En) : Missing;
        return value;
    }

    private static string StripEn(string p) => p.Replace("<en>", "", StringComparison.Ordinal).Trim();

    private string? ResolveSegmentPhone(string segment, string lang)
    {
        var lw = RustText.ToLower(segment);
        if (CachedMerged(lw) is { } p) return StripEn(p);
        if (CachedCommon(lw) is { } c)
        {
            if (lang == "en" && c.En.Length > 0) return StripEn(c.En);
            if (c.Vi.Length > 0) return c.Vi.Trim();
            return StripEn(c.En);
        }
        return null;
    }

    private sealed class Path
    {
        public uint Cost;
        public uint Top;
        public List<byte> Lens = [];
        public List<string> Phones = [];

        public Path Clone() => new() { Cost = Cost, Top = Top, Lens = [.. Lens], Phones = [.. Phones] };
    }

    private static bool Better(Path a, Path b)
    {
        if (a.Cost != b.Cost) return a.Cost < b.Cost;
        if (a.Top != b.Top) return a.Top > b.Top;
        for (int i = a.Lens.Count - 1, j = b.Lens.Count - 1; i >= 0 && j >= 0; i--, j--)
            if (a.Lens[i] != b.Lens[j]) return a.Lens[i] > b.Lens[j];
        return a.Lens.Count < b.Lens.Count;
    }

    private static int CountChar(string s, char c)
    {
        var n = 0;
        foreach (var x in s)
            if (x == c) n++;
        return n;
    }

    /// <summary>
    /// Split an out-of-vocabulary word into dictionary pieces, minimising cost: 1 per real word, 4 + length for a
    /// spelled-out entry or a run of up to three letters spelled by name. Ties go to more top-list English words, then
    /// the longer final piece, then fewer pieces.
    /// </summary>
    private string? SegmentOov(string word, string lang)
    {
        var key = word + "_" + lang;
        if (_segmentation.TryGetValue(key, out var cached)) return cached;

        const uint junkCost = 4;
        var chars = RustText.Chars(word);
        var n = chars.Count;
        var dp = new Path?[n + 1];
        dp[0] = new Path();
        var segment = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            var b = dp[i];
            if (b == null) continue;
            segment.Clear();
            for (var j = i + 1; j <= n; j++)
            {
                segment.Append(chars[j - 1]);
                var seg = segment.ToString();
                var segLen = j - i;
                string? phone = null;
                uint cost = 1;
                if (HasVowelAndConsonant(seg) && ResolveSegmentPhone(seg, lang) is { } p)
                {
                    var primary = CountChar(p, 'ˈ');
                    var total = primary + CountChar(p, 'ˌ');
                    if ((segLen <= 4 && total >= 2) || primary >= 2) cost = junkCost + (uint)segLen;
                    phone = p;
                }
                if (phone == null && segLen <= 3)
                {
                    var spelled = CharFallback(seg, lang);
                    if (spelled.Trim().Length > 0)
                    {
                        phone = spelled;
                        cost = junkCost + (uint)segLen;
                    }
                }
                if (phone == null) continue;

                var cand = b.Clone();
                cand.Cost += cost;
                if (WordLists.EnTopWords.Contains(seg)) cand.Top += 1;
                cand.Lens.Add((byte)segLen);
                cand.Phones.Add(phone);
                if (dp[j] == null || Better(cand, dp[j]!)) dp[j] = cand;
            }
        }
        var result = dp[n] is { } best ? string.Join(" ", best.Phones) : null;
        if (_segmentation.Count >= 20_000) _segmentation.Clear();
        _segmentation[key] = result;
        return result;
    }

    /// <summary>Character-by-character reading, the last resort.</summary>
    private string CharFallback(string content, string lang)
    {
        var sb = new StringBuilder();
        foreach (var cp in RustText.CodePoints(content))
        {
            var cl = RustText.LowerChar(cp);
            if (CachedMerged(cl) is { } m)
            {
                sb.Append(StripEn(m));
            }
            else if (CachedCommon(cl) is { } c)
            {
                var p = lang == "en" && c.En.Length > 0 ? c.En : c.Vi.Length > 0 ? c.Vi : c.En;
                sb.Append(StripEn(p));
            }
            else
            {
                sb.Append(cl);
            }
        }
        return sb.ToString();
    }

    /// <summary><c>G2P("vi").convert(text)</c>: phonemes for normalized text.</summary>
    public string Phonemize(string text)
    {
        if (text.Contains('’') || text.Contains('‘')) text = text.Replace('’', '\'').Replace('‘', '\'');
        var tokens = new List<Token>();

        foreach (Match cap in ReToken().Matches(text))
        {
            if (cap.Groups[1].Success)
            {
                var content = ReTagStrip().Replace(cap.Groups[1].Value, "").Trim();
                foreach (Match sc in ReTagContent().Matches(content))
                {
                    if (sc.Groups[1].Success)
                    {
                        var word = sc.Groups[1].Value;
                        var lw = RustText.ToLower(word);
                        string? phone = null;
                        if (CachedMerged(lw) is { } p) phone = StripEn(p);
                        else if (CachedCommon(lw) is { } c && c.En.Length > 0) phone = StripEn(c.En);
                        tokens.Add(new Token("en", word, phone, true));
                    }
                    else if (sc.Groups[2].Success)
                    {
                        tokens.Add(new Token(Punct, sc.Groups[2].Value, sc.Groups[2].Value, true));
                    }
                }
            }
            else if (cap.Groups[2].Success)
            {
                var word = cap.Groups[2].Value;
                var lw = RustText.ToLower(word);
                if (CachedMerged(lw) is { } p)
                {
                    var lang = p.Contains("<en>", StringComparison.Ordinal) ? "en" : "vi";
                    tokens.Add(new Token(lang, word, StripEn(p), false));
                }
                else if (CachedCommon(lw) is { } c)
                {
                    tokens.Add(new Token(Common, word, Sep + c.Vi.Trim() + Sep + StripEn(c.En) + Sep, false));
                }
                else
                {
                    var hasViAccent = lw.Any(ch => ViAccents.Contains(ch));
                    tokens.Add(new Token(hasViAccent ? "vi" : "en", word, null, false));
                }
            }
            else if (cap.Groups[3].Success)
            {
                tokens.Add(new Token(Punct, cap.Groups[3].Value, cap.Groups[3].Value, false));
            }
        }

        PropagateLanguage(tokens);

        var result = new List<string>(tokens.Count);
        foreach (var t in tokens)
        {
            if (t.Lang == Punct)
            {
                if (MapPunct(t.Content) is { } mp) result.Add(mp);
                continue;
            }
            string phone;
            if (t.Phone is { } p)
            {
                if (p.Length >= 2 && p[0] == Sep && p[^1] == Sep)
                {
                    var inner = p[1..^1];
                    var sep = inner.IndexOf(Sep);
                    if (sep < 0) sep = inner.Length;
                    if (t.Lang == "en")
                    {
                        phone = sep + 1 <= inner.Length ? inner[(sep + 1)..] : "";
                        if (RustText.ToLower(t.Content) == "a" && !t.IsExplicitEn) phone = "ɐ";
                    }
                    else
                    {
                        phone = inner[..sep];
                    }
                }
                else
                {
                    phone = p;
                    if (t.Lang == "en" && RustText.ToLower(t.Content) == "a" && !t.IsExplicitEn) phone = "ɐ";
                }
            }
            else
            {
                phone = SegmentOov(RustText.ToLower(t.Content), t.Lang) ?? CharFallback(t.Content, t.Lang);
            }
            result.Add(phone.Trim());
        }

        var joined = string.Join(" ", result)
            .Replace(" .", ".", StringComparison.Ordinal)
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace(" !", "!", StringComparison.Ordinal)
            .Replace(" ?", "?", StringComparison.Ordinal)
            .Replace(" ;", ";", StringComparison.Ordinal)
            .Replace(" :", ":", StringComparison.Ordinal);
        while (joined.Contains("..", StringComparison.Ordinal)) joined = joined.Replace("..", ".", StringComparison.Ordinal);
        while (joined.Contains(",,", StringComparison.Ordinal)) joined = joined.Replace(",,", ",", StringComparison.Ordinal);
        return joined;
    }

    private static bool IsStopPunct(Token t) => t.Content.Length > 0
        && RustText.CharCount(t.Content) == 1 && t.Content.Length == 1 && ".!?;:()[]{}".Contains(t.Content[0]);

    private static void PropagateLanguage(List<Token> tokens)
    {
        var n = tokens.Count;
        var defaultLang = tokens.Any(t => t.Lang == "vi") ? "vi" : "en";
        var i = 0;
        while (i < n)
        {
            if (tokens[i].Lang != Common)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < n && tokens[i].Lang == Common) i++;
            var end = i - 1;

            string? leftAnchor = null;
            var leftDist = 999;
            var d = 0;
            for (var l = start - 1; l >= 0; l--)
            {
                if (IsStopPunct(tokens[l])) break;
                if (tokens[l].Lang == Punct) continue;
                d++;
                if (tokens[l].Lang is "vi" or "en")
                {
                    leftAnchor = tokens[l].Lang;
                    leftDist = d;
                    break;
                }
            }

            string? rightAnchor = null;
            var rightDist = 999;
            d = 0;
            for (var r = end + 1; r < n; r++)
            {
                if (IsStopPunct(tokens[r])) break;
                if (tokens[r].Lang == Punct) continue;
                d++;
                if (tokens[r].Lang is "vi" or "en")
                {
                    rightAnchor = tokens[r].Lang;
                    rightDist = d;
                    break;
                }
            }

            string finalLang;
            if (leftAnchor != null && rightAnchor != null)
            {
                if (rightDist < leftDist) finalLang = rightAnchor;
                else if (leftDist < rightDist) finalLang = leftAnchor;
                else
                {
                    var bareLetters = true;
                    for (var k = start; k <= end; k++)
                        if (RustText.CharCount(tokens[k].Content) != 1) { bareLetters = false; break; }
                    finalLang = !bareLetters && (leftAnchor == "en" || rightAnchor == "en") ? "en" : rightAnchor;
                }
            }
            else finalLang = leftAnchor ?? rightAnchor ?? defaultLang;

            for (var k = start; k <= end; k++) tokens[k].Lang = finalLang;
        }
    }
}
