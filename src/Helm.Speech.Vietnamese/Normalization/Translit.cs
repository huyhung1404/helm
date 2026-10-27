// Port of sea-g2p src/lang/vi/translit.rs.
using System.Text;
using Helm.Speech.Vietnamese.Core;

namespace Helm.Speech.Vietnamese.Normalization;

/// <summary>
/// Foreign Latin letters folded onto their ASCII base ("Müller" becomes "Muller", "Straße" becomes "Strasse"), so the
/// final whitelist does not cut such a word in half. Vietnamese letters, non-Latin scripts and symbols such as µ and Ω
/// are never folded.
/// </summary>
internal static class Translit
{
    private static readonly HashSet<char> ViLetterSet = [.. Resources.ViLetters];

    private static readonly Dictionary<char, string> Irreducible = new()
    {
        ['ß'] = "ss", ['ẞ'] = "SS",
        ['ø'] = "o", ['Ø'] = "O",
        ['ǿ'] = "o", ['Ǿ'] = "O",
        ['æ'] = "ae", ['Æ'] = "AE",
        ['œ'] = "oe", ['Œ'] = "OE",
        ['ł'] = "l", ['Ł'] = "L",
        ['þ'] = "th", ['Þ'] = "TH",
        ['ð'] = "d", ['Ð'] = "D",
        ['ħ'] = "h", ['Ħ'] = "H",
        ['ŧ'] = "t", ['Ŧ'] = "T",
        ['ı'] = "i",
        ['ĸ'] = "k",
        ['ŋ'] = "ng", ['Ŋ'] = "NG",
        ['ĳ'] = "ij", ['Ĳ'] = "IJ",
        ['ŀ'] = "l", ['Ŀ'] = "L",
        ['ŉ'] = "n",
        ['ſ'] = "s",
    };

    private static bool IsCombiningMark(int c) =>
        c is >= 0x0300 and <= 0x036F or >= 0x1AB0 and <= 0x1AFF or >= 0x1DC0 and <= 0x1DFF or >= 0x20D0 and <= 0x20FF
            or >= 0xFE20 and <= 0xFE2F;

    private static string? FoldLetter(string ch)
    {
        var cp = RustText.FirstChar(ch);
        if (cp < 0x80 || !RustText.IsAlphabetic(cp) || (ch.Length == 1 && ViLetterSet.Contains(ch[0]))) return null;
        if (ch.Length == 1 && Irreducible.TryGetValue(ch[0], out var b)) return b;
        string decomposed;
        try
        {
            decomposed = ch.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var d in RustText.CodePoints(decomposed))
            if (!IsCombiningMark(d)) sb.Append(RustText.FromCodePoint(d));
        var baseText = sb.ToString();
        if (baseText.Length > 0 && baseText.All(RustText.IsAsciiAlpha)) return baseText;
        return null;
    }

    public static string FoldForeignLetters(string text)
    {
        if (RustText.IsAscii(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in RustText.Chars(text)) sb.Append(FoldLetter(ch) ?? ch);
        return sb.ToString();
    }
}
