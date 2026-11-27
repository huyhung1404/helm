using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Capture;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The sheet a long press on the text opens ("Câu này"): the held sentence with its pinyin, a speaker that says it, and
/// the held word as a chip: ticked, it goes to Vocabulary in one tap, with the sentence as its note, and an Undo right
/// after. Vocabulary is reached through its Quick Capture target, so the two tools stay apart; without it there is no
/// chip. UI thread only.
/// </summary>
public sealed partial class HeldSentenceViewModel(IEnumerable<ICaptureTarget>? targets, Func<string, Task<string?>>? say = null)
    : ObservableObject
{
    /// <summary>Vocabulary's target id (Helm.Modules.Vocab.VocabCaptureTarget.TargetId).</summary>
    public const string VocabularyTarget = "vocab";

    /// <summary>How long "added to Vocabulary" stays with its Undo.</summary>
    internal static TimeSpan ToastFor { get; set; } = TimeSpan.FromSeconds(8);

    private readonly ICaptureTarget? _vocabulary = targets?.FirstOrDefault(t => t.Id == VocabularyTarget);
    // A newer toast outlives the older one's timer.
    private int _toastVersion;
    private Action? _undo;

    [ObservableProperty] private bool _isShown;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Sentence))] private HeldSentence? _held;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPinyin))] private string _pinyin = "";

    /// <summary>Why the sentence could not be said or its words added.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] private string _problem = "";

    /// <summary>The toast after words went to Vocabulary ("Đã thêm 2 từ vào Vocabulary"), with Undo while it can.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToast))] private string _toast = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UndoCommand))] private bool _canUndo;

    /// <summary>The held sentence's Chinese.</summary>
    public string Sentence => Held?.Chinese ?? "";

    public bool HasPinyin => Pinyin.Length > 0;

    public bool HasProblem => Problem.Length > 0;

    public bool HasToast => Toast.Length > 0;

    /// <summary>Words can go to Vocabulary (the tool is there and the sentence has some).</summary>
    public bool HasWords => _vocabulary is not null && Words.Count > 0;

    /// <summary>The sentence's words: the held one. None is ticked at first.</summary>
    public ObservableCollection<SentenceWordChoice> Words { get; } = [];

    public int KeptCount => Words.Count(w => w.Keep);

    /// <summary>"Thêm 2 từ".</summary>
    public string AddWordsText => $"Thêm {KeptCount.ToString(CultureInfo.InvariantCulture)} từ";

    /// <summary>A sentence was held: show it.</summary>
    public void Show(HeldSentence held)
    {
        Held = held;
        Pinyin = held.Pinyin;
        Problem = "";
        Words.Clear();
        if (held.Word.Word.Length > 0) AddChoice(held.Word);
        WordsChanged();
        IsShown = true;
    }

    /// <summary>The speaker: says the sentence in standard Mandarin (the voice a held word was said with before).</summary>
    [RelayCommand]
    private async Task SayAsync()
    {
        if (say is null || Sentence.Length == 0) return;
        if (await say(Sentence) is { } problem) Problem = problem;
    }

    private bool CanAddWords() => _vocabulary is not null && KeptCount > 0;

    /// <summary>"Thêm n từ": the ticked words go to Vocabulary at once, the sentence as their note; the sheet closes, Undo shows.</summary>
    [RelayCommand(CanExecute = nameof(CanAddWords))]
    private void AddWords()
    {
        var kept = Words.Where(w => w.Keep).Select(w => w.Word.Word).ToList();
        if (_vocabulary is null || kept.Count == 0) return;
        var result = _vocabulary.Capture($"{string.Join(", ", kept)} | {Sentence}");
        if (!result.Saved)
        {
            Problem = result.Message;
            return;
        }
        Dismiss();
        var count = result.Count > 0 ? result.Count : kept.Count;
        _ = ShowToastAsync($"Đã thêm {count.ToString(CultureInfo.InvariantCulture)} từ vào Vocabulary", result.Undo);
    }

    /// <summary>"Hoàn tác": the words just added leave Vocabulary again.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var undo = _undo;
        if (undo is null) return;
        undo();
        _ = ShowToastAsync("Đã hoàn tác", null);
    }

    [RelayCommand]
    private void DismissToast()
    {
        _toastVersion++;
        _undo = null;
        CanUndo = false;
        Toast = "";
    }

    [RelayCommand]
    private void Dismiss() => IsShown = false;

    private async Task ShowToastAsync(string text, Action? undo)
    {
        var mine = ++_toastVersion;
        _undo = undo;
        CanUndo = undo is not null;
        Toast = text;
        await Task.Delay(ToastFor);
        if (mine == _toastVersion) DismissToast();
    }

    private void AddChoice(SentenceWord word)
    {
        var choice = new SentenceWordChoice(word);
        choice.PropertyChanged += (_, _) => WordsChanged();
        Words.Add(choice);
    }

    private void WordsChanged()
    {
        OnPropertyChanged(nameof(HasWords));
        OnPropertyChanged(nameof(KeptCount));
        OnPropertyChanged(nameof(AddWordsText));
        AddWordsCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A word of the held sentence, a chip that is ticked to go to Vocabulary.</summary>
public sealed partial class SentenceWordChoice(SentenceWord word) : ObservableObject
{
    public SentenceWord Word { get; } = word;

    [ObservableProperty] private bool _keep;

    /// <summary>"错过 bỏ lỡ".</summary>
    public string Label => Word.Meaning.Length > 0 ? $"{Word.Word} {Word.Meaning}" : Word.Word;

    /// <summary>The chip's tip: its reading and meaning.</summary>
    public string Tip => string.Join(" · ", new[] { Word.Reading, Word.Meaning }.Where(s => s.Length > 0));
}
