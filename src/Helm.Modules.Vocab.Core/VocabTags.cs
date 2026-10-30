namespace Helm.Modules.Vocab;

/// <summary>The user's labels on words: tidied, without a leading #, each once (case ignored).</summary>
public static class VocabTags
{
    public const int MaxTags = 8;

    public static IReadOnlyList<string> Clean(IEnumerable<string> tags) =>
        tags.Select(t => t.Trim().TrimStart('#').Trim())
            .Where(t => t.Length is > 0 and <= 30)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxTags)
            .ToList();

    /// <summary>"Aptis, Truyện" (as typed in a box) → [Aptis, Truyện].</summary>
    public static IReadOnlyList<string> Parse(string text) => Clean(text.Split([',', ';', '，']));
}
