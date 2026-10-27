// Port of sea-g2p src/lang/vi/misc.rs (the acronym branch: normalize_acronyms, is_vietnamese_syllable, spell_vi).
using System.Text.RegularExpressions;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Acronyms, formulas and codes ("CTV", "CO2", "HbA1c"). The abbreviation table decides first; an all-caps token that
/// is one valid Vietnamese syllable and a dictionary word is a shouted word ("tôi HO một cái"); anything else is spelled,
/// with Vietnamese letter names by default and English ones in an English sentence. A heading or shouted sentence in
/// all caps is left as prose.
/// </summary>
internal static partial class Acronyms
{
    private const string ViUpper = "ĐĂÂÊÔƠƯ";
    private const string ViLowerMarks = "đăâêôơư";

    /// <summary>At least two uppercase-initial units, each with an optional lowercase letter and digits, plus a tail after a digit.</summary>
    public static readonly Regex ReAcronym = Rx.Build(
        Rx.BL + @"(?=[A-Z" + ViUpper + "a-z" + ViUpper + "0-9]*[A-Z" + ViUpper + "])(?:[A-Z" + ViUpper + "][a-z" + ViLowerMarks
        + @"]?\d*){2,}(?:(?<=\d)[a-z" + ViLowerMarks + @"]+)?" + Rx.BR);

    /// <summary>Sentence boundaries for the all-caps test: terminators, or a newline (headings end without a period).</summary>
    [GeneratedRegex(@"([.!?]+(?:\s+|\z)|\n+)", Rx.Options)]
    private static partial Regex ReAcronymsSplit();

    private const string ViVowels = "aáàảãạăắằẳẵặâấầẩẫậeéèẻẽẹêếềểễệiíìỉĩịoóòỏõọôốồổỗộơớờởỡợuúùủũụưứừửữựyýỳỷỹỵ";

    private static bool IsViVowel(string c) => c.Length == 1 && ViVowels.Contains(c[0]);

    private static readonly string[] Onsets =
    [
        "ngh", "ng", "nh", "ch", "gh", "gi", "kh", "ph", "th", "tr", "qu",
        "b", "c", "d", "đ", "g", "h", "k", "l", "m", "n", "p", "q", "r", "s", "t", "v", "x",
    ];

    /// <summary>Is the token one valid Vietnamese syllable (onset, vowel nucleus, final)? Strict: unsure means no.</summary>
    public static bool IsVietnameseSyllable(string s)
    {
        var chars = RustText.Chars(RustText.ToLower(s));
        var n = chars.Count;
        if (n == 0 || chars.Any(c => !RustText.IsAlphabetic(RustText.FirstChar(c)))) return false;
        var i = 0;
        foreach (var cand in Onsets)
        {
            var cl = cand.Length;
            if (i + cl < n)
            {
                var prefix = string.Concat(chars.GetRange(i, cl));
                if (prefix == cand && IsViVowel(chars[i + cl]))
                {
                    i += cl;
                    break;
                }
            }
        }
        var vStart = i;
        while (i < n && IsViVowel(chars[i])) i++;
        if (i == vStart) return false;
        var coda = string.Concat(chars.Skip(i));
        return coda is "" or "c" or "ch" or "m" or "n" or "ng" or "nh" or "p" or "t";
    }

    /// <summary>Spell a token with Vietnamese letter names, digits figure by figure: "CTV" is "xê tê vê".</summary>
    public static string SpellVi(string word)
    {
        var parts = new List<string>();
        foreach (var ch in RustText.Chars(word))
        {
            if (!RustText.IsAlphanumeric(RustText.FirstChar(ch))) continue;
            if (ch.Length == 1 && RustText.IsAsciiDigit(ch[0]))
            {
                parts.Add(Num2Vi.N2WSingle(ch));
            }
            else
            {
                var cl = RustText.ToLower(ch);
                parts.Add(Resources.ViLetterNames.GetValueOrDefault(cl, cl));
            }
        }
        return string.Join(" ", parts);
    }

    private static string SpacedLower(string word) => string.Join(" ", RustText.Chars(word).Select(RustText.ToLower));

    private static bool IsSubscriptDigit(int c) => c is >= '₀' and <= '₉';

