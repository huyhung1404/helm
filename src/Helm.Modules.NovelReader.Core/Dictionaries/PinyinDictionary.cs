using System.Globalization;

namespace Helm.Modules.NovelReader.Dictionaries;

/// <summary>
/// Pinyin with tone marks for study mode, from mozillazg's lists (MIT): pinyin-data, one line per character
/// ("U+4E2D: zhōng,zhòng  # 中", the most common reading first), and phrase-pinyin-data, one line per word
/// ("银行: yín háng"), which settles characters with several readings. A word is read by its longest listed phrases
/// first, the rest character by character; a character with no pinyin is left out. Load once, then read from any thread.
/// </summary>
public sealed class PinyinDictionary
{
    private readonly Dictionary<string, string> _chars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _phrases = new(StringComparer.Ordinal);
    private int _longest = 1;

    /// <summary>Characters and phrases with a reading.</summary>
    public int Count => _chars.Count + _phrases.Count;

    public static PinyinDictionary Parse(string text)
    {
        var dictionary = new PinyinDictionary();
        dictionary.Load(text);
        return dictionary;
    }

    /// <summary>Adds the readings of a list in either format; a reading already known stays.</summary>
    public void Load(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var hash = line.IndexOf('#');
            if (hash > 0) line = line[..hash].TrimEnd();
            var colon = line.IndexOf(':');
            var equals = line.IndexOf('=');
            var split = colon > 0 ? colon : equals;
            if (split <= 0) continue;
            var key = line[..split].Trim();
            var reading = line[(split + 1)..].Trim();
            if (reading.Length == 0) continue;
            if (key.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(key[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 0x10FFFF) continue;
                key = char.ConvertFromUtf32(code);
                // The first reading is the most common one.
                reading = reading.Split(',')[0].Trim();
            }
            if (key.Length == 0) continue;
            var single = key.Length == 1 || key.Length == 2 && char.IsSurrogatePair(key, 0);
            var map = single ? _chars : _phrases;
            if (!map.ContainsKey(key)) map[key] = reading;
            if (!single) _longest = Math.Max(_longest, key.Length);
        }
    }

    /// <summary>The pinyin of a Chinese word, syllables separated by spaces ("yóu zhǐ sǎn"); "" when none is known.</summary>
    public string Of(string chinese)
    {
        var parts = new List<string>();
        for (var i = 0; i < chinese.Length;)
        {
            var matched = false;
            for (var length = Math.Min(_longest, chinese.Length - i); length >= 2; length--)
            {
                if (!_phrases.TryGetValue(chinese.Substring(i, length), out var phrase)) continue;
                parts.Add(phrase);
                i += length;
                matched = true;
                break;
            }
            if (matched) continue;
            var character = char.IsSurrogatePair(chinese, i) ? chinese.Substring(i, 2) : chinese.Substring(i, 1);
            if (_chars.TryGetValue(character, out var reading)) parts.Add(reading);
            else if (char.IsAsciiDigit(character[0])) parts.Add(character);
            i += character.Length;
        }
        return string.Join(" ", parts);
    }

    /// <summary>A name's pinyin, every syllable capitalised (Lín Wǎn).</summary>
    public static string Capitalize(string pinyin) =>
        string.Join(" ", pinyin.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpper(s[0], CultureInfo.InvariantCulture) + s[1..]));
}
