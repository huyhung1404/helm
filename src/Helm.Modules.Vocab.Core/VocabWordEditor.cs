using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>
/// Editing a word by hand: its own fields as text boxes (examples and confusables one per line, "sentence | translation";
/// word forms one per line, "word | part of speech | meaning"; collocations, synonyms, antonyms and tags separated by
/// commas). Saved as a whole; the tree is the agent's. UI thread only.
/// </summary>
public sealed partial class VocabWordEditor(VocabStore store, string id) : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _phonetic = "";
    [ObservableProperty] private string _meaning = "";
    [ObservableProperty] private string _hanViet = "";
    [ObservableProperty] private string _traditional = "";
    [ObservableProperty] private string _explanation = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _mnemonic = "";
    [ObservableProperty] private string _examples = "";
    [ObservableProperty] private string _collocations = "";
    [ObservableProperty] private string _confusables = "";
    [ObservableProperty] private string _level = "";
    [ObservableProperty] private string _partOfSpeech = "";
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private string _forms = "";
    [ObservableProperty] private string _synonyms = "";
    [ObservableProperty] private string _antonyms = "";
    [ObservableProperty] private string _error = "";

    public bool HasError => Error.Length > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void Begin()
    {
        if (store.GetWord(id) is not { } w) return;
        Phonetic = w.Phonetic;
        Meaning = w.Meaning;
        HanViet = w.HanViet;
        Traditional = w.Traditional;
        Explanation = w.Explanation;
        Note = w.Note;
        Mnemonic = w.Mnemonic;
        Examples = string.Join(Environment.NewLine, w.Examples.Select(e => e.Translation.Length > 0 ? $"{e.Sentence} | {e.Translation}" : e.Sentence));
        Collocations = string.Join(", ", w.Collocations);
        Confusables = string.Join(Environment.NewLine, w.Confusables.Select(c => c.Note.Length > 0 ? $"{c.Word} | {c.Note}" : c.Word));
        Level = w.Level;
        PartOfSpeech = w.PartOfSpeech;
        Topic = w.Topic;
        Tags = string.Join(", ", w.Tags);
        Forms = string.Join(Environment.NewLine, w.Forms.Select(f => string.Join(" | ", new[] { f.Word, f.PartOfSpeech, f.Meaning }.Where(s => s.Length > 0))));
        Synonyms = string.Join(", ", w.Synonyms);
        Antonyms = string.Join(", ", w.Antonyms);
        Error = "";
        IsOpen = true;
    }

    /// <summary>Keeps the user's wording; the page shows it once the store reports the change.</summary>
    [RelayCommand]
    private void Save()
    {
        if (store.GetWord(id) is not { } w) return;
        var level = Level.Trim();
        var normalized = level.Length == 0 ? "" : VocabLevels.Normalize(level, w.Language);
        if (normalized is null)
        {
            Error = w.Language == VocabLanguage.Chinese ? "Level is HSK1 … HSK9 (or empty)." : "Level is A1, A2, B1, B2, C1 or C2 (or empty).";
            return;
        }
        store.UpdateWord(id, word => word with
        {
            Phonetic = Phonetic.Trim(),
            Meaning = Meaning.Trim(),
            HanViet = HanViet.Trim(),
            Traditional = Traditional.Trim(),
            Explanation = Explanation.Trim(),
            Note = Note.Trim(),
            Mnemonic = Mnemonic.Trim(),
            Examples = Pairs(Examples).Select(p => new VocabExample { Sentence = p.Left, Translation = p.Right }).ToList(),
            Collocations = List(Collocations),
            Confusables = Pairs(Confusables).Select(p => new VocabConfusable { Word = p.Left, Note = p.Right }).ToList(),
            Level = normalized,
            PartOfSpeech = PartOfSpeech.Trim(),
            Topic = Topic.Trim(),
            Tags = VocabTags.Parse(Tags),
            Forms = Triples(Forms).Select(t => new VocabForm { Word = t.Word, PartOfSpeech = t.PartOfSpeech, Meaning = t.Meaning }).ToList(),
            Synonyms = List(Synonyms),
            Antonyms = List(Antonyms),
        });
        Error = "";
        IsOpen = false;
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;

    private static List<string> List(string text) =>
        text.Split([',', '，', ';', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>"word | part of speech | meaning" lines; "word | meaning" leaves the part of speech empty.</summary>
    internal static IEnumerable<(string Word, string PartOfSpeech, string Meaning)> Triples(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', 3).Select(p => p.Trim()).ToArray())
            .Where(p => p[0].Length > 0)
            .Select(p => p.Length switch
            {
                1 => (p[0], "", ""),
                2 => (p[0], "", p[1]),
                _ => (p[0], p[1], p[2]),
            });

    /// <summary>"left | right" lines (right optional); empty lines skipped.</summary>
    internal static IEnumerable<(string Left, string Right)> Pairs(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('|', 2))
            .Select(p => (Left: p[0].Trim(), Right: p.Length > 1 ? p[1].Trim() : ""))
            .Where(p => p.Left.Length > 0);
}
