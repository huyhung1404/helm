namespace Helm.Modules.Vocab;

/// <summary>A word cut around where one of its parts sits (re<b>lat</b>ive for lat), so the part can be highlighted.</summary>
public sealed record VocabWordSplit(string Before, string Match, string After)
{
    public static VocabWordSplit None(string text) => new(text, "", "");
}

/// <summary>
/// Where a part shows in a word. The part is followed down the word's tree; the part itself, or else the nearest box
/// above it, that is written inside the word is the spot: lat in relative, relat(e) in relative, 救 for 攵 in 补救
/// (a component is not written apart from its character).
/// </summary>
public static class VocabHighlight
{
    public static VocabWordSplit Find(VocabStore store, VocabWord word, string partId)
    {
        var path = new List<VocabTreeNode>();
        if (!Walk(VocabTree.Build(store, word), partId, path)) return VocabWordSplit.None(word.Text);
        // From the part up to (not including) the word itself.
        for (var i = path.Count - 1; i >= 1; i--)
        {
            foreach (var form in Forms(path[i].Form))
            {
                var at = path[i].Kind == VocabPartKind.Suffix
                    ? word.Text.LastIndexOf(form, StringComparison.OrdinalIgnoreCase)
                    : word.Text.IndexOf(form, StringComparison.OrdinalIgnoreCase);
                if (at >= 0) return new VocabWordSplit(word.Text[..at], word.Text.Substring(at, form.Length), word.Text[(at + form.Length)..]);
            }
        }
        return VocabWordSplit.None(word.Text);
    }

    private static bool Walk(VocabTreeNode node, string partId, List<VocabTreeNode> path)
    {
        path.Add(node);
        if (node.PartId == partId || node.Children.Any(c => Walk(c, partId, path))) return true;
        path.RemoveAt(path.Count - 1);
        return false;
    }

    /// <summary>The part as written without its dashes, and without a final silent e (relate → relat in relative).</summary>
    private static IEnumerable<string> Forms(string form)
    {
        var plain = form.Trim().Trim('-');
        if (plain.Length == 0) yield break;
        yield return plain;
        if (plain.Length > 3 && plain.EndsWith('e')) yield return plain[..^1];
    }
}
