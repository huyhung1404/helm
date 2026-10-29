using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Helm.Core.Services;
using Helm.Core.Settings;
using Helm.Core.Sync;
using Helm.Core.Text;
using Helm.Core.Ui;
using Helm.Shell.Services;

namespace Helm.Modules.Vocab;

/// <summary>One line of a word list. A word still waiting for the agent opens nothing; it can be removed (a typo).</summary>
public sealed record VocabWordRow(string Id, string Text, string Phonetic, string Meaning, bool IsPending, string LevelText, IRelayCommand Open,
    IRelayCommand? Remove = null)
{
    public bool HasPhonetic => Phonetic.Length > 0;

    public bool CanRemove => Remove is not null;
}

/// <summary>The page's tabs: learning, adding new words, looking words up.</summary>
public enum VocabTab
{
    Learn,
    Add,
    Words,
}

/// <summary>A choice of how often the PC pop-up comes (minutes between, picked at random).</summary>
public sealed record VocabPopupFrequency(string Name, int MinSeconds, int MaxSeconds);

/// <summary>
/// The Vocabulary page, shared by the Windows and Android apps: adding words, the list, the opened word with its tree,
/// the guessing card, and the settings. All members are used on the UI thread; store changes (local or synced) are
/// coalesced into one refresh through <see cref="IUiDispatcher"/>.
/// </summary>
public sealed partial class VocabViewModel : ObservableObject
{
    /// <summary>What to ask Claude Code for the waiting words (copied by the button next to the count).</summary>
    public const string AgentPrompt = "Phân tích các từ mới trong Helm Vocabulary (vocab_pending, rồi vocab_save_analysis cho từng từ).";

    public static IReadOnlyList<VocabPopupFrequency> PopupFrequencies { get; } =
    [
        new("Often (every 10–30 minutes)", 10 * 60, 30 * 60),
        new("Now and then (every 20–60 minutes)", 20 * 60, 60 * 60),
        new("Rarely (every 1–2 hours)", 60 * 60, 120 * 60),
        new("Seldom (every 2–4 hours)", 120 * 60, 240 * 60),
    ];

    /// <summary>The shortest custom gap: a card stays up to 90 seconds unanswered, so less than this only stacks up.</summary>
    public const int MinPopupSeconds = 5;

    public const int MaxPopupSeconds = 24 * 60 * 60;

    /// <summary>Index of "Custom" in the choices: after the fixed ones.</summary>
    public static int CustomFrequencyIndex => PopupFrequencies.Count;

