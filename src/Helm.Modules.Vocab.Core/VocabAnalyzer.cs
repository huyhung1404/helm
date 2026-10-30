namespace Helm.Modules.Vocab;

/// <summary>An analysis that cannot be saved: the agent reads the message and corrects it.</summary>
public sealed class VocabAnalysisException(string message) : Exception(message);

/// <summary>What an agent found about a word: its own fields and the tree of its parts.</summary>
public sealed record WordAnalysis
{
    public string Phonetic { get; init; } = "";
    public string Meaning { get; init; } = "";
    public string? HanViet { get; init; }
    public string? Traditional { get; init; }
    public string? Explanation { get; init; }
    public IReadOnlyList<AnalysisPart> Parts { get; init; } = [];

    // Optional: left out, the word keeps what it has.
    public IReadOnlyList<VocabExample>? Examples { get; init; }
    public IReadOnlyList<string>? Collocations { get; init; }
    public string? Mnemonic { get; init; }
    public IReadOnlyList<VocabConfusable>? Confusables { get; init; }
    public string? Level { get; init; }
    public string? PartOfSpeech { get; init; }
    public string? Topic { get; init; }
    public IReadOnlyList<VocabForm>? Forms { get; init; }
    public IReadOnlyList<string>? Synonyms { get; init; }
    public IReadOnlyList<string>? Antonyms { get; init; }
}

/// <summary>
/// One part in an analysis. With <see cref="Id"/> it is a part Helm already has: the fields given replace its own, and
/// <see cref="Parts"/> left out keeps its smaller parts. Without an id it is a new part, unless one with the same form
/// and meaning already exists (then that one is used).
/// </summary>
public sealed record AnalysisPart
{
    public string? Id { get; init; }
    public string? Form { get; init; }
    public VocabPartKind? Kind { get; init; }
    public VocabPartRole Role { get; init; }
    public string? Phonetic { get; init; }
    public string? Meaning { get; init; }
    public string? HanViet { get; init; }
    public string? Traditional { get; init; }
    public string? Origin { get; init; }
    public IReadOnlyList<AnalysisPart>? Parts { get; init; }
}

/// <summary>
/// Checks a whole analysis against the word and the parts already saved, then writes its parts bottom-up. Nothing is
/// written unless everything passes, so a rejected analysis leaves no half-made parts behind. Used under the store's lock.
/// </summary>
internal sealed class VocabAnalyzer(VocabStore store, VocabWord word)
{
    public const int MaxDepth = 8;
    public const int MaxParts = 80;
    private const int MaxForm = 24;
    private const int MaxText = 600;

    private static readonly VocabPartKind[] EnglishKinds = [VocabPartKind.Prefix, VocabPartKind.Root, VocabPartKind.Suffix, VocabPartKind.Word];
    private static readonly VocabPartKind[] ChineseKinds = [VocabPartKind.Character, VocabPartKind.Component];

    private readonly bool _chinese = word.Language == VocabLanguage.Chinese;
    private int _count;
    // New parts made by this analysis, by form and meaning: the same new part twice is written once.
    private readonly Dictionary<(string, string), string> _made = [];

    public IReadOnlyList<VocabPartLink> Write(WordAnalysis analysis)
    {
        Check(analysis);
        return analysis.Parts.Select(p => new VocabPartLink { PartId = Save(p), Role = p.Role }).ToList();
    }

    // ---- checking ------------------------------------------------------------------------------------------------

