// C# port of sea-g2p 0.10.0 (https://github.com/pnnbao97/sea-g2p, Apache-2.0): the Vietnamese pipeline only.
using System.Text;
using Helm.Speech.Vietnamese.Core;
using Helm.Speech.Vietnamese.G2p;
using Helm.Speech.Vietnamese.Normalization;

namespace Helm.Speech.Vietnamese;

/// <summary>
/// Vietnamese text to phonemes for the VieNeu voices, byte-identical to sea-g2p 0.10.0's
/// <c>SEAPipeline(lang="vi").run(text, punc_norm=True)</c>, which is what the VieNeu models were trained on. The text is
/// normalized first (numbers, dates, units, abbreviations, URLs become words), then each word is looked up in the
/// memory-mapped phoneme dictionary, with Vietnamese or English chosen per word from context. After
/// <see cref="Open"/> the engine is read-only, so <see cref="Phonemize"/> may be called from many threads at once.
/// </summary>
public sealed class VietnamesePhonemizer : IDisposable
{
    private readonly PhonemeDictionary _dict;
    private readonly VietnameseNormalizer _normalizer;
    private readonly G2pEngine _g2p;

    private VietnamesePhonemizer(PhonemeDictionary dict)
    {
        _dict = dict;
        _normalizer = new VietnameseNormalizer(dict);
        _g2p = new G2pEngine(dict);
    }

    /// <summary>
    /// Map sea-g2p's dictionary (sea_g2p.bin, the "SEAP" format) read-only. Throws <see cref="IOException"/> when the file
    /// cannot be read and <see cref="InvalidDataException"/> when it is not a SEAP dictionary.
    /// </summary>
    public static VietnamesePhonemizer Open(string dictionaryPath)
    {
        ArgumentNullException.ThrowIfNull(dictionaryPath);
        return new VietnamesePhonemizer(PhonemeDictionary.Open(dictionaryPath));
    }

    /// <summary>Normalize, settle the trailing punctuation, then phonemize: <c>SEAPipeline("vi").run(text, punc_norm=True)</c>.</summary>
    public string Phonemize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return "";
        var clean = ReplaceLoneSurrogates(text);
        var normalized = _normalizer.Normalize(clean, puncNorm: true);
        return _g2p.Phonemize(normalized);
    }

    /// <summary>The normalized text alone (<c>Normalizer("vi").normalize(text, punc_norm=True)</c>), for diagnostics.</summary>
    internal string Normalize(string text) => _normalizer.Normalize(ReplaceLoneSurrogates(text), puncNorm: true);

    /// <summary>
    /// <c>sea_g2p.punc_norm(text)</c>: a short sentence (under five words) ends in exactly one ".", a longer one gains a "."
    /// unless it already ends in , . ! ?, and a one- or two-word fragment ending in "." mid-string gets a "," instead.
    /// </summary>
    public static string PunctuationNorm(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PunctuationRules.ApplyPuncNorm(text);
    }

    /// <summary>Release the dictionary map. No call may still be running on another thread.</summary>
    public void Dispose() => _dict.Dispose();

    // A Rust string cannot hold a lone surrogate, so sea-g2p never sees one; the port maps one to U+FFFD rather than
    // letting Unicode normalization throw.
    private static string ReplaceLoneSurrogates(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var bad = char.IsHighSurrogate(c) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])
                : char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));
            if (bad)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append('�');
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? s;
    }
}
