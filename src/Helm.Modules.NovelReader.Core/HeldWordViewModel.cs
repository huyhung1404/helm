using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Ai;
using Helm.Core.Capture;

namespace Helm.Modules.NovelReader;

/// <summary>
/// After a long press on a word (which says it): a small bar that offers to add the word to Vocabulary, with the
/// sentence it was in as the note, and (with AI) to explain that sentence: its meaning, grammar, a natural translation,
/// and the words worth learning from it, which go to Vocabulary in one tap. Vocabulary is reached through its Quick
/// Capture target, so the two tools stay apart; without it (the tool not installed) the bar never shows. UI thread only.
/// </summary>
public sealed partial class HeldWordViewModel(IEnumerable<ICaptureTarget>? targets, IAiText? ai = null) : ObservableObject
{
    /// <summary>Vocabulary's target id (Helm.Modules.Vocab.VocabCaptureTarget.TargetId).</summary>
    public const string VocabularyTarget = "vocab";

    private readonly ICaptureTarget? _vocabulary = targets?.FirstOrDefault(t => t.Id == VocabularyTarget);
    // A word held meanwhile makes an explanation still on its way stale.
    private int _held;

    [ObservableProperty] private bool _isShown;
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private string _sentence = "";
    [ObservableProperty] private string _result = "";

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExplainCommand))] private bool _isExplaining;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExplanation))] private SentenceExplanation? _explanation;

    /// <summary>Where the explanation came from ("Claude Code trên PC-HUNG"), or the problem when there is none.</summary>
    [ObservableProperty] private string _explainStatus = "";

    public bool HasResult => Result.Length > 0;

    public bool HasExplanation => Explanation is not null;

    /// <summary>The sentence can be explained: AI is set up and a sentence came with the word.</summary>
    public bool CanExplainSentence => ai is not null && Sentence.Length > 0;

    /// <summary>The explanation's words to learn; the user unticks the ones they do not want.</summary>
    public ObservableCollection<SentenceWordChoice> Words { get; } = [];

    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(HasResult));

    partial void OnSentenceChanged(string value)
    {
        OnPropertyChanged(nameof(CanExplainSentence));
        ExplainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>A word was held: offer it (when Vocabulary is there).</summary>
    public void Show(string word, string sentence)
    {
        if (_vocabulary is null || word.Trim().Length == 0) return;
        _held++;
        Word = word.Trim();
        Sentence = sentence;
        Result = "";
        Explanation = null;
        ExplainStatus = "";
        Words.Clear();
        IsExplaining = false;
        IsShown = true;
    }

    [RelayCommand]
    private void Add()
    {
        if (_vocabulary is null || Word.Length == 0) return;
        // "word | note": the target's own way to take a note.
        Result = _vocabulary.Capture(Sentence.Length > 0 ? $"{Word} | {Sentence}" : Word).Message;
    }

    private bool CanExplain() => CanExplainSentence && !IsExplaining;

    /// <summary>Explains the held word's sentence (an agent on a Helm PC first, else the API).</summary>
    [RelayCommand(CanExecute = nameof(CanExplain))]
    private async Task ExplainAsync()
    {
        if (ai is null || Sentence.Length == 0) return;
        var held = _held;
        IsExplaining = true;
        ExplainStatus = "Explaining…";
        var request = SentenceExplainer.Request(Sentence);
        AiTextAnswer answer;
        try
        {
            answer = await Task.Run(() => ai.AskAsync(request, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = AiTextAnswer.Failed(ex.Message);
        }
        if (held != _held) return;
        IsExplaining = false;
        var explanation = answer.Ok ? SentenceExplainer.Parse(answer.Text) : null;
        if (explanation is null)
        {
            ExplainStatus = answer.Problem ?? "The answer could not be read. Try again.";
            return;
        }
        Explanation = explanation;
        Words.Clear();
        foreach (var w in explanation.Words)
        {
            var choice = new SentenceWordChoice(w);
            choice.PropertyChanged += (_, _) => AddWordsCommand.NotifyCanExecuteChanged();
            Words.Add(choice);
        }
        ExplainStatus = answer.RanOn is { } pc ? $"{pc} · free" : "API";
        AddWordsCommand.NotifyCanExecuteChanged();
    }

    private bool CanAddWords() => _vocabulary is not null && Words.Any(w => w.Keep);

    /// <summary>The ticked words go to Vocabulary at once, the sentence as their note.</summary>
    [RelayCommand(CanExecute = nameof(CanAddWords))]
    private void AddWords()
    {
        var kept = Words.Where(w => w.Keep).Select(w => w.Word.Word).ToList();
        if (_vocabulary is null || kept.Count == 0) return;
        Result = _vocabulary.Capture(Sentence.Length > 0 ? $"{string.Join(", ", kept)} | {Sentence}" : string.Join(", ", kept)).Message;
    }

    [RelayCommand]
    private void Dismiss() => IsShown = false;
}

/// <summary>A word of an explained sentence, ticked to go to Vocabulary.</summary>
public sealed partial class SentenceWordChoice(SentenceWord word) : ObservableObject
{
    public SentenceWord Word { get; } = word;

    [ObservableProperty] private bool _keep = true;
}