    private readonly VocabStore _store;
    private readonly VocabQuiz _quiz;
    private readonly ISettingsStore<VocabSettings> _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly VocabVoice? _voice;
    private int _refreshQueued;
    private bool _loading;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(AddCommand))] private string _newWord = "";
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string _searchText = "";

    /// <summary>The Words tab lists one language at a time, in alphabetical order (Chinese by pinyin).</summary>
    [ObservableProperty] private VocabLanguage _wordsLanguage = VocabLanguage.English;

    [ObservableProperty] private int _englishCount;
    [ObservableProperty] private int _chineseCount;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private VocabWordDetailViewModel? _detail;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private int _wordCount;
    [ObservableProperty] private int _learnableCount;

    /// <summary>The tab in front: Learn (only the card), Add (new words and what waits for the agent), Words (the list, a word's tree).</summary>
    [ObservableProperty] private VocabTab _tab = VocabTab.Learn;

    // Settings
    [ObservableProperty] private bool _showTraditional;
    [ObservableProperty] private bool _showYouGlish;
    [ObservableProperty] private bool _lockScreenWord;
    [ObservableProperty] private int _itemsShownIndex;
    [ObservableProperty] private bool _popupEnabled;
    [ObservableProperty] private int _popupFrequencyIndex;
    [ObservableProperty] private string _customFromText = "";
    [ObservableProperty] private string _customToText = "";

    /// <summary>What is wrong with the typed range; empty when it is saved.</summary>
    [ObservableProperty] private string _customError = "";

    public VocabViewModel(VocabStore store, VocabQuiz quiz, ISettingsStoreFactory settings, IUiDispatcher ui, IDialogService dialogs, IClipboardService clipboard,
        VocabVoice? voice = null)
    {
        _voice = voice;
        _store = store;
        _quiz = quiz;
        _settings = settings.Get<VocabSettings>(VocabIds.ModuleId);
        _ui = ui;
        _dialogs = dialogs;
        _clipboard = clipboard;
        // Revealing a card shows how the word is built: the same tree as the Words tab, its links open the word there.
        Card = new VocabCardViewModel(store, quiz,
            (id, word) => new VocabWordDetailViewModel(store, id, word, quiz.StatsOf(id), ShowTraditional, w => Open(w), voice: voice, showYouGlish: ShowYouGlish), voice);
        if (voice is not null) voice.Problem += (_, problem) => _ui.Post(() => Message = problem);

        _loading = true;
        var s = _settings.Current;
        Pager = new ListPager(RefreshList, s.ItemsShown);
        ItemsShownIndex = ListPager.IndexOf(s.ItemsShown);
        ShowTraditional = s.ShowTraditional;
        ShowYouGlish = s.ShowYouGlish;
        LockScreenWord = s.LockScreenWord;
        Card.ShowYouGlish = s.ShowYouGlish;
        PopupEnabled = s.PopupEnabled;
        PopupFrequencyIndex = s.PopupCustom ? CustomFrequencyIndex : FrequencyIndex(s.PopupMinSeconds, s.PopupMaxSeconds);
        CustomFromText = s.PopupMinSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture);
        CustomToText = s.PopupMaxSeconds.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _loading = false;

        _store.Changed += (_, _) => ScheduleRefresh();
        _store.ReviewsChanged += (_, _) => ScheduleRefresh();
        Refresh();
        Card.ShowNext();
    }

    public ObservableCollection<VocabWordRow> Words { get; } = [];

    public ListPager Pager { get; }

    /// <summary>The guessing card on the page.</summary>
    public VocabCardViewModel Card { get; }

    public IReadOnlyList<string> ItemsShownNames => ListPager.SizeNames;

    public IReadOnlyList<string> PopupFrequencyNames { get; } = [.. PopupFrequencies.Select(f => f.Name), "Custom (type the seconds)"];

    public bool IsCustomFrequency => PopupFrequencyIndex == CustomFrequencyIndex;

    public bool HasCustomError => CustomError.Length > 0;

    public bool IsLearning => Tab == VocabTab.Learn;

    public bool IsAdding => Tab == VocabTab.Add;

    public bool IsWordsTab => Tab == VocabTab.Words;

    /// <summary>The words added last (Add tab), newest first: what was just added and whether the agent got to it.</summary>
    public ObservableCollection<VocabWordRow> RecentWords { get; } = [];

    public bool HasRecentWords => RecentWords.Count > 0;

    /// <summary>"Words (6)" on the tab.</summary>
    public string WordsTabText => $"Words ({WordCount})";

    /// <summary>Above the card: how many words take part (the ones waiting for the agent are on the Add tab).</summary>
    public string LearnSummary => LearnableCount switch
    {
        0 => "",
        1 => "1 word to learn",
        _ => $"{LearnableCount} words to learn",
    };

    public bool IsEnglishList => WordsLanguage == VocabLanguage.English;

    public bool IsChineseList => WordsLanguage == VocabLanguage.Chinese;

    public string EnglishTabText => $"English ({EnglishCount})";

    public string ChineseTabText => $"中文 ({ChineseCount})";

    public bool HasWords => WordCount > 0;

    public bool HasNoWords => WordCount == 0;

    public bool HasPending => PendingCount > 0;

    public bool HasDetail => Detail is not null;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string PendingText => PendingCount == 1
        ? "1 word waits for an AI agent to break it down."
        : $"{PendingCount} words wait for an AI agent to break them down.";

    public string Summary => WordCount == 1 ? "1 word" : $"{WordCount} words";

    partial void OnWordCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasWords));
        OnPropertyChanged(nameof(HasNoWords));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(WordsTabText));
    }

    partial void OnLearnableCountChanged(int value) => OnPropertyChanged(nameof(LearnSummary));

    partial void OnTabChanged(VocabTab value)
    {
        OnPropertyChanged(nameof(IsLearning));
        OnPropertyChanged(nameof(IsAdding));
        OnPropertyChanged(nameof(IsWordsTab));
    }

    partial void OnPendingCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
    }

    partial void OnDetailChanged(VocabWordDetailViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    partial void OnSearchTextChanged(string value) => RefreshList();

    partial void OnWordsLanguageChanged(VocabLanguage value)
    {
        OnPropertyChanged(nameof(IsEnglishList));
        OnPropertyChanged(nameof(IsChineseList));
        RefreshList();
    }

    partial void OnEnglishCountChanged(int value) => OnPropertyChanged(nameof(EnglishTabText));

    partial void OnChineseCountChanged(int value) => OnPropertyChanged(nameof(ChineseTabText));

    [RelayCommand]
    private void ShowEnglish() => WordsLanguage = VocabLanguage.English;

    [RelayCommand]
    private void ShowChinese() => WordsLanguage = VocabLanguage.Chinese;

    // ---- tabs --------------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void ShowLearn() => Tab = VocabTab.Learn;

    [RelayCommand]
    private void ShowAdd() => Tab = VocabTab.Add;

    [RelayCommand]
    private void ShowWords() => Tab = VocabTab.Words;

    // ---- adding --------------------------------------------------------------------------------------------------

    private bool CanAdd() => NewWord.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        try
        {
            var (_, added) = _store.AddWord(NewWord, NewNote);
            // The new word shows at the top of "Recently added", waiting for the agent; the user can add the next one.
            Message = added ? null : $"“{VocabText.Tidy(NewWord)}” is already in your words.";
            NewWord = "";
            NewNote = "";
        }
        catch (ArgumentException ex)
        {
            Message = ex.Message;
        }
    }

    [RelayCommand]
    private void CopyPrompt()
    {
        _clipboard.SetText(AgentPrompt);
        Message = "Copied. Paste it into Claude Code (Helm's MCP server must be connected).";
    }

    [RelayCommand]
    private void DismissMessage() => Message = null;

    // ---- the opened word -----------------------------------------------------------------------------------------

    /// <summary>Opens a word on the Words tab (its own box selected, or the given part's).</summary>
    public void Open(string id, string? partId = null)
    {
        Detail = Build(id, partId);
        if (Detail is null) return;
        Tab = VocabTab.Words;
        WordsLanguage = Detail.Word.Language;
    }

    /// <summary>The opened word again after a change (an analysis arriving, a sync), on whichever tab is in front.</summary>
    private void Reload(VocabWordDetailViewModel detail) => Detail = Build(detail.Id, detail.SelectedNode?.Node.PartId);

    private VocabWordDetailViewModel? Build(string id, string? partId) =>
        _store.GetWord(id) is { } word
            ? new VocabWordDetailViewModel(_store, id, word, _quiz.StatsOf(id), ShowTraditional, w => Open(w), partId, _voice, ShowYouGlish)
            : null;

    [RelayCommand]
    private void CloseDetail() => Detail = null;

    [RelayCommand]
    private async Task DeleteWordAsync()
    {
        if (Detail is not { } detail) return;
        if (!await _dialogs.ConfirmAsync("Delete this word?", $"“{detail.Word.Text}” leaves your words. Its parts stay for your other words.", "Delete")) return;
        _store.DeleteWord(detail.Id);
        Detail = null;
    }

    // ---- refreshing ----------------------------------------------------------------------------------------------

    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    private void Refresh()
    {
        RefreshList();
        // The opened word follows its changes (an analysis arriving from the agent, a sync), keeping the box selected.
        if (Detail is { } detail) Reload(detail);
        if (!Card.HasWord) Card.ShowNext();
    }

    private void RefreshList()
    {
        var all = _store.Words();
        // The Words tab holds only words with their analysis; the ones still waiting are on the Add tab.
        var analyzed = all.Where(w => w.Value.IsAnalyzed).ToList();
        WordCount = analyzed.Count;
        PendingCount = all.Count - analyzed.Count;
        LearnableCount = analyzed.Count;
        EnglishCount = analyzed.Count(w => w.Value.Language == VocabLanguage.English);
        ChineseCount = analyzed.Count - EnglishCount;
        var terms = TextSearch.Terms(SearchText);
        var matches = analyzed
            .Where(w => w.Value.Language == WordsLanguage)
            .Where(w => terms.Count == 0 || TextSearch.Score(terms, w.Value.Text, $"{w.Value.Meaning} {w.Value.HanViet} {w.Value.Note}") > 0)
            .OrderBy(w => SortKey(w.Value), StringComparer.Ordinal).ThenBy(w => w.Value.Text, StringComparer.Ordinal)
            .ToList();
        var stats = _quiz.Stats();
        VocabWordRow Row(SyncedItem<VocabWord> w)
        {
            var id = w.Id;
            return w.Value.IsAnalyzed
                ? new VocabWordRow(id, w.Value.Text, w.Value.Phonetic, w.Value.Meaning, false,
                    VocabCardViewModel.Level(stats.GetValueOrDefault(id) ?? VocabWordStats.New), new RelayCommand(() => Open(id)))
                : new VocabWordRow(id, w.Value.Text, "", "Waiting for analysis…", true, "", new RelayCommand(() => { }),
                    new RelayCommand(() => _store.DeleteWord(id)));
        }
        Words.Clear();
        foreach (var w in Pager.Take(matches, (SearchText, WordsLanguage))) Words.Add(Row(w));
        RecentWords.Clear();
        foreach (var w in all.Take(RecentCount)) RecentWords.Add(Row(w));
        OnPropertyChanged(nameof(HasRecentWords));
    }

    private const int RecentCount = 8;

    /// <summary>Alphabetical: English by the word, Chinese by its pinyin without tone marks (then by the characters).</summary>
    internal static string SortKey(VocabWord word) =>
        TextSearch.Fold(word.Language == VocabLanguage.Chinese && word.Phonetic.Length > 0 ? word.Phonetic : word.Text);

    // ---- settings ------------------------------------------------------------------------------------------------

    partial void OnShowTraditionalChanged(bool value)
    {
        if (_loading) return;
        _settings.Update(s => s.ShowTraditional = value);
        if (Detail is { } detail) Reload(detail);
    }

    partial void OnShowYouGlishChanged(bool value)
    {
        Card.ShowYouGlish = value;
        if (_loading) return;
        _settings.Update(s => s.ShowYouGlish = value);
        if (Detail is { } detail) Reload(detail);
    }

    partial void OnLockScreenWordChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.LockScreenWord = value);
    }

    partial void OnItemsShownIndexChanged(int value)
    {
        if (_loading) return;
        var size = ListPager.SizeAt(value);
        _settings.Update(s => s.ItemsShown = size);
        Pager.PageSize = size;
        RefreshList();
    }

    partial void OnPopupEnabledChanged(bool value)
    {
        if (!_loading) _settings.Update(s => s.PopupEnabled = value);
    }

    partial void OnPopupFrequencyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomFrequency));
        if (_loading) return;
        if (value == CustomFrequencyIndex)
        {
            SaveCustomRange();
            return;
        }
        var f = PopupFrequencies[Math.Clamp(value, 0, PopupFrequencies.Count - 1)];
        CustomError = "";
        _settings.Update(s =>
        {
            s.PopupMinSeconds = f.MinSeconds;
            s.PopupMaxSeconds = f.MaxSeconds;
            s.PopupCustom = false;
        });
    }

    partial void OnCustomFromTextChanged(string value) => SaveCustomRange();

    partial void OnCustomToTextChanged(string value) => SaveCustomRange();

    partial void OnCustomErrorChanged(string value) => OnPropertyChanged(nameof(HasCustomError));

    /// <summary>Saves the typed range once both numbers make sense; until then says what is wrong.</summary>
    private void SaveCustomRange()
    {
        if (_loading || !IsCustomFrequency) return;
        if (!int.TryParse(CustomFromText.Trim(), out var from) || !int.TryParse(CustomToText.Trim(), out var to))
        {
            CustomError = "Type whole numbers of seconds, e.g. 30 and 90.";
            return;
        }
        if (from < MinPopupSeconds)
        {
            CustomError = $"At least {MinPopupSeconds} seconds.";
            return;
        }
        if (to < from)
        {
            CustomError = "The second number is the longest gap: at least the first.";
            return;
        }
        if (to > MaxPopupSeconds)
        {
            CustomError = $"At most {MaxPopupSeconds} seconds (a day).";
            return;
        }
        CustomError = "";
        _settings.Update(s =>
        {
            s.PopupMinSeconds = from;
            s.PopupMaxSeconds = to;
            s.PopupCustom = true;
        });
    }

    private static int FrequencyIndex(int min, int max)
    {
        for (var i = 0; i < PopupFrequencies.Count; i++)
            if (PopupFrequencies[i].MinSeconds == min && PopupFrequencies[i].MaxSeconds == max) return i;
        return 1;
    }
}
