using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Ai;
using Helm.Core.Capture;

namespace Helm.Modules.NovelReader;

/// <summary>
/// The sheet a long press on the text opens ("Câu này"): the held sentence explained by AI — its meaning, grammar and a
/// natural translation — with its pinyin, a speaker that says it, and its words as chips: the ones ticked go to
/// Vocabulary in one tap, with the sentence as their note, and an Undo right after. The held word is a chip from the
/// start, so it can go to Vocabulary without AI. Vocabulary is reached through its Quick Capture target, so the two tools
/// stay apart; without it there are no chips. UI thread only.
/// </summary>
public sealed partial class HeldSentenceViewModel(IEnumerable<ICaptureTarget>? targets, IAiText? ai = null, Func<string, Task<string?>>? say = null)
    : ObservableObject
{
    /// <summary>Vocabulary's target id (Helm.Modules.Vocab.VocabCaptureTarget.TargetId).</summary>
    public const string VocabularyTarget = "vocab";

    /// <summary>How long "added to Vocabulary" stays with its Undo.</summary>
    internal static TimeSpan ToastFor { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>Without AI the sheet still says the sentence and offers the held word.</summary>
    public const string NoAi = "Explaining a sentence needs AI: an agent on a Helm PC, or a Claude or Grok key (AI → Models & keys on the PC).";

    private readonly ICaptureTarget? _vocabulary = targets?.FirstOrDefault(t => t.Id == VocabularyTarget);
    // A sentence held meanwhile makes an explanation still on its way stale; a newer toast outlives the older one's timer.
    private int _version;
    private int _toastVersion;
    private Action? _undo;

    [ObservableProperty] private bool _isShown;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Sentence))] private HeldSentence? _held;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPinyin))] private string _pinyin = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ExplainCommand))] private bool _isExplaining;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasExplanation), nameof(HasGrammar), nameof(Grammar))] private SentenceExplanation? _explanation;

    /// <summary>Where the explanation ran and what it cost ("Claude Code trên PC-HUNG · miễn phí").</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRanOn))] private string _ranOn = "";

    /// <summary>Why there is no explanation, or why the sentence could not be said or its words added.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasProblem))] private string _problem = "";

    /// <summary>The toast after words went to Vocabulary ("Đã thêm 2 từ vào Vocabulary"), with Undo while it can.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasToast))] private string _toast = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(UndoCommand))] private bool _canUndo;

    /// <summary>The held sentence's Chinese.</summary>
    public string Sentence => Held?.Chinese ?? "";

    public bool HasPinyin => Pinyin.Length > 0;

    public bool HasExplanation => Explanation is not null;

    /// <summary>The grammar points, one per line.</summary>
    public string Grammar => Explanation is { } e ? string.Join("\n", e.Grammar.Select(g => "• " + g)) : "";

    public bool HasGrammar => Explanation is { Grammar.Count: > 0 };

    public bool HasRanOn => RanOn.Length > 0;

    public bool HasProblem => Problem.Length > 0;

    public bool HasToast => Toast.Length > 0;

    /// <summary>Words can go to Vocabulary (the tool is there and the sentence has some).</summary>
    public bool HasWords => _vocabulary is not null && Words.Count > 0;

    /// <summary>The sentence's words: the held one first, then the ones the explanation picked. None is ticked at first.</summary>
    public ObservableCollection<SentenceWordChoice> Words { get; } = [];

    public int KeptCount => Words.Count(w => w.Keep);

    /// <summary>"Thêm 2 từ".</summary>
    public string AddWordsText => $"Thêm {KeptCount.ToString(CultureInfo.InvariantCulture)} từ";

    /// <summary>A sentence was held: show it and explain it (an agent on a Helm PC first, else the API).</summary>
    public Task ShowAsync(HeldSentence held)
    {
        _version++;
        Held = held;
        Pinyin = held.Pinyin;
        Explanation = null;
        RanOn = "";
        Problem = "";
        Words.Clear();
        if (held.Word.Word.Length > 0) AddChoice(held.Word);
        WordsChanged();
        IsExplaining = false;
        IsShown = true;
        return ExplainAsync();
    }

    private bool CanExplain() => Sentence.Length > 0 && !IsExplaining;

    /// <summary>Explains the sentence; again after a problem.</summary>
    [RelayCommand(CanExecute = nameof(CanExplain))]
    private async Task ExplainAsync()
    {
        if (Sentence.Length == 0) return;
        if (ai is null)
        {
            Problem = NoAi;
            return;
        }
        if (ai.Unavailable(SentenceExplainer.Feature) is { } unavailable)
        {
            Problem = unavailable;
            return;
        }
        var version = _version;
        Problem = "";
        IsExplaining = true;
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
        if (version != _version) return;
        IsExplaining = false;
        var explanation = answer.Ok ? SentenceExplainer.Parse(answer.Text) : null;
        if (explanation is null)
        {
            Problem = answer.Problem ?? "The answer could not be read. Try again.";
            return;
        }
        Explanation = explanation;
        foreach (var word in explanation.Words)
            if (!Words.Any(w => w.Word.Word == word.Word)) AddChoice(word);
        WordsChanged();
        RanOn = RanOnText(answer);
    }

    /// <summary>"Claude Code trên PC-HUNG · miễn phí", or "claude-haiku-4-5 qua API · $0.0012".</summary>
    internal static string RanOnText(AiTextAnswer answer)
    {
        if (answer.RanOn is { } pc) return $"{pc} · miễn phí";
        var where = answer.Model is { } model ? $"{model} qua API" : "API";
        return answer.Usd is { } usd ? $"{where} · {usd.ToString(usd is > 0 and < 0.01m ? "$0.0000" : "$0.00", CultureInfo.InvariantCulture)}" : where;
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
    private void Dismiss()
    {
        // An explanation still on its way is not wanted any more.
        _version++;
        IsExplaining = false;
        IsShown = false;
    }

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
