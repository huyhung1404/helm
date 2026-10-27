// Port of sea-g2p src/lang/en/top_words.rs, src/lang/vi/vi_top_syllables.rs and src/lang/vi/vi_bigrams.rs (the data).
using System.IO.Compression;
using System.Text;

namespace Helm.Speech.Vietnamese.Core;

/// <summary>
/// The three frequency lists sea-g2p compiles in: the top English words (they settle ambiguous splits and decide when a
/// sentence is English), the frequent toneless Vietnamese syllables, and the two-syllable Vietnamese compounds. They ship
/// as gzip'd embedded resources, one entry per line, and are read once on first use.
/// </summary>
internal static class WordLists
{
    private static readonly Lazy<HashSet<string>> EnTopWordsLazy = new(() => Load("en_top_words.txt.gz"));
    private static readonly Lazy<HashSet<string>> ViTopSyllablesLazy = new(() => Load("vi_top_syllables.txt.gz"));
    private static readonly Lazy<HashSet<string>> ViBigramsLazy = new(() => Load("vi_bigrams.txt.gz"));

    /// <summary>The google-10000-english list plus the brand names sea-g2p adds.</summary>
    public static HashSet<string> EnTopWords => EnTopWordsLazy.Value;

    /// <summary>Skeletons (tone marks dropped) of the most frequent Vietnamese syllables.</summary>
    public static HashSet<string> ViTopSyllables => ViTopSyllablesLazy.Value;

    /// <summary>Skeletons of two-syllable Vietnamese compounds, "tin hoc", "khi tuong".</summary>
    public static HashSet<string> ViBigrams => ViBigramsLazy.Value;

    private static HashSet<string> Load(string name)
    {
        var asm = typeof(WordLists).Assembly;
        using var stream = asm.GetManifestResourceStream("Helm.Speech.Vietnamese.Data." + name)
            ?? throw new InvalidOperationException("Missing embedded resource " + name);
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0) set.Add(line);
        return set;
    }
}
