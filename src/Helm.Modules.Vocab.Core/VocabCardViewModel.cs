using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>
/// The guessing card: a word alone; a tap shows its meaning with "Remembered" and "Forgot", and the answer picks the
/// next word. Used on the Vocabulary page, by the PC pop-up and by the Android widget's page. UI thread only.
/// </summary>
/// <param name="explain">Builds the word's tree, shown with the meaning (the Learn tab); null shows none (the pop-up).</param>
/// <param name="voice">Says the word (the speaker button); null hides it.</param>
public sealed partial class VocabCardViewModel(VocabStore store, VocabQuiz quiz, Func<string, VocabWord, VocabWordDetailViewModel>? explain = null,
    VocabVoice? voice = null) : ObservableObject
{
    private string? _wordId;
    private VocabWord? _word;

    [ObservableProperty] private bool _hasWord;
    [ObservableProperty] private bool _isRevealed;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _phonetic = "";
    [ObservableProperty] private string _hanViet = "";
    [ObservableProperty] private string _traditional = "";
    [ObservableProperty] private string _meaning = "";
    [ObservableProperty] private string _explanation = "";
    [ObservableProperty] private string _partsText = "";
    [ObservableProperty] private string _levelText = "";

    /// <summary>Each part with its meaning and pieces ("补 · vá, bù ← 衤 áo + 卜 (âm)"), shown with the meaning.</summary>
    [ObservableProperty] private IReadOnlyList<string> _partLines = [];

    /// <summary>Show the YouGlish button (a setting).</summary>
    [ObservableProperty] private bool _showYouGlish = true;

    public bool CanSay => voice?.CanSay == true;

    /// <summary>How the word is built (its tree and the tapped part), once the meaning is shown.</summary>
    [ObservableProperty] private VocabWordDetailViewModel? _explanationTree;

    public bool HasExplanationTree => ExplanationTree is not null;

    public bool HasParts => PartLines.Count > 0;

    partial void OnPartLinesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasParts));

    partial void OnExplanationTreeChanged(VocabWordDetailViewModel? value) => OnPropertyChanged(nameof(HasExplanationTree));

    /// <summary>Raised after an answer is recorded (the PC pop-up closes on it).</summary>
    public event EventHandler? Answered;

    /// <summary>The word on the card; null when there is none.</summary>
    public string? WordId => _wordId;

    /// <summary>Shows a word picked by the quiz (another than the one shown); false when no word is analyzed yet.</summary>
    public bool ShowNext()
    {
        var next = quiz.Next(except: _wordId);
        Show(next?.Id, next?.Value);
        return next is not null;
    }

    /// <summary>Shows this word, meaning hidden.</summary>
    public void Show(string? id, VocabWord? word)
    {
        _wordId = word is null ? null : id;
        _word = word;
        ExplanationTree = null;
        HasWord = word is not null;
        IsRevealed = false;
        Text = word?.Text ?? "";
        Phonetic = word?.Phonetic ?? "";
        HanViet = word?.HanViet ?? "";
        Traditional = word?.Traditional ?? "";
        Meaning = word?.Meaning ?? "";
        Explanation = word?.Explanation ?? "";
        // "补 (衤 + 卜) + 救 (求 + 攵)": two levels, as a reminder of how the word is built.
        PartsText = word is null ? "" : VocabTree.Outline(store, word);
        PartLines = word is null ? [] : VocabTree.Lines(store, word);
        LevelText = id is null ? "" : Level(quiz.StatsOf(id));
    }

    [RelayCommand]
    private void Reveal()
    {
        IsRevealed = HasWord;
        if (IsRevealed && explain is not null && _wordId is { } id && _word is { } word && word.Parts.Count > 0) ExplanationTree = explain(id, word);
    }

    [RelayCommand]
    private Task SayAsync() => _word is { } word && voice is not null ? voice.SayAsync(word.Text, word.Language) : Task.CompletedTask;

    [RelayCommand]
    private void YouGlish()
    {
        if (_word is { } word) voice?.OpenYouGlish(word.Text, word.Language);
    }

    [RelayCommand]
    private void Remembered() => Answer(true);

    [RelayCommand]
    private void Forgot() => Answer(false);

    /// <summary>Another word without answering this one.</summary>
    [RelayCommand]
    private void Skip() => ShowNext();

    private void Answer(bool remembered)
    {
        if (_wordId is null || !IsRevealed) return;
        store.Record(_wordId, remembered);
        Answered?.Invoke(this, EventArgs.Empty);
        ShowNext();
    }

    internal static string Level(VocabWordStats stats) =>
        stats.IsNew ? "New" : $"Level {stats.Level}/{VocabQuiz.TopLevel} · remembered {stats.Remembered}, forgot {stats.Forgot}";
}
