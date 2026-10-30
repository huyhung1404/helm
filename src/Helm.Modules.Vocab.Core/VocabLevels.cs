namespace Helm.Modules.Vocab;

/// <summary>How hard a word is: CEFR for English (A1 … C2), HSK for Chinese (HSK1 … HSK9).</summary>
public static class VocabLevels
{
    public static IReadOnlyList<string> English { get; } = ["A1", "A2", "B1", "B2", "C1", "C2"];

    public static IReadOnlyList<string> Chinese { get; } = ["HSK1", "HSK2", "HSK3", "HSK4", "HSK5", "HSK6", "HSK7", "HSK8", "HSK9"];

    /// <summary>"b1" → "B1", "HSK 3" / "3" (Chinese) → "HSK3"; null when it is not a level of that language.</summary>
    public static string? Normalize(string text, VocabLanguage language)
    {
        var t = text.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (language == VocabLanguage.Chinese)
        {
            if (!t.StartsWith("HSK", StringComparison.Ordinal)) t = "HSK" + t;
            return Chinese.Contains(t) ? t : null;
        }
        return English.Contains(t) ? t : null;
    }

    /// <summary>Easier first: the place of a level in its list; unknown levels last.</summary>
    public static int Rank(string level)
    {
        var i = English.IndexOf(level);
        if (i >= 0) return i;
        i = Chinese.IndexOf(level);
        return i >= 0 ? i : int.MaxValue;
    }

    private static int IndexOf(this IReadOnlyList<string> list, string item)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == item) return i;
        return -1;
    }
}
