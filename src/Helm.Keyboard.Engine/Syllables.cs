using System.Text;

namespace Helm.Typing;

/// <summary>
/// The spell check: whether a word being typed can still become a Vietnamese syllable. Built from the parts of a
/// syllable (initial consonant + vowel cluster + final consonant) and the rules that bind them, e.g. "k" only before
/// i/e/ê/y, "ch"/"nh" only after a/ê/i, "iê" always needs a final.
/// </summary>
internal static class Syllables
{
    private static readonly string[] Initials =
        ["", "b", "c", "ch", "d", "đ", "g", "gh", "gi", "h", "k", "kh", "l", "m", "n", "ng", "ngh", "nh", "p", "ph", "qu", "r", "s", "t", "th", "tr", "v", "x"];

    private static readonly string[] Finals = ["c", "ch", "m", "n", "ng", "nh", "p", "t"];

    /// <summary>Clusters that end the syllable: no final consonant after them.</summary>
    private static readonly string[] OpenClusters =
    [
        "y", "ai", "ao", "au", "ay", "âu", "ây", "eo", "êu", "ia", "iu", "oi", "ôi", "ơi", "ua", "ui", "ưa", "ưi", "ưu", "uơ",
        "oai", "oay", "oeo", "uôi", "ươi", "ươu", "iêu", "yêu", "uya", "uyu", "uây",
    ];

    /// <summary>Clusters with or without a final consonant.</summary>
    private static readonly string[] FreeClusters = ["a", "e", "ê", "i", "o", "ô", "ơ", "u", "ư", "oa", "oe", "uê", "uy"];

    /// <summary>Clusters that need a final consonant, with the finals they take.</summary>
    private static readonly (string Cluster, string[] Finals)[] ClosedClusters =
    [
        ("ă", ["c", "m", "n", "ng", "p", "t"]),
        ("â", ["c", "m", "n", "ng", "p", "t"]),
        ("iê", ["c", "m", "n", "ng", "p", "t"]),
        ("yê", ["m", "n", "ng", "t"]),
        ("uô", ["c", "m", "n", "ng", "t"]),
        ("ươ", ["c", "m", "n", "ng", "p", "t"]),
        ("oă", ["c", "m", "n", "ng", "t"]),
        ("uâ", ["n", "ng", "t"]),
        ("oo", ["c", "ng"]),
        ("uyê", ["n", "t"]),
    ];

    private static readonly HashSet<string> Prefixes = BuildPrefixes();

    /// <summary>
    /// True when <paramref name="word"/> (lowercase, no tone) is the start of a syllable, counting a plain a/e/o/u/d
    /// as the marked letter it may still become ("tie" → "tiê…", since the circumflex can come later).
    /// </summary>
    public static bool IsPrefix(string word) => word.Length == 0 || Walk(word, 0, new StringBuilder(word.Length));

    private static bool Walk(string word, int at, StringBuilder built)
    {
        if (at == word.Length) return true;
        foreach (var letter in Variants(word[at]))
        {
            built.Append(letter);
            if (Prefixes.Contains(built.ToString()) && Walk(word, at + 1, built)) return true;
            built.Length--;
        }
        return false;
    }

    private static string Variants(char letter) => letter switch
    {
        'a' => "aăâ",
        'e' => "eê",
        'o' => "oôơ",
        'u' => "uư",
        'd' => "dđ",
        _ => letter.ToString(),
    };

    private static HashSet<string> BuildPrefixes()
    {
        var syllables = new List<string>();
        foreach (var initial in Initials)
        {
            foreach (var cluster in OpenClusters)
            {
                if (Fits(initial, cluster)) syllables.Add(initial + cluster);
            }
            foreach (var cluster in FreeClusters)
            {
                if (!Fits(initial, cluster)) continue;
                syllables.Add(initial + cluster);
                foreach (var final in Finals)
                {
                    if (FreeClusterTakes(cluster, final)) syllables.Add(initial + cluster + final);
                }
            }
            foreach (var (cluster, finals) in ClosedClusters)
            {
                if (!Fits(initial, cluster)) continue;
                foreach (var final in finals) syllables.Add(initial + cluster + final);
            }
        }
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var syllable in syllables)
        {
            for (var length = 1; length <= syllable.Length; length++) prefixes.Add(syllable[..length]);
        }
        return prefixes;
    }

    private static bool Fits(string initial, string cluster)
    {
        var first = VietnameseLetters.Base(cluster[0]);
        return initial switch
        {
            "k" or "gh" or "ngh" => first is 'i' or 'e' or 'y',
            "c" or "ng" => first is not ('i' or 'e' or 'y'),
            "g" => first is not ('e' or 'y'),
            "gi" => first is not ('i' or 'y'),
            "qu" => first is not 'u',
            "" => true,
            // yê and yêu are written with y only when nothing comes before them (yên, yêu; but tiên, khuyên).
            _ => cluster[0] != 'y' || cluster == "y",
        };
    }

    private static bool FreeClusterTakes(string cluster, string final) => final switch
    {
        "ch" or "nh" => cluster is "a" or "ê" or "i" or "oa" or "uê" or "uy",
        "c" or "ng" => cluster is not ("i" or "uy"),
        _ => true,
    };
}
