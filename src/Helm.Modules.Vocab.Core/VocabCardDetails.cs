namespace Helm.Modules.Vocab;

/// <summary>One line of a section of a word's full details, with a quieter second line (a translation, a meaning).</summary>
public sealed record VocabDetailItem(string Text, string Note = "")
{
    public bool HasNote => Note.Length > 0;
}

/// <summary>A titled section of a word's full details: examples, collocations, the tree…</summary>
public sealed record VocabDetailSection(string Title, IReadOnlyList<VocabDetailItem> Items, bool IsTree = false);

/// <summary>
/// Everything Helm knows about a word, as plain sections the pop-up card and the phone's floating card show under
/// "Full details" (so there is no need to open the app): examples, collocations, the mnemonic, words easy to mix up,
/// the word family, synonyms, antonyms and the whole tree of parts. Empty sections are left out.
/// </summary>
public static class VocabCardDetails
{
    public static IReadOnlyList<VocabDetailSection> Build(VocabStore store, VocabWord word)
    {
        var sections = new List<VocabDetailSection>();
        void Add(string title, IEnumerable<VocabDetailItem> items, bool tree = false)
        {
            var list = items.Where(i => i.Text.Length > 0).ToList();
            if (list.Count > 0) sections.Add(new VocabDetailSection(title, list, tree));
        }
        Add("Examples", word.Examples.Select(e => new VocabDetailItem(e.Sentence, e.Translation)));
        Add("Goes with", word.Collocations.Select(c => new VocabDetailItem(c)));
        Add("To remember it", [new VocabDetailItem(word.Mnemonic)]);
        Add("Easy to mix up", word.Confusables.Select(c => new VocabDetailItem(c.Word, c.Note)));
        Add("Word family", word.Forms.Select(f => new VocabDetailItem(f.PartOfSpeech.Length > 0 ? $"{f.Word} ({f.PartOfSpeech})" : f.Word, f.Meaning)));
        Add("Synonyms", word.Synonyms.Select(s => new VocabDetailItem(s)));
        Add("Antonyms", word.Antonyms.Select(s => new VocabDetailItem(s)));
        Add("How it's built", TreeLines(VocabTree.Build(store, word)).Select(l => new VocabDetailItem(l)), tree: true);
        return sections;
    }

    /// <summary>
    /// The tree as text, one box per line under its parent: "补救", "├─ 补 bǔ · vá, bù", "│  ├─ 衤 · áo (meaning)",
    /// "│  └─ 卜 bǔ (sound)". Nothing for a word without parts.
    /// </summary>
    public static IReadOnlyList<string> TreeLines(VocabTreeNode root)
    {
        if (root.Children.Count == 0) return [];
        var lines = new List<string> { Box(root) };
        Walk(root.Children, "", lines);
        return lines;
    }

    private static void Walk(IReadOnlyList<VocabTreeNode> nodes, string indent, List<string> lines)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var last = i == nodes.Count - 1;
            lines.Add(indent + (last ? "└─ " : "├─ ") + Box(nodes[i]));
            Walk(nodes[i].Children, indent + (last ? "   " : "│  "), lines);
        }
    }

    private static string Box(VocabTreeNode node)
    {
        var head = string.Join(" ", new[] { node.Form, node.Phonetic }.Where(s => s.Length > 0));
        var meaning = string.Join(" · ", new[] { node.HanViet, node.Meaning }.Where(s => s.Length > 0));
        var role = node.Role switch
        {
            VocabPartRole.Sound => " (sound)",
            VocabPartRole.Meaning => " (meaning)",
            _ => "",
        };
        return meaning.Length > 0 ? $"{head} · {meaning}{role}" : head + role;
    }
}
