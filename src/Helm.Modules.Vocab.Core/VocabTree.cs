namespace Helm.Modules.Vocab;

/// <summary>One box of a word's tree: the word itself at the top, its parts below, theirs below those.</summary>
public sealed record VocabTreeNode
{
    /// <summary>Null for the word at the top.</summary>
    public string? PartId { get; init; }
    public string Form { get; init; } = "";
    public VocabPartKind? Kind { get; init; }
    public VocabPartRole Role { get; init; }
    public string Phonetic { get; init; } = "";
    public string HanViet { get; init; } = "";
    public string Traditional { get; init; } = "";
    public string Meaning { get; init; } = "";
    public string Origin { get; init; } = "";
    public IReadOnlyList<VocabTreeNode> Children { get; init; } = [];
}

public static class VocabTree
{
    /// <summary>The word's tree. A part met again on its own path (two devices' edits meeting) is not followed.</summary>
    public static VocabTreeNode Build(VocabStore store, VocabWord word)
    {
        var path = new HashSet<string>(StringComparer.Ordinal);
        return new VocabTreeNode
        {
            Form = word.Text,
            Phonetic = word.Phonetic,
            HanViet = word.HanViet,
            Traditional = word.Traditional,
            Meaning = word.Meaning,
            Origin = word.Explanation,
            Children = Children(store, word.Parts, path, 0),
        };
    }

    /// <summary>
    /// The first two levels in one line, as a reminder of how the word is built: "补 (衤 + 卜) + 救 (求 + 攵)",
    /// "relate (re- + lat) + -ive". Empty for a word without parts.
    /// </summary>
    public static string Outline(VocabStore store, VocabWord word) =>
        string.Join(" + ", word.Parts.Select(l => store.GetPart(l.PartId)).OfType<VocabPart>().Select(p =>
        {
            var inner = p.Parts.Select(c => store.GetPart(c.PartId)?.Form).OfType<string>().ToList();
            return inner.Count > 0 ? $"{p.Form} ({string.Join(" + ", inner)})" : p.Form;
        }));

    /// <summary>
    /// One line per part with its meaning and its own pieces, so a forgotten word comes back at a glance:
    /// "补 · vá, bù ← 衤 áo + 卜 (âm)", "relate · liên hệ ← re- lại + lat mang", "-ive · có tính chất".
    /// </summary>
    public static IReadOnlyList<string> Lines(VocabStore store, VocabWord word) =>
        word.Parts.Select(l => store.GetPart(l.PartId)).OfType<VocabPart>().Select(p =>
        {
            var head = p.Meaning.Length > 0 ? $"{p.Form} · {p.Meaning}" : p.Form;
            var inner = p.Parts.Select(c => (Link: c, Part: store.GetPart(c.PartId))).Where(c => c.Part is not null)
                .Select(c => c.Link.Role == VocabPartRole.Sound ? $"{c.Part!.Form} (âm)" : $"{c.Part!.Form} {Short(c.Part.Meaning)}".TrimEnd())
                .ToList();
            return inner.Count > 0 ? $"{head} ← {string.Join(" + ", inner)}" : head;
        }).ToList();

    /// <summary>The first sense of a meaning ("đánh, thúc (bộ Phộc)" → "đánh"), to keep a line short.</summary>
    private static string Short(string meaning)
    {
        var end = meaning.IndexOfAny([',', ';', '(']);
        return (end > 0 ? meaning[..end] : meaning).Trim();
    }

    private static List<VocabTreeNode> Children(VocabStore store, IReadOnlyList<VocabPartLink> links, HashSet<string> path, int depth)
    {
        var list = new List<VocabTreeNode>();
        if (depth > VocabAnalyzer.MaxDepth) return list;
        foreach (var link in links)
        {
            if (store.GetPart(link.PartId) is not { } part || !path.Add(link.PartId)) continue;
            list.Add(new VocabTreeNode
            {
                PartId = link.PartId,
                Form = part.Form,
                Kind = part.Kind,
                Role = link.Role,
                Phonetic = part.Phonetic,
                HanViet = part.HanViet,
                Traditional = part.Traditional,
                Meaning = part.Meaning,
                Origin = part.Origin,
                Children = Children(store, part.Parts, path, depth + 1),
            });
            path.Remove(link.PartId);
        }
        return list;
    }
}
