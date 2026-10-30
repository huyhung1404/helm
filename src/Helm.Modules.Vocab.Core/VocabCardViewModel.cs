using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Helm.Modules.Vocab;

/// <summary>What the front of a card asks for.</summary>
public enum VocabCardMode
{
    /// <summary>The word: say its meaning.</summary>
    WordToMeaning,
    /// <summary>The meaning: say the word (harder, and what writing and speaking need).</summary>
    MeaningToWord,
    /// <summary>Only its sound: say the word and its meaning.</summary>
    Listen,
    /// <summary>An example sentence with the word hidden, and its translation: say the missing word.</summary>
    FillGap,
    /// <summary>Each card one of the others, at random (Listen and FillGap only where they can be).</summary>
    Mixed,
}

/// <summary>How the Learn tab's cards are asked.</summary>
/// <param name="Mode">The kind of card.</param>
/// <param name="AutoSay">Say the word when its meaning is shown.</param>
public sealed record VocabCardOptions(VocabCardMode Mode, bool AutoSay)
{
    public static VocabCardOptions Default { get; } = new(VocabCardMode.WordToMeaning, false);
}

/// <summary>
/// The guessing card: the front asks (the word, its meaning, its sound or a sentence with a gap); a tap shows everything
/// with "Remembered" and "Forgot", and the answer picks the next word. Used on the Learn tab and by the PC pop-up. UI
/// thread only.
/// </summary>
/// <param name="explain">Builds the word's tree, shown with the meaning (the Learn tab); null shows none (the pop-up).</param>
/// <param name="voice">Says the word (the speaker button); null hides it.</param>
/// <param name="options">The Learn tab's settings, read for each card; null asks word → meaning.</param>
public sealed partial class VocabCardViewModel(VocabStore store, VocabQuiz quiz, Func<string, VocabWord, VocabWordDetailViewModel>? explain = null,
    VocabVoice? voice = null, Func<VocabCardOptions>? options = null, Random? random = null) : ObservableObject
{
    private readonly Random _random = random ?? Random.Shared;
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
    [ObservableProperty] private VocabWordInfo? _info;

    // The front, as this card asks it.
    [ObservableProperty] private VocabCardMode _mode;
    [ObservableProperty] private string _frontText = "";
    [ObservableProperty] private string _frontHint = "";
    [ObservableProperty] private string _frontNote = "";

    /// <summary>Each part with its meaning and pieces ("补 · vá, bù ← 衤 áo + 卜 (âm)"), shown with the meaning.</summary>
    [ObservableProperty] private IReadOnlyList<string> _partLines = [];

    /// <summary>Show the YouGlish button (a setting).</summary>
    [ObservableProperty] private bool _showYouGlish = true;

    public bool CanSay => voice?.CanSay == true;

    /// <summary>How the word is built (its tree and the tapped part), once the meaning is shown.</summary>
    [ObservableProperty] private VocabWordDetailViewModel? _explanationTree;

    public bool HasExplanationTree => ExplanationTree is not null;

    public bool HasParts => PartLines.Count > 0;

    public bool IsChinese => _word?.Language == VocabLanguage.Chinese;

    /// <summary>The front is the word itself (shown large); the other modes show it once revealed.</summary>
    public bool FrontIsWord => Mode == VocabCardMode.WordToMeaning;

    public bool FrontIsListen => Mode == VocabCardMode.Listen;

    /// <summary>The front is a meaning or a sentence: written smaller than a word.</summary>
    public bool FrontIsText => Mode is VocabCardMode.MeaningToWord or VocabCardMode.FillGap;

    public bool HasFrontNote => FrontNote.Length > 0;

    /// <summary>After revealing a card that did not show the word, the word comes up large.</summary>
    public bool ShowsWordOnReveal => IsRevealed && !FrontIsWord;

    partial void OnPartLinesChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasParts));

    partial void OnExplanationTreeChanged(VocabWordDetailViewModel? value) => OnPropertyChanged(nameof(HasExplanationTree));

    partial void OnModeChanged(VocabCardMode value)
    {
        OnPropertyChanged(nameof(FrontIsWord));
        OnPropertyChanged(nameof(FrontIsListen));
        OnPropertyChanged(nameof(FrontIsText));
        OnPropertyChanged(nameof(ShowsWordOnReveal));
    }

    partial void OnFrontNoteChanged(string value) => OnPropertyChanged(nameof(HasFrontNote));

    partial void OnIsRevealedChanged(bool value) => OnPropertyChanged(nameof(ShowsWordOnReveal));

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

    /// <summary>Shows this word, the answer hidden.</summary>
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
        Info = word is null ? null : new VocabWordInfo(word);
        // "补 (衤 + 卜) + 救 (求 + 攵)": two levels, as a reminder of how the word is built.
        PartsText = word is null ? "" : VocabTree.Outline(store, word);
        PartLines = word is null ? [] : VocabTree.Lines(store, word);
        LevelText = id is null ? "" : VocabFormat.Level(quiz.StatsOf(id), store.Now);
        OnPropertyChanged(nameof(IsChinese));
        AskFront(word);
    }

    /// <summary>Asks the same word again the way the settings now say (the mode changed), unless its answer is shown.</summary>
    public void AskAgain()
    {
        if (!IsRevealed) AskFront(_word);
    }

    /// <summary>Picks how this card asks (in Mixed mode, at random among the ways this word can be asked).</summary>
    private void AskFront(VocabWord? word)
    {
        var wanted = (options?.Invoke() ?? VocabCardOptions.Default).Mode;
        var cloze = word is null ? null : VocabCloze.Make(word);
        var mode = wanted;
        if (wanted == VocabCardMode.Mixed)
        {
            var ways = new List<VocabCardMode> { VocabCardMode.WordToMeaning, VocabCardMode.MeaningToWord };
            if (CanSay) ways.Add(VocabCardMode.Listen);
            if (cloze is not null) ways.Add(VocabCardMode.FillGap);
            mode = ways[_random.Next(ways.Count)];
        }
        // A word without a sentence to hide it in, or no voice to say it: asked the usual way.
        if (mode == VocabCardMode.FillGap && cloze is null || mode == VocabCardMode.Listen && !CanSay) mode = VocabCardMode.WordToMeaning;
        Mode = mode;
        (FrontText, FrontHint, FrontNote) = mode switch
        {
            VocabCardMode.MeaningToWord => (Meaning, "Which word means this? Say it, then check.", ""),
            VocabCardMode.Listen => ("", "Play it, then say the word and its meaning.", ""),
            VocabCardMode.FillGap => (cloze!.Value.Sentence, "Which word fills the gap? Say it, then check.", cloze.Value.Translation),
            _ => (Text, "Say the meaning in your head, then check.", ""),
        };
    }

    [RelayCommand]
    private void Reveal()
    {
        IsRevealed = HasWord;
        if (!IsRevealed) return;
        if (explain is not null && _wordId is { } id && _word is { } word && word.Parts.Count > 0) ExplanationTree = explain(id, word);
        if (options?.Invoke().AutoSay == true && Mode != VocabCardMode.Listen) _ = SayAsync();
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
}
