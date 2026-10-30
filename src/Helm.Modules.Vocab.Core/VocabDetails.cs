namespace Helm.Modules.Vocab;

/// <summary>
/// The details an agent gives beside a word's tree (examples, collocations, mnemonic, confusables, labels, word forms,
/// synonyms, antonyms): checked the same way in a whole analysis and when only the details are filled in later.
/// </summary>
public static class VocabDetails
{
    public const int MaxExamples = 3;
    public const int MaxCollocations = 6;
    public const int MaxConfusables = 3;
    public const int MaxForms = 8;
    public const int MaxSynonyms = 6;
    public const int MaxAntonyms = 4;
    private const int MaxText = 600;

    /// <summary>Throws what to fix in the optional fields of <paramref name="a"/>.</summary>
    /// <exception cref="VocabAnalysisException">A field is too long, too many, or not a level.</exception>
    public static void Check(VocabWord word, WordAnalysis a)
    {
        var chinese = word.Language == VocabLanguage.Chinese;
        Limit(a.Explanation, "explanation", word.Text);
        Limit(a.Mnemonic, "mnemonic", word.Text);
        if (a.Examples is { Count: > MaxExamples }) throw new VocabAnalysisException($"{word.Text}: at most {MaxExamples} examples.");
        if (a.Examples?.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.Sentence)) is not null)
            throw new VocabAnalysisException($"{word.Text}: an example has no sentence.");
        if (a.Collocations is { Count: > MaxCollocations }) throw new VocabAnalysisException($"{word.Text}: at most {MaxCollocations} collocations.");
        if (a.Confusables is { Count: > MaxConfusables }) throw new VocabAnalysisException($"{word.Text}: at most {MaxConfusables} confusable words.");
        if (a.Forms is { Count: > MaxForms }) throw new VocabAnalysisException($"{word.Text}: at most {MaxForms} word forms; keep the common ones.");
        if (a.Forms?.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Word)) is not null)
            throw new VocabAnalysisException($"{word.Text}: a word form has no word.");
        if (a.Synonyms is { Count: > MaxSynonyms }) throw new VocabAnalysisException($"{word.Text}: at most {MaxSynonyms} synonyms.");
        if (a.Antonyms is { Count: > MaxAntonyms }) throw new VocabAnalysisException($"{word.Text}: at most {MaxAntonyms} antonyms.");
        if (a.Level is { Length: > 0 } level && VocabLevels.Normalize(level, word.Language) is null)
            throw new VocabAnalysisException($"{word.Text}: level is {(chinese ? "HSK1 … HSK9" : "A1, A2, B1, B2, C1 or C2")}.");
    }

    /// <summary>The word with the details <paramref name="a"/> gives; the ones it leaves out stay as they are.</summary>
    public static VocabWord Apply(VocabWord word, WordAnalysis a) => word with
    {
        Explanation = a.Explanation?.Trim() ?? word.Explanation,
        Examples = a.Examples?.Select(e => new VocabExample { Sentence = e.Sentence.Trim(), Translation = e.Translation.Trim() }).ToList() ?? word.Examples,
        Collocations = Words(a.Collocations) ?? word.Collocations,
        Mnemonic = a.Mnemonic?.Trim() ?? word.Mnemonic,
        Confusables = a.Confusables?.Where(c => c.Word.Trim().Length > 0)
            .Select(c => new VocabConfusable { Word = c.Word.Trim(), Note = c.Note.Trim() }).ToList() ?? word.Confusables,
        Level = a.Level is { } level ? VocabLevels.Normalize(level, word.Language) ?? "" : word.Level,
        PartOfSpeech = a.PartOfSpeech?.Trim() ?? word.PartOfSpeech,
        Topic = a.Topic?.Trim() ?? word.Topic,
        Forms = a.Forms?.Where(f => f.Word.Trim().Length > 0)
            .Select(f => new VocabForm { Word = f.Word.Trim(), PartOfSpeech = f.PartOfSpeech.Trim(), Meaning = f.Meaning.Trim() }).ToList() ?? word.Forms,
        Synonyms = Words(a.Synonyms) ?? word.Synonyms,
        Antonyms = Words(a.Antonyms) ?? word.Antonyms,
        Details = VocabWord.CurrentDetails,
    };

    /// <summary>Trimmed, without empties or repeats; null stays null (not given).</summary>
    private static IReadOnlyList<string>? Words(IReadOnlyList<string>? words) =>
        words?.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void Limit(string? value, string what, string of)
    {
        if (value is { Length: > MaxText }) throw new VocabAnalysisException($"{of}: {what} is too long (at most {MaxText} characters); keep it short.");
    }
}