    private static string ReadAcronym(PhonemeDictionary dict, string word, bool enCtx)
    {
        if (word.All(RustText.IsAsciiDigit)) return word;
        var reading = Resources.ViAbbrev.Get(word);
        if (reading is { } r)
        {
            switch (r.Kind)
            {
                case ReadingKind.WordEn:
                    return Technical.EnMarked(RustText.ToLower(word));
                case ReadingKind.LettersNative:
                    if (enCtx) return "__start_en__" + SpacedLower(word) + "__end_en__";
                    var names = new List<string>();
                    foreach (var ch in RustText.Chars(word))
                        if (Resources.ViLetterNames.TryGetValue(RustText.ToLower(ch), out var name)) names.Add(name);
                    return string.Join(" ", names);
                case ReadingKind.LettersEn:
                    return "__start_en__" + SpacedLower(word) + "__end_en__";
            }
        }

        var hasViLetter = RustText.Any(word, c => c >= 0x80 && RustText.IsAlphabetic(c));
        var isMixedCase = RustText.Any(word, RustText.IsLowercase) && RustText.Any(word, RustText.IsUppercase);
        var hasSubscript = RustText.Any(word, IsSubscriptDigit);

        if (hasViLetter && IsVietnameseSyllable(word)) return RustText.ToLower(word);

        if (!hasViLetter && !isMixedCase && !RustText.Any(word, RustText.IsNumeric) && IsVietnameseSyllable(word)
            && Technical.DictHasVi(dict, RustText.ToLower(word)))
            return RustText.ToLower(word);

        if (hasViLetter || isMixedCase || hasSubscript)
        {
            var parts = new List<string>();
            foreach (var ch in RustText.Chars(word))
            {
                var cl = RustText.ToLower(ch);
                var cp = RustText.FirstChar(ch);
                if (ch.Length == 1 && RustText.IsAsciiDigit(ch[0])) parts.Add(Num2Vi.N2WSingle(ch));
                else if (Resources.ViLetterNames.TryGetValue(cl, out var name)) parts.Add(name);
                else if (ch.Length == 1 && Resources.SubscriptsMap.TryGetValue(ch[0], out var sub)) parts.Add(sub.Trim());
                else if (RustText.IsAlphabetic(cp)) parts.Add(cl);
            }
            return string.Join(" ", parts);
        }

        if (RustText.Any(word, c => c is >= '0' and <= '9' || IsSubscriptDigit(c)))
        {
            var res = new List<string>();
            foreach (var ch in RustText.Chars(word))
            {
                if (ch.Length == 1 && RustText.IsAsciiDigit(ch[0])) res.Add(Num2Vi.N2WSingle(ch));
                else if (ch.Length == 1 && Resources.SubscriptsMap.TryGetValue(ch[0], out var sub)) res.Add(sub.Trim());
                else res.Add(Resources.ViLetterNames.GetValueOrDefault(RustText.ToLower(ch), ch));
            }
            return string.Join(" ", res);
        }

        if (!enCtx)
        {
            var spelled = SpellVi(word);
            return spelled.Length == 0 ? word : spelled;
        }

        var lower = RustText.ToLower(word);
        if (Technical.DictHasEn(dict, lower)) return lower;
        var spaced = string.Join(" ", RustText.Chars(word)
            .Where(ch => RustText.IsAlphanumeric(RustText.FirstChar(ch)))
            .Select(RustText.ToLower));
        return spaced.Length > 0 ? "__start_en__" + spaced + "__end_en__" : word;
    }

    public static string NormalizeAcronyms(PhonemeDictionary dict, string text, bool enCtx)
    {
        var parts = new List<string>();
        var last = 0;
        foreach (Match m in ReAcronymsSplit().Matches(text))
        {
            parts.Add(text[last..m.Index]);
            parts.Add(m.Value);
            last = m.Index + m.Length;
        }
        parts.Add(text[last..]);

        var result = new System.Text.StringBuilder(text.Length + 16);
        for (var i = 0; i < parts.Count; i += 2)
        {
            var s = parts[i];
            var sep = i + 1 < parts.Count ? parts[i + 1] : "";
            if (s.Length == 0)
            {
                result.Append(sep);
                continue;
            }
            var words = RustText.SplitWhitespace(s);
            var letterTokens = words.Where(w => RustText.Any(w, RustText.IsAlphabetic)).ToList();
            var isAllCaps = letterTokens.Count > 0 && letterTokens.All(w =>
                !RustText.Any(w, RustText.IsNumeric)
                && RustText.All(w, c => !RustText.IsAlphabetic(c) || RustText.IsUppercase(c)));

            var processed = s;
            if (!isAllCaps) processed = ReAcronym.Replace(processed, m => ReadAcronym(dict, m.Value, enCtx));
            result.Append(processed).Append(sep);
        }
        return result.ToString();
    }
}
