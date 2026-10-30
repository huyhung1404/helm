using System.Text;
using System.Text.RegularExpressions;

namespace Helm.Modules.Vocab;

/// <summary>A piece of a pinyin text: a syllable with its tone (1-4, 5 neutral), or anything else (0).</summary>
public sealed record PinyinPiece(string Text, int Tone);

/// <summary>
/// Pinyin cut into syllables with their tones ("bǔjiù" → bǔ 3, jiù 4), so each can be coloured: the tones are what
/// learners mix up most. Text without any tone mark (IPA, plain text) comes back whole, as tone 0.
/// </summary>
public static partial class PinyinTones
{
    private const string Marked = "āáǎàēéěèīíǐìōóǒòūúǔùǖǘǚǜĀÁǍÀĒÉĚÈĪÍǏÌŌÓǑÒŪÚǓÙǕǗǙǛ";
    private const string Bases = "aeiouvAEIOUV";

    public static IReadOnlyList<PinyinPiece> Split(string text)
    {
        if (!text.Any(c => Marked.Contains(c))) return [new PinyinPiece(text, 0)];
        // A plain copy of the same length: tone marks removed, ü written v, so the syllable pattern can run on it.
        var plain = new StringBuilder(text.Length);
        var tones = new int[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var at = Marked.IndexOf(c);
            if (at >= 0)
            {
                plain.Append(char.ToLowerInvariant(Bases[at / 4 % 6]));
                tones[i] = at % 4 + 1;
            }
            else plain.Append(c is 'ü' or 'Ü' ? 'v' : char.ToLowerInvariant(c));
        }
        var pieces = new List<PinyinPiece>();
        var last = 0;
        foreach (Match m in Syllable().Matches(plain.ToString()))
        {
            if (m.Index > last) pieces.Add(new PinyinPiece(text[last..m.Index], 0));
            var tone = tones.Skip(m.Index).Take(m.Length).FirstOrDefault(t => t > 0);
            pieces.Add(new PinyinPiece(text.Substring(m.Index, m.Length), tone > 0 ? tone : 5));
            last = m.Index + m.Length;
        }
        if (last < text.Length) pieces.Add(new PinyinPiece(text[last..], 0));
        return pieces;
    }

    /// <summary>
    /// One syllable: an initial, then a final. A closing n, ng or r goes to the next syllable when a vowel follows
    /// ("jīnān" → jī + nān is not decided by spelling alone; this keeps the common reading).
    /// </summary>
    [GeneratedRegex(@"(?:zh|ch|sh|[bpmfdtnlgkhjqxrzcsyw])?(?:i(?:ang|ong|ao|an|ng|ai|a|e|n|o|u)?|u(?:ang|ai|an|en|eng|ei|n|o|a|e|i)?|v(?:an|e|n)?|a(?:ng|n|i|o)?|o(?:ng|u)?|e(?:ng|n|i|r)?)(?![aeiouv])|(?:zh|ch|sh|[bpmfdtnlgkhjqxrzcsyw])?[aeiouv]+", RegexOptions.CultureInvariant)]
    private static partial Regex Syllable();
}
