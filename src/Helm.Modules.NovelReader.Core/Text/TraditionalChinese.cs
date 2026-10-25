using System.IO.Compression;
using System.Text;

namespace Helm.Modules.NovelReader.Text;

/// <summary>
/// Traditional Chinese to Simplified, with OpenCC's tables (phrases first, longest match, then characters; built into
/// this assembly, Apache-2.0). The VietPhrase dictionaries are written in Simplified: on a Traditional novel (Taiwanese
/// sites such as xbanxia) converting first lets about 74% of its characters be read as phrases instead of 57%.
/// </summary>
public static class TraditionalChinese
{
    private sealed record Tables(Dictionary<string, string> Phrases, Dictionary<string, string> Characters, int LongestPhrase);

    private static readonly Lazy<Tables> Loaded = new(Load);

    /// <summary>The text is Traditional: at least 1% of its Chinese characters (and five of them) exist only in Traditional.</summary>
    public static bool Looks(string text)
    {
        var characters = Loaded.Value.Characters;
        int chinese = 0, traditional = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is < '㐀' or > '鿿') continue;
            chinese++;
            if (characters.ContainsKey(c.ToString())) traditional++;
        }
        return traditional >= 5 && traditional * 100 >= chinese;
    }

    /// <summary>The text in Simplified characters; anything else stays as it is.</summary>
    public static string ToSimplified(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var tables = Loaded.Value;
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length;)
        {
            var replaced = false;
            for (var n = Math.Min(tables.LongestPhrase, text.Length - i); n >= 2 && !replaced; n--)
            {
                if (!tables.Phrases.TryGetValue(text.Substring(i, n), out var phrase)) continue;
                (sb ??= new StringBuilder(text, 0, i, text.Length)).Append(phrase);
                i += n;
                replaced = true;
            }
            if (replaced) continue;
            var width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            var character = text.Substring(i, width);
            if (tables.Characters.TryGetValue(character, out var simple))
                (sb ??= new StringBuilder(text, 0, i, text.Length)).Append(simple);
            else
                sb?.Append(character);
            i += width;
        }
        return sb?.ToString() ?? text;
    }

    private static Tables Load()
    {
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        var characters = new Dictionary<string, string>(StringComparer.Ordinal);
        using var resource = typeof(TraditionalChinese).Assembly.GetManifestResourceStream("Helm.Modules.NovelReader.opencc-ts.txt.gz")
            ?? throw new InvalidOperationException("The Traditional Chinese tables are missing from the build.");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var parts = line.Split('\t');
            if (parts.Length != 3 || parts[1].Length == 0 || parts[1] == parts[2]) continue;
            (parts[0] == "P" ? phrases : characters)[parts[1]] = parts[2];
        }
        return new Tables(phrases, characters, phrases.Count == 0 ? 0 : phrases.Keys.Max(k => k.Length));
    }
}