    private void Check(WordAnalysis a)
    {
        Require(a.Phonetic, _chinese ? "phonetic (pinyin with tone marks)" : "phonetic (IPA)", word.Text);
        Require(a.Meaning, "meaning (in Vietnamese)", word.Text);
        if (_chinese) Require(a.HanViet, "han_viet", word.Text);
        Limit(a.Meaning, "meaning", word.Text);
        VocabDetails.Check(word, a);
        if (_chinese && VocabText.Length(word.Text) > 1 && a.Parts.Count > 0)
        {
            // A Chinese word breaks into its characters first, in order.
            var chars = a.Parts.Select(p => Form(p)).ToList();
            if (string.Concat(chars) != word.Text)
                throw new VocabAnalysisException($"The parts of {word.Text} are its characters in order ({string.Join(" + ", VocabText.Characters(word.Text))}), " +
                    $"not {string.Join(" + ", chars)}. Break each character into components below it.");
            if (a.Parts.FirstOrDefault(p => Kind(p) != VocabPartKind.Character) is { } other)
                throw new VocabAnalysisException($"{Form(other)} in {word.Text} is a character (kind character).");
        }
        var path = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in a.Parts) CheckPart(part, word.Text, 1, path);
    }

    private void CheckPart(AnalysisPart p, string parent, int depth, HashSet<string> path)
    {
        if (++_count > MaxParts) throw new VocabAnalysisException($"At most {MaxParts} parts in one word; stop where the parts stop adding meaning.");
        if (depth > MaxDepth) throw new VocabAnalysisException($"At most {MaxDepth} levels below the word.");
        var existing = p.Id is null ? null : store.GetPart(p.Id) ?? throw new VocabAnalysisException($"There is no part {p.Id}; leave id out to make a new one.");
        if (existing is not null && existing.Language != word.Language)
            throw new VocabAnalysisException($"Part {p.Id} ({existing.Form}) is {existing.Language}, the word is {word.Language}.");
        var form = Form(p);
        if (form.Length == 0) throw new VocabAnalysisException($"A part of {parent} has no form.");
        if (form.Length > MaxForm) throw new VocabAnalysisException($"\"{form}\" is too long for a part (at most {MaxForm} characters).");
        if (VocabStore.FormKey(form) == VocabStore.FormKey(parent))
            throw new VocabAnalysisException($"{parent} cannot be a part of itself; stop the tree there.");
        var kind = Kind(p) ?? throw new VocabAnalysisException($"Give the kind of {form}.");
        if (Array.IndexOf(_chinese ? ChineseKinds : EnglishKinds, kind) < 0)
            throw new VocabAnalysisException($"{form}: a {(_chinese ? "Chinese" : "English")} part is {string.Join(", ", (_chinese ? ChineseKinds : EnglishKinds).Select(Name))}.");
        if (_chinese && VocabText.Length(form) != 1)
            throw new VocabAnalysisException($"\"{form}\" is not one character. Chinese parts are single characters or components, never strokes.");
        if (!_chinese && p.Role != VocabPartRole.Plain)
            throw new VocabAnalysisException($"{form}: role (meaning or sound) is for the components of Chinese characters.");
        Require(p.Meaning ?? existing?.Meaning, "meaning (in Vietnamese)", form);
        if (_chinese && kind == VocabPartKind.Character)
        {
            Require(p.Phonetic ?? existing?.Phonetic, "phonetic (pinyin)", form);
            Require(p.HanViet ?? existing?.HanViet, "han_viet", form);
        }
        Limit(p.Meaning, "meaning", form);
        Limit(p.Origin, "origin", form);

        if (p.Id is { } id && !path.Add(id)) throw new VocabAnalysisException($"{form} cannot contain itself.");
        if (p.Parts is { } children)
        {
            foreach (var child in children) CheckPart(child, form, depth + 1, path);
        }
        else if (existing is not null && p.Id is { } kept)
        {
            // Its saved smaller parts stay: they must not lead back up the tree.
            if (Reaches(kept, path, new HashSet<string>(StringComparer.Ordinal), skipSelf: true))
                throw new VocabAnalysisException($"The saved parts of {form} lead back to a part above it; give its parts again.");
        }
        if (p.Id is { } added) path.Remove(added);
    }

    private bool Reaches(string id, HashSet<string> targets, HashSet<string> seen, bool skipSelf = false)
    {
        if (!skipSelf && targets.Contains(id)) return true;
        if (!seen.Add(id) || store.GetPart(id) is not { } part) return false;
        return part.Parts.Any(l => Reaches(l.PartId, targets, seen));
    }

    // ---- writing -------------------------------------------------------------------------------------------------

    private string Save(AnalysisPart p)
    {
        var children = p.Parts?.Select(c => new VocabPartLink { PartId = Save(c), Role = c.Role }).ToList();
        if (p.Id is { } id)
        {
            var current = store.GetPart(id)!;
            var updated = current with
            {
                Form = p.Form?.Trim() ?? current.Form,
                Kind = p.Kind ?? current.Kind,
                Phonetic = p.Phonetic?.Trim() ?? current.Phonetic,
                Meaning = p.Meaning?.Trim() ?? current.Meaning,
                HanViet = p.HanViet?.Trim() ?? current.HanViet,
                Traditional = p.Traditional?.Trim() ?? current.Traditional,
                Origin = p.Origin?.Trim() ?? current.Origin,
                Parts = children ?? current.Parts,
            };
            if (!SameContent(updated, current)) store.ReplacePart(id, updated);
            return id;
        }
        var form = Form(p);
        var meaning = p.Meaning!.Trim();
        var key = (VocabStore.FormKey(form), meaning.ToLowerInvariant());
        if (_made.TryGetValue(key, out var made)) return made;
        var part = new VocabPart
        {
            Language = word.Language,
            Form = form,
            Kind = Kind(p)!.Value,
            Phonetic = p.Phonetic?.Trim() ?? "",
            Meaning = meaning,
            HanViet = p.HanViet?.Trim() ?? "",
            Traditional = p.Traditional?.Trim() is { } t && t != form ? t : "",
            Origin = p.Origin?.Trim() ?? "",
            Parts = children ?? [],
        };
        // The same piece with the same meaning is already known: use it, so its family stays one.
        if (store.PartsWritten(form, word.Language).FirstOrDefault(e => string.Equals(e.Value.Meaning.Trim(), meaning, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            var merged = same.Value with
            {
                Phonetic = part.Phonetic.Length > 0 ? part.Phonetic : same.Value.Phonetic,
                HanViet = part.HanViet.Length > 0 ? part.HanViet : same.Value.HanViet,
                Traditional = part.Traditional.Length > 0 ? part.Traditional : same.Value.Traditional,
                Origin = part.Origin.Length > 0 ? part.Origin : same.Value.Origin,
                Parts = children ?? same.Value.Parts,
            };
            if (!SameContent(merged, same.Value)) store.ReplacePart(same.Id, merged);
            return _made[key] = same.Id;
        }
        return _made[key] = store.AddPart(part);
    }

    private static readonly IReadOnlyList<VocabPartLink> NoLinks = [];

    // Records compare lists by reference: the links are compared one by one instead.
    private static bool SameContent(VocabPart a, VocabPart b) =>
        a with { Parts = NoLinks, UpdatedAt = default } == b with { Parts = NoLinks, UpdatedAt = default } && a.Parts.SequenceEqual(b.Parts);

    // ---- helpers -------------------------------------------------------------------------------------------------

    private string Form(AnalysisPart p) => (p.Form ?? (p.Id is { } id ? store.GetPart(id)?.Form : null) ?? "").Trim();

    private VocabPartKind? Kind(AnalysisPart p) => p.Kind ?? (p.Id is { } id ? store.GetPart(id)?.Kind : null);

    internal static string Name(VocabPartKind kind) => kind.ToString().ToLowerInvariant();

    private static void Require(string? value, string what, string of)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new VocabAnalysisException($"{of}: {what} is missing.");
    }

    private static void Limit(string? value, string what, string of)
    {
        if (value is { Length: > MaxText }) throw new VocabAnalysisException($"{of}: {what} is too long (at most {MaxText} characters); keep it short.");
    }
}
