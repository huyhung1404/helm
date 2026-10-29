using System.Globalization;
using System.Text;

namespace Helm.Modules.Vocab;

/// <summary>Small text rules: which language a word is in, how it is tidied and compared.</summary>
public static class VocabText
{
    public const int MaxWordLength = 60;

    /// <summary>Chinese when it has a Han character, English otherwise.</summary>
    public static VocabLanguage LanguageOf(string text) => text.Any(IsHan) ? VocabLanguage.Chinese : VocabLanguage.English;

    /// <summary>A CJK ideograph or radical (the blocks Chinese words and their components are written in).</summary>
    public static bool IsHan(char c) =>
        c is >= '一' and <= '鿿'   // CJK Unified Ideographs
            or >= '㐀' and <= '䶿' // Extension A
            or >= '⺀' and <= '⿟' // CJK Radicals Supplement, Kangxi Radicals
            or >= '豈' and <= '﫿' // Compatibility Ideographs
            or >= '㇀' and <= '㇯' // CJK Strokes
            || char.IsSurrogate(c);        // Extensions B and later (the pair is checked as a whole elsewhere)

    /// <summary>Trimmed, inner runs of spaces made one; Chinese loses its spaces altogether.</summary>
    public static string Tidy(string text)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return LanguageOf(text) == VocabLanguage.Chinese ? string.Concat(parts) : string.Join(' ', parts);
    }

    /// <summary>The key two spellings of the same word share: tidied, case ignored.</summary>
    public static string Key(string text) => Tidy(text).ToLowerInvariant().Normalize(NormalizationForm.FormC);

    /// <summary>How many characters as the eye sees them (a Han character outside the BMP is one).</summary>
    public static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The characters of a Chinese word, one string each.</summary>
    public static IReadOnlyList<string> Characters(string text)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }
}
