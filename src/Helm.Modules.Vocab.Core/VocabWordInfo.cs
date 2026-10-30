using System.Text.RegularExpressions;

namespace Helm.Modules.Vocab;

/// <summary>
/// What helps to use a word, as the card and the word's page show it: example sentences, collocations, the mnemonic,
/// the words it is easy to mix up with, and its labels.
/// </summary>
public sealed class VocabWordInfo(VocabWord word)
{
    public IReadOnlyList<VocabExample> Examples => word.Examples;

    public bool HasExamples => word.Examples.Count > 0;

    public string Collocations => string.Join(" · ", word.Collocations);

    public bool HasCollocations => word.Collocations.Count > 0;

    public string Mnemonic => word.Mnemonic;

    public bool HasMnemonic => word.Mnemonic.Length > 0;

    /// <summary>"affect: động từ, tác động lên…".</summary>
    public IReadOnlyList<string> Confusables => word.Confusables.Select(c => c.Note.Length > 0 ? $"{c.Word}: {c.Note}" : c.Word).ToList();

    public bool HasConfusables => word.Confusables.Count > 0;

    /// <summary>"B1 · danh từ · gia đình · #Aptis".</summary>
    public string Labels => string.Join(" · ", new[] { word.Level, word.PartOfSpeech, word.Topic }.Where(s => s.Length > 0)
        .Concat(word.Tags.Select(t => "#" + t)));

    public bool HasLabels => Labels.Length > 0;

    /// <summary>Something more than the meaning to show.</summary>
    public bool HasAny => HasExamples || HasCollocations || HasMnemonic || HasConfusables;
}

/// <summary>Fill-the-gap: an example sentence with the word hidden.</summary>
public static partial class VocabCloze
{
    public const string Gap = "_____";

    /// <summary>
    /// The first example that contains the word, with it replaced by <see cref="Gap"/> (English: any form starting with
    /// the word, "related" for "relate"); null when no example has it.
    /// </summary>
    public static (string Sentence, string Translation)? Make(VocabWord word)
    {
        foreach (var example in word.Examples)
        {
            if (Hide(example.Sentence, word) is { } hidden) return (hidden, example.Translation);
        }
        return null;
    }

    internal static string? Hide(string sentence, VocabWord word)
    {
        var text = word.Text.Trim();
        if (text.Length == 0) return null;
        if (word.Language == VocabLanguage.Chinese)
            return sentence.Contains(text, StringComparison.Ordinal) ? sentence.Replace(text, Gap, StringComparison.Ordinal) : null;
        // A word ending in a silent e also shows without it: relate → relating.
        var stem = text.Length > 4 && text.EndsWith('e') ? text[..^1] : text;
        var pattern = new Regex($@"\b{Regex.Escape(stem)}[\p{{L}}'’]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return pattern.IsMatch(sentence) ? pattern.Replace(sentence, Gap) : null;
    }
}
