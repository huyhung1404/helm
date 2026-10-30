using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Capture;

namespace Helm.Modules.NovelReader;

/// <summary>
/// After a long press on a word (which says it): a small bar that offers to add the word to Vocabulary, with the
/// sentence it was in as the note. Vocabulary is reached through its Quick Capture target, so the two tools stay apart;
/// without it (the tool not installed) the bar never shows. UI thread only.
/// </summary>
public sealed partial class HeldWordViewModel(IEnumerable<ICaptureTarget>? targets) : ObservableObject
{
    /// <summary>Vocabulary's target id (Helm.Modules.Vocab.VocabCaptureTarget.TargetId).</summary>
    public const string VocabularyTarget = "vocab";

    private readonly ICaptureTarget? _vocabulary = targets?.FirstOrDefault(t => t.Id == VocabularyTarget);

    [ObservableProperty] private bool _isShown;
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private string _sentence = "";
    [ObservableProperty] private string _result = "";

    public bool HasResult => Result.Length > 0;

    partial void OnResultChanged(string value) => OnPropertyChanged(nameof(HasResult));

    /// <summary>A word was held: offer it (when Vocabulary is there).</summary>
    public void Show(string word, string sentence)
    {
        if (_vocabulary is null || word.Trim().Length == 0) return;
        Word = word.Trim();
        Sentence = sentence;
        Result = "";
        IsShown = true;
    }

    [RelayCommand]
    private void Add()
    {
        if (_vocabulary is null || Word.Length == 0) return;
        // "word | note": the target's own way to take a note.
        Result = _vocabulary.Capture(Sentence.Length > 0 ? $"{Word} | {Sentence}" : Word).Message;
    }

    [RelayCommand]
    private void Dismiss() => IsShown = false;
}
